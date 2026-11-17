# Explicit raw password-file fallback

`PasswordBackupCapture.ReadAsync` attempts [portable capture](passwords-capture.md) first.
An allowlist of portable-data/schema/provider/unlock failures offers the host one fresh
`PasswordRawFallbackWarning` callback. It carries only the profile directory and safe
reason code, never native exception text or credential values. The message states that
portable capture failed, raw/native files may be incomplete, portability/recoverability
is not guaranteed, and the choice does not authorize an unencrypted save.

The callback runs on the caller's captured context when available; file/native access
runs on workers. Hosts without a UI context must dispatch their own dialog. Decline or
cancellation stops capture. Missing source, source/key-source changes, running/unknown
Brave and cancellation are not silently converted to raw success. Source changes require
a fresh close-and-retry operation. Native-auth cancellation remains cancellation; an
ordinary unlock failure can offer the explicit fallback.

After acceptance, `BraveRawPasswordSnapshot.Read` captures a fresh stable view of the
available files. Failure to read those files is reported explicitly, without another
automatic fallback/retry. No archive, CSV, local plaintext save or UI control is introduced
by these APIs. Password protection and destination-specific save consent remain separate
requirements for future archive publication.

## Raw capture contract

The raw API requires `preservationExplicitlyChosen=true` before any source inspection.
It copies only these names from the selected profile:

- `Login Data` and `Login Data For Account`;
- their `-wal`, `-shm` and `-journal` companions.

It never invokes SQLite, decodes fields, unlocks a keyring, recovers a journal or opens a
source for writing. Corrupt/unsupported databases and nonempty journals are retained as
uninterpreted bytes. Other filenames, whole directories, Local State, OS key stores,
cookies and unknown companions are excluded. No database may be represented as portable
credentials simply because its raw files were copied.

Available orphan companions are retained even if their main database is missing, because
this is preservation of available bytes, not a restoration guarantee. The affected store
names are explicitly returned in `MissingMainDatabases`; an empty available file is still
preserved. If no allowlisted files exist, capture fails rather than reporting zero saved
credentials. This policy introduces no claim that orphan/empty/corrupt files can recover
passwords.

Require Brave observed stopped throughout capture and publication checks; running,
unknown, failed observations and mid-operation starts require close/retry. Source paths
must traverse safe directory ancestors and be regular files, with no symlinks/reparse
points, FIFOs, devices or sockets. Reuse Platform's safe file opening. Each file is bounded
to 64 MiB and the full set to 128 MiB, checked before allocation. Lengths, timestamps,
file sets and SHA-256 hashes are rechecked. Same-size/same-timestamp changes are rejected.
`VerifyUnchanged` must run again before any future archive publication. These are
conservative filesystem checks, not support for hostile concurrent directory replacement.

`Files` exposes borrowed read-only byte memories. Disposal clears all captured arrays;
callers must not retain copies or use them concurrently with disposal. `CapturedUtc`
records the successful capture time. No source data is logged. Native Windows behavior
still needs execution on Windows.

## Result and validation

`PasswordBackupCapture.Kind` distinguishes `Portable` from `RawNative`. Only the matching
`Portable` or `Raw` accessor succeeds. `PortableFailureCode` records why a raw choice was
offered; `CredentialCount` is **null** for raw data, not zero. Disposal and source checks
dispatch to the owned result. Reports must explicitly say raw/native preservation and
portable capture incomplete; they must not infer a credential count or recoverability.

Seventeen raw snapshot tests cover byte-exact corrupt/companion capture, excluded files,
explicit selection, missing/empty/orphan data, disposal, process gates, cancellation,
changed sets/hashes, unsafe objects and individual/aggregate limits. Nine orchestration
tests cover accepted/declined raw choices, portable empty success, unlock failure,
missing/running/cancelled gates, source-change retry, raw-read failure, ownership and
safe result classification. All profiles/files/keys are synthetic; no desktop credentials
were accessed. Existing archive versions and Passwords UI remain unchanged.

Next work includes archive entries/protection/consent for both payload kinds, truthful
reports, browser CSV projection and UI integration. Acceptance of raw capture alone is
not authorization to save unencrypted raw files or bypass the protected default.
