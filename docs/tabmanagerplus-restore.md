# Tab Manager Plus saved-window restore backend

`SavedWindowRestore.Prepare` in the dedicated TabManagerPlus project prepares a
single LevelDB recovery log for an existing 7.0.0 store. It takes a stable in-memory
snapshot of the closed destination database and the validated portable saved-window
payload. Restore review and profile publication are pending. The backend performs
no filesystem I/O or browser operations.

The backend checks the destination's stored `version` and requires an initialized,
valid `sessions` map. It converts the archived collection to the extension's
ID-keyed map using the approved portable fields. An explicitly empty collection
clears the map. The result replaces the saved windows as one value and preserves
unrelated keys. Extension settings and other data are outside that replacement.
The portable payload's scope determines which fields are restored.

The update uses one Put in a new numbered LevelDB log. It keeps every existing
input file byte for byte. The log number follows the manifest and all existing
numbered files. The sequence follows the highest validated manifest, live table or
replayed WAL sequence. Small updates use one FULL record. Large updates use
CRC32C-checked FIRST/MIDDLE/LAST records across 32 KiB log blocks. The backend
replays the combined image and requires the resulting `sessions` bytes to match
the serialized collection before returning the proposed log. Disposal clears the
owned output buffer.
[LevelDB WriteBatch format](https://github.com/google/leveldb/blob/main/db/write_batch.cc),
[LevelDB log format](https://github.com/google/leveldb/blob/main/doc/log_format.md),
[LevelDB recovery ordering](https://github.com/google/leveldb/blob/main/db/db_impl.cc).

The writer accepts the bounded layouts supported by the [offline reader](tabmanagerplus-storage-reader.md).
It rejects malformed, missing, unsupported and over-limit data, unknown filenames,
nonempty lock files, and snapshots containing LevelDB temporary files. It does not
repair a destination or initialize a missing saved-window map. The app must still
verify the official registered package and stable destination state before using
the log. It must also resolve any overlap with a raw Extensions restore targeting
the same Local store and verify encrypted safety data before publication.

Tests combine the independently generated LevelDB fixtures under
`tests/Core.Tests/Fixtures/TabManagerPlus/storage` with synthetic portable payloads.
They cover replacement, explicit empty state, fragmented log records, consecutive
updates, ordering after a newer write to a different key, corrupt inputs, and
preservation of unrelated settings. In a separate Linux probe, Brave 1.96.61
generated a Local store for a disposable synthetic extension. After the browser
closed, the probe safely added the new log. When reopened, Brave read the new
saved-window map and preserved the extension's unrelated setting. The probe used a
synthetic extension with the same storage shape as Tab Manager Plus. Verification
of the installed official package and the application's Restore workflow remains
a separate checkpoint.
