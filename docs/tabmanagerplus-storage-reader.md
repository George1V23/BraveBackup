# TabManagerPlus offline storage reader

Implemented parser checkpoint, 2026-11-17. This is a storage component, not yet a working
TabManagerPlus backup category. No profile is opened, no package was added and archive
versions 1–4 are unchanged.

## Project and API

`src/Extensions/TabManagerPlus/TabManagerPlus.csproj` targets .NET 10 with no project or
NuGet dependencies. Backup now references it for the separate
[saved-window capture component](tabmanagerplus-capture.md); archive/UI wiring remains pending. Its namespace is
`BraveBackup.Extensions.TabManagerPlus`.

`OfflineLevelDb.ReadValue(files, key, cancellationToken)` synchronously reads a map of
direct file names to captured `ReadOnlyMemory<byte>` values. The caller must keep the
map and bytes unchanged throughout the call. No filesystem, native database or network
operation occurs. The returned array is an owned copy.

- Present keys return raw value bytes; JSON/saved-window validation belongs to the next layer.
- Missing/deleted keys return `null`. A present `{}` value remains bytes, distinguishing
  an explicit empty collection from unavailable data.
- Malformed, unsupported or over-budget snapshots throw `InvalidDataException` with a
  fixed message containing no source paths or values.
- Cancellation throws `OperationCanceledException` at bounded parsing checkpoints;
  invalid API arguments use standard argument exceptions.

## Decoding and safety boundaries

The private reader validates CURRENT and replays its manifest to identify live tables,
log numbers, comparator and sequence metadata. It rejects unknown tags, non-bytewise
comparators, duplicate table identities and overlapping internal-key ranges within
nonzero levels. Unreferenced tables and obsolete WALs do not influence lookup.

Each live table's size, magic, footer handles, consumed-block CRC32C, restart framing,
internal ordering, sequence numbers and manifest key bounds are checked. Consumed blocks
must not overlap. Both `.ldb` and `.sst` suffixes work; ambiguous identities fail.
Optional filter contents are unused and are not decoded or authenticated; metadata/index
entry framing is checked. Blocks support uncompressed and raw Snappy data; output lengths,
literals and backreferences are bounded. Other compression, including Zstandard, fails.

Eligible WALs are replayed in file-number order, using CRC-checked full/fragmented records,
validated batch counts/operations and sequence ranges. Current and nonzero previous WALs
must exist. Replayed/overlapping nonempty WAL sequence ranges fail. The highest-sequence
value or tombstone wins; conflicting equal-sequence values for the requested key fail.

Incomplete records fail instead of invoking repair. Empty WALs and format-defined zero
padding are valid; the reader cannot distinguish valid empty bytes from a file externally
truncated to that same valid state. Future capture consistency checks remain essential.
CRC32C is accidental-corruption detection, not attacker authentication. Source definitions
are in [the research note](tabmanagerplus-research.md) and the
[upstream Snappy format](https://github.com/google/snappy/blob/main/format_description.txt).

## Limits

| Resource                                                     | Limit                                          |
|--------------------------------------------------------------|------------------------------------------------|
| Captured files / live tables                                 | 4,096 each                                     |
| Aggregate captured bytes                                     | 256 MiB                                        |
| Logical fragmented record / decoded block / individual value | 32 MiB                                         |
| Aggregate decoded block bytes                                | 256 MiB                                        |
| Reconstructed table-key bytes                                | 256 MiB                                        |
| Table entries across consumed blocks                         | 250,000                                        |
| Parsing work units                                           | 4,000,000, including block-overlap comparisons |
| Internal key                                                 | 64 KiB                                         |
| Batch operation count                                        | 1,000,000, also subject to aggregate work      |
| Requested key                                                | 4,096 UTF-16 code units                        |

These are conservative compatibility boundaries. The caller must bound capture before
allocating inputs. Input, decoded blocks and reconstructed keys coexist: the separate
budgets must not be described as a single 256 MiB process-memory cap.

## Verification and remaining work

36 parser tests pass. Frozen databases were generated and verified by upstream C++
LevelDB independently of the C# reader. They cover newer WAL values, deletions, explicit
emptiness, table-only values after compaction and multi-block fragmented WAL values.
Original frozen fixtures remain byte-for-byte unchanged.

Other cases cover input/result independence, missing live files, obsolete-file ignoring,
checksum damage, every nonempty truncation of the original manifest/table/WAL, malformed
CURRENT, `.sst` aliases/ambiguity, cancellation, count/byte limits and valid-checksum
structural failures in batches, manifest tags, fragments, Snappy and compression types.

Solution validation on Linux: **453 tests passed** (386 Core, 48 Brave, 13 Linux,
6 Windows-labelled). Full solution including CLI builds with zero warnings/errors.
These are not native Windows or real installed-Brave end-to-end results.

The subsequent capture checkpoint implements store-package research, typed saved-window
parsing and stable stopped-browser capture with additional tests. Archive capability/version
validation, UI feedback and more multi-table/manifest-transition coverage remain pending.
The UI still blocks this category explicitly until integration is complete.
