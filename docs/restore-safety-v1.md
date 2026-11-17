# Restore safety snapshot and journal v1

This is an application-owned recovery contract, separate from `.bravebackup` versions 1–8.
It does not change those versions or define a Brave/Chromium format. The phase-3 engine
is not connected to profile writers or the UI; only synthetic adapters exercise writes.

Both artifacts require the existing authenticated BBKENC1 envelope with a nonempty safety
password supplied independently from the source archive password. No plaintext artifact
is written by the engine. Each encoded plaintext buffer is owned and cleared after use.
The storage adapter must privately create, flush, atomically publish and read back each
artifact; its implementation is part of a later production integration checkpoint.

## Snapshot plaintext

All integers are little-endian. GUIDs use .NET `Guid.ToByteArray()` order. Fields:

- Eight bytes `BBKSF001`, operation GUID, int32 entry count (1–10,000).
- Per entry: nonempty unique mutation GUID, installation GUID, profile GUID;
  int32 UTF-8 category byte length (1–64), then lowercase ASCII category ID bytes
  (`a-z`, `0-9`, `-`); byte original-existed flag (0/1);
  int32 content length and original bytes (at most 64 MiB);
  int32 opaque adapter metadata length and bytes (at most 64 KiB).
- No trailing bytes. Original absence requires both lengths zero.

The complete plaintext snapshot is at most 128 MiB. Metadata is opaque to Core; an adapter
must establish its own recoverable metadata contract rather than silently discard modes,
ACLs or other required metadata. Snapshot IDs refer to the frozen in-memory plan, never
infer filesystem paths from archived profile names.

## Journal plaintext

Eight bytes `BBKJR001`, operation GUID, int64 monotonic revision, int32 transaction-state
value, int32 entry count, then each mutation GUID and int32 entry-state value in frozen
plan order. Transaction states: New=0, Prepared=1, Running=2, Paused=3, Completed=4,
RollingBack=5, RolledBack=6, Failed=7, RecoveryIncomplete=8. Entry states:
Untouched=0, Intent=1, Applied=2, Recovered=3. Unknown versions/states, duplicate/empty IDs,
negative revisions, mismatched counts/order and trailing bytes are invalid.

Snapshot publication and authenticated decode/comparison must finish before Prepared.
Persist the complete encrypted journal before each destination write (Intent), and again
after publication (Applied). Original safety state is never refreshed on resume. Track
intent even when a write reports failure after publication. During rollback, only original
or expected applied state is accepted; unknown contents/metadata are a conflict and are
not overwritten. Persist recovery results, including partial failures, without success claims.

## Boundaries and limits

The engine owns frozen mutation bytes, originals and metadata, and clears them on disposal.
The storage adapter owns path identity/access checks, source fingerprints, stopped-browser
checks, per-file atomic publication and metadata fidelity. These must be rechecked on
prepare, every write, resume and rollback. Pause is requested between atomic operations;
no cancellation token interrupts a destination publication once it has begun.

There is no cross-file atomicity, power-loss/directory-fsync guarantee or automatic crash
resume/recovery in this checkpoint. The journal records intent/results for diagnosis and
future recovery integration; it is not a complete persisted executable plan. Destination
mapping and restore payloads remain in memory. Do not enable real-profile writes until
production adapters, recovery/lifetime handling and category writers are tested.

## Implementation checkpoint

The snapshot/journal codecs and initial Core transaction coordinator are present. Three
focused codec/ownership tests pass; the solution compiled successfully. The initial codec checkpoint was followed by the synthetic transaction tests below;
production-store integration is still pending.
This coordinator is not production-ready and is not connected to Restore execution.
Next: exercise an adapter over synthetic directories, including encrypted artifact readback,
write-intent persistence, original absence, pause/resume, rollback conflicts and partial failure.

## Synthetic transaction tests

The file-only test adapter in `RestoreTransactionTests` uses unique temporary directories,
private encrypted artifact staging with flush and atomic rename, and atomic synthetic
file publication/deletion. It is not a production profile adapter: its empty metadata
contract does not establish Windows ACL, Unix mode, directory, link or path-race recovery.

Transaction tests cover verified safety/journal readback before destination writes, a
pause after the first file of an unfinished category, declining rollback without resuming,
explicit resume with unchanged original safety bytes, and recovery of original absence.
They also cover unsafe/changed resume contexts, failure after file publication while the
journal still records intent, rollback conflict/partial recovery/retry, failed journal
publication, preparation cancellation and changed safety artifacts after preparation.
The coordinator reauthenticates the saved safety artifact before execution/resumption and
rollback, verifies target publication readback, and rechecks pause requests after journaling
before starting a target publication. A production store must additionally preserve and
revalidate recovery artifact identity throughout writes, not merely at operation entry.

No UI/profile adapter is connected. Remaining phase-3 work includes production-grade
filesystem identity/metadata/permission handling, bounded artifact I/O, safe lifecycle and
recovery integration, and additional injected failures. Actual restore actions remain
unavailable; the existing Backup and Export paths are unchanged.

Validation: the full solution run passed 1,046 tests with six environment-dependent
native tests skipped. No real browser profile or credential store was modified.

## Additional boundary coverage

The focused transaction suite now passes 12 cases. Four added cases verify pausing during
intent-journal publication before a target write begins, detecting a publication that
returns without changing the target, recovering after rollback writes fail, and blocking
rollback writes while context verification fails. Rollback requires an explicit retry
when those failures are resolved. These additions use synthetic directories only; no
production adapter or UI execution was enabled. The full solution suite was not rerun
for this test-only increment; its previous 1,046-pass result remains the last full run.

## Encrypted artifact I/O component

`RestoreArtifactStorage.Publish/Read` now provides bounded artifact I/O for later adapter
integration. Publication requires encrypted framing, an existing directory with no linked
ancestors, private staging permissions, flush and atomic rename. Existing files require
explicit replacement. Reads reject plaintext, oversized/special/linked files and changed
lengths. Authentication is deliberately performed by the transaction coordinator; framing
alone is not proof of valid ciphertext. Two focused tests pass, including Linux private
permissions and plaintext/link rejection. No profile writer is connected.

This helper is not a complete production transaction store: callers must establish
artifact-path authorization outside browser/source data, exclusive ownership, physical
filesystem identity and race-safe directory/target handling. Type checks do not prevent a
same-type concurrent replacement or ancestor race. Directory durability across power loss
is not established. Do not enable restoration on the strength of this component alone.

Artifact replacement now fingerprints bounded encrypted content before staging and again
before publication to reject observed same-type content changes. Explicit replacement
cannot clobber a plaintext or directory destination. Failed bounded reads clear their
allocated buffers. All three focused artifact-storage tests pass. These checks still
leave the final check/rename and ancestor races described above; physical identity and
handle-relative production I/O remain pending, and restore execution is not enabled.

## Frozen artifact-path authorization

`RestoreArtifactScope` freezes caller-supplied browser roots, source archive and an operation
GUID. It derives distinct safety/journal filenames in an existing chosen folder, rejects
browser-root and source collisions, and repeats destination/link checks before each I/O.
Construction is write-free. Safety publication never replaces an existing artifact; only
journal revisions may replace their fixed journal path. Two focused scope tests pass.
The wrapper still requires complete custom roots from discovery and does not solve bind
mount aliases, physical directory identity or final path races. No profile writer is enabled.
