#!/usr/bin/env node
// PreToolUse hook of the spec-driven kit. Reads the hook input on stdin; exit 2 blocks the
// tool call and shows the reason to Claude. Does nothing until .claude/spec-driven.json exists.
//
// Guards:
//   1. No source edits on a protected branch (specs and Claude's own files are allowed).
//   2. No slice recap (_specs/<Feature>/slice-<n>.md) while the Verification Commands fail.
//   3. No git command that changes files, history or branches inside the review agents.
//   4. In every run, attended or a Zyggy job: no force-push, no push to a protected branch (also not a bare
//      push from one), no remote ref deletion, no moved or deleted tag, no deleted GitHub release.
//   5. In an attended session, git commit and git push ask the owner; in a Zyggy job run (ZYGGY_JOB set)
//      the job is the owner's go (CLAUDE.md § Job runs) and they pass without a prompt.

import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { isAbsolute, relative, resolve } from 'node:path';
import { loadConfig, projectRoot, readState, runVerify, treeFingerprint, writeState } from './verify.mjs';

const REVIEW_AGENTS = ['spec-critic', 'slice-verifier', 'spec-reviewer'];
const MUTATING_GIT =
  /\bgit\b[^|;&\n]*?\s(commit|push|add|rm|mv|reset|checkout|switch|restore|stash|clean|merge|rebase|cherry-pick|revert|apply|am|tag)\b/;

function block(reason) {
  console.error(`spec-driven guard: ${reason}`);
  process.exit(2);
}

function ask(reason) {
  console.log(
    JSON.stringify({
      hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: 'ask', permissionDecisionReason: reason },
    }),
  );
  process.exit(0);
}

const inJobRun = () => Boolean(process.env.ZYGGY_JOB);

// Each git/gh invocation in a shell line (the program opens a command segment, so words inside a commit
// message do not count), as a token list starting after the program name.
function invocations(command, program) {
  const isProgram = (t) => [program, `${program}.exe`].includes(t.split(/[\\/]/).pop());
  return command
    .split(/[;&|\n(){}]+/)
    .map((part) => part.trim().split(/\s+/))
    .filter((tokens) => isProgram(tokens[0] ?? ''))
    .map((tokens) => tokens.slice(1).map((t) => t.replace(/^["']|["']$/g, '')));
}

// The verb of a git invocation, skipping global options such as -C <dir> or -c k=v.
function gitVerb(tokens) {
  for (let i = 0; i < tokens.length; i++) {
    if (tokens[i] === '-C' || tokens[i] === '-c') i++;
    else if (!tokens[i].startsWith('-')) return { verb: tokens[i], rest: tokens.slice(i + 1) };
  }
  return { verb: null, rest: [] };
}

function refusedGit(command, root, protectedBranches) {
  for (const tokens of invocations(command, 'git')) {
    const { verb, rest } = gitVerb(tokens);
    if (verb === 'push') {
      const flags = rest.filter((t) => t.startsWith('-'));
      if (flags.some((f) => /^(--force(-with-lease|-if-includes)?(=.*)?|-f|--mirror|--all|--delete|-d|--prune)$/.test(f) || /^-[a-z]*f/.test(f))) {
        return `git push ${flags.join(' ')} is never run here: no force-push, mirror, all-branches or ref deletion.`;
      }
      // Positional arguments: the remote first, then the refspecs.
      const [, ...refspecs] = rest.filter((t) => !t.startsWith('-'));
      for (const spec of refspecs) {
        if (spec.startsWith('+') || spec.startsWith(':')) return `git push ${spec} forces or deletes a remote ref.`;
        const target = (spec.includes(':') ? spec.split(':').pop() : spec).replace(/^refs\/heads\//, '');
        if (protectedBranches.includes(target)) return `git push to "${target}" is refused: ${target} changes only through a merged PR.`;
      }
      if (refspecs.length === 0 && protectedBranches.includes(currentBranch(root))) {
        return `a bare git push from "${currentBranch(root)}" would push a protected branch; switch to the feature branch.`;
      }
    }
    if (verb === 'tag' && rest.some((t) => /^(-f|--force|-d|--delete)$/.test(t) || /^-[a-z]*[fd]/.test(t))) {
      return 'a published tag is never moved or deleted; a broken release gets a new PATCH version.';
    }
  }
  for (const tokens of invocations(command, 'gh')) {
    if (tokens[0] === 'release' && tokens[1] === 'delete') return 'a published release is never deleted or re-cut.';
  }
  return null;
}

function commitsOrPushes(command) {
  return invocations(command, 'git').some((tokens) => ['commit', 'push'].includes(gitVerb(tokens).verb));
}

function relativePath(root, file) {
  const rel = relative(root, isAbsolute(file) ? file : resolve(root, file));
  if (rel.startsWith('..') || isAbsolute(rel)) return null; // outside the project
  return rel.split('\\').join('/');
}

function allowedOnProtected(rel, entries) {
  return entries.some((entry) => (entry.endsWith('/') ? rel.startsWith(entry) : rel === entry));
}

function currentBranch(root) {
  try {
    return execFileSync('git', ['rev-parse', '--abbrev-ref', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim();
  } catch {
    return null; // not a repository, or no commit yet
  }
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
    const refused = refusedGit(command, root, config.protectedBranches ?? []);
    if (refused) block(refused);
    if (!inJobRun() && commitsOrPushes(command)) {
      ask('git commit and git push need the owner\'s confirmation in an attended session (CLAUDE.md § Owner constraints).');
    }
    return;
  }

  if (tool !== 'Edit' && tool !== 'Write' && tool !== 'NotebookEdit') return;
  const file = args.file_path ?? args.notebook_path;
  if (!file) return;
  const rel = relativePath(root, file);
  if (rel === null) return;

  if (inReviewAgent && rel.startsWith(`${specsDir}/`)) {
    block(`the ${agent} agent does not change specs or recaps. Report what you found instead.`);
  }

  const allowed = config.allowOnProtected ?? [`${specsDir}/`, '.claude/', 'CLAUDE.md'];
  const branch = currentBranch(root);
  if (branch && (config.protectedBranches ?? []).includes(branch) && !allowedOnProtected(rel, allowed)) {
    block(
      `"${branch}" is a protected branch and ${rel} is not a spec. ` +
        (inJobRun()
          ? 'Create the feature branch the workflow names (feature/<NN>-<short-name>, fix/<short-name>, docs/<topic>) from main and continue there.'
          : 'Ask the user to create or name the feature branch, then continue there.'),
    );
  }

  if (new RegExp(`^${specsDir}/[^/]+/slice-\\d+\\.md$`).test(rel)) {
    const fingerprint = treeFingerprint(root, specsDir);
    if (readState(root).verifiedFingerprint === fingerprint) return;
    const outcome = runVerify(root, config);
    if (outcome.empty) return; // nothing established yet; the recap says so
    if (!outcome.ok) {
      const failed = outcome.results.at(-1);
      block(
        'the slice recap is written after the proof, and the Verification Commands fail.\n' +
          `FAIL  ${failed.command}  (${failed.reason})\n${failed.output}\n` +
          'Make them pass (node .claude/hooks/verify.mjs), then write the recap.',
      );
    }
    writeState(root, { verifiedFingerprint: fingerprint });
  }
}

try {
  main();
} catch (error) {
  // A broken guard must be seen, not silently skipped: exit 1 shows the error without blocking.
  console.error(`spec-driven guard failed: ${error.message}`);
  process.exit(1);
}
