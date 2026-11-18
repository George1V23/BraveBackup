# Restore: bookmarks only (Phase 4)

`BraveBookmarksRestoreSession` restores the **Bookmarks** category of selected archive profiles into
explicitly mapped destination profiles through the Phase 3 safety engine. The Restore page drives it on Linux; see
[execution controls](restore-execution-controls-draft.md).

## Research note: what is written

The archive already stores, per profile, `bookmarks/source.json` (role `native-source`): the exact byte
snapshot of the source profile's `Bookmarks` file ([archive format](archive-format-v1.md)). Restore writes
those exact bytes as the destination profile's `Bookmarks` file. No Chromium format is converted,
re-serialized or invented, so rule 11 is not engaged; the portable `portable-v1.json` is never used to
write. Eligibility is checked against what the project already parses: strict UTF-8 JSON, Chromium
`version` 1 (BOM tolerated), at most 64 MiB (the Linux capture limit; larger snapshots are rejected
with a reason rather than truncated). Fixture: the synthetic snapshot in `BraveBookmarksRestoreTests`
is produced by the real archive writer. Not modified: `Bookmarks.bak`, sync state, favicons, the
embedded `checksum` field (Chromium recomputes it; this was not exercised against a real Brave).

## Flow and guarantees

1. `Create` authenticates the archive, verifies the payload length and SHA-256 against the manifest,
   validates every mapping, freezes the restore context (stopped browser, path boundaries, duplicate
   and physical-alias checks, source fingerprint), acquires recovery-artifact ownership and retains
   each target. It writes nothing.
2. After final user approval the host calls `Transaction.Prepare()` (encrypted safety backup and
   verification, outside browser data) and `Run()`. `Pause`, `Rollback(true)` and resume behave as in
   [restore-safety-v1](restore-safety-v1.md); rollback restores the exact original bytes, or removes a
   `Bookmarks` file that was originally absent.
3. Only `Bookmarks` is touched; other profile files, other categories and unselected profiles are unchanged.

Metadata: an existing destination keeps its permissions with the modified time set to the restore time.
An absent destination (Linux only) borrows permissions from the same profile's `Preferences`; if that is
missing the profile has never been used and the error asks the user to open it in Brave once. Windows
absent destinations are not supported yet, and Windows is not natively validated at all.

## Tests

Seven Linux tests use a real synthetic archive (plain and encrypted): exact-byte restore with unrelated
files unchanged, safety artifact free of bookmark contents, full rollback, absent destination creation and
removal, never-used profile blocked without writes, running browser blocking creation and execution, wrong
password/unknown profile rejection, and unsupported snapshot rejection. Full run: 996 Core, 48 Brave, 51 Linux
(4 skipped), 14 Windows-labelled (7 skipped).

## Remaining for Phase 4

Your review of the implemented controls, real-Brave verification of a restored profile and Windows native
validation are still outstanding.
