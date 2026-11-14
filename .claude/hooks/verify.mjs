#!/usr/bin/env node
// Runs the project's Verification Commands from .claude/spec-driven.json.
//   node .claude/hooks/verify.mjs          run all, print each result, exit 1 on the first failure
// Also imported by guard.mjs, which refuses a slice recap while verification fails.

import { spawnSync, execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const CONFIG = '.claude/spec-driven.json';
const STATE = '.claude/spec-driven.state.json';

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

/** A hash of HEAD plus every uncommitted change outside the specs folder. */
export function treeFingerprint(root, specsDir = '_specs') {
  const scope = ['--', '.', `:(exclude)${specsDir}`, `:(exclude)${STATE}`];
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

/**
 * Runs every command in order and stops at the first failure.
 * A command fails on a non-zero exit code, or when its output matches verify.failPattern.
 */
export function runVerify(root, config, onResult = () => {}) {
  const verify = config.verify ?? {};
  const commands = verify.commands ?? [];
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
    const result = { command, ok: reason === null, reason, output: tail(output) };
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
  const outcome = runVerify(root, config, (r) => {
    console.log(`${r.ok ? 'PASS' : 'FAIL'}  ${r.command}${r.ok ? '' : `  (${r.reason})`}`);
    if (!r.ok) console.log(r.output);
  });
  if (outcome.empty) {
    console.error(`No commands under "verify.commands" in ${CONFIG}.`);
    process.exit(1);
  }
  if (outcome.ok) {
    writeState(root, { verifiedFingerprint: treeFingerprint(root, config.specsDir) });
    console.log('Verification green.');
  }
  process.exit(outcome.ok ? 0 : 1);
}
