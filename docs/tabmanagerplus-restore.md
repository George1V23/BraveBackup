# Restore Tab Manager Plus saved windows on Linux

Choose a validated backup containing Tab Manager Plus, map the source profile to
an existing destination, close all Brave processes and select **Recheck**. The
destination must use the same recorded Brave product and browser version. Recheck
looks for the internal registration under the Tab Manager Plus extension ID, the
7.0.0 Manifest V3 package and its existing Local database. The database must
contain version 7.0.0 and a valid initialized saved-window map. This check uses
the registration, manifest and package file fingerprints on disk. It does not
verify a store signature.

Select **Tab Manager Plus** and review the full path under the profile's
`Local Extension Settings` directory. Restore replaces the entire saved-window
collection, including clearing it when the archived collection is empty. Other
Local keys are preserved. If the raw **Extensions** selection also targets this
store, clear one of the two categories before reviewing. Other eligible categories can use the same transaction.

Confirming rechecks the package and the destination database captured by Recheck.
The transaction verifies the encrypted safety backup before atomically publishing
the updated directory. Pause leaves completed profiles visible in Restore. Resume
rechecks the reviewed package registrations and verifies remaining targets before writing. Rollback uses the encrypted
original directory images, allowing recovery within the retained Restore session
even if the source archive or package is gone. Brave must be closed for writes
and rollback. Windows and restart recovery are pending.

## Storage update

`SavedWindowRestore.ValidateDestination` checks the destination's layout and
whether its numbering and sequence space can accept another log.
`SavedWindowRestore.Prepare` converts the validated portable payload to the
approved ID-keyed map and writes a single `sessions` Put into a new numbered
LevelDB recovery log in memory. The log number follows the manifest and existing
numbered files. The sequence follows the highest validated manifest, live table
or replayed WAL sequence. Small updates use one FULL record. Large updates use
CRC32C-checked FIRST/MIDDLE/LAST records across 32 KiB log blocks. The backend
replays the combined image and requires the new value to match the serialized
map before returning the log. The verified directory image preserves the existing
file bytes and their supported Linux recovery metadata. The new log has private
file permissions.
[LevelDB WriteBatch format](https://github.com/google/leveldb/blob/main/db/write_batch.cc),
[LevelDB log format](https://github.com/google/leveldb/blob/main/doc/log_format.md),
[LevelDB recovery ordering](https://github.com/google/leveldb/blob/main/db/db_impl.cc).

The writer accepts the bounded layouts supported by the [offline reader](tabmanagerplus-storage-reader.md).
It rejects malformed and over-limit data, unknown filenames, nonempty lock
files, and LevelDB temporary files. A missing or unsupported database requires
a different destination. The portable payload's existing field scope determines
which window and tab details are restored. The backup format is unchanged.

## Verification

Storage tests use independent LevelDB fixtures to check replacement, empty
collections, fragmented and consecutive writes, sequence ordering, corrupt
inputs and preservation of unrelated settings. Linux Restore view-model tests
create encrypted archives from those fixtures, then exercise Recheck, review,
confirmation, blocked mappings, pause, resume and rollback on disposable
profiles. A separate Linux probe reopened a Brave 1.96.61 database after adding
the log and confirmed preservation of an unrelated setting. That probe used a
synthetic extension with the same storage shape. A live run with the official
extension remains unverified.
