# Restore safety snapshot and journal v1

This is an application-owned recovery contract, separate from `.bravebackup` versions 1–8.
It does not change those versions or define a Brave/Chromium format. The
[Linux Bookmarks workflow](restore-bookmarks-execution.md) uses this contract.
The Linux session also supports [an opaque directory image](restore-directory-exchange.md)
for an existing extension Local store. The implementation checkpoints below record
earlier engine and native adapter testing.

Both artifacts require the existing authenticated BBKENC1 envelope with a nonempty safety
password supplied independently from the source archive password. No plaintext artifact
is written by the engine. Each encoded plaintext buffer is owned and cleared after use.
The storage adapter must privately create, flush, atomically publish and read back each
artifact. The Linux restore adapter uses a retained recovery directory lease and flushes
the directory after publishing each safety snapshot or journal revision.

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
checks, atomic publication per target and metadata fidelity. These must be rechecked on
prepare, every write, resume and rollback. Pause is requested between atomic operations;
no cancellation token interrupts a destination publication once it has begun.

Multi-target restoration progresses one atomic target at a time. Directory flushing
supports persistence of Linux artifact publication. Power-loss recovery and automatic
restart recovery remain outside the current workflow. The journal records intent and
results for diagnosis. Destination mappings and replacement payloads remain in memory.

## Inspecting persisted artifacts after restart

`RestoreArtifactStorage.Inspect` reads the selected safety backup and journal
through the bounded regular-file reader. It authenticates both files with the
safety password and verifies their operation identity and complete ordered
mutation list. The result contains logical installation, profile, category
and mutation IDs, original presence, and recorded write state. The method
clears decoded buffers and does not return original bytes, opaque metadata
or destination paths. Inspection does not publish files or change either
artifact.

The journal state describes the last supplied authenticated revision. V1
contains no persistent proof that it is the newest revision or that it
binds to the exact safety contents. It also has no destination mapping or
expected post-write image fingerprints. In-flight and applied entries cannot
be reconciled against current profile files with those two artifacts alone.

A future restart recovery format needs to bind the exact safety snapshot to
the operation and its destination mapping, and carry expected original and
replacement fingerprints for each target. The recovery workflow must verify
the selected operation, stopped-browser state, target and ancestor identities,
and current image before any write. If current state matches neither image,
it must report a conflict. The v1 reader remains useful for inspection and
neither changes the backup archive version nor grants write access.

Tests cover authenticated matching files, wrong passwords, tampering, wrong
operation IDs, reordered entries, cancellation and unchanged artifact bytes.

## Evidence for restart rollback

`RestoreRecoveryEvidence` computes SHA-256 over the following frame. The
fingerprint representation is 64 lowercase hexadecimal characters.

| Field | Encoding |
| --- | --- |
| Domain | Eight ASCII bytes `BBKIMG01` |
| Existence | One byte, 0 for absent and 1 for present |
| Content length | 32-bit little-endian integer |
| Adapter metadata length | 32-bit little-endian integer |
| Payload | Content bytes followed by metadata bytes |

An absent target and an existing empty target have different fingerprints.
The two lengths separate content from metadata. For an `Intent` or `Applied`
entry, a current image matching the original needs no write. A current image
matching only the expected replacement is eligible for rollback after the
adapter verifies context and the user confirms. Any other image is a
conflict. For restart recovery, an `Untouched` or `Recovered` entry can
be skipped only if its image still matches the original. Otherwise the
observation conflicts with the recorded state, which may come from an older
authenticated journal revision. The existing in-memory rollback uses exact
byte comparisons for touched entries and retains its authoritative state
for entries it can skip.

A persisted recovery record will need to authenticate these fields together
before any destination write:

- Operation ID and an exact safety-snapshot fingerprint, plus the journal's
  ordered mutation IDs. Journal revisions must remain bound to the same
  record through recovery and retry.
- For each mutation, the independently reviewed destination installation and
  profile, the exact target name, the adapter type, and original and
  replacement image fingerprints. The safety snapshot remains the source of
  original bytes.
- Platform evidence sufficient to reopen the same destination and its parent
  chain. The adapter must check path boundaries, retained directory identity,
  stopped-browser state and category prerequisites before publishing. A path
  or image fingerprint alone cannot establish directory identity.

The first restart operation should support explicit rollback of verified
targets. Forward resume additionally requires reproducible replacement
bytes and source revalidation. Older v1 artifacts remain inspectable. The fingerprint format applies to active and reopened recovery
without changing source archive versions.

## Restart record and bound journal codecs

The restart record is a separate application-owned artifact. It and the bound
journal use the authenticated BBKENC1 envelope with the safety password. The
safety snapshot remains BBKSF001. Source backup formats 1–8 and the v1 recovery
inspection path remain compatible.

The restart record plaintext starts with eight ASCII bytes `BBKRP001`, the
operation GUID, the raw 32-byte SHA-256 of the exact safety plaintext, and a
32-bit little-endian entry count from 1 through 10,000. Each entry contains
the following fields in order.

1. Mutation GUID and one-byte adapter kind, 1 for a file and 2 for a flat
   directory.
2. Installation data root, profile directory, profile-relative target and
   opaque directory identity evidence. Each is strict UTF-8 with a 32-bit
   little-endian length, between 1 and 4,096 bytes for paths and 1 and 8,192
   bytes for directory evidence. Relative names use slash-separated
   components. Empty, current-directory and parent-directory components are
   rejected.
3. Raw 32-byte original and replacement image fingerprints. GUIDs use the
   same byte order as the v1 safety snapshot.

The plaintext is limited to 16 MiB. Duplicate mutation IDs, duplicate
profile/target strings, unsupported kinds and trailing bytes are rejected.
Path syntax and identity must also be validated by the native adapter,
including aliasing and the meaning of its opaque directory evidence.

The bound journal plaintext is eight ASCII bytes `BBKJR002`, the raw
32-byte SHA-256 of the exact restart record plaintext and the full v1
journal body including its `BBKJR001` header. Each revision must name the
same ordered targets. Inspection authenticates the three envelopes,
validates both exact hash bindings, and rechecks the operation ID, target
order and original image fingerprints. Authentication does not establish
that the journal is the newest revision. The recovery adapter must still
reconcile every reported target against the current original and replacement
fingerprints.

### Publication in Linux Restore

Final review lists the safety backup, journal, and
`<operation-id>.restart.encrypted` in the selected recovery folder. Preparing
the transaction publishes and verifies the safety image first, publishes the
restart record once under the same directory lease, authenticates its
readback, then publishes the bound Prepared journal. Failure leaves any
already-published artifacts available without changing the profile. The
active transaction verifies the record before forward and rollback preflight.
The Linux adapter compares its retained encrypted digest when saving journal
revisions and again at profile publication boundaries. Stores without restart
evidence still emit v1 journals.

Linux evidence starts with `linux-chain-v1:complete:` or
`linux-chain-v1:partial:` followed by a lowercase SHA-256 digest. The digest
covers the ASCII domain `linux-directory-chain-v1`, the boot ID in .NET GUID
byte order, then a 40-byte frame for each directory from the filesystem root
through the target's parent. Each frame stores, in little-endian order, the
32-bit availability mask for birth time and unique mount ID, 32-bit device major
and minor numbers, 64-bit inode, 64-bit signed birth seconds, 32-bit birth
nanoseconds and 64-bit mount ID. For a flat-directory target, the leaf is
excluded because publication exchanges it. The no-link walk must end at the
same retained parent. [Linux statx reference](https://man7.org/linux/man-pages/man2/statx.2.html).

A reopened parent verifies only complete same-boot evidence. Profiles on
filesystems without birth times or kernels without unique mount IDs can use
in-process restore and rollback. Their records carry partial evidence, which cannot be used to
verify an identity after restart. These identifiers detect observed
directory replacement and do not replace retained handles or publication
checks against concurrent mutation.

`RestoreArtifactStorage.InspectRestart` authenticates the three selected
files without accessing the profile. Tests cover blocked record publication,
record changes before and during publication, reopening the same directory
chain, and replacement of an ancestor while the leaf survives. The application
uses the explicit restart rollback workflow below.

## Roll back after restarting BraveBackup

On Linux, open **Restore** and expand **Roll back a previous restore**. Close
Brave and choose the operation's `.restart.encrypted` file. Keep its
`.safety.encrypted` and `.journal.encrypted` files with the same operation ID
in the same folder. Enter the safety-backup password and select **Review
previous rollback**. The original backup archive is unnecessary.

Review authenticates all three files and shows the exact target paths and
their observed states. Destinations must match currently detected Brave
profiles, the supported category paths, and the authenticated directory
chain for the same system boot. A profile with incomplete directory evidence
cannot be reopened. The review itself changes neither the profile nor the
artifacts.

**Confirm previous rollback** reopens the files and verifies that the review
still matches. It locks the recovery directory, checks Brave's process state,
and records rollback intent before publishing original images. Each target
must match the expected image, and incompatible targets are preserved.
A nonempty password SQLite sidecar blocks that target. Eligible targets can
recover independently, and partial recovery is recorded in the bound journal.
Reopen the review to retry an incomplete operation. The safety backup and
restart record remain unchanged.

This workflow supports app restart within the same Linux boot. Forward resume
after restart, reboot recovery, and Windows profile recovery remain unavailable.
V1 safety/journal pairs remain readable by the inspection backend.

Synthetic tests exercise a fresh Restore view model, changed targets between
review and confirmation, missing source archives, file and flat-directory
rollback, older journal revisions and password sidecar conflicts. The native
Avalonia smoke test exercises the review and confirm controls on disposable
profiles.

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

## Native metadata transaction integration

A Linux integration test now combines the real encrypted artifact lease, Core coordinator
and native metadata codec through a test-only synthetic target adapter. It passes a
complete prepare/apply/rollback cycle with two file mutations, verifying original bytes,
exact persisted permission/mtime/UID/GID metadata, original absence and retained encrypted
artifacts. Staging metadata is applied before rename and verified by the coordinator's
publication readback. The adapter deliberately does not implement production path-race,
directory metadata or source/browser-state policy and is not used by the application.

This is one focused integration case; the previous full run remains 1,075 passed with six
skipped. Phase 3 remains open for production target adapters, Windows support, complete
supported-metadata policy and lifecycle/recovery integration. No profile writes are enabled.

## Read-only Linux target capture

`Backup.LinuxRestoreFileCapture.Read` captures one authorized directory child as original
bytes/metadata or original absence. It uses Platform retained directory handles and native
metadata eligibility checks, rejects linked/special children and traversal names, and bounds
each file to 64 MiB. Two full captures compare bytes and metadata; per-read metadata/length
checks and cancellation reject observed changes. Temporary buffers are cleared, and the
returned image owns independent bytes. A native synthetic test passes for contents,
metadata, absence, link/traversal rejection and pre-cancellation with no target writes.

The caller must establish explicit operation scope and closed-browser state. Equal bytes
and metadata do not prove unchanged inode identity, nor prevent changes after capture;
the future writer must retain/revalidate physical target identity before publication.
Original-absence checks are observations, not an atomic creation reservation. This is a
read-side production component only; no application transaction adapter or target writer
has been connected. Windows capture and complete publication/recovery handling remain pending.

## Metadata-aware no-clobber publication

`RecoveryDirectoryIdentity.PublishNewRecoveryFile` stages a new Linux child privately,
applies/verifies supported metadata through its open handle, flushes, verifies content
readback and staging identity, and publishes with RENAME_NOREPLACE. Existing targets are
never overwritten. Nine native directory tests now pass, including exact metadata on
publication, preservation of an existing target and normal staging cleanup.

This is a low-level new-file primitive, not a complete transaction writer. Authorization,
closed-browser checks, existing-target compare/publication, deletion for original absence,
concurrent same-user mutation handling and Windows support remain pending. No application
restore command is connected to this primitive.

## Windows directory identity primitive (native validation pending)

`WindowsRecoveryDirectoryIdentity` opens a retained directory handle without delete
sharing, checks directory/reparse attributes through that handle, and compares volume plus
128-bit file ID on verification. It does not rely on the older 64-bit file index that may
be insufficient on ReFS. Sources:
[CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew),
[FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info).

The Windows test project compiles and its existing six nonnative tests pass on Linux;
three native cases, including the new rename/retention case, are explicitly skipped.
No native Windows behavior has been verified here. The primitive is not connected to
artifact storage or target publication. Ancestor-handle retention, child operations,
exclusive recovery ownership and metadata/ACL fidelity remain pending. No restore
capability is enabled by this addition.

## Windows ancestor-handle retention (native validation pending)

`WindowsRecoveryDirectoryChain` opens local-drive ancestors root-to-leaf and retains all
identity handles without delete sharing, then revalidates each. Disposal releases them
leaf-to-root without deleting artifacts. UNC/device paths fail closed. A Windows-only
parent-rename/disposal test was added. The Windows project compiles; six nonnative tests
pass and four native cases are explicitly skipped on this Linux host. Native behavior,
child-file publication, operation ownership and ACL recovery remain unverified/pending.
The chain is not connected to artifact storage or target writers; no restore is enabled.

## Windows metadata capture prototype (native validation pending)

`WindowsRecoveryMetadata.Capture` records creation/write/access UTC ticks and the
handle-based owner/group/DACL security descriptor in opaque `BBKWM001` metadata: eight
magic bytes, three little-endian int64 tick fields, int32 descriptor length and descriptor
bytes, bounded to 64 KiB. Native descriptor memory is released with LocalFree. Source:
[GetSecurityInfo](https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-getsecurityinfo).

The Windows project compiles; six nonnative tests pass and five native cases are skipped
on Linux, including the new synthetic metadata-capture test. No native capture behavior
has been verified. This prototype does not capture SACL, alternate streams, EAs or full
filesystem metadata, does not establish target file eligibility, and has no apply or
publication path. It is disconnected from restore execution pending native validation,
metadata policy and production adapter integration.

## Windows opaque metadata framing validation

Capture now requires the native security descriptor to advertise self-relative form before
copying it. `WindowsRecoveryMetadata.Validate` checks exact magic/length, all timestamp
ranges, descriptor revision/self-relative flag and aligned in-bounds owner/group/SACL/DACL
offsets. Seven new cross-platform framing tests pass on Linux; the Windows-labelled project
now passes 13 nonnative cases with five native cases skipped. These checks do not validate
the full SID/ACL structures or authorize applying them. Native Windows capture and ACL
application remain unverified/unimplemented; no profile writer is enabled.

## Windows SID/ACL structural bounds

Cross-platform metadata validation now bounds SID subauthority arrays, ACL headers and
sizes, declared ACE counts/lengths and partially overlapping descriptor regions. Shared
identical SID regions remain permitted. Eight Windows-format tests pass on Linux; the
Windows-labelled project totals 14 passed and five native skips. Sources:
[SID](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-sid),
[ACL](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-acl),
[ACE_HEADER](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-ace_header).
These are structural bounds only: ACE payload semantics, native ACL validity and recovery
authorization are not established. Windows application/publication remains disabled.

## Reviewed safety settings UI

Restore now contains the approved mandatory encrypted safety-backup settings: existing
folder Browse/text/full-path display and separate masked password/confirmation fields.
Changing settings or choosing a folder creates no files. Source unlock passwords are
never copied into these fields. Source changes, leaving Restore and disposal clear the
safety secrets; pending picker results are invalidated. Folder validation checks recognized
browser roots/links, while final custom-root authorization remains the frozen operation's
responsibility. Final artifact filenames must be assigned and shown when an executable
plan is available. No safety creation/restore action is exposed yet.

Two focused view-model tests pass for independent password validation, clearing, no writes
and stale picker rejection. The solution build passes with zero warnings/errors, including
compiled Avalonia bindings. Native visual/picker validation of this new section remains
pending. Production transaction integration and Windows recovery remain unfinished.

## Native safety-settings validation

The Linux desktop runner now passes 47 checks. Added native checks verify both safety
password fields are masked, mismatch feedback renders, entered settings do not enable
restoration, and tab departure clears both safety secrets. Existing source-profile byte
checks also pass. See [safety settings screenshot](screenshots/restore-safety-settings.png).
The native folder picker was wired but not manually driven; Windows desktop validation
remains unavailable. All inputs were synthetic.

Latest full run: 1,088 passed, nine environment-dependent tests skipped (994 Core,
48 Brave, 32 Linux and 14 Windows-labelled tests passed). Native Windows recovery
validation and production restore integration remain pending.

## Rollback progress for untouched work

Rollback progress now counts untouched files as requiring no recovery when determining
resolved totals and category completion. Their journal entries remain Untouched; they are
not falsely labelled restored/recovered. This fixes a category staying incomplete after
all changed files were successfully recovered when later files were never modified.
The post-publication-failure test now checks complete recovery progress for that mixed
Intent/Untouched case. All 12 focused transaction cases pass. Apply progress is unchanged;
production UI execution remains disabled.

## Visible staging-cleanup failures

Linux publication now surfaces a failed unlink of an observed owned staging child as an
I/O failure instead of ignoring it. Both artifact publication and metadata-aware new-file
publication use this cleanup behavior. A new failure-path test rejects malformed metadata,
checks no new destination/staging remains and verifies unrelated existing content stays
unchanged. All ten native directory tests pass. The failing-unlink branch itself has not
been deterministically injected; noncooperating substitution/ownership limitations remain
as documented. Production restore execution is still disabled.

## Retained target identity during Linux capture

`VerifyChildIdentity` now compares a named regular, single-link child with a retained file
handle. Linux original-state capture keeps the initial file handle open across both full
reads and rechecks the named child against it, rejecting an observed replacement even
when its bytes/metadata are identical. Twelve focused native directory/capture tests pass,
including a deterministic same-content replacement case.

This improves inspection-time identity only. The returned image does not retain a handle
for the transaction lifetime; publication must retain/revalidate target identity separately.
Original absence still requires atomic no-clobber creation, and concurrent modifications
after capture remain a production-adapter responsibility. No restore is enabled.

## Retained read-only Linux target lease

`LinuxRestoreTargetLease` now keeps directory identity, the original file handle (when
present), and independently owned original bytes/metadata across calls. `VerifyOriginal`
compares physical identity and exact captured state without refreshing originals;
`CopyOriginal` supplies a separate safety image owner. Observed absence is rechecked but
not reserved. Disposal clears buffers/releases handles without writing files.

A native synthetic test passes for unchanged state, same-content physical replacement,
original-absence changes, retained original copies and disposed-lease rejection. This is
not yet connected to transaction target publication: the future adapter must track applied
state, handle safe replacement/deletion and enforce explicit scope/browser checks. Windows
parity and complete restore/lifetime recovery remain pending; no profile writes are enabled.

## Expected target checkpoints without baseline replacement

`LinuxRestoreExpectedState` now separates the immutable original safety image from the
currently expected physical target state. After an explicit caller-controlled publication,
`AcceptPublished` requires exact expected existence/content/metadata before retaining its
new identity. Failed observations do not adopt the candidate. Verification uses the retained
checkpoint; original copies remain unchanged across applied/deleted observations.

One focused native synthetic test passes for applied-state verification, conflicting edits,
failed acceptance, deletion observation and original-byte/metadata retention. The class
performs no target writes and cannot prove who made an observed change. Callers must not
use it to authorize changes or adopt arbitrary external edits. Production replacement/
deletion and rollback publication are still pending; no restore command is connected.

## Frozen read-only restore context

`BraveRestoreContext` freezes source path/fingerprint and explicit installation/profile
mappings, then rechecks supported target directory boundaries/access, duplicate normalized
destinations, source bytes and stopped-browser observations before/after verification.
A focused synthetic test passes for unchanged context, source edits and Running/Unknown
browser states without destination writes. The host must first authenticate the archive
and obtain explicit final operation approval; context capture does not grant either.

Fingerprint verification currently rereads the whole bounded archive; large archives may
incur significant I/O. This component does not establish physical target identity (the
separate target leases do), atomic absence/reservation, metadata completeness or safe
replacement/deletion. It is not yet wired into an application transaction store. Production
restore and Windows/native recovery integration remain pending.

## Physical Linux duplicate-target checks

The frozen restore context now retains Linux directory identities during target verification
and rejects targets referring to the same device/inode, in addition to normalized path
checks. Identities are revalidated and released after the read-only check; execution must
still retain/recheck its own target leases. Twelve native directory tests pass, including
same/different directory comparison. Bind-mount aliases have not been created in tests;
physical comparison uses the existing native device/inode identity contract. Windows
physical duplicate checking and production write integration remain pending.

Latest full regression run: 1,094 passed, nine environment-dependent tests skipped
(995 Core, 48 Brave, 37 Linux and 14 Windows-labelled tests passed).

## Absolute path freezing

Integration review found that expected-state tracking retained the caller's potentially
relative directory string, and frozen context mappings could contain relative user-data/
profile directories. Both now normalize those directory paths once during capture.
Subsequent observations therefore do not intentionally re-resolve relative targets against
a changed process working directory. The focused expected-state and restore-context tests
pass after this change. Working-directory mutation was not exercised because it is global
to concurrently running tests. Physical identity and publication protections remain separate;
production replacement/deletion and UI execution remain pending.

## Target revalidation after journaling

The coordinator rereads the target after recording intent and verifying context, just
before the store publishes a restore or rollback write. It compares the observed bytes,
metadata and existence with the expected pre-write image. A restore conflict stops the
operation. A rollback conflict preserves the changed target, leaves its entry unresolved
and allows recovery of other targets. Tests inject edits during journal publication and
check those outcomes using synthetic files.

This extra read narrows the interval in which the target can change. The store still
needs to enforce target identity and conflict checks at its own publication boundary.
Native browser observation now uses the current context verification's cancellation
token, including during resumption, instead of retaining the token from capture.

## Rollback when the source archive is unavailable

The original source is required for prepare, apply and resume. Rollback uses the verified
encrypted safety snapshot and rechecks target paths and browser state through a separate
recovery context. The coordinator uses this check before rollback begins, before inspecting
each affected file and after recording recovery intent. A store keeps its existing full
context check by default. An adapter can opt into source-independent rollback by calling
`BraveRestoreContext.VerifyRecovery` while retaining its target identity and artifact checks.

Tests exercise resume blocked by an unavailable source, explicit rollback of changes made
before the pause, and recovery checks with the source deleted, the browser running or
unverifiable, and the target directory missing. Production restore remains disabled.

## Guarded Linux target publication

The Linux target checkpoint now exposes a guarded write path. It locks the retained
destination directory for publication and readback. Before the final filesystem
operation it rechecks the caller's restore context and the expected content, metadata,
and file identity. Replacements are staged and verified before rename. Creation uses
a no-clobber rename. A rollback to original absence unlinks only after checking the
retained single-link file. Successful publication takes a new expected checkpoint and
keeps the original safety image.

The synthetic transaction test now exercises this path with the real encrypted artifact
lease. It verifies replacement, creation, rollback of original contents and metadata, and
deletion of a file created by the restore. Two tests inject external edits during the final
context check and verify that replacement and deletion both preserve the conflicting file.

Directory locking serializes cooperating callers. There is still a check-to-write window
for a same-user process that ignores the lock. Recovery must report failures that occur
after publication as unresolved when the new target cannot be verified. The test harness
supplies the mapping and context callback. Application-level authorization, lifecycle and
Windows target publication still need integration before this path is used on profiles.

## Linux session integration

The internal `LinuxRestoreSession` binds explicit profile/file mappings to the coordinator,
retained target checkpoints and the encrypted artifact lease. It rejects duplicate file
mappings, bounds retained target images and excludes the authorized browser roots from
the artifact location. The same operation ID names both artifacts and identifies their
contents. Opening the session captures baselines without publishing target or artifact
files. Prepare verifies the safety snapshot before the coordinator authorizes target writes.

Forward publication checks the original source before writing. Rollback publication uses
the recovery context and still checks target paths, browser state, and encrypted artifact
ownership. Native synthetic tests cover restored bytes and metadata, removal of originally
absent files, source loss while paused, a running browser blocking rollback, and rejection
of duplicate mappings or a recovery directory inside browser data.

This session is an internal integration point for a later category writer. Its caller must
authenticate the source archive, derive compatible file mutations, and obtain explicit
restore approval. The existing cooperative Linux locking limit still applies. The Restore
UI continues to offer planning while category execution and native Windows validation
remain pending.
