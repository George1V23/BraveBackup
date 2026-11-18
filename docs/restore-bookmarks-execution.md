# Restore Bookmarks on Linux

1. Open a `.bravebackup` archive in **Restore** and unlock it if encrypted. Choose
   the source profiles, their destination installations and profiles, and **Bookmarks**.
   Source and destination must record the same Brave product and version and have
   [supported native Bookmarks data](restore-bookmarks-backend.md).
2. Close Brave and recheck the destinations. Choose a recovery folder outside Brave's
   data and enter an independent safety-backup password twice.
3. Select **Review restore** and check the listed destination files and recovery paths.
   **Confirm restore** revalidates the reviewed archive and destination context. It
   publishes and reads back the encrypted safety backup before replacing any Bookmarks
   file. The app checks Brave's process state again at write boundaries.
4. The progress panel identifies the destination installation, profile and category.
   The completion report shows target counts, the final state of every selected target,
   sanitized error codes on failure and the path to the encrypted safety backup.
   **Open safety-backup folder** opens its directory.

**Cancel restore** stops preparation before any profile change or requests a pause at
the next safe write boundary. When paused, **Confirm rollback** recovers the touched
targets from verified safety data. Rollback can run even if the original archive has
been removed. **Decline rollback** leaves the operation paused. **Resume restoration**
rechecks the archive, safety backup, destinations and Brave's process state before
continuing. Keep Brave closed while the restore or rollback runs.

The workspace and window close controls are held while the restore is active or paused.
If rollback cannot verify or recover a target, the report flags the unresolved entry and
preserves the recovery artifacts. The app can retry rollback from the retained session.
Recovery after an app restart or operating system interruption remains unsupported.
The archive password is retained in memory while the inspected archive is open, and
the transaction retains the independent safety password for recovery. The UI clears the
password fields and releases its references when the corresponding session is closed.

The automated tests use synthetic profiles to cover confirmation, archive replacement
after review, pausing across two profiles, explicit resume, and rollback with the source
archive removed. The Avalonia desktop smoke test clicks the review and confirmation
buttons and checks exact restored bytes, recovery artifact creation and preservation of
unrelated profile data. Windows execution and restoration of other categories remain
pending.
