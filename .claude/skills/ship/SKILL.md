---
name: ship
description: "Use right after the owner approves a finished feature (last slice + reviewer report) or a bugfix, or says 'release'. Ships it end to end without further questions: push, PR to main with CI green, merge, version tag, tag CI green, package and publish the GitHub release, instance pin, upgrade Central and this laptop, run the spec's owner-run checks, record the evidence. Stops only on a red step or a step it cannot do, and then hands the owner exact commands."
argument-hint: "What is being released, e.g. 'the discovery registry' or 'fix for context stdin'"
---

# Ship

The owner's approval is the go for the whole chain (`CLAUDE.md` § Release after approval). Do every step you can yourself, in order, without asking again. Stop at the first red step, report it with its output, and fix it through the normal workflow (`bugfix` agent, then a PATCH release). Hand the owner a step only when you cannot do it (a refused command, a missing permission, branch protection, another machine), with the exact commands prefixed by machine and user (`[central/<vm-admin>]`, `[home-laptop/user PowerShell]`), then continue after they confirm.

This repository is public: release notes, scripts and commits carry placeholders only; machine names, the VM, resource group and instance values come from the private instance repository and auto-memory, never from this file.

## Never

- force-push, push to `main` directly, delete or move a published tag, re-cut a published release (a broken release gets a new PATCH version);
- release with a red check, a warning, or an acceptance criterion the reviewer reported as not met;
- commit secrets, or upload anything but the four release assets.

## 0. Preconditions

- Every slice committed on `feature/<NN>-<short-name>` (or the fix on `fix/<short-name>`), working tree clean, the Verification Commands green.
- Feature: the after-last-slice reviewer reported every acceptance criterion met (or the owner accepted the exceptions) and the owner approved.
- `git log origin/main` — another session may have moved `main`; merge it into the branch and re-run the Verification Commands if it did.

## 1. Merge

1. `git push -u origin <branch>`.
2. `gh pr create --base main` — title in plain words, body: what changed, the spec link, the reviewer's verdict, then the PR attribution line.
3. `gh pr checks <n> --watch` — all green. Then `gh pr merge <n> --merge --delete-branch`. Branch protection refusing the merge → hand it to the owner.

## 2. Tag

1. `git switch main && git pull --ff-only`.
2. Next version from the latest `v*` tag, SemVer pre-1.0: a deliverable → MINOR, a fix → PATCH. Say which and why in the report.
3. `git tag -a v<x.y.z> -m "Release v<x.y.z> — <feature in words>"` and `git push origin v<x.y.z>`.
4. Watch the tag's CI run (`gh run list --branch v<x.y.z>`, `gh run watch <id>`): both runners green, including the smoke step. Copy the `instance/zyggy.json` notice line the run prints.

## 3. Publish the GitHub release

1. In a new scratch folder: `gh run download <id>`.
2. Package per RID, keeping the assets named as in earlier releases: `zyggy-linux-x64.tar.gz` (`tar -czf`), `zyggy-win-x64.zip`, and `SHA256SUMS-<rid>` = the archive's inner `SHA256SUMS`. Verify each archive's contents with `sha256sum -c SHA256SUMS` before uploading.
3. `gh release create v<x.y.z> <the four assets> --title "v<x.y.z> — <feature in words>" --notes "<tag CI run id green on both runners; what is new in plain words; verify with sha256sum -c SHA256SUMS; the instance pin line>"`.

## 4. Instance pin

In the private instance repository: set `instance/zyggy.json` to the pin line from step 2.4 on a branch, PR, its CI green, merge. A release without the pin stops the Central brief with `version_mismatch`.

## 5. Deploy

Follow `zyggy-core/runbooks/install-node.md` "Upgrade" and the operating facts in auto-memory (Central VM operations, laptop node operations) — read them first.

- **Central**: write the upgrade script to the scratchpad (download the release on the VM itself with `curl` + `SHA256SUMS`, stop `zyggy-node`, install under `/opt/zyggy/<version>/` with `docs/`, switch the symlinks, any new unit or drop-in the release needs, start), run it as the VM admin through `az vm run-command invoke --scripts @<file>`, then `git pull --ff-only` the instance checkout as the `zyggy` user. Verify: `curl -s http://localhost:4711/health` shows the new `version`, `zyggy --version`. A refused or failing run → hand the script to the owner.
- **This laptop**: download and unpack `zyggy-win-x64.zip` into a new folder, run `.\deploy\windows\install.ps1` with the `-Tenant`, `-Machine` and `-SignWith` values from the existing `node.json`. Verify `zyggy --version` and `/health`. Settings the release makes mandatory (a new `node.json` key) are added before the restart; say which.
- **Other laptops**: give the owner the one command and what to check.

## 6. Prove it live and record

1. Run the spec's owner-run checks you can run yourself (for example a `zyggy submit` round trip, `zyggy status`, a `discover --dry-run`), and quote the evidence.
2. Ask the `project-manager` to flip the roadmap entry to Done.
3. On a branch `docs/release-v<x.y.z>`: add a Status line to the spec (release version, tag CI run, Central and laptop versions, live-check evidence, what is left for the owner) plus the roadmap change; commit, push, PR, merge (a docs-only change runs no CI). Never push `main` directly.
4. Report in plain words: version, what shipped, where it runs, the evidence, and anything handed to the owner — with the visual report (`CLAUDE.md` § Visual approval report): the release chain merge → tag → GitHub release → pin → Central → laptop with each step's state and evidence, and the machines now on the new version.
