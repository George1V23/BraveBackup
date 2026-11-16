# Tab Manager Plus: automatic-storage research checkpoint

Inspected 2026-11-17. Scope: the accepted [saved-window proposal](tabmanagerplus-backup-proposal.md).
The subsequent [offline reader checkpoint](tabmanagerplus-storage-reader.md) implements
storage decoding with tests. No package dependency, archive capability or UI behavior
has changed. TabManagerPlus remains explicitly unsupported in backup creation.

## Primary sources and storage semantics

Chromium revision `c0837d415025e31505c769f6ef13ba0196feaea2`:

- [LeveldbValueStore](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/components/value_store/leveldb_value_store.cc)
  serializes values with JSONWriter and stores the storage key directly in a LevelDB
  WriteBatch. Reads parse JSON. Its corruption recovery can delete invalid data;
  BraveBackup must not reuse that repair behavior against browser profiles.
- [LocalValueStoreCache](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/extensions/browser/api/storage/local_value_store_cache.cc)
  selects per-extension local settings stores. The existing Extensions research pins
  the `Local Extension Settings` directory name.

Tab Manager Plus revision `3937a32841441b7349673dddf0be895d67e9ef99`:
the proposal links the inspected saved-window type, consolidated `sessions` map,
official export format and migration code. Initial target: researched 7.0.0 modern
consolidated records; legacy individual records/localStorage are not yet supported.
Missing `sessions` is unavailable, not proof of an empty collection. An explicit valid
empty object is distinct. Record fields and ordering need parser fixtures before release.

Identity caveat: the official store ID is `cnkdjjdmfiffagllbiiilooaoofcoeff`, but
deriving the ID from the inspected repository manifest's embedded public key yields
`jajkejgbojkolgjiimaebpgenkgnejfe`. Do not silently add that development identity or
require its public key for store installs. A matching display name/version alone is
not evidence of the same package. Subsequent official store-package inspection confirmed
7.0.0's schema; see [the capture checkpoint](tabmanagerplus-capture.md) for digests and limits.

Google LevelDB revision `29465588d3dfdc80fba5e198037582b8fdb5dd60`:

- [Database organization/recovery](https://github.com/google/leveldb/blob/29465588d3dfdc80fba5e198037582b8fdb5dd60/doc/impl.md):
  CURRENT selects a manifest; manifest edits identify live tables. Recovery replays
  eligible logs, creates files and removes obsolete files. Ordinary DB::Open is not
  a read-only browser-profile operation, even if the caller only invokes Get.
- [Manifest encoding](https://github.com/google/leveldb/blob/29465588d3dfdc80fba5e198037582b8fdb5dd60/db/version_edit.cc),
  [log framing](https://github.com/google/leveldb/blob/29465588d3dfdc80fba5e198037582b8fdb5dd60/doc/log_format.md),
  [write batches](https://github.com/google/leveldb/blob/29465588d3dfdc80fba5e198037582b8fdb5dd60/db/write_batch.cc),
  [table framing](https://github.com/google/leveldb/blob/29465588d3dfdc80fba5e198037582b8fdb5dd60/doc/table_format.md)
  define records, checksums, sequence numbers, tombstones and block handles.
  File enumeration or searching bytes for JSON is insufficient: it can resurrect
  deleted/overwritten sessions or consult obsolete tables.

## Reader strategy for the next checkpoint

Use a bounded managed offline reader over immutable captured bytes, without a new
NuGet dependency or native database open. Keep it in the dedicated extension project,
with filesystem/process safety integration in Backup/Platform. The initial implementation
and tests are documented in the reader checkpoint; installed-extension compatibility
remains a separate task.

Require a stopped-Brave observation and safe regular-file reads, then recheck shape,
lengths and hashes before publication, following the existing Extensions capture gate.
Read only CURRENT's manifest and the resulting live tables/eligible logs. Validate
CRC32C, log fragments, manifest edits, bytewise comparator, table blocks, Snappy bounds,
internal sequences and deletion records. Reject corruption, truncated records, unknown
tags/comparators/compression and inconsistent references instead of repairing/skipping.
Support uncompressed/Snappy blocks first; Zstandard is not implied by this research.
Set aggregate size, record, allocation and replay-work limits before parsing.

Do not create plaintext database staging as a shortcut. Synthetic fixture generation
uses native LevelDB only in a development temporary directory; it is not a runtime
dependency and never opens a user's profile. Native library bindings on a private copy
would still need packaging, corruption behavior and plaintext staging review; that
alternative has not been selected.

## Independent fixtures and verification

`tests/Core.Tests/Fixtures/TabManagerPlus/generate-storage.cpp` uses the pinned upstream
LevelDB implementation (not application parsing code). It creates three small synthetic
databases under `storage/`, verifies their final results through upstream DB::Get,
and preserves CURRENT, MANIFEST, SSTable and WAL bytes. Diagnostic LOG/LOCK files are
removed after close; they are not part of the database state being tested.

- `table-and-wal`: an obsolete sessions value is in a compressed table; a newer
  saved window exists only in the write-ahead log.
- `deleted`: the table's sessions value is superseded by a WAL deletion.
- `empty`: the table's sessions value is superseded by an explicit empty object.
- `table-only`: a second compaction moves the current value into a table with an empty WAL.
- `fragmented`: an 80,000-character synthetic value spans multiple WAL fragments.

The producer emits matching `*.expected.json`; `storage.sha256.json` freezes file
hashes. URLs use `example.invalid`. These are synthetic database fixtures, not captured
Brave profiles or official extension exports. They establish binary interoperability
targets, not full installed-extension compatibility. The reader checkpoint adds parser,
fragmentation and adversarial tests; further multi-table compatibility fixtures remain.

Regenerate using the pinned source with Snappy development headers/library available:

```sh
cmake -S /path/to/pinned-leveldb -B /tmp/tmp-leveldb-build \
  -DLEVELDB_BUILD_TESTS=OFF -DLEVELDB_BUILD_BENCHMARKS=OFF -DCMAKE_BUILD_TYPE=Release
cmake --build /tmp/tmp-leveldb-build -j 4
g++ -std=c++17 tests/Core.Tests/Fixtures/TabManagerPlus/generate-storage.cpp \
  -I/path/to/pinned-leveldb/include /tmp/tmp-leveldb-build/libleveldb.a \
  -lsnappy -lpthread -o /tmp/tmp-generate-storage
/tmp/tmp-generate-storage /tmp/tmp-new-storage-fixtures
```

The producer deliberately refuses an existing output directory. Generated manifest
identities/timing-dependent layout can vary between runs; check semantic results rather
than expecting regenerated hashes to match frozen bytes. Upstream `leveldbutil dump`
independently confirmed the newer value, deletion and explicit-empty WAL operations,
and the manifest's live-table reference. See the reader checkpoint for subsequent
.NET test results; fixture generation alone does not establish application compatibility.
