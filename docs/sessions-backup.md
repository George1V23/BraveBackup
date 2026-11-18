# Persisted Sessions backup and archive v4

The user kept all six [Sessions scope choices](sessions-backup-proposal.md). The Backup
form now supports Sessions alone or with existing supported categories, with optional
additional native files. [Restore on Linux](restore-sessions.md) can apply the
verified native snapshot to a compatible existing profile. Existing archive-v1/v2/v3
and HTML/JSON behavior remains compatible.

## Supported data and capture meaning

The portable payload contains reconstructed persisted windows, visually ordered tabs,
the selected/current persisted navigation URL/title, pinned state, available active-window
and selected-tab information, and open-group membership/title/color/collapsed state.
Missing group metadata is explicit null metadata and counted in feedback; it is not
replaced with invented titles/colors. Closed tabs/windows and unreferenced group metadata
are replayed as closed state, not added to the open-session projection.

The form calls this **last persisted session**, not a real-time browser snapshot. Chromium
can delay writes, so visible browser state may be newer. Capture time is read time; the
numeric filename suffix is not presented as the last flush time. Source provenance stores
the relative filename, native format version, exact byte length and SHA-256.

Read-only capture can run while Brave is open if the source is readable and stable.
The app never launches/closes Brave, forces a flush, enables remote debugging, installs
a helper extension or reads process memory. Source permissions or native file sharing
may prevent capture; that is reported as unavailable, not bypassed.

When Extensions is also selected, its existing Brave-closed rule remains mandatory for
the operation. Deselect Extensions to attempt running-browser persisted capture. No
session path weakens the Extensions gate. A missing/unsupported source is Unavailable;
only a complete successfully decoded empty snapshot reports zero persisted windows.

## Current native format support

| Source | Current behavior |
| --- | --- |
| Modern unencrypted SNSS format 3, complete initial-state marker | Bounded 64-bit little-endian command replay. |
| OS-encrypted format 5, other framing versions or legacy format 1 | Portable capture blocked explicitly. |
| Relevant Session_/Tabs_ files in Sessions_Encrypted | Blocked conservatively, even if plaintext exists; authoritative runtime migration stage is not known. |
| Newest source incomplete, unstable, unknown commands or unsupported native layout | Fail/retry boundedly; no silent older-source fallback. |
| Required window/type/order/navigation data missing, or active split-tab state | Block an incomplete projection instead of dropping tabs or inventing values. |

Selection uses the greatest canonical positive numeric suffix among `Sessions/Session_*`
files, not lexicographic sort or filesystem mtime. `Tabs_*` is a distinct family and is
never merged into the portable window session. Unknown family names do not become inputs;
malformed Session_/Tabs_ suffixes fail explicitly. No supported modern source means no
portable capture, even if legacy files exist. Private/incognito memory, independent saved
or closed group collections, custom window geometry/titles, and TabManagerPlus-specific
saved windows are outside this projection.

The decoder uses the pinned Chromium command definitions, 4-byte-aligned Pickle fields,
positive signed 32-bit IDs and source-defined replay rules. It handles tab moves/closure,
window closure, visual ordering, selected navigation, navigation replacement/pruning,
pinning and group updates. URL/title strings are preserved without executing them.
Known non-projection metadata is bounded/checked and omitted; unknown command IDs fail.
The optional navigation tail is accepted only at supported historical field boundaries.
Split-tab layout is not silently flattened: an active split blocks creation.

## Native-file option and privacy

The existing Include native session files option is unchecked by default and enabled
only with Sessions. When checked, it additionally preserves all included modern
`Sessions/Session_<number>` / `Sessions/Tabs_<number>` files and present explicit legacy
Current Session/Current Tabs/Last Session/Last Tabs files as **raw bytes**. Legacy files
are not thereby declared decoded or restorable. Missing legacy names are explicit in the
native inventory. Session Storage, Apps_* and unrelated profile files are excluded.
Encrypted-directory coexistence still blocks portable capture; raw files cannot silently
replace an unsupported requested portable session.

The exact selected source bytes are used both for portable decoding and for its native
payload, avoiding two different snapshots under one provenance digest. Other native
files are streamed read-only, and all captured source hashes/metadata are rechecked
before publication. There are at most three bounded read/decode attempts for transient
framing/marker/read/source-change problems. A new source or changed source before final
publication fails the archive. Links, non-regular files, unsafe ancestors and excessive
resource use are rejected through the existing Platform file-safety service.

Portable JSON excludes navigation history, encoded page state, POST/referrer data,
user-agent/storage identifiers and arbitrary embedded metadata. Raw native files retain
whatever metadata the browser wrote. Encryption stays enabled by default. An explicitly
unencrypted native save requires a new destination-specific confirmation, showing that
those files contain browsing data beyond portable URLs/titles. Cancel/dismissal/no callback
means no save. Portable URLs/titles are readable when encryption is off, as disclosed in
the form. No machine key or credential database is read or copied.

## Archive-v4 contract

Archives containing Sessions use manifest `formatVersion: 4`, with `sessions-v1` required.
Capabilities are the selected subset, in canonical order:
`bookmarks-v1`, `settings-v1`, `extensions-v1`, `sessions-v1`.
Every profile has exactly each requested category once. Writes without Sessions retain
their previous manifest versions. Current validation reads v1 through v4; older readers
reject v4 explicitly. BBKENC1 envelope version 1 and standalone bookmark JSON schema 1
are unchanged.

Session category version 1 has these payloads:

```text
.../sessions/portable-v1.json                         role: portable-session
.../sessions/native-inventory-v1.json                 role: native-session-inventory (optional)
.../sessions/native/<relative-native-filename>        role: native-session-file (optional)
```

The common prefix is `installations/<installation-id>/profiles/<profile-id>/`.
Each descriptor includes exact uncompressed length and SHA-256. Core checks canonical
paths/roles/counts, JSON shape, unique IDs, selection bounds, tab ordering, group references,
and all hashes. Native inventory links every raw file exactly once; its selected source
must match portable provenance. No raw files are allowed when the flag is false.

Portable schema 1 fields are `schemaVersion`, `captureKind: "last-persisted"`, `capturedUtc`,
`source: { path, formatVersion, length, sha256 }`, `includeNativeFiles`, `activeWindowId`
and `windows`. Windows have `id`, persisted `type`, nullable `selectedTabIndex`, `tabs`
and `groups`. Tabs have `id`, `visualIndex`, `url`, `title`, `pinned`, nullable `groupId`.
Groups have token `id` and nullable `title`, `color`, `collapsed`. Token representation is
32 lowercase hex characters (high 64 bits, then low), an application representation.
This JSON is not a native Chromium file.

Native inventory schema 1 has `schemaVersion`, `absentLegacyFiles`, and `files`, whose
rows contain `relativePath`, `archivePath`, `length`, `sha256`. Backup additionally checks
the actual supported names/format. When native files are included, validation replays the
archived selected source and compares its windows/tabs/groups with portable data. Rehashing
a tampered URL/group title therefore does not make a mismatched native/portable pair valid.
No file is extracted into a browser profile.

## Limits, implementation and testing

Limits: 256 MiB source/native file, 64 MiB portable JSON, 1,024 native-family files per
profile, 1,000,000 commands, 100,000 retained tabs/windows/groups, and 5,000,000 replay work
units. Strings/counts/arithmetic are bounded; pruning work is budgeted. Existing global
archive/entry limits apply. Bounded JSON serialization stops before escaping expands past
the output ceiling. A source limit or unsupported field fails explicitly.

- Core `PortableSession` and related records define the application projection/provenance.
  `PortableSessionFormat.Parse` checks it; `SerializeBounded` limits JSON output.
  `SessionArchiveFormat.ValidateCategory` checks descriptors/integrity and native linkage;
  `ReadJson` rejects duplicate names and owns its parsed bytes independently of cleared
  temporary buffers. Core contains no browser-native decoder or host API.
- Backup `BraveSessionDecoder.Decode` replays the source-defined v3 frames. Its private
  Pickle reader checks lengths/alignment; replay tracks window/tab/navigation/group state
  with bounded pruning and indexed window grouping.
- `SessionCaptureSession.Capture` chooses/reads a stable source and writes selected payloads;
  `VerifySources` rechecks family shape, metadata and hashes before publication.
  `BraveSessionBackup.ValidateCategory` enforces native scope/provenance and exact replay.
- `SessionBackupOptions` freezes native inclusion/consent. Existing CreateAsync overloads
  remain available; the new overload supplies session options. `BackupResult.SessionsSummary`
  carries safe persisted counts/limits. Reports never include URLs, titles or source paths.
- UI connects existing Sessions/native choices and the native privacy dialog; disposal
  cancels work and clears callbacks/secrets. No package or new CLI command was added.

All **417 solution test cases** pass on Linux: 350 Core, 48 Brave, 13 Linux and 6
Windows-labelled cases. They cover independent C++ binary and Python archive-v4 fixtures,
Unicode/groups/pinning/moves/closure/pruning/current navigation, empty versus unavailable,
unknown formats/commands/layouts, truncated/invalid payloads, limits/cancellation,
newest numeric source selection, no stale fallback, native inclusion/consent, both protection
modes/four compression choices, mixed categories/Extensions gate, source changes/links,
rehashed tampering and unchanged sources. Prior archive-v1/v2/v3/crypto/HTML/JSON tests pass.

A Linux desktop fixture used the real Sessions/native controls, created/verified a combined
encrypted v4 archive and cancelled the actual unencrypted-native warning without output.
Its synthetic session source stayed unchanged. The Extensions process observer was injected
for synthetic data, not evidence of a real-browser shutdown. [Review screenshot](screenshots/sessions-completion.png).

See [pinned research and fixture provenance](sessions-research.md). Native Windows/real
Brave-version end-to-end validation and independent encryption review remain release checks.
OS-encrypted and legacy decoding and live capture are outside the Sessions backup scope.
The [native restore workflow](restore-sessions.md) uses the verified plaintext snapshot.
