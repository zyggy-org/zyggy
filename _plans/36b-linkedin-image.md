# Plan: 36b — LinkedIn image attachment — "My one approved post can carry one image I have seen"

## Overview

**Origin**: owner, 2026-10-08, during 36 Step 13 part 6: "Zyggy should be able to attach images or other media … start the image
feature now so I can test it with an image; I'll not do several tests as it's published on my real account." The owner ruled the full
roadmap → spec chain too heavy for this follow-up: this plan is the only planning artefact; its **Decisions** section stands in for the
spec's Decision Table (no Open Questions — anything unclear stops at a gate). Spec 36 and its contracts stay in force except where a
decision below extends them.

**Goal**: `publish_post` may carry **one image** (PNG, JPEG or GIF) that the owner was shown, identified in the permission prompt by its
path and SHA-256. Video and documents are out of scope (decision D8). Release `v0.4.1`; the one real post of 36 Step 13 part 6 carries
the image and proves this plan live.

**Verified LinkedIn facts** (learn.microsoft.com/linkedin/marketing/community-management/shares/images-api, version 2026-09, read
2026-10-08):

| # | Fact |
|---|---|
| L1 | `POST https://api.linkedin.com/rest/images?action=initializeUpload`, headers `Linkedin-Version`, `X-Restli-Protocol-Version: 2.0.0`, body `{"initializeUploadRequest":{"owner":"urn:li:person:<sub>"}}` → 200 `{"value":{"uploadUrlExpiresAt":…,"uploadUrl":"https://www.linkedin.com/dms-uploads/…","image":"urn:li:image:…"}}`. |
| L2 | The bytes go to `uploadUrl` with a PUT (Assets API "Upload the Image": bearer token, raw bytes). |
| L3 | `w_member_social` may write `rest/images` but **cannot GET** them (versioned): no status polling is possible with this app. |
| L4 | Formats JPG, GIF (≤ 250 frames), PNG; fewer than 36,152,320 pixels. |
| L5 | The post: the spec 36 body plus `"content":{"media":{"altText":"…","id":"urn:li:image:…"}}`; `altText` ≤ 4,086 characters (≤ 120 recommended); 201 + `x-restli-id`. |
| L6 | A member-owned image may only be used by that member (owner = caller). |

## Decisions

| # | Decision | Why |
|---|---|---|
| D1 | `publish_post` arguments gain three optional keys: `image_path` (absolute path), `image_sha256` (64 lowercase hex) and `image_alt` (≤ 300 characters). `image_path` and `image_sha256` come together or not at all; `image_alt` only with them. The schema stays `additionalProperties: false`. | The permission prompt shows the tool's arguments: path + hash + alt are what the owner approves, next to the text. |
| D2 | The image must be a **regular file** (no symlink in any path component — the resolved path equals the given path) **inside `image.dir`**, a new instance key with default `~/.local/share/zyggy/linkedin/media`, ≤ `image.max_bytes` (default 10 MiB, may only be lowered), whose first bytes are a PNG, JPEG or GIF signature, and whose dimensions read from the header give fewer than 36,152,320 pixels (L4). | One folder the session writes images into; nothing else on the machine (memory, credentials, mail downloads) can be attached. `~/.local/state/zyggy/**` stays Edit-denied, so the media folder is elsewhere. |
| D3 | The file is read **once** into memory; its SHA-256 must equal `image_sha256`, and exactly those bytes are uploaded. | Closes the gap between what the owner saw and what is sent (no re-read, no swap). |
| D4 | Order inside the tool: arguments → switched on → text checks → **image checks (D2, D3)** → token → duplicate → initialize → upload → **one post**. Every check before "initialize" is local; a failure is one `refused: image …` row. | Same "nothing sent until everything local passed" rule as spec 36 Assumption 3. |
| D5 | Upload failures (initialize not 200, an `uploadUrl` that is not `https://www.linkedin.com/dms-uploads/…` or the loopback stand-in, PUT not 2xx, a timeout) end the call with `rejected: image upload: <reason>` — **no post was sent**, so never `outcome_unknown`. An uploaded-but-unused image is harmless (it is never shown). | Only the post request can create something visible; its failures keep spec 36's mapping. |
| D6 | No status polling (L3): after a 2xx PUT the tool waits `image.settle_ms` (default 3000, 0..10000) once, then posts. A post refused because the image is not ready is an ordinary `rejected: <LinkedIn's message>`; nothing is retried. | The token cannot read image status; one short settle is the only lever. |
| D7 | The duplicate guard keys on `sha256(text)` for a text post (unchanged, so older rows still match) and on `sha256(text + "\n" + image_sha256)` for a post with an image. The action row gains `image_sha256`, `image_bytes`, `image_urn` (null when absent; schema stays 1, the reader ignores unknown keys). The fact line adds ` (with an image)`. | The same text with another image is a different post; the log shows which bytes went out. |
| D8 | Out of scope: video, documents, several images, editing or deleting posts. | Video needs a chunked multi-part upload and processing waits the token cannot observe (L3); one image covers the owner's ask now. |
| D9 | The skill: when the owner wants an image, Zyggy writes it into `image.dir`, **shows it to the owner**, prints the file's SHA-256, and calls `publish_post` with that path and hash only after his explicit go for text **and** image. The owner compares the hash's first characters in the prompt. | The prompt cannot render the picture; the hash ties the prompt to the picture he saw. |
| D10 | `LinkedInEndpoints` stays the only file naming LinkedIn hosts: it gains the images path and the allowed upload prefix. AC-13 (HTTPS only, no redirects, token only in the header) applies to both new requests. | Spec 36 AC-9/AC-13 unchanged in spirit. |

**Contract impact**: ⚠️ the `publish_post` input schema (additive, optional keys), the action-log row (additive keys), `instance/linkedin.json` (new
optional keys `image.dir`, `image.max_bytes`, `image.settle_ms`), the skill. ⚠️ Risk areas: public channel (what the owner approves), file
access (D2), no secret anywhere new.

---

## Step 1 — The tool accepts and checks an image locally: arguments, schema, configuration, and the file rules — every refusal before any request

- [x] Done — 2026-10-08 (executor notes: `PostImage.Load(path, sha, dir, maxBytes)`; the pixel refusal carries the numbers: `image too many pixels (<n> ≥ 36152320)`; `image.dir not configured` when neither the key nor HOME gives one; golden `tool-publish_post.json` updated on purpose.)

**Scope**:
- `src/Zyggy.Core/LinkedIn/PostArguments.cs` *(modify)*: optional `ImagePath`, `ImageSha256`, `ImageAlt`; D1 pairing; `image_alt` ≤ 300 runes, no control characters; `image_sha256` `^[0-9a-f]{64}$`.
- `src/Zyggy.Core/LinkedIn/PublishPostDescriptor.cs` *(modify)*: the three optional properties in the schema (`pattern` for the hash, `maxLength` 300 for alt); description adds "optionally one image the owner was shown, by path and SHA-256".
- `src/Zyggy.Core/LinkedIn/LinkedInConfiguration.cs` *(modify)*: `image.dir` (absolute after `~` expansion; default `~/.local/share/zyggy/linkedin/media`), `image.max_bytes` (1..10485760), `image.settle_ms` (0..10000); bad value → exit 3 naming the key.
- `src/Zyggy.Core/LinkedIn/PostImage.cs` *(create)*: `static PostImageLoad Load(string path, string expectedSha256, LinkedInConfiguration config)` → bytes + media type + width/height, or a refusal reason: `image outside image.dir`, `image not a regular file`, `image too large (<n> > <max>)`, `image not PNG, JPEG or GIF`, `image dimensions unreadable`, `image too many pixels`, `image hash mismatch`. Header readers: PNG IHDR, GIF logical screen, JPEG SOFn scan.

**Seams**: none (pure + file system in a temp dir).

**RED**:
- `PostArgumentsTests`: `[Theory] TryParse_ImageKeys_PairingAndShapes` — rows: text only (ok), path+hash (ok), path+hash+alt (ok), path without hash, hash without path, alt alone, uppercase hash, 63-char hash, alt 301 runes, alt with a tab, an unknown fourth key → `invalid arguments`.
- `PublishPostDescriptorTests.InputSchema_HasOptionalImageKeys_RequiredStillTextAndVisibility`.
- `LinkedInConfigurationTests`: `[Theory] Load_ImageKeys_DefaultsAndBounds` (defaults; `max_bytes` 0 / 10485761; `settle_ms` −1 / 10001; relative `image.dir` → exit 3 naming the key).
- `PostImageTests` (temp dir): `Load_Png_ReturnsBytesTypeAndSize`, `Load_Jpeg_…`, `Load_Gif_…`; `[Theory] Load_Refusals` — outside the dir (`../`), a symlink inside the dir to a file inside the dir, a directory, too large, a text file renamed `.png`, a PNG claiming 7000×7000, wrong hash → each reason exactly.

**GREEN**: as Scope.

**Contract impact**: ⚠️ schema and instance keys (additive).

**VERIFY**: build + `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~LinkedIn"` + full suite + format.

**REFACTOR**: §9 check; `@code-analysis` sweep.

---

## Step 2 — The adapter uploads an image and posts with it: initialize, PUT, the post body with `content.media`

- [x] Done — 2026-10-08 (executor notes: `InitializeImageUploadAsync`/`UploadImageAsync`; `CreatePostAsync(..., PostMedia? media = null)` keeps text posts byte-identical; reasons `initialize: …`, `upload: …`, `upload address not allowed`; the stub handler records raw bytes and allows the image POST and the upload PUT.)

**Scope**:
- `src/Zyggy.Core/LinkedIn/LinkedInEndpoints.cs` *(modify)*: `ImagesInitializePath = "/rest/images?action=initializeUpload"`, `UploadPrefix = Www + "/dms-uploads/"`; routes gain `ImagesInitializeUrl`; `IsAllowedUploadUrl(Uri, LinkedInRoutes)` (the prefix, or the loopback base in tests).
- `src/Zyggy.Core/LinkedIn/ILinkedInApi.cs` + `LinkedInApi.cs` *(modify)*: `InitializeImageUploadAsync(token, ownerUrn, apiVersion)` → `(uploadUrl, imageUrn)` or a reason; `UploadImageAsync(token, uploadUrl, bytes, mediaType)` → ok or reason; `CreatePostAsync(…, PostMedia? media)` writes `content.media` (`altText` only when given, then `id`) between `distribution` and `lifecycleState`. Image URN `^urn:li:image:[A-Za-z0-9_-]+$`. No token or URL query in any reason.
- `tests/golden/linkedin/post-request-image.json` *(create)*.

**Seams**: `HttpMessageHandler` (mocked).

**RED**:
- `LinkedInApiImageTests`: `Initialize_200_ReturnsUploadUrlAndUrn_ExactRequest` (URL, headers, body); `[Theory] Initialize_Failures` (401, 403, 500, bad JSON, URN shape, upload URL on another host → `upload url not allowed`); `Upload_Put_ExactBytesAndHeaders_201Ok`; `[Theory] Upload_Failures` (400, 500, timeout); `CreatePost_WithMedia_BodyEqualsGolden`; `CreatePost_WithMedia_NoAlt_OmitsAltText`.
- `LinkedInEndpointsTests.Resolve_Loopback_MovesImagesAndAllowsLoopbackUpload`.
- Source hygiene (existing AC-9 test) still green: no new file names a LinkedIn host.

**GREEN**: as Scope.

**Contract impact**: ⚠️ two new outbound requests (AC-13 rules).

**VERIFY**: build + `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~LinkedInApi"` + full suite + format.

**REFACTOR**: §9 check; `@code-analysis` sweep.

---

## Step 3 — `publish_post` publishes a post with the approved image end to end: order, refusals, row, fact, duplicate key

- [ ] Done

**Scope**:
- `src/Zyggy.Core/LinkedIn/PublishPostTool.cs` *(modify)*: D4 order; D5 mapping; D6 one settle wait through `TimeProvider` (`Task.Delay(…, clock, ct)`); D7 duplicate key; result unchanged (`published: <urn> — <link>`).
- `src/Zyggy.Core/LinkedIn/LinkedInActionLog.cs` *(modify)*: `ActionRow` gains `ImageSha256`, `ImageBytes`, `ImageUrn` (written only when present); the reader tolerates their absence (old rows).
- `src/Zyggy.Core/LinkedIn/PostFact.cs` *(modify)*: ` (with an image)` suffix.
- `tests/Zyggy.Integration/Infrastructure/StubLinkedInServer.cs` *(modify)*: answers `POST /rest/images?action=initializeUpload` (upload URL on its own loopback base, `/dms-uploads/<id>`) and `PUT /dms-uploads/…` (201), recording bodies.

**Seams**: `ILinkedInApi` substitute (unit); `TimeProvider` (`FakeTimeProvider` for the settle); loopback stand-in (integration).

**RED**:
- `PublishPostToolImageTests`: `Call_WithImage_OrderIsLocalChecksThenInitializeUploadSettlePost` (recorded call order; settle 3000 ms advanced on the fake clock); `[Theory] Call_ImageRefusals_NoRequest_OneRow` (each Step 1 reason); `Call_ImageWithoutToken_NotConnected_NoRequest`; `[Theory] Call_UploadFailures_Rejected_NoPostSent` (D5); `Call_PostRejectedAfterUpload_Rejected_NoRetry`; `Call_SameTextOtherImage_NotDuplicate`; `Call_SameTextSameImage_Duplicate`; `Call_TextOnly_DuplicateKeyUnchanged_OldRowStillMatches`.
- `LinkedInActionLogTests.Append_WithImage_WritesImageKeys`, `Read_OldRowWithoutImageKeys_Ok`.
- `PostFactTests.Line_WithImage_AddsSuffix`.
- Integration `PublishEndToEndTests.Publish_WithImage_OnLinux_InitializeUploadPost_BytesExact_Row_Fact` (real files, real socket; the uploaded bytes equal the file; the post body equals the golden; the row's `image_sha256`/`image_urn`; the token only in its file).

**GREEN**: as Scope.

**Contract impact**: ⚠️ action-log row (additive); fact line wording.

**VERIFY**: build + `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~LinkedIn"` + `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~LinkedIn"` + full suite + format.

**REFACTOR**: §9 check; `@code-analysis` sweep.

---

## 🛑 HUMAN GATE — end of Slice A (an approved image goes out with the post, nothing else can) *(covers Steps 1–3)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification: the test names above green on Windows and in the Linux stand-in; one integration run shows initialize → PUT (exact bytes) → post with `content.media` against the stub.
- [ ] Contract review: the new schema keys, the refusal strings, the row keys, the golden `post-request-image.json`, D5 (no `outcome_unknown` before the post).
- [ ] ⚠️ Risk review (public channel, file access): only files inside `image.dir`, no symlinks; the hash ties prompt and bytes; no token or upload-URL query in any row, result or stderr.
- [ ] User approved — implementation may continue past this gate

---

## Step 4 — The template teaches the image flow; the runbook has its entries

- [ ] Done

**Scope** (`zyggy-core`, then `zyggy-geoffrey`):
- `.claude/skills/linkedin/SKILL.md`: "Text posts only" → "text posts, optionally with one image"; a section **Image**: create or save the image in `~/.local/share/zyggy/linkedin/media/` (create the folder if missing), show it to the owner, print `sha256sum <file>`, keep the text + image approval together, call `publish_post` with `image_path`, `image_sha256` and a short `image_alt`; the new refusals and `rejected: image upload …` → runbook 16; never attach a file the owner has not seen; never a file from memory, mail downloads or credentials.
- `.claude/rules/operations.md` / `security.md`: the media folder, D2's limits.
- `.claude/zyggy-min-version` → `0.4.1`; `tests/repo.bats`: the skill's image sentences, the min version.
- `runbooks/central-claude-config.md` (this repo) section 16: "Image refused" (each reason → fix), "Image upload failed" (nothing posted; ask again), the media folder and its cleanup.

**Seams**: none.

**RED**: bats rows for the skill sentences and `0.4.1` fail.

**GREEN**: as Scope; bats green in podman; template CI green.

**Contract impact**: skill (public-channel consent flow).

**VERIFY**: template bats 0 failures; `dotnet build/test/format` for this repo's runbook change (docs only).

---

## Step 5 — `v0.4.1` released and switched on: tag, release, install, pin, pull, one restart

- [ ] Done

**Scope** (as 36 Steps 12–13 part 1–2, runbook 14a/14b):
- PR from the branch, CI green, merged; tag `v0.4.1`, tag CI green on both runners; release with `zyggy` + `SHA256SUMS`.
- Central: `/opt/zyggy/0.4.1/zyggy` (sha = SHA256SUMS); rehearsal on a scratch instance with the session's memory variables (`auth status` connected, `tools/list` → `publish_post` whose schema has `image_path`); template + instance merged, `instance/zyggy.json` → `0.4.1`, one push; symlink → 0.4.1, pull, live settings unchanged unless the instance changed them, `systemctl restart claude-remote` once (the stdio server must respawn with the new binary).
- `mkdir -p ~zyggy/.local/share/zyggy/linkedin/media` (0700, zyggy).

**Seams**: the real Central; no `claude` probe needed beyond the rehearsal (the tool's argument shape is already proven by AC-11).

**RED**: `zyggy --version` ≠ 0.4.1; the schema lacks `image_path`.

**GREEN**: as Scope.

**Contract impact**: live binary.

**VERIFY**: symlink + pin = 0.4.1; `claude-remote` active; m365 probe/check exit 0; `linkedin auth status` connected; units unchanged.

---

## 🛑 HUMAN GATE — `v0.4.1` live, ready for the one real post *(covers Steps 4–5)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification: release row (tag, CI ids, SHA-256), rehearsal output, restart time, `tools/list` schema with the image keys.
- [ ] Contract review: the skill's image section; runbook 16 entries.
- [ ] ⚠️ Risk review: nothing published yet; the media folder 0700; the instance carries no secret.
- [ ] User approved — implementation may continue past this gate

---

## Step 6 — The one real post with an image (= 36 Step 13 part 6, AC-10/A3/A4) and the secret sweep (36 Step 13 part 7–8)

- [ ] Done

**Scope** (owner-run on the phone or desktop; agent checks):
- The owner asks for the post with its image; Zyggy saves the image in the media folder, shows it, prints its hash, shows the exact text, count and visibility; he says "post it".
- First prompt: **Deny** → nothing published, no new `ok` row, no upload (the call never reached the server). Second: **Allow once** → `published: <urn> — <link>`; he opens the link: text **and image** as approved.
- Agent: one `ok` row with `image_sha256` = the shown hash and an `image_urn`; one fact line `(with an image)`; a `ps -eo args` sampler running across the Allow; then the AC-28 sweep (token and client secret only in their files); 0002 §36 rows A3, A4, AC-10, AC-28 and a 36b row; 36 Step 13 ticked.

**Seams**: the real LinkedIn, only through the owner's prompt.

**RED**: no post with an image on the profile; rows empty.

**GREEN**: as Scope.

**Contract impact**: ⚠️ the first public post.

**VERIFY**: as Scope; the agent re-reads its rows: no token, secret, post text or image bytes in them.

---

## 🛑 HUMAN GATE — definition of done for 36b, and 36 Step 13's live run *(covers Step 6)*

- [ ] Behavioral verification: the post on the profile with the image; the rows; the sweep clean.
- [ ] Contract review: 0002 §36 complete for Step 13.
- [ ] ⚠️ Risk review: nothing published that the owner did not approve; the image was the one shown (hash match).
- [ ] User approved — 36b done; 36 continues with its Step 14 (definition of done)
