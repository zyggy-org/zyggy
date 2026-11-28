#!/usr/bin/env node
// Runs the project's Verification Commands from .claude/spec-driven.json. Installed as
// <project>/.claude/hooks/verify.mjs.
//   node .claude/hooks/verify.mjs                     the feature gate: run all, print each result, exit 1 on the first failure
//   node .claude/hooks/verify.mjs --feature           the same
//   node .claude/hooks/verify.mjs --slice <test>...   the slice gate: every command but the format check
//                                                     (verify.format), the integration command (verify.integration)
//                                                     filtered to the named tests; red when the filter matches zero tests
// Kit 3.0.0: the format check (dotnet format --verify-no-changes) never runs in the slice gate; it runs once per feature,
// in the final review (the feature gate, then the Analysis), like the analyzers.
//   --verbose (with either gate)                      every command's full output; by default one line per command
//                                                     and, for a failing one, its failing tests and at most 30 lines
// Kit 2.6.4: no red check (2.6.1 and 2.6.2 removed); the slice's tests prove they can fail through the mutation check of
// the dotnet-shared pack (scripts/mutation-check.mjs), which build-slice runs after the slice gate.
// Also imported by guard.mjs, which refuses a slice file (_specs/<Feature>/slice-<n>.md, written by builds before kit 2.6.0)
// while verification fails.

import { spawnSync, execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const CONFIG = '.claude/spec-driven.json';
const STATE = '.claude/spec-driven.state.json';
/** The review agents' reports: not source, not part of the proof. */
const ARTIFACTS = 'artifacts';

/** The project is the folder this script is installed in (<project>/.claude/hooks/), whatever the session's folder is. */
export function projectRoot() {
  return resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
}

/** Returns the parsed config, or null when the project has not been set up. */
export function loadConfig(root) {
  const file = join(root, CONFIG);
  if (!existsSync(file)) return null;
  return JSON.parse(readFileSync(file, 'utf8'));
}

function git(root, args) {
  return execFileSync('git', args, { cwd: root, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 });
}

/** A hash of HEAD plus every uncommitted change outside the specs folder and the artifacts folder. */
export function treeFingerprint(root, specsDir = '_specs') {
  const scope = ['--', '.', `:(exclude)${specsDir}`, `:(exclude)${ARTIFACTS}`, `:(exclude)${STATE}`];
  const hash = createHash('sha256');
  try {
    hash.update(git(root, ['rev-parse', 'HEAD']));
  } catch {
    hash.update('no-commit');
  }
  hash.update(git(root, ['status', '--porcelain', ...scope]));
  try {
    hash.update(git(root, ['diff', 'HEAD', ...scope]));
  } catch {
    hash.update(git(root, ['diff', ...scope])); // no commit yet
  }
  const untracked = git(root, ['ls-files', '--others', '--exclude-standard', ...scope]);
  for (const rel of untracked.split('\n').filter(Boolean)) {
    try {
      hash.update(rel).update(readFileSync(join(root, rel)));
    } catch {
      hash.update(rel);
    }
  }
  return hash.digest('hex');
}

function tail(text, lines = 40) {
  return text.trimEnd().split(/\r?\n/).slice(-lines).join('\n');
}

/** The lines of a failing command's output printed by default (the guard keeps its own 40). */
const EXCERPT = 30;

/**
 * How test runners name a failed test: dotnet test (VSTest) `Failed <name> [12 ms]`, the Microsoft testing platform
 * `failed <name> (12ms)`, Jest `● <name>`, pytest `FAILED <name> - <assertion>`, node's TAP `not ok 3 - <name>`.
 */
const FAILED_TEST = [
  /^\s*Failed\s+(\S.*?)\s+\[[^\]]*\]\s*$/,
  /^\s*failed\s+(\S.*?)\s+\(\d[^)]*\)\s*$/,
  /^\s*●\s+(\S.*?)\s*$/,
  /^\s*FAILED\s+(\S+)(?:\s+-\s+(.*?))?\s*$/,
  /^\s*not ok \d+ - (.+?)\s*$/,
];
/** A line that only announces what follows (`Error Message:`, `---`). */
const HEADER = /^\s*(Error Message:|Stack Trace:|---|\.\.\.)\s*$/;
const ASSERTION = /assert|expect|error/i;

/**
 * The failed tests a runner's output names, each with its first assertion line: the first line after the name (up to
 * the next failed test, at most 8 lines) that reads as an assertion, else the first non-empty one; pytest gives it on
 * the name's own line.
 */
export function failingTests(output) {
  const lines = output.split(/\r?\n/);
  const found = [];
  for (let i = 0; i < lines.length; i += 1) {
    const match = FAILED_TEST.map((pattern) => lines[i].match(pattern)).find(Boolean);
    if (!match) continue;
    let assertion = match[2] ?? null;
    if (!assertion) {
      const next = [];
      for (let j = i + 1; j < lines.length && j <= i + 8; j += 1) {
        if (FAILED_TEST.some((pattern) => pattern.test(lines[j]))) break;
        if (lines[j].trim() && !HEADER.test(lines[j])) next.push(lines[j].trim());
      }
      assertion = next.find((line) => ASSERTION.test(line)) ?? next[0] ?? '';
    }
    if (!found.some((t) => t.name === match[1])) found.push({ name: match[1], assertion });
  }
  return found;
}

/** A test name the slice gate puts into a shell command: letters, digits, _ . : - only. */
const TEST_NAME = /^[\w.:-]+$/;

/**
 * What a test runner prints when a filter matched no test while it still exits 0 (or with its own code): dotnet test
 * (VSTest), the Microsoft testing platform, Jest and pytest. A project sets verify.integration.noTests for another runner.
 */
const NO_TESTS = 'No test matches the given testcase filter|No test is available in|Zero tests ran|No tests found|no tests ran';

/**
 * What marks a Verification Command as the format check, which the slice gate leaves out: any `dotnet format` command
 * (in verify.commands always its non-modifying form). A project sets verify.format, a regular expression, for another
 * formatter's check.
 */
const FORMAT_CHECK = '\\bdotnet\\s+format\\b';

/**
 * The commands of a gate. The feature gate (the default) is every command of verify.commands. The slice gate is
 * the same list without the format check (verify.format, or FORMAT_CHECK; returned as skipped), and with the
 * integration command (verify.integration.command) filtered to the given tests:
 * `${command} ${filter}`, where {tests} in filter is each name put into test ({name}), joined by separator.
 * Without verify.integration the slice gate runs every command, and says so in note.
 * The slice gate's filtered command is also returned as filtered, with noTests: the pattern (verify.integration.noTests,
 * or NO_TESTS) whose match in its output means the filter matched zero tests, which fails the gate.
 * Throws on a slice gate without tests, on a test name with other characters, or on an unknown integration command.
 */
export function gateCommands(config, gate = { mode: 'feature' }) {
  const verify = config.verify ?? {};
  const commands = verify.commands ?? [];
  if (gate.mode !== 'slice') return { mode: 'feature', commands, skipped: [], integrationTests: null, note: null, filtered: null, noTests: null };
  const tests = gate.tests ?? [];
  if (tests.length === 0) throw new Error('the slice gate needs the integration tests written in this slice: --slice <test> [<test>...]');
  for (const name of tests) {
    if (!TEST_NAME.test(name)) throw new Error(`"${name}" is not a test name (letters, digits, _ . : - only)`);
  }
  const integration = verify.integration;
  const format = new RegExp(verify.format || FORMAT_CHECK, 'i');
  const isFormatCheck = (c) => c !== integration?.command && format.test(c);
  const skipped = commands.filter(isFormatCheck);
  const kept = commands.filter((c) => !isFormatCheck(c));
  if (!integration?.command) {
    return { mode: 'slice', commands: kept, skipped, integrationTests: tests, note: `no verify.integration in ${CONFIG}: the slice gate runs every integration test`, filtered: null, noTests: null };
  }
  if (!commands.includes(integration.command)) {
    throw new Error(`verify.integration.command "${integration.command}" is not one of verify.commands in ${CONFIG}`);
  }
  const each = integration.test ?? '{name}';
  const joined = tests.map((name) => each.split('{name}').join(name)).join(integration.separator ?? ' ');
  const filter = (integration.filter ?? '{tests}').split('{tests}').join(joined);
  const filtered = `${integration.command} ${filter}`;
  return {
    mode: 'slice', commands: kept.map((c) => (c === integration.command ? filtered : c)), skipped, integrationTests: tests, note: null,
    filtered, noTests: integration.noTests || NO_TESTS,
  };
}

/**
 * Runs every command of the gate in order and stops at the first failure; without a gate, the feature gate.
 * A command fails on a non-zero exit code, or when its output matches verify.failPattern; the slice gate's filtered
 * integration command also fails when its output says the filter matched zero tests.
 */
export function runVerify(root, config, onResult = () => {}, gate = { mode: 'feature' }) {
  const verify = config.verify ?? {};
  const { commands, filtered, noTests } = gateCommands(config, gate);
  const noTestsPattern = noTests ? new RegExp(noTests, 'i') : null;
  const failPattern = verify.failPattern ? new RegExp(verify.failPattern, 'm') : null;
  const results = [];
  for (const command of commands) {
    const run = spawnSync(command, {
      cwd: root,
      shell: verify.shell || true,
      encoding: 'utf8',
      maxBuffer: 256 * 1024 * 1024,
    });
    const output = `${run.stdout ?? ''}${run.stderr ?? ''}`;
    let reason = null;
    if (run.error) reason = `could not start: ${run.error.message}`;
    else if (run.status !== 0) reason = `exit code ${run.status}`;
    else if (failPattern && failPattern.test(output)) reason = `output matches failPattern /${verify.failPattern}/`;
    else if (command === filtered && noTestsPattern?.test(output)) reason = 'the filter matched zero tests: the slice is not done until --slice names its integration test class';
    // output stays the last 40 lines the guard shows; fullOutput is for the printout (--verbose, the failing tests).
    const result = { command, ok: reason === null, reason, output: tail(output), fullOutput: output };
    results.push(result);
    onResult(result);
    if (!result.ok) break;
  }
  return { ok: commands.length > 0 && results.every((r) => r.ok), empty: commands.length === 0, results };
}

export function readState(root) {
  try {
    return JSON.parse(readFileSync(join(root, STATE), 'utf8'));
  } catch {
    return {};
  }
}

export function writeState(root, state) {
  writeFileSync(join(root, STATE), JSON.stringify(state, null, 2) + '\n');
}

const isMain = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  const root = projectRoot();
  const config = loadConfig(root);
  if (!config) {
    console.error(`${CONFIG} not found in ${root}. Run /setup from the kit folder first.`);
    process.exit(1);
  }
  const args = process.argv.slice(2);
  const verbose = args.includes('--verbose');
  const [flag, ...tests] = args.filter((a) => a !== '--verbose');
  let gate;
  if (flag === undefined || (flag === '--feature' && tests.length === 0)) gate = { mode: 'feature' };
  else if (flag === '--slice') gate = { mode: 'slice', tests };
  else {
    console.error('Usage: node .claude/hooks/verify.mjs [--feature | --slice <test> [<test>...]] [--verbose]');
    process.exit(1);
  }
  let plan;
  try {
    plan = gateCommands(config, gate);
  } catch (error) {
    console.error(`Gate not run: ${error.message}.`);
    process.exit(1);
  }
  const label = plan.mode === 'slice'
    ? `slice gate (integration tests: ${plan.integrationTests.join(', ')})`
    : 'feature gate (every Verification Command, every integration test)';
  if (verbose) console.log(`Running the ${label}.`);
  if (plan.note) console.log(`Note: ${plan.note}.`);
  for (const command of plan.skipped) console.log(`SKIP  ${command}  (the format check runs once per feature, in the final review)`);
  // By default one line per command; for the failing one its failing tests and at most EXCERPT lines of its output.
  const outcome = runVerify(root, config, (r) => {
    if (verbose && r.fullOutput.trim()) console.log(r.fullOutput.trimEnd());
    console.log(`${r.ok ? 'PASS' : 'FAIL'}  ${r.command}${r.ok ? '' : `  (${r.reason})`}`);
    if (r.ok || verbose) return;
    const failed = failingTests(r.fullOutput);
    if (failed.length > 0) {
      console.log(`Failing tests (${failed.length}):`);
      for (const t of failed) console.log(`  ${t.name}${t.assertion ? `\n    ${t.assertion}` : ''}`);
    }
    const excerpt = tail(r.fullOutput, EXCERPT);
    if (excerpt) console.log(`Last ${Math.min(EXCERPT, excerpt.split('\n').length)} lines of its output (--verbose for all):\n${excerpt}`);
  }, gate);
  if (outcome.empty) {
    console.error(`No commands under "verify.commands" in ${CONFIG}.`);
    process.exit(1);
  }
  const ran = { gate: plan.mode, integrationTests: plan.integrationTests ?? 'all' };
  if (outcome.ok) {
    writeState(root, { verifiedFingerprint: treeFingerprint(root, config.specsDir), ...ran });
    console.log(`Verification green: ${label}.`);
  } else {
    // A red run of either gate clears the verified tree: the guard then runs every command and refuses a slice file.
    writeState(root, { ...ran, failed: outcome.results.at(-1).command });
    console.log(`Verification red: ${label}.`);
  }
  process.exit(outcome.ok ? 0 : 1);
}
