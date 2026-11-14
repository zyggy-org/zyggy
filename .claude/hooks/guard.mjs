#!/usr/bin/env node
// PreToolUse hook of the spec-driven kit. Reads the hook input on stdin; exit 2 blocks the
// tool call and shows the reason to Claude. Does nothing until .claude/spec-driven.json exists.
//
// Guards:
//   1. No source edits on a protected branch (specs and Claude's own files are allowed).
//   2. No slice recap (_specs/<Feature>/slice-<n>.md) while the Verification Commands fail.
//   3. No git command that changes files, history or branches inside the review agents.

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
    const match = inReviewAgent && MUTATING_GIT.exec(String(args.command ?? ''));
    if (match) {
      block(`the ${agent} agent reviews and does not run "git ${match[1]}". Report what you found instead.`);
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
        'Ask the user to create or name the feature branch, then continue there.',
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
