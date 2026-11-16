# Sessions backup: scope checkpoint

Status: **proposal for keep/edit/defer review; not implemented**. Existing Bookmarks,
Settings, Extensions and Export behavior remain unchanged. Sessions is next in
[the agreed category sequence](backup-completion-plan.md).

The user already kept Sessions (windows, tabs and tab groups) and optional native session
files. This checkpoint defines what those controls can truthfully capture and how to
handle unsupported browser formats before implementing their functionality.

## Current implementation and gaps

`BraveProfileDetailsReader.GetSessionInfo` counts matching files/directories and sizes.
Its tests create arbitrary byte arrays and verify inventory only. Neither those fixtures
nor the existing reader establish a session parser, complete snapshot, open-tab count or
restore workflow. The native-file checkbox already depends on Sessions and clears when
Sessions is deselected. Keep that existing dependency.

## Proposed choices

| Choice | Proposed behavior |
| --- | --- |
| Portable Sessions contents | Save reconstructed persisted windows, ordered tabs with their selected/current navigation URL and title, active-tab/window selection where available, pinned state, and supported open-tab group membership/title/color/collapsed state. Include source provenance and capture limits. |
| Capture condition | Allow read-only capture while Brave is running if a stable supported on-disk snapshot can be obtained. Label it “last persisted session”; do not claim a real-time snapshot or close/launch Brave. |
| Session source | Select the newest numeric `Session_*` source in the supported modern session directory; clearly label current-versus-legacy provenance. Never merge independent `Session_*` and `Tabs_*` streams or silently fall back to an older session when the selected source cannot be read. |
| Native session files | When the existing optional checkbox is checked, additionally preserve supported native session/tab files as raw bytes, with their inventory, lengths and hashes. This is best-effort preservation, not a portable restore guarantee. |
| Unsupported/incomplete data | Fail the selected Sessions operation clearly on unsupported encryption/version, truncated/unstable input, unknown commands that could change the requested result, or missing required source data. Do not silently produce a URL-only, group-free or raw-only replacement. |
| Privacy | Keep archive encryption on by default. Explain that portable URLs/titles are readable if encryption is off; require confirmation before saving unencrypted native session files, which contain more than that portable list. Do not put browsing contents in reports/logs. |

No new per-tab selection control is proposed in this increment. The existing Sessions
choice applies to all selected profiles, with totals/provenance in the summary/feedback.
The existing Include native session files checkbox remains optional and unchecked by
default, enabled only when Sessions is selected. It is additional to portable capture;
checking it does not authorize silently downgrading unsupported portable Sessions to raw files.

## Capture semantics and conflicts

Chromium delays persistence, so the visible tab/window state can be newer than the files.
A successful backup therefore means a validated persisted session snapshot, not every
window/tab currently on screen. State capture time and source file provenance belong in
the payload. Completion reports persisted window/tab/group counts and warns about the
persistence limit without logging URLs or titles.

Do not force a browser flush, enable remote debugging, install a helper extension, collect
process memory or close Brave as part of this disk-reader increment. Closing windows may
change the very state the user wants to capture; it is not a universal way to obtain an
accurate live-window snapshot. A future live browser integration requires separate review.

When Extensions is also selected, its approved Brave-closed gate still applies to the
whole operation. For a running-browser persisted Sessions backup, the user must deselect
Extensions. The UI must explain the conflict rather than weakening the Extensions gate.

A profile without a readable supported session source is Unavailable, not “zero open tabs.”
A complete successfully decoded snapshot with no windows may report zero persisted windows.
Private/incognito memory state, independent saved/closed tab-group collections and
TabManagerPlus-specific saved windows are outside this disk-session slice.

## Format and source policy to research and freeze

Primary source inspection found modern session directories/prefixes, marker-based
unencrypted files and OS-encrypted files. The first implementation should target the
source-defined unencrypted framing only after pinning its exact revision and producing
independent fixtures. Do not claim encrypted-file support from an SNSS magic check alone.

- For modern sources, use the source-defined session-family filenames and numeric suffix
  ordering. Do not guess source ordering from filesystem enumeration or lexicographic sort.
  Verify the selected file's framing, initial-state marker and complete records.
- If only legacy Current Session / Last Session files exist, support them only with a
  separately documented, fixture-tested version-1 decoder. Otherwise explain the limitation.
  No filename-based assumption that an older file is the current browser state.
- Detect `Sessions_Encrypted` and source-defined encrypted framing. If the authoritative
  supported source cannot be decoded, fail clearly. Do not choose a stale plaintext copy
  instead merely because it is easier to parse. Resolving authoritative encrypted versus
  plaintext coexistence must follow pinned upstream behavior, not timestamp guesses alone.
- Handle commands for window/tab identity, ordering, selected navigation, moves, closure,
  navigation pruning, pinned tabs and groups using their actual definitions and replay
  semantics. Optional native metadata may be outside the portable projection, but it
  must not be mistaken for an unknown command that could alter requested windows/groups.
- Decode Chromium Pickle fields using bounded, checked reads and documented alignment;
  never search arbitrary strings out of a binary file and call that a session decoder.
- Portable data includes current URLs/titles, not navigation history, page state, POST
  payloads or form values. Native raw bytes may retain additional serialized metadata;
  do not rewrite them or promise that they contain only URLs/titles.

The final source/version/command support matrix and independent fixtures must be recorded
before enabling Sessions. If that matrix cannot support all requested portable fields for
a detected source, creation is blocked with an actionable limitation. This review does
not authorize undocumented decryption or copying machine encryption keys.

## Native-file scope

Proposed additional raw scope: source-defined session-family files such as
`Sessions/Session_<number>` and `Sessions/Tabs_<number>`, plus explicitly supported legacy
Current Session/Current Tabs/Last Session/Last Tabs files when present and tested.
Any encrypted-directory raw preservation requires its own documented inventory and remains
additional to a successfully decoded portable source, not a fallback around unsupported
primary capture. Do not copy the entire profile or interpret the Session Storage directory
as window/tab command data.

Require regular files, reject links/special files, capture bounded stable bytes, preserve
original filenames through a canonical relative-path inventory, and hash before/after
capture. Missing optional native families are explicit absence; unreadable files in the
included scope fail the operation. Unknown files are outside the named supported scope
and must not be represented as included. No source file is opened for writing.

## Planned archive and implementation

Archives containing Sessions would use **manifest v4**, with required `sessions-v1` and
the selected existing category capabilities. The optional native-file choice is explicit
in Sessions metadata, with exactly declared payload roles/counts. Prior v1/v2/v3 archives
remain readable and writes without Sessions keep their existing versions. BBKENC1 and
standalone bookmark JSON schema 1 remain unchanged.

Proposed payloads:

```text
installations/<installation-id>/profiles/<profile-id>/sessions/portable-v1.json
installations/<installation-id>/profiles/<profile-id>/sessions/native-inventory-v1.json  (optional)
installations/<installation-id>/profiles/<profile-id>/sessions/native/<relative-name>    (optional)
```

The portable schema is application-owned and source-neutral. Core validates its shape,
references/counts and payload integrity; Backup owns Chromium/Brave framing and replay.
OS services stay in Platform. Native-file confirmation is bound to the frozen operation
and destination, like the existing unencrypted Extensions warning.

Proposed resource limits: 256 MiB per source/native file, 64 MiB portable JSON, 1,000,000
commands and 100,000 tabs per profile, with bounded strings, collections and arithmetic.
Global archive/path/entry limits remain. Exceeding a limit fails explicitly. No package
is proposed; any decryption or third-party parser dependency needs a separate explanation.

## Primary sources and uncertainty

Inspected 2026-11-16; these moving source references must be pinned during implementation:

- [Session constants](https://github.com/chromium/chromium/blob/main/components/sessions/core/session_constants.cc):
  Sessions and Sessions_Encrypted directories, and distinct Session/Tabs/Apps families.
- [Command storage backend](https://github.com/chromium/chromium/blob/main/components/sessions/core/command_storage_backend.cc):
  framing versions, initial-state markers and encrypted reading requirements. Current
  source distinguishes marker-based unencrypted version 3 from OS-encrypted version 5.
- [Command storage manager](https://github.com/chromium/chromium/blob/main/components/sessions/core/command_storage_manager.cc):
  asynchronous persistence and separate encrypted/plaintext backend handling.
- [Session command definitions/replay](https://github.com/chromium/chromium/blob/main/components/sessions/core/session_service_commands.cc):
  window/tab/navigation/group command families and their replay behavior.
- [Serialized navigation entries](https://github.com/chromium/chromium/blob/main/components/sessions/core/serialized_navigation_entry.cc):
  URLs/titles are only part of native serialized navigation data.

These sources establish implementation complexity and scope limits, not verification of
an installed Brave version. No personal session data was inspected. Existing arbitrary-byte
inventory tests are not valid binary-decoder fixtures and will not be relabelled as such.

## Acceptance and next checkpoint

After scope review, pin format sources, add independently generated valid/invalid session
fixtures, implement bounded decoding/replay and capture, then connect the controls and
archive v4. Test multiple windows, moved/closed/pinned tabs, selected navigation, pruning,
Unicode, groups and optional metadata, valid empty snapshots, unsupported encryption/
versions/commands, truncation, source churn, mixed-category stopped-state behavior, privacy
confirmation, native inclusion/exclusion, limits, cancellation and unchanged sources.
Preserve all existing archive, crypto and HTML/JSON regressions.

Review the six choices above with keep/edit/defer before implementation. Restore, broader
live capture, OS-encrypted session decoding, TabManagerPlus-specific data and Passwords
remain separate work; no restoration or decryption bypass is introduced here.
