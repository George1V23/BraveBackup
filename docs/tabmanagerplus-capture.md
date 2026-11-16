# Tab Manager Plus saved-window capture checkpoint

Implemented 2026-11-17. The dedicated extension project now validates saved windows;
Backup supplies stable read-only profile capture. The subsequent
[archive/UI increment](tabmanagerplus-backup.md) now enables this category and documents
archive v5. This document describes the capture layer; no restore operation is included.

## Verified source and initial compatibility

The
official [Chrome Web Store listing](https://chromewebstore.google.com/detail/tab-manager-plus-for-chro/cnkdjjdmfiffagllbiiilooaoofcoeff)
identifies `cnkdjjdmfiffagllbiiilooaoofcoeff`. Its 7.0.0 CRX3 package was downloaded through
Google's HTTPS update endpoint and inspected as data only. The signed-header ID matches
that store identity; manifest version is 3 and extension version is 7.0.0. The package
signature cryptography was not independently verified. No extension code was executed.

The bundled popup JavaScript confirms the consolidated `storage.local` sessions map,
stored version key, saved-window fields, migration and export structure researched at
upstream revision `3937a32841441b7349673dddf0be895d67e9ef99`. Package/popup SHA256 digests
and inspection provenance are recorded in
`tests/Core.Tests/Fixtures/TabManagerPlus/Store7.source-evidence.json`; the complete
third-party package is not redistributed as an application asset or test dependency.

Additional pinned
source: [saved-window ordering](https://github.com/stefanXO/Tab-Manager-Plus/blob/3937a32841441b7349673dddf0be895d67e9ef99/src/popup/sessionOrder.ts).
Chrome documents the optional stored [tab](https://developer.chrome.com/docs/extensions/reference/api/tabs#type-Tab)
and [window](https://developer.chrome.com/docs/extensions/reference/api/windows#type-Window) properties.

Initial capture requires:

- The official extension ID under the profile's Extensions directory. The repository's
  development identity is not an alias, and display names are not used as identity.
- Package directories `7.0.0` or `7.0.0_<uint suffix>`, each with a regular manifest
  declaring version 7.0.0 and manifest_version 3. Retained different versions block
  capture conservatively because this component does not determine the active package.
- A readable Local Extension Settings database for that exact ID, a stored `version`
  string equal to 7.0.0, and an explicit supported `sessions` map.

Other versions, ambiguous layouts, missing/deleted collections and incomplete legacy
migrations fail. No legacy localStorage reader, silent per-record migration or manual
export-file fallback is introduced. These version checks establish researched compatibility;
they do not authenticate an arbitrary locally modified extension package.

## Saved-window API and projection

`SavedWindows.Parse(sessionsBytes, extensionVersion, token)` in
`BraveBackup.Extensions.TabManagerPlus` returns a `SavedWindowCollection` of typed records.
It preserves:

- Stored window ID, name/color, date/sessionStartTime, optional updated/order fields,
  custom-name and private flags.
- Tab array order and stored indexes, URLs, optional titles, pinned/active/private flags.
- Optional stored window ID/type/state, position, size, private/focused/always-on-top flags.

Absent optional fields stay null. Tabs retain their array order; index gaps are not
renumbered. Window ordering follows upstream: explicit finite order first ascending,
then windows without order by date descending; ties retain JavaScript object-property
order, including canonical integer keys before other keys. All saved windows are captured,
including private records if the extension actually stored them. No unsaved live state
is inferred and URLs are never opened, even when they use non-HTTP schemes.

Required fields, map-key/record-ID agreement, unique nonnegative tab indexes, usable stored
URLs, field types and limits are checked. Missing URLs do not fall back to pendingUrl.
Invalid records block the whole collection rather than being dropped or repaired as the
extension's own permissive importer might do. Duplicate JSON properties are rejected at
every depth, including unknown metadata. Malformed JSON/UTF-8/unpaired surrogates receive
a fixed content-free `InvalidDataException`; cancellation remains cancellation.

The portable projection excludes unknown metadata, favicons, transient bookkeeping,
preferences and native Chromium group IDs. A saved-window color does not imply native
tab-group metadata. Generic Extensions raw snapshots remain separate and unchanged.

Parser limits: 32 MiB input, JSON depth 32, 1,000,000 nodes, 16 Mi UTF-16 code units of
aggregate property/string text, 10,000 windows and 100,000 tabs. IDs are at most 512
characters; names/titles 32,768; colors 128; URLs 1 Mi characters. Millisecond timestamps
must be nonnegative integers within JavaScript's safe integer range. Optional order is
finite; geometry sizes, if present, are positive Int32 values, with signed positions.

## Read-only capture API

`BraveTabManagerPlusCapture.Read(profile, observeBrowserState, token)` creates a disposable
capture with `Data`, `CapturedUtc` and `SourceFileCount`. The optional host observer should
include custom Brave executable names when applicable; by default the existing Platform
probe observes Brave name variants. Running, unknown, failed or invalid observations block
capture; Brave is never launched, closed or flushed.

Capture uses Platform's safe object inspection/read access. Existing directories and
regular files are required through every ancestor; linked/special sources and nested
database directories fail. At most 4,096 captured tree nodes and 256 MiB aggregate source
bytes are accepted; manifests are limited to 1 MiB before allocation, and package directories
to 16. Database decoding retains the [offline reader's limits](tabmanagerplus-storage-reader.md).
These separate memory budgets coexist and are not a single process-memory cap.

Files are read into memory only. Discovery records source shape, sizes and modification
times; every captured file is hashed. Capture performs a final shape/hash/stopped-state
check before returning. `VerifyUnchanged()` repeats those checks and must be called again
immediately before future archive publication. Same-size edits with restored modification
times are detected by hashes. This observed-process/filesystem policy cannot guarantee
atomicity against a process starting or changing files after the last observation.

Capture errors use sanitized `BackupOperationException` codes/messages, without paths,
URLs, titles or underlying exception text. Temporary raw capture arrays are cleared on
success/failure. `Dispose()` releases hash metadata and decoded-model references and
prevents further data access/checks; managed strings or models retained by callers cannot
be securely erased. No source file, staging database, archive or plaintext export is written.

## Verification and next checkpoint

61 new tests cover typed fields, absence/privacy/order, invalid/missing fields and Unicode,
version restrictions, count/text limits, cancellation, closed/running/unknown/error process
states, source preservation, hash/shape changes, version mismatches, missing/deleted data,
development identity rejection, oversized/special/linked sources and disposal.

All **514 solution tests pass** on Linux (447 Core, 48 Brave, 13 Linux, 6 Windows-labelled).
The solution including UI/CLI builds with zero warnings/errors. These results do not
constitute a native Windows or real installed-Brave end-to-end capture test.

The subsequent archive/UI increment adds portable version handling with backward-compatible
v1–4 reads, semantic verification, feedback, warnings and publication tests. More multi-table
LevelDB compatibility fixtures remain useful coverage work before release.
