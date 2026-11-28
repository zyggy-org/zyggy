---
name: new-feature
description: "Take a new feature from a request to working slices: investigate, interview, spec, then build slice by slice. Use when the user asks for a new feature or new behavior."
argument-hint: "Feature name plus any initial context, e.g. 'Subscriptions - user views and creates subscriptions'"
---

# New Feature

A new feature goes from a request to working slices. The workflow is defined in `.claude/rules/spec-driven.md`; this file only orders the hand-offs.

## Input

A feature name and, optionally, user stories or acceptance criteria. If `_specs/<FeatureName>/spec.md` already exists, read it and its Status first and continue from the phase it shows.

## Workflow

1. **Investigate**: use the `investigate` skill. It first creates the spec's own branch (`.claude/rules/spec-driven.md` § One branch per spec), then writes the findings and the questions they raise into `_specs/<FeatureName>/spec.md`. No code.
2. **Interview and specify**: use the `write-spec` skill. It interviews the user, writes the rest of the spec, checks it against the code, has the `spec-critic` agent read it only when the user asks (one pass; with no report, the spec's Status says so and the work goes on), and stops for approval. It produces `spec.md` with its Scaffold (per slice the files to create, modify or delete, and the DI registrations and wiring), presented for one approval. No code. The approved spec becomes the first commit on the branch, by the user or by Claude when asked; a draft pull request can then be opened.
3. **Build**: once the spec is approved, each slice starts only on the user's go for it, in a fresh session (`/clear` or a new conversation) with the `build-slice` skill. It builds exactly that slice from its Scaffold rows, test-driven, ends on the slice gate green and answers with the record (each acceptance criterion with its test and the failure seen when it was red, what differs, how to try it), and stops. After the slice gate no agent reviews the slice: the slice gate and the user's check verify it; a slice review agent left in a project by an older kit runs only when the user asks for it, never by the build on its own. It does not commit unless the user asks for it.
4. **Approve and repeat**: the user reads the record, tries the slice, reviews the diff and accepts it; committing is the user's choice, and the user sets the slice's state in the spec. Then the next slice is built on the user's go, again in a fresh session.
5. **Review**: after the last slice, the final review, once for the whole feature:
   - the feature gate: `node .claude/hooks/verify.mjs --feature`, every Verification Command and every integration test, green; red: the failing command and why, and stop;
   - the code analysis the project declares in `CLAUDE.md` § Project settings (the **Analysis** line, for .NET the build with the built-in analyzers and `dotnet format --verify-no-changes`; its findings on the feature's changed code fixed with the **Fix analysis**, test first where a fix touches logic, never by weakening, disabling or suppressing a rule, then the feature gate again); findings it cannot fix go to `artifacts/<FeatureName>-analysis.md`, one line each, for the final review report; when no analysis is declared, say "no code analysis declared" and skip it; when it cannot run, say what is missing and why;
   - the `spec-reviewer` agent, given only the spec path, the diff range and that analysis file, compares the full diff with the spec; it answers with one line and writes its report to `artifacts/<FeatureName>-reviewer-report.md`: read the file and relay the report to the user as it is; when a slice is ⚠️, `security-review` runs once with it over the whole diff, never per slice;
   - an end-to-end browser test is optional, at most once per feature.
   The user reviews that report, then pushes the spec's branch and completes the pull request; the spec's Status gets "<date>: shipped".

Phases 1 and 2 run in one conversation, because the interview builds on the findings. Phase 3 does not continue in it: the spec, not the conversation, has to carry the context.

When the spec has to change after approval, use the `amend-spec` skill.
