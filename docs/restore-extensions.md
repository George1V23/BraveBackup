# Restore extension local storage on Linux

1. Open and validate a backup containing Extensions. Select an archived profile,
   map it to an existing destination profile, close Brave and choose **Recheck**.
2. For each mapped profile with eligible stores, select **Extensions**. The selection
   includes all eligible Local stores shown by the check for that profile.
3. Set the encrypted safety-backup options and choose **Review restore**. The review
   lists each extension ID, the registered package version and the exact
   `Local Extension Settings/<id>` directory that will be replaced.
4. **Confirm restore** revalidates the archive, package registration and destination.
   It verifies an encrypted safety backup before writing. Each directory is
   replaced in one atomic operation. Pause, explicit resume and rollback use the
   same controls as [Bookmarks restore](restore-bookmarks-execution.md).

The comparison distinguishes missing registration, unsupported locations, versions
missing from the archive, different files at the same version and storage-only
archive identities. Recheck displays the first 50 identities, gives totals for the
archived identities and eligible Local stores, and clears the prior Extensions
selection. Final review lists every selected directory. Changes to the mapping
invalidate that review.

## Supported data

Both profiles must record the same Brave product and version. The archive must
contain one observed package version for the extension and a present, valid Local
store. The destination must have a matching registered internal package and an
existing flat Local directory, which may be empty. Archives record observed
packages rather than the active source registration. Multiple archived package
versions are excluded.

The check compares the destination's registered version and relative package file
paths and SHA-256 hashes. The install-directory suffix may differ. Package metadata
files participate in the comparison. Empty package directories do not affect it.
The app needs one registration under `extensions.settings` in `Preferences` or
`Secure Preferences`. Links, missing or conflicting records and unsupported
registration locations cannot qualify.

The archive's Local filenames must follow the supported LevelDB layout. The offline
reader checks the manifest, live tables and write-ahead logs before the store can be
selected and again before preparation. It reads no values for display, runs no
database repair and executes no extension code. It checks storage structure and does
not interpret extension-specific value semantics. Files outside an eligible Local
store, including Sync storage, package files and registration records, stay unchanged.
Missing destination stores, installation and Windows restore remain pending.
[LevelDB file naming](https://github.com/google/leveldb/blob/main/db/filename.cc),
[LevelDB storage layout](https://github.com/google/leveldb/blob/main/doc/impl.md),
[Chromium storage API](https://developer.chrome.com/docs/extensions/reference/api/storage).

The read-only check is bounded to 100,000 package paths and 512 MiB of package hashing
per destination profile. The compact archive inventory has a 100,000-entry budget.
Local validation examines up to 128 MiB of directory images across an archive and
each image can contain up to 32 MiB and 4,096 files. A store outside those limits
cannot be selected. The app keeps archive inspection available for the other data.

Confirmation binds the reviewed archive ID and digest, exact destination, package
identity and Local directory contents and supported metadata. It rechecks registration
at the write boundary and on resume. Rollback uses the original encrypted safety
image and can proceed after the source archive or extension registration disappears,
provided Brave is closed and the target still matches this operation's expected state.
[Directory exchange and recovery](restore-directory-exchange.md).

## Sources and verification

- Chromium reads the registered version, relative package path and persisted install
  location from the preference record. Brave controls effective enablement and
  installation approval. [Chromium extension preferences](https://chromium.googlesource.com/chromium/src/+/234f53daea569f56d53d88d3d143a8c9bbdaeec9/extensions/browser/extension_prefs.cc#1460),
  [Chromium manifest location](https://chromium.googlesource.com/chromium/src/+/84fa9085bed1e927afa7c952ee0d8d5d636be37e/extensions/common/mojom/manifest.mojom).
- Integration tests use encrypted archives and the checked-in
  `Fixtures/TabManagerPlus/storage/table-and-wal` fixture to exercise valid and
  corrupt Local stores, exact review paths, changes after review and after staging,
  pause, resume and rollback. The desktop smoke test uses **Recheck**, **Review
  restore** and **Confirm restore** on a synthetic profile.
- A separate probe used Brave 1.96.61 with two disposable profiles. It wrote distinct
  values through the same synthetic extension, closed both processes, and passed the
  browser-generated store through an encrypted archive and the production planner.
  The app-side registration was synthetic and was removed before reopening Brave.
  The probe verified byte-exact rollback, then reapplied the restore and read the
  expected storage value in Brave.
