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

Prepared for review: [archive-format proposal](archive-format-proposal.md). No writer is implemented.

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

Extend the tested archive contract through settings, extensions, sessions/native session
files, TabManagerPlus and passwords. Research, fixtures, tests, capability limitations and
user review accompany each category. TabManagerPlus belongs in its own project under
Extensions. Password protection and the explicit plaintext exception follow AGENTS.md.

Restore-from-backup, Import, Backups management and Merge/Sync retain their own later
reviews. No restore operation is introduced by approval of Backup-page controls.
