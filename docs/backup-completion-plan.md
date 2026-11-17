# Next Backup implementation checkpoints

The user has kept all previously deferred Backup-page controls. These decisions are
recorded in [ui-decisions.md](ui-decisions.md). This plan separates completing their UI
from enabling real archive operations, following the originally approved phased sequence.
Each numbered increment ends with user review before the next increment starts.

## 1. Complete the approved Backup interface

Implemented for user review. See [behavior and validation](backup-interface-completion.md).

Scope:

- Add Sessions and optional Include native session files, enabled only with Sessions.
- Add a live summary of selected installations/profiles, categories, encryption,
  compression and the complete destination path.
- Show a clearly labelled size estimate where reliable data is available, with
  Calculating/Unavailable states otherwise. Do not invent sizes for unsupported categories.
- Add the About the backup panel with accurate encryption and restoration descriptions.
- Add the approved status bar using actual operation state, active Brave version and OS.
  Keep OS metadata implementation in Platform.
- Add a decorative graphic consistent with the placeholder branding.
- Provide the Create Backup/progress/cancel/completion layout. Archive creation stays
  unavailable with an explicit reason until the engine exists; no fake progress or
  success states. Idle progress/completion controls remain hidden.

Acceptance:

- Single-installation and checked-installation scope update the summary correctly.
- Sessions/native-file option dependency, Reset and refresh preservation work.
- HTML and JSON export remain functional and independent from backup choices.
- No archive or browser-profile write is introduced in this UI increment.
- Behavior tests and documentation accompany the changes.

## 2. Review the concrete archive contract

Accepted for implementation through user continuation. See [the archive contract](archive-format-proposal.md).

Prepare a separate, reviewable format proposal before implementing the writer:

- `.bravebackup` container, manifest, format identification/version and category layout;
- encrypted and explicitly unencrypted modes, password handling and integrity checks;
- compression choices, incomplete-output cleanup and compatibility policy;
- multi-installation/profile identity and explicit supported/unsupported category behavior;
- privacy-safe operation reports and the required plaintext-CSV confirmation;
- backward compatibility with existing schema-1 bookmark JSON, without redefining it.

Do not promise cross-machine credential or native-session portability without research
and tests. Undocumented browser structures require research notes and fixtures. Any new
NuGet package needs its purpose explained. This contract gets its own user review.

## 3. Deliver working bookmark archive creation

Implemented for user review: [archive-v1 behavior, limits and validation](archive-format-v1.md).

After contract approval, implement a complete bookmarks-only archive workflow:

- Create Backup validates scope, destination, password/confirmation and category support.
- Encryption/compression, actual progress, safe cancellation and atomic output work.
- Success/error feedback, opening the destination folder and safe reports are connected
  to actual outcomes.
- Unsupported selected categories are explained and block creation; nothing is silently
  omitted or represented as backed up when it is not.
- Tests cover successful output, integrity/decryption failure, cancellation and preservation
  of existing files/profile data. Existing HTML/JSON export remains covered.

## 4. Add remaining categories as separately reviewable increments

Settings scope was kept in full and is implemented for user review:
[Settings backup and archive v2](settings-backup.md).

Extensions scope was kept in full and is implemented for user review:
[Extensions backup and archive v3](extensions-backup.md).

Sessions scope was kept in full. Tested unencrypted format-3 capture and optional raw
files are implemented for review: [Sessions backup and archive v4](sessions-backup.md).
Encrypted/legacy decoding remains unsupported and blocks capture explicitly.

TabManagerPlus automatic profile capture and saved-window scope were accepted:
[source findings and proposed choices](tabmanagerplus-backup-proposal.md).
[Storage research and independent fixtures](tabmanagerplus-research.md) establish the
first checkpoint. The [bounded storage reader](tabmanagerplus-storage-reader.md) is
implemented and tested in its own extension project. [Saved-window parsing, store-package
research and read-only capture](tabmanagerplus-capture.md) are implemented and tested.
Archive/UI integration is implemented for review: [TabManagerPlus backup and archive v5](tabmanagerplus-backup.md).
The existing category now creates verified supported saved-window backups. Passwords are
the next category scope review; no password implementation starts from this checkpoint.

The [Passwords scope decisions](passwords-backup-proposal.md) are accepted after individual
review of all seven items. They include on-demand access, protected defaults, optional CSV,
consistent live capture where possible, browser-assisted fallback investigation and an
explicit raw-file preservation choice on portable-capture failure. Research and fixtures
checkpoint is complete: [Passwords sources, compatibility limits and synthetic fixtures](passwords-research.md).
Six independent fixture checks pass. [Isolated encrypted-database capture and schema
validation](passwords-snapshot.md) are implemented with 36 new tests, without enabling the
UI category. [Provider-aware password and note decoding](passwords-decoding.md) is
implemented with 20 synthetic tests. A [Windows DPAPI key adapter and envelope reader](passwords-windows-key.md)
now have synthetic coverage. The [Linux Secret Service adapter](passwords-linux-key.md)
has synthetic wrapper and isolated native D-Bus coverage.
[Explicit capture orchestration](passwords-capture.md) is implemented with 24 synthetic
integration tests. The [portable password payload codec](passwords-payload.md) now has
independent synthetic fixture and capture-mapping coverage. The [CSV formatter](passwords-csv.md)
has 17 tests against synthetic fixtures. [Confirmed standalone CSV saving](passwords-csv-save.md)
is implemented with synthetic transaction coverage; browser row projection remains separate work. The [explicit raw-file fallback](passwords-raw-fallback.md) now has capture and host-choice
coverage. [Archive-v6 protected portable password validation](archive-format-v6.md) is
implemented. [Protected portable password writing](passwords-protected-backup.md) now
creates verified encrypted v6 archives through explicit advanced options. [Encrypted raw fallback writing](archive-format-v7.md) now emits v7 with explicit
portable/raw result classification. CSV archive writing and UI integration remain pending. Real Windows/keyring validation
and UI wiring remain pending. Live capture remains conditional; the initial path requests Brave closed.

Extend the tested archive contract through settings, extensions, sessions/native session
files, TabManagerPlus and passwords. Research, fixtures, tests, capability limitations and
user review accompany each category. TabManagerPlus belongs in its own project under
Extensions. Password protection and the explicit plaintext exception follow AGENTS.md.

Restore-from-backup, Import, Backups management and Merge/Sync retain their own later
reviews. No restore operation is introduced by approval of Backup-page controls.

[CSV canonical-note projection](passwords-csv-notes.md) now has independent fixture
coverage. Full browser grouping/origin/affiliation projection, CSV archive saving and
Password UI integration remain unfinished.

[Supported ASCII CSV web-origin projection](passwords-csv-origins.md) now has source-derived
fixtures and tests. Complete browser grouping/affiliation and broader origin families
remain pending before a complete CSV export flow can be enabled.

[Verified-group CSV row projection](passwords-csv-projection.md) now combines the tested
origin/note components with source-derived grouping and URL deduplication. Its offline
affiliation/group resolver input remains unresolved; the full CSV UI is still disabled.

[Offline affiliation-cache schema-v7 reading](passwords-affiliation-cache.md) now has
upstream fixture coverage. Main/WAL source capture, canonical facets and PSL/private-registry
merging remain pending before CSV group identities can be resolved from real profiles.

[Affiliation main/WAL source capture](passwords-affiliation-snapshot.md) now preserves
source bytes and revalidates the cache source through publication. Canonical facet
construction and PSL/private-registry related-group merging remain unfinished.

[Canonical facets and offline PSL/private-registry group merging](passwords-offline-groups.md)
now resolve supported cache/singleton groups. The full CSV operation/UI and broader
origin/version compatibility work remain pending.

[Complete single-profile standalone CSV export](passwords-csv-operation.md) now connects
capture, offline grouping, projection, fresh confirmation and atomic saving behind a
researched-version gate. UI wiring, broader compatibility and CSV archive entries remain pending.

[Approved password-protection UI](passwords-ui-protection.md) now preserves safe defaults
and explains explicit CSV/container choices. The new provider-selector control is awaiting
the user’s requested individual review; password UI actions remain blocked meanwhile.

The user approved the initially unselected [Password source selector](passwords-ui-provider.md).
Protected password backup now connects that explicit choice to v6 portable/v7 raw fallback
archives with UI-thread consent. CSV Export controls and CSV archive support remain pending.
