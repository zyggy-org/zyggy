#!/usr/bin/env node
// PreToolUse hook of the spec-driven kit, installed as <project>/.claude/hooks/guard.mjs. Reads the
// hook input on stdin; exit 2 blocks the tool call and shows the reason to Claude; a JSON decision
// on stdout asks the user for a confirmation. Does nothing until .claude/spec-driven.json exists.
//
// Guards:
//   1. No source edits on a protected branch (Claude's own files are allowed), and no spec files
//      there either: every spec is written on a branch of its own.
//   2. No slice file (_specs/<Feature>/slice-<n>.md, written by builds before kit 2.6.0) while the
//      Verification Commands fail.
//   3. No git command that changes files, history or branches inside the review agents; the
//      read-only review agents write nothing but their report under artifacts/.
//   4. git commit and git push: refused on a protected branch (the project's list, and always
//      main, master, dev and release/*); a force-push and a tag deletion are refused always;
//      otherwise the user is asked in an attended session, and an unattended job (ZYGGY_JOB set,
//      nobody there to answer) passes them on to the job's own permissions.

import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { isAbsolute, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadConfig, projectRoot, readState, runVerify, treeFingerprint, writeState } from './verify.mjs';

const REVIEW_AGENTS = ['spec-critic', 'spec-reviewer'];
/** Review agents that change no file but their report. */
const REPORT_ONLY_AGENTS = ['spec-critic', 'spec-reviewer'];
/** The report channel of the review agents: one file per report, ignored by git. */
const REPORT_FILE = /^artifacts\/[^/]+-report\.md$/;
/** Branches on which commit and push are refused whatever the project's list says. */
export const ALWAYS_PROTECTED = ['main', 'master', 'dev', 'release/*'];
const MUTATING_GIT =
  /\bgit\b[^|;&\n]*?\s(commit|push|add|rm|mv|reset|checkout|switch|restore|stash|clean|merge|rebase|cherry-pick|revert|apply|am|tag)\b/;
/** Global git options that take a value as the next token (git -C <path> commit ...). */
const GIT_OPTIONS_WITH_VALUE = new Set(['-C', '-c', '--git-dir', '--work-tree', '--namespace', '--exec-path', '--super-prefix', '--config-env']);
const SHORT_FLAG = (letter) => new RegExp(`^-[a-zA-Z]*${letter}[a-zA-Z]*$`);

function block(reason) {
  console.error(`spec-driven guard: ${reason}`);
  process.exit(2);
}

function ask(reason) {
  const output = { hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: 'ask', permissionDecisionReason: `spec-driven guard: ${reason}` } };
  console.log(JSON.stringify(output));
  process.exit(0);
}

function relativePath(root, file) {
  const rel = relative(root, isAbsolute(file) ? file : resolve(root, file));
  if (rel.startsWith('..') || isAbsolute(rel)) return null; // outside the project
  return rel.split('\\').join('/');
}

function allowedOnProtected(rel, entries) {
  return entries.some((entry) => (entry.endsWith('/') ? rel.startsWith(entry) : rel === entry));
}

/** Whether a branch name matches one of the patterns (an exact name, or a glob with `*`). */
export function matchesBranch(branch, patterns) {
  return patterns.some((pattern) => {
    if (!pattern.includes('*')) return branch === pattern;
    const parts = pattern.split('*').map((part) => part.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
    return new RegExp(`^${parts.join('.*')}$`).test(branch);
  });
}

function currentBranch(root) {
  try {
    return execFileSync('git', ['rev-parse', '--abbrev-ref', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim();
  } catch {
    return null; // not a repository, or no commit yet
  }
}

/** The lines of a shell command without heredoc bodies: a commit message is not a command. */
function commandLines(command) {
  const lines = [];
  let marker = null;
  for (const line of String(command).split(/\r?\n/)) {
    if (marker !== null) {
      if (line.trim() === marker) marker = null;
      continue;
    }
    lines.push(line);
    const heredoc = /<<-?\s*(?:'([^']+)'|"([^"]+)"|(\w+))/.exec(line);
    if (heredoc) marker = heredoc[1] ?? heredoc[2] ?? heredoc[3];
  }
  return lines;
}

/** Splits a line into simple commands (on |, ||, ;, &, &&), each as quote-aware tokens. */
function simpleCommands(line) {
  const commands = [];
  let tokens = [];
  let token = '';
  let inToken = false;
  let quote = null;
  const endToken = () => {
    if (inToken) tokens.push(token);
    token = '';
    inToken = false;
  };
  const endCommand = () => {
    endToken();
    if (tokens.length) commands.push(tokens);
    tokens = [];
  };
  for (let i = 0; i < line.length; i++) {
    const char = line[i];
    if (quote) {
      if (char === quote) quote = null;
      else token += char;
      continue;
    }
    if (char === '"' || char === "'") {
      quote = char;
      inToken = true;
    } else if (char === '\\' && i + 1 < line.length) {
      token += line[++i];
      inToken = true;
    } else if (/\s/.test(char)) {
      endToken();
    } else if (char === '|' || char === ';' || char === '&') {
      endCommand();
    } else {
      token += char;
      inToken = true;
    }
  }
  endCommand();
  return commands;
}

/** The git subcommand and its arguments of a token list, or null when it is not a git command. */
function gitInvocation(tokens) {
  let i = tokens.findIndex((token) => token === 'git' || /[\\/]git(\.exe)?$/i.test(token));
  if (i < 0) return null;
  for (i += 1; i < tokens.length; i++) {
    const token = tokens[i];
    if (!token.startsWith('-')) break;
    if (GIT_OPTIONS_WITH_VALUE.has(token)) i += 1;
  }
  if (i >= tokens.length) return null;
  return { subcommand: tokens[i], args: tokens.slice(i + 1) };
}

/** The branch a push refspec writes to (`src:dst` gives dst), without refs/heads/. */
function pushTarget(refspec) {
  const dst = refspec.includes(':') ? refspec.slice(refspec.indexOf(':') + 1) : refspec;
  return dst.replace(/^refs\/heads\//, '');
}

/**
 * What the guard does with a shell command, as { action: 'block' | 'ask' | 'pass', reason }.
 * context: { branch, protectedBranches, unattended }.
 */
export function judgeGitCommand(command, context) {
  const protectedBranches = [...new Set([...ALWAYS_PROTECTED, ...(context.protectedBranches ?? [])])];
  let confirm = null;
  for (const line of commandLines(command)) {
    for (const tokens of simpleCommands(line)) {
      const git = gitInvocation(tokens);
      if (!git) continue;
      const { subcommand, args } = git;
      if (subcommand === 'tag' && args.some((arg) => arg === '--delete' || SHORT_FLAG('d').test(arg))) {
        return { action: 'block', reason: 'a tag is never deleted by Claude (git tag -d). Leave the tag and report it.' };
      }
      if (subcommand !== 'commit' && subcommand !== 'push') continue;
      if (subcommand === 'push') {
        const options = args.filter((arg) => arg.startsWith('-'));
        const operands = args.filter((arg) => !arg.startsWith('-'));
        const forced = options.some((arg) => arg === '--force' || arg.startsWith('--force-with-lease') || arg === '--force-if-includes' || SHORT_FLAG('f').test(arg));
        if (forced || operands.some((arg) => arg.startsWith('+'))) {
          return { action: 'block', reason: 'a force-push (git push --force, -f, --force-with-lease, +refspec) is never run by Claude. Push without force, or leave it to the user.' };
        }
        if (options.some((arg) => arg === '--delete' || SHORT_FLAG('d').test(arg)) || operands.some((arg) => arg.startsWith(':'))) {
          return { action: 'block', reason: 'a remote branch or tag is never deleted by Claude (git push --delete, git push <remote> :<ref>). Leave it and report it.' };
        }
        const named = operands.slice(1).map(pushTarget).find((target) => target !== 'HEAD' && matchesBranch(target, protectedBranches));
        if (named) return { action: 'block', reason: `"${named}" is a protected branch and is never pushed to by Claude; it changes through a pull request.` };
      }
      if (context.branch && matchesBranch(context.branch, protectedBranches)) {
        return {
          action: 'block',
          reason: `"${context.branch}" is a protected branch: no git ${subcommand} there. Create or switch to the spec's branch as .claude/rules/spec-driven.md § One branch per spec says, then ${subcommand} there.`,
        };
      }
      confirm ??= subcommand === 'push'
        ? 'git push publishes the branch; the user confirms it (.claude/rules/spec-driven.md § Commit and push).'
        : 'git commit is the user\'s acceptance of the work; the user confirms it (.claude/rules/spec-driven.md § Commit and push).';
    }
  }
  if (confirm === null) return { action: 'pass', reason: null };
  if (context.unattended) return { action: 'pass', reason: 'unattended job: the job\'s own permissions decide' };
  return { action: 'ask', reason: confirm };
}

function main() {
  const input = JSON.parse(readFileSync(0, 'utf8') || '{}');
  const root = projectRoot();
  const config = loadConfig(root);
  if (!config) return;

  const tool = input.tool_name;
  const args = input.tool_input ?? {};
  const agent = String(input.agent_type ?? '').split(':').pop();
  const inReviewAgent = (config.reviewAgents ?? REVIEW_AGENTS).includes(agent);
  const specsDir = (config.specsDir ?? '_specs').replace(/\/+$/, '');

  if (tool === 'Bash' || tool === 'PowerShell') {
    const command = String(args.command ?? '');
    const match = inReviewAgent && MUTATING_GIT.exec(command);
    if (match) {
      block(`the ${agent} agent reviews and does not run "git ${match[1]}". Report what you found instead.`);
    }
    const verdict = judgeGitCommand(command, {
      branch: currentBranch(root),
      protectedBranches: config.protectedBranches ?? [],
      unattended: Boolean(process.env.ZYGGY_JOB),
    });
    if (verdict.action === 'block') block(verdict.reason);
    if (verdict.action === 'ask') ask(verdict.reason);
    return;
  }

  if (tool !== 'Edit' && tool !== 'Write' && tool !== 'NotebookEdit') return;
  const file = args.file_path ?? args.notebook_path;
  if (!file) return;
  const rel = relativePath(root, file);
  if (rel === null) return;

  if (inReviewAgent && REPORT_FILE.test(rel)) return; // the report channel, on any branch
  if (REPORT_ONLY_AGENTS.includes(agent)) {
    block(`the ${agent} agent changes no file: the only file it writes is its report, artifacts/<name>-report.md. Report what you found there.`);
  }
  if (inReviewAgent && rel.startsWith(`${specsDir}/`)) {
    block(`the ${agent} agent does not change specs. Report what you found instead.`);
  }

  const allowed = config.allowOnProtected ?? [`${specsDir}/`, '.claude/', 'CLAUDE.md'];
  const branch = currentBranch(root);
  const onProtected = branch && matchesBranch(branch, config.protectedBranches ?? []);
  const howToBranch =
    'Switch to the branch named in the spec\'s Status, or create one as .claude/rules/spec-driven.md ' +
    '§ One branch per spec says (git fetch, then git switch -c <branch> origin/<base>), then continue there.';
  if (onProtected && new RegExp(`^${specsDir}/[^/]+/`).test(rel)) {
    block(`"${branch}" is a protected branch, and every spec is written on a branch of its own. ${howToBranch}`);
  }
  if (onProtected && !allowedOnProtected(rel, allowed)) {
    block(`"${branch}" is a protected branch and ${rel} is not one of Claude's own files. ${howToBranch}`);
  }

  if (new RegExp(`^${specsDir}/[^/]+/slice-\\d+\\.md$`).test(rel)) {
    const fingerprint = treeFingerprint(root, specsDir);
    if (readState(root).verifiedFingerprint === fingerprint) return;
    const outcome = runVerify(root, config);
    if (outcome.empty) return; // nothing established yet; the slice file says so
    if (!outcome.ok) {
      const failed = outcome.results.at(-1);
      block(
        'a slice file is written after the proof, and the Verification Commands fail.\n' +
          `FAIL  ${failed.command}  (${failed.reason})\n${failed.output}\n` +
          'Make them pass (node .claude/hooks/verify.mjs), then write it.',
      );
    }
    writeState(root, { verifiedFingerprint: fingerprint });
  }
}

const isMain = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  try {
    main();
  } catch (error) {
    // A broken guard must be seen, not silently skipped: exit 1 shows the error without blocking.
    console.error(`spec-driven guard failed: ${error.message}`);
    process.exit(1);
  }
}
