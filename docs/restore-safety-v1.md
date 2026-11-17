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

## Linux directory identity primitive

`Platform.RecoveryDirectoryIdentity` retains a native directory handle and verifies that
reopening its authorized path still gives the same device/inode. Same-named directory
replacement and symbolic-link paths are rejected. The statx layout/mask follows the
[Linux UAPI definition](https://github.com/torvalds/linux/blob/master/include/uapi/linux/stat.h).
Two native Linux tests pass for replacement, links and disposed-handle rejection.
This primitive is not connected to artifact publication yet. Handle-relative I/O and
publication race handling remain pending; path rechecking alone is not a race-free write.
Windows implementation is pending and this primitive fails closed on unsupported hosts.
No restore/profile writes are enabled by adding it.

## Handle-relative Linux artifact operations

Linux artifact storage now opens children and stages/publishes/cleans them relative to a
retained directory handle (`openat`, `renameat2`, `unlinkat`). New-artifact publication uses
RENAME_NOREPLACE. Reads reject final links and nonregular files using the opened handle.
Directory identity is checked before/after operations; detected replacement fails rather
than redirecting operations to a new directory. Four native directory tests and five
artifact/scope tests pass. Sources: [openat](https://man7.org/linux/man-pages/man2/open.2.html),
[renameat2](https://man7.org/linux/man-pages/man2/rename.2.html).

This does not establish complete production race safety: opening the initial ancestor
chain, concurrent changes to child names/staging files, same-type replacement before an
overwriting rename, artifact ownership and directory durability still require protection.
Windows remains on the earlier path-based helper. No profile writer is enabled.

## Linux ancestor traversal

Initial directory acquisition now walks from `/` through directory-relative `openat`
handles, applying O_DIRECTORY/O_NOFOLLOW to every component. It no longer opens the final
absolute path after a separate ancestor check. The resulting handle is identity-verified
before returning. Six native tests now pass, including linked ancestors and attempted
child-name traversal. Existing absolute-path verification remains a detection check;
subsequent artifact I/O uses the retained handle.

Child-name/staging ownership and overwrite races, complete production adapter integration,
metadata recovery and Windows handle-based support remain unfinished. No browser-profile
restoration has been enabled.

Latest full regression run: 1,061 passed, six environment-dependent tests skipped
(987 Core, 48 Brave, 20 Linux and six Windows-labelled tests passed).

## Retained staging-file identity

Linux publication now keeps the staging file handle open through rename, compares its
device/inode and single-link status with the named staging entry before publication,
and compares the published entry with that same owned handle afterward. Cleanup does
not knowingly unlink a substituted staging entry. Child reads reject multiple-link files
as well as links/special files. Seven native directory tests and five artifact/scope tests
pass after this change. Tests cover normal replacement, linked child rejection and no
leftover staging under normal operation; adversarial mid-syscall substitution is not yet
covered by a deterministic test.

These are detection checks, not atomic compare-and-swap. Concurrent mutation of child
names between checks and rename/unlink, and exclusive operation-directory ownership,
remain pending. Existing Windows limitations and the prohibition on enabling profile
writers remain unchanged.

## Cooperating Linux writer serialization

`RecoveryDirectoryIdentity.AcquireExclusive` takes a nonblocking advisory directory lock
until handle disposal. Linux artifact publication acquires it before the final fingerprint
check, staging and rename. Initially missing children always use no-clobber publication,
even if the caller permits replacement, preventing an intervening new artifact from being
replaced. Eight native directory cases and six artifact/scope cases pass, including a
contender failing without staging writes and lock release after disposal.

The [flock contract](https://man7.org/linux/man-pages/man2/flock.2.html) is advisory: this
serializes cooperating publishers, not arbitrary same-user processes. It is currently a
per-publication lock, not an operation-lifetime recovery lease. Remote filesystem semantics,
noncooperating child replacement, metadata fidelity and Windows parity remain unresolved.
No restore execution has been enabled.

## Operation-lifetime Linux artifact lease

`RestoreArtifactLease` retains directory identity and its advisory lock across safety,
journal and readback calls, releasing it only on disposal without deleting artifacts.
It uses the frozen authorized scope and bounded encrypted handle-relative I/O. Initial
safety/journal creation never clobbers existing artifacts. Journal updates compare the
previous ciphertext hash; a newly acquired lease can inspect retained artifacts but does
not silently adopt/overwrite an existing journal. Two focused lease tests pass for lock
contention, retained artifacts, repeated journal publication, directory replacement and
disposal. No production target adapter or UI execution is connected yet. Windows parity,
metadata recovery and noncooperating filesystem mutation remain pending.

## Leased transaction integration

Two synthetic integration cases now connect the real Linux `RestoreArtifactLease` to the
Core transaction coordinator. A test-only target adapter writes ordinary fixture files;
it is not a production profile writer. Tests verify exclusive ownership while paused,
explicit resume, unchanged encrypted safety originals, rollback to original content,
retained artifacts after disposal/reacquisition, and external journal edits blocking
further transaction writes. Both integration cases pass. Browser-profile writes, platform
metadata fidelity, Windows parity and production recovery/lifetime handling remain pending.

Latest full regression run after lease integration: 1,068 passed, six environment-dependent
tests skipped (992 Core, 48 Brave, 22 Linux and six Windows-labelled tests passed).

## Linux ordinary permission/mtime metadata

`Platform.LinuxRecoveryMetadata` captures/applies ordinary permission bits and last-write
UTC ticks through an already authorized file handle. Its opaque v1 metadata is exactly
20 bytes: `BBKLM001`, little-endian int32 mode (only 0777 bits permitted), and little-endian
int64 .NET UTC ticks. Unknown framing/length, special mode bits and invalid ticks are
rejected before applying metadata; exact capture readback verifies application. A native
synthetic test passes for permission/mtime roundtrip and malformed-input rejection.

The handle-based APIs are documented in
[GetUnixFileMode](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.getunixfilemode?view=net-10.0)
and [SetLastWriteTimeUtc](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.setlastwritetimeutc?view=net-10.0).
This component is not connected to profile publication. It does not capture UID/GID, ACLs,
xattrs, creation/access times, filesystem flags or directory metadata. A production target
adapter must establish its supported metadata policy and reject cases requiring unsupported
recovery fidelity, rather than claiming this codec alone provides complete rollback.
Windows metadata and production target integration remain pending.

## Unsupported extended-metadata gate

Linux metadata capture/application now calls handle-based `flistxattr` with a zero-size
buffer before reading/applying supported metadata. Any visible extended attributes or an
inspection error blocks the operation without reading attribute names/values. Two native
metadata tests pass, including an explicitly added synthetic user attribute that prevents
both capture and application without changing mode/mtime.

The [API contract](https://man7.org/linux/man-pages/man2/listxattr.2.html) permits attributes
inaccessible to the caller to be omitted. Therefore an empty result is not proof that no
privileged/hidden metadata exists. This gate is conservative evidence only; it does not
establish full ACL/xattr fidelity or production target eligibility. Windows and production
metadata recovery integration remain pending; no profile writes are enabled.

## Linux effective-ownership restriction

Metadata capture/application now requires handle-reported UID/GID to match the current
effective UID/GID before any supported metadata changes. Unavailable ownership fails
closed; no chown, privilege elevation or fallback occurs. This intentionally rejects
other-group files even when supplementary group access exists. Five focused metadata
cases pass: native mode/mtime and extended-attribute checks plus matching/foreign owner
and group policy cases. Platform exposes the pure policy internally to Linux.Tests only.
The native ownership fields follow the Linux statx layout already referenced above.
This restriction is not a persisted ownership recovery contract; exact ownership restoration
and context continuity across process restarts remain pending. No profile writer is enabled.

## Persisted Linux ownership metadata v2

New Linux metadata captures emit `BBKLM002`: the previous mode/ticks fields followed by
little-endian uint32 UID and GID, for exactly 28 bytes. Apply requires saved identities
to match both the authorized destination handle and effective process identities before
mode/time changes. It never changes ownership. Exact readback includes UID/GID. Six focused
metadata tests pass, including tampered saved ownership rejection without changes.

The previous 20-byte `BBKLM001` metadata remains readable/applicable under the earlier
current-effective-owner restriction, but cannot prove historical ownership because those
fields were absent. No `.bravebackup` or safety snapshot container version changes.
ACL/xattr fidelity, Windows metadata and production target integration remain pending.

## Linux file eligibility

Metadata capture/application now requires handle-reported regular-file type, exactly one
hard link and available ownership/link/type fields. Directories and multiply linked files
are not eligible for this file-only recovery path. Seven focused native metadata cases
pass; the new hard-link test verifies rejection of capture/application and unchanged
metadata after the extra link is removed. This remains a point-in-time check; production
publication must still guard identity and concurrent link/metadata changes.

Latest full regression run (2026-11-18): 1,075 passed, six environment-dependent tests
skipped (992 Core, 48 Brave, 29 Linux and six Windows-labelled tests passed). Production
target integration, Windows recovery support and full metadata fidelity remain pending.
