# Explicit portable-password capture checkpoint

`BravePasswordCapture.Read` connects the [encrypted snapshot](passwords-snapshot.md),
[provider-aware decoding](passwords-decoding.md) and existing
[Windows](passwords-windows-key.md)/[Linux Secret Service](passwords-linux-key.md) key
adapters. It returns an owned in-memory result only; it does not write any backup or CSV.
No UI, CLI, startup, discovery or navigation caller has been added yet.

## API and operation order

The caller supplies a profile and established `PasswordEncryptionProvider` context.
The implementation does not guess a cross-platform algorithm from a v10 prefix or
switch keyring backends. Call it on a worker, only after an explicit password operation:

1. Require Brave observed stopped and capture/validate both locally present supported
   password stores and companions. Missing is not interpreted as empty.
2. Preflight every eligible password/note frame against the selected provider. Unknown
   or structurally incomplete frames fail before any key lookup. No rows are omitted.
3. If encrypted values exist on Windows, capture `Local State` from the profile's parent
   user-data directory. Only a safe regular file through safe ancestors is accepted,
   with a 4 MiB limit, timestamps, length checks and SHA-256 comparisons. It is never
   opened for writing, added to an archive or treated as a profile replacement path.
4. Verify sources, request at most one owned key, validate its provider, then verify
   sources again. Use native adapters by default; tests inject an unlocker receiving
   borrowed Local State bytes (empty on Linux) and the operation cancellation token.
5. Decode both stores and every note; apply a combined 32 MiB decoded-secret limit.
   A validated empty store or actual empty blobs need no key and do not manufacture one.
6. Verify source bytes, source sets and stopped-browser state before returning. Dispose
   the key before the caller receives the capture. Any failure/cancellation clears all
   completed decoded stores, captured key-source bytes and encrypted snapshot buffers.

`Databases` exposes store-keyed `DecodedPasswordDatabase` results. Source metadata and
exclusion counts remain available, without logging them. `CapturedUtc` is the source
snapshot time, not authentication completion time. `ToString` is redacted.
`VerifyUnchanged` must be called again before a future save is published; it checks the
key source between two database/source-state verifications. `Dispose` clears owned secret
values, encrypted buffers and Local State. Borrowed memory must not be retained or used
concurrently with disposal; consumers are responsible for any copies they create.

The native adapters can show provider-supported authentication UI; DPAPI can instead use
the current logon context. They do not collect OS passwords, generate replacement keys
or bypass unsupported protection. Injected unlockers must return an owned key and retain
no borrowed source bytes/copies. Expected source and native failures have safe messages.

## Validation and limits

Twenty-four synthetic integration tests cover both stores with one key request, end-to-end
adapter-to-decoder vectors, unchanged source bytes, disposal, empty data without OS access,
unsupported/incomplete frames before authentication, missing/oversized/symlink key sources,
wrong/null keys, corrupt input, unknown/running browser state, cancellation, a later-store
failure, added WAL companions, and database/key changes with preserved size/timestamps.
All source mutation in these tests uses disposable synthetic profiles, never live Brave.

Real Windows DPAPI and desktop-keyring prompt validation remain pending. This operation
initially requests Brave closed; a write-free consistent live snapshot remains unproven.
Direct KWallet, portal/basic/app-bound variants remain unsupported. The caller must
establish the actual supported backend rather than treating OS detection as sufficient.

The approved raw-file-preservation-or-cancel flow is not implemented by this result type:
a portable-capture error must not be relabeled as an empty or successful capture.
Protected payload serialization, CSV export with fresh destination-specific confirmation,
archive integration, reports and UI wiring are the next work. Passwords remains disabled
in backup UI; existing archive versions and working categories are unchanged.
