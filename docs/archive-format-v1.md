# .bravebackup archive version 1

Bookmark-only writes still use this version. Settings-only and mixed archives now use
[manifest version 2](settings-backup.md); current validation preserves v1 support.

Implemented for bookmarks-only creation and read-only verification. The detailed
[format contract](archive-format-proposal.md) defines the manifest, entry layout and
BBKENC1 envelope byte offsets. Its implementation was authorized by the user's
continuation after proposal review. Existing standalone [bookmark JSON schema 1](backup-format-v1.md)
and browser-compatible HTML exports are unchanged.

## Current behavior

Select profiles, leave Bookmarks selected, choose compression and a `.bravebackup`
destination, and provide a matching password when encryption is on. Encryption is on
by default. Other selected categories disable Create Backup and explain what to deselect.
Missing, malformed or oversized source bookmarks fail the entire operation; they never
become empty or silently omitted backup payloads. Valid empty bookmark trees are allowed.

Each profile has the exact native Bookmarks byte snapshot (including a UTF-8 BOM if
present) and a portable schema-1 JSON tree generated from that same snapshot. UUIDs keep
identically named installations/profiles distinct. Metadata has product/channel/version,
profile display names and directory basenames, but no absolute source paths. The manifest
lists every payload's length, SHA-256 and role. No favicon database is included.

Encryption is AES-256-GCM in 1 MiB authenticated records with PBKDF2-HMAC-SHA-256,
600,000 iterations, random 16-byte salt, random 8-byte nonce prefix and an authenticated
terminal record. The entire ZIP, including names and manifest, is encrypted. The framing
is application-specific; passing tests is not an independent cryptographic audit.
Turning encryption off explicitly writes an ordinary ZIP with the same extension.
Unencrypted payload hashes detect accidental damage, not malicious rewriting.

The implementation streams ZIP directly into encryption and the private temporary sibling.
Encrypted verification first authenticates every record and the terminal marker, then
provides seekable access using one bounded chunk cache. It never writes a plaintext ZIP
staging file. Parser objects/immutable strings live in managed memory and cannot be
promised securely erased. Owned key, password-byte and snapshot buffers are cleared.

Before publication, the file is reopened and checked for valid framing, bounded ZIP
metadata, supported manifest/category versions, unique identities and entry names,
exactly declared payloads, JSON validity, lengths and hashes. Legacy portable bookmark
trees are validated as schema 1. Native Brave semantics are validated during capture by
the existing parser; Core does not implement a Brave reader or a restore operation.

## Publication, cancellation and feedback

The UI freezes its request and prevents sidebar/form changes during backup. Confirming
replacement authorizes only the destination fingerprint observed before confirmation.
Changes detected before publication abort with an instruction to retry and review again.
The fingerprint includes contents, size and modification time. Same-directory rename
publishes the complete verified output; it does not guarantee power-loss durability or
protection against a hostile last-instant filesystem race. Linked/profile destinations
are rejected through the existing destination guard.

Temporary output is created with owner read/write permissions on Linux and a protected
current-user ACL on Windows. Failed/cancelled writes retain existing output and remove
the temporary file. Cleanup failures are surfaced explicitly. Empty parent directories
created during an unsuccessful operation may remain.

Actual stage/profile progress appears during work. Stages without a reliable byte total
use an indeterminate bar; no false percentage is shown. Cancellation is checked during
capture, writing, verification and immediately before publication. The short final move
completes once begun. A successful operation shows the actual destination/file size and
Open destination folder. The in-app report contains IDs, UTC times, counts, status and
sanitized codes, without source paths, bookmark contents, profile names or secrets.
No report sidecar or telemetry is written. Password fields clear after each attempt.

A source may change while Brave is open. The reader makes up to three immediate bounded
attempts, checking length/mtime and JSON parsing. These checks do not create a browser
transaction or guarantee all profiles were captured at one instant. Close Brave and retry
if a stable snapshot cannot be read. No source profile file is changed.

## Limits and compatibility

- 100 GiB ZIP stream and 100 GiB total declared uncompressed entry bytes.
- At most 100,000 ZIP entries; central directory at most 64 MiB and entry names at most
  1,024 bytes. Preflight checks occur before ZipArchive materializes the entry list.
- 16 MiB manifest; 256 MiB each native or portable bookmark payload.
- ZIP64 single-disk directories are supported; multi-disk ZIPs are rejected.
- Unknown required versions/capabilities, malformed/traversing/duplicate paths and
  symbolic-link entries are rejected. Verification extracts no files.
- Standalone schema-1 JSON remains readable through the existing model. It is not
  misidentified as archive v1. No standalone import or restore UI is added.

## Code responsibilities

- Core `ArchiveManifest` and related records define browser-independent metadata;
  `ArchiveLimits` defines ceilings. `BackupArchive.Compression` maps the existing choices;
  `WriteFile` and `WriteManifest` write content; `Validate` checks structure and hashes.
- Core `ArchiveEncryption.CreateWriter` encrypts bounded records;
  `OpenReader` authenticates and provides read-only seeking; `IsEncrypted` detects magic.
  Deterministic writer material is internal and accessible only to the friend test assembly.
- Core `ArchiveZipGuard` preflights ZIP/ZIP64 counts and directory sizes before allocation.
- Backup `BraveArchiveBackup.CreateAsync` freezes selection, validates scope/destination,
  obtains overwrite approval, captures Brave snapshots and verifies/publishes the archive.
  `BackupProgress`, `BackupResult` and `BackupOperationException` carry measured stages,
  safe outcomes and error codes. Passwords are arguments rather than record fields.
- Platform `BackupFileOperations.CreatePrivateFile` sets creation-time permissions;
  `OpenFolder` uses the host desktop without shell text interpolation.
- UI `ShellViewModel.Backup` owns validation, async create/cancel/report/folder commands and
  disposal cancellation. `MainWindow` supplies the explicit overwrite dialog.
- CLI builds independently of UI. Its existing command interface is unchanged; no new
  archive CLI command or package dependency was introduced.

## Verification

All 235 solution test cases pass on Linux (168 Core, 48 Brave, 13 Linux, 6 Windows test
cases). Windows-labelled cases on Linux do not constitute native Windows runtime testing.
Builds report no warnings/errors. Tests include:

- encrypted/unencrypted round trips for four compression choices, duplicate display names,
  exact native bytes, BOM/valid empty trees, unchanged sources and frozen legacy schema 1;
- an independently generated OpenSSL encrypted vector and Python ZIP-v1 golden fixture;
- chunk boundaries/random access, fresh random headers, exact password handling, wrong
  password, changed header/ciphertext/tag, truncation, ordering, terminal and trailing data;
- ZIP/ZIP64 count bounds, oversized manifests, malformed/duplicate/undeclared/missing
  entries, versions/capabilities/IDs/roles, invalid JSON and wrong lengths/hashes;
- unsupported categories, source size/read failures, protected destinations, overwrite
  decline and destination changes, cancellation at each stage, failed verification,
  destination IO failure and explicit cleanup failure;
- UI command validation, encrypted completion, secret clearing, pending-confirmation
  cancellation and sanitized reports. Existing HTML/JSON export tests still pass.

The Linux desktop fixture also invoked the actual Create Backup and report buttons,
verified the encrypted output, and checked that its source file remained unchanged.
[Completion screenshot](screenshots/archive-completion.png) uses synthetic browser data.
Fixture generation is documented in
`tests/Core.Tests/Fixtures/generate-archive-vectors.py`; fixed salts/nonces are test-only.

Native Windows ACL/desktop/runtime validation and independent cryptographic review remain
release checks. Restore, Import, other data categories and Merge/Sync still need their
own reviewed implementation increments. This workflow does not promise restoration
before that work is complete.

## Version 6 protected-password reader extension

The [archive-v6 contract](archive-format-v6.md) adds encrypted-envelope-only portable
password validation. Existing v1–v5 contracts and writer behavior are unchanged.

## Version 7 raw-password extension

[Archive v7](archive-format-v7.md) adds encrypted, explicitly classified raw/native
password preservation while retaining all earlier readers/contracts.
