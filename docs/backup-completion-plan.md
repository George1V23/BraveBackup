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
is implemented with synthetic transaction coverage; browser row projection remains separate work. Real Windows/keyring validation, archive saving, raw-file fallback and
UI wiring remain pending. Live capture remains conditional; the initial path requests Brave closed.

Extend the tested archive contract through settings, extensions, sessions/native session
files, TabManagerPlus and passwords. Research, fixtures, tests, capability limitations and
user review accompany each category. TabManagerPlus belongs in its own project under
Extensions. Password protection and the explicit plaintext exception follow AGENTS.md.

Restore-from-backup, Import, Backups management and Merge/Sync retain their own later
reviews. No restore operation is introduced by approval of Backup-page controls.
