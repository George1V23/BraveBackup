# Password database snapshot checkpoint

Implemented 2026-11-17 under the accepted [Passwords scope](passwords-backup-proposal.md).
This is an encrypted-source capture and schema-validation component. It does **not**
decrypt passwords, access OS keys, export CSV, create password archives or enable the
Passwords UI category. Existing bookmark/category/archive behavior remains unchanged.

## Capture and source safety

`BravePasswordSnapshot.Read(profile, observeBrowserState, token)` reads supported locally
present `Login Data` and `Login Data For Account` stores on explicit invocation. It is
not called during discovery, navigation or category selection. No real user profile or
keyring was accessed to implement or test this checkpoint.

The initial strategy requires Brave observed closed. Running, unknown, failed or invalid
process observations return `passwords-close-brave`, requesting close-and-retry. This is
the accepted fallback while source-write-free consistent **live** capture is unproven;
it does not replace the requirement to allow live capture when a compliant path exists.
The default observer checks Brave name variants; a host can supply its controlled observer
for custom executable names, following the existing process-probe pattern.

Only safe regular source files are opened through Platform's read-only file helper;
every ancestor must be an ordinary directory. Known database, `-wal`, `-shm` and `-journal`
files are captured into memory, with sizes/timestamps and byte hashes rechecked before
returning. SQLite never receives a source filename: its only connection is `:memory:`.
The WAL fixture test verifies all source bytes, including shared memory, remain unchanged.
There are no temporary database files, recovery writes or decrypted credential staging.

Each file is limited to 64 MiB; total captured files to 128 MiB across the two known
families. Missing stores are not empty collections. Orphaned companions, links, special
files, source changes and resource-limit failures block capture. Nonempty rollback
journals are explicitly unsupported in this first slice, including potentially inactive
PERSIST journals; the app never attempts recovery against the original source.

`VerifyUnchanged()` repeats source-set/stamp/hash checks and stopped-browser observation.
Future archive code must call it immediately before publication. Observation and filesystem
checks do not prevent changes after the final check or defeat process-visibility limits.
`Dispose()` clears captured bytes and owned encrypted-value buffers, drops metadata and
prevents subsequent owner access. Managed strings or caller-retained metadata cannot be
securely erased; callers must treat returned collections/buffers as read-only.

## Documented WAL image construction in Core

`SqliteSnapshotImage.Create(databaseBytes, walBytes, token)` accepts already stable
captured input, returning an owned database image. It is not a live filesystem-snapshot
API. Core remains independent of native libraries, OS APIs and NuGet packages.

The implementation follows [SQLite's database/WAL format](https://sqlite.org/fileformat2.html#walformat):
page-size/header validation, WAL magic/version, both checksum byte orders, matching salts,
rolling header/frame checksums and commit sizes. It applies page versions only through
the last complete commit, drops valid uncommitted tail frames, handles final file size
and rejects missing expansion pages. Inputs are not modified.

Resource limits are 64 MiB per database/WAL/image and 100,000 WAL frames. Truncated,
checksum-damaged, salt-mismatched or stale recycled WAL tails fail conservatively rather
than being skipped. This is stricter than SQLite's normal recovery tolerance; sources
outside this tested subset must use a future explicit fallback rather than stale success.
No B-tree/SQL record format is invented: SQLite itself reads the reconstructed image.

## Native memory ownership in Platform

`SqliteMemoryImage` owns a private native copy, with padding recommended by SQLite for
untrusted input. Its read-only `sqlite3_deserialize` connection is configured with
trusted-schema off, memory-only temporary storage, query-only mode, no attached databases,
bounded SQLite limits and a cancellation/work-budget progress callback. The progress
callback stops execution after approximately 100 million VM instructions.

SQLite documents that deserialized WAL-mode headers need private-copy bytes 18/19 set
to rollback mode; only this memory copy is normalized. See the
[deserialize API](https://sqlite.org/c3ref/deserialize.html). Neither original files nor
the caller's image are changed. Connection pooling is disabled. Dispose all caller-owned
commands/readers before disposing the owner; SQLite is closed before its buffer is cleared
and released. `Connection` is for trusted, fixed read queries, not user-supplied SQL.

Explained dependencies added to **Platform**:

- `Microsoft.Data.Sqlite` **10.0.15** supplies standard managed SQLite connections/queries.
- `SQLitePCLRaw.bundle_e_sqlite3` **2.1.14** pins the native deployment/provider bundle
  rather than relying on an arbitrary system SQLite. The Linux x64 native library reports **SQLite 3.54.0**, and
  successfully reads the research fixtures generated with 3.53.4.

Direct native SQLite interop stays in Platform; Backup consumes the managed query API.
Core has no dependency on these packages. CLI still depends on Backup independently of
UI. The runtime assets need native Windows/platform validation before release; a successful
Linux build is not proof of those native executions. No Selenium or keyring package was added.

## Researched Brave schema and owned encrypted rows

`BravePasswordDatabase.Read(image, token)` checks SQLite integrity, required real tables,
metadata version **43**, compatible version **40**, selected column storage values, row
identity and note-parent relationships. Required views, virtual tables and triggers are
not supported substitutes. Missing required columns or invalid types fail. The pinned
source-defined schema and fixtures are described in [the research note](passwords-research.md).

The first tested eligible scheme is HTML (`scheme = 0`). Other eligible schemes fail
explicitly rather than being discarded. Blocklisted entries and federated-only entries
are counted separately. Both the empty federation string and serialized opaque-origin
marker `null` mean no federation; they must not hide a password row. An excluded entry
with a nonempty password blob is rejected as an untested combination. A valid empty
eligible password blob is retained. This stage does not guess a decryption provider from
a prefix or claim that every retained blob can be decrypted.

Returned `StoredPasswordDatabase.Rows` contain `StoredPasswordRow` source identity,
origin/realm, nullable username/display name, encrypted value and encrypted notes. Each
`StoredPasswordNote` preserves its source name, creation value and confidential flag.
Notes are not silently dropped when missing/corrupt; orphaned parents and duplicate
identities fail. No ciphertext is converted into a plaintext password or note here.

Rows and notes are bounded to 100,000 per database, with 100,000 total login records
across captured stores. UTF-8 text plus encrypted blobs is limited to 32 MiB per store;
individual text fields are limited to 1 Mi characters, and native SQLite's row/blob/text
limit is 4 MiB. Additional native statement/schema/execution limits apply. Captured bytes,
assembled image, native image, SQLite caches and row buffers coexist: these separate
budgets are not a single 128 MiB process-memory cap.

`StoredPasswordDatabase.Dispose()` clears its owned byte buffers and rejects further row
access. Row/note/database `ToString()` methods suppress contents to avoid accidental
debug-string exposure. Expected source/schema failures use fixed `BackupOperationException`
messages without source paths, SQL data or exception inner text. No report/log writes occur.

## Verification and remaining work

**593 solution tests pass** on Linux: 526 Core, 48 Brave, 13 Linux and 6 Windows-labelled.
Full solution, including independent CLI, builds with zero warnings/errors. There are
36 new snapshot tests covering:

- Windows-shaped GCM and Linux-shaped CBC encrypted records/notes, exclusion counts and empty stores;
- both local stores, WAL-only committed updates and unchanged database/WAL/shared-memory bytes;
- corruption, unsupported schema/schemes, invalid types, orphaned notes and view substitution;
- documented checksum byte orders, truncated/damaged frames and valid uncommitted tails;
- running/unknown/error process state, late starts, missing/linked/unsafe/oversized sources;
- same-size changes with restored timestamps, added companions, unsupported journals;
- read-only native memory, forbidden ATTACH, native cancellation, disposal and buffer clearing.

Tests operate on synthetic temporary profiles only. The production SQLite connection
never opens those profile paths either. No actual Windows key access, live-Brave capture
or real credential extraction is established by these tests.

Next: fixture-driven provider-aware crypto decoding, then verified OS lookup/unlock
adapters. Raw-file fallback consent, protected payload/CSV serialization, archive versioning
and UI integration remain pending. Until those are connected and tested, Passwords stays
explicitly unavailable; the existing v1–5 archive contracts did not change.
