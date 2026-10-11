# Archive offload goldens (spec 38)

## `quickxor-vectors.tsv` — QuickXorHash expected values (AC-12)

One row per case: `case<TAB>length<TAB>input<TAB>expected base64`. The input is `b64:<bytes in base64>` or `gen`, meaning
`byte[i] = (byte)((i * 31 + 7) % 256)` for `i` in `0 … length-1` (so no 25 MiB file is committed).

The expected values never come from `QuickXorHash.cs`:

- `rclone-*`: the 70 test vectors of rclone's `backend/onedrive/quickxorhash/quickxorhash_test.go` (MIT), copied verbatim
  (inputs re-joined where the Go raw string wraps).
- `gen-*`, `text-item` and `png-2x2`: computed on 2026-10-11 with rclone's `quickxorhash.go` (a port of namazso's C implementation,
  BSD-0) under `golang:1.23` in podman — an implementation independent of the code under test. `text-item` is the 68-byte item
  of `../sidecar-text.md` (`sha256` c5f2cab5…4f88); `png-2x2` is the 65-byte PNG of `../sidecar-png.md` (`ImageBytes.Png(2, 2)`).

## Sidecar and `list` goldens (AC-13, AC-24)

- `sidecar-offloaded.md`: `../sidecar-text.md` with `updated` set to the offload date (2026-10-01) and the five offload keys appended
  after `source_name` (derived with `sed`); `sidecar-offloaded-no-url.md` is the same without `drive_url` (Graph gave no `webUrl`).
- `list-offloaded.txt` / `.json`: the four-row tree of `ArchiveListOffloadTests` — `fence-quote` indexed with two throttled failures,
  `quote-2026` offloaded (sidecar only), the 37 orphan `roof-invoice` sidecar, and `roof-photo` unindexed and parked
  (`target_conflict`). Written by hand from plan assumption A7.

## Commit and inbox goldens (AC-14, AC-18)

- `commit-message-offload.txt`: the commit of the 68-byte text item uploaded as drive item `01ITEMQUOTE` (the id the test hands `StubOneDrive`
  for the upload), with its SHA-256 and the `text-item` QuickXorHash; hand-written from spec 38 AC-14.
- `inbox-after-offload.md`: the inbox file after one offload on 2026-10-01 — `RememberService`'s header and the spec's `Moved …` line
  (plan assumption A3), hand-written.
