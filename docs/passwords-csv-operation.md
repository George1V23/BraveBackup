# Complete standalone CSV operation for the researched profile

`BravePasswordCsvExport.CreateAsync` connects the previously isolated components into one
explicit single-profile capture-to-save API. It does not enable a UI control, archive CSV
entries, raw-file substitution or automatic discovery-time credential reads.

## Pre-access guards

Require an explicit plaintext CSV choice before any credential capture. Require the
researched Brave version profile `1.102.13` (a fourth version component of zero is also
accepted), an explicit supported Windows-v10/Linux-Secret-Service-v11 provider context,
and an explicit Linux-LF/Windows-CRLF target line ending. Other versions fail with
`passwords-csv-version-unsupported` rather than claiming an identical PSL/grouping snapshot.
Version metadata is a conservative compatibility gate, **not proof** that a binary has the
exact pinned source revision; real Brave/native compatibility validation remains pending.

Normalize and require the profile to be an immediate child of the selected installation's
user-data directory, matching the capture/key-source assumptions. Check the destination
outside selected browser roots, standard roots, recognizable browser markers and filesystem
links. Require a `.csv` filename in an existing folder. No source is unlocked for a rejected
choice, version, scope or destination.

## Worker capture and independent ownership

After those guards, a worker:

1. Captures/validates both locally present password stores with Brave observed stopped,
   then unlocks at most one provider key and decodes complete passwords/notes.
2. Copies them into the portable document contract. A validated collection with no eligible
   logins produces header-only CSV without key access or an unnecessary affiliation cache.
3. For nonempty collections, captures the known affiliation database/main/WAL/companions,
   validates schema-v7 and checks combined retained password/cache source bytes against
   a 128 MiB operation limit. A transient newly read cache may coexist before that check.
4. Constructs canonical facets, selects cached/singleton groups, merges related groups
   with the pinned offline PSL/private rules/extensions, and projects distinct CSV rows.
   Unsupported URL families and nonempty named notes fail rather than being omitted.
5. Rechecks password sources, affiliation sources, then password sources again. Keys are
   already disposed before a local-save warning is presented.

The context owns all captures, document and independent projection secret buffers through
completion/failure/cancellation, then disposes them in reverse order. Returned metadata is
only destination, source-login/group/CSV-row/excluded-entry counts and byte length. Metadata
strings have the previously documented managed-memory limits; no credentials are logged.
No real profile/keyring or browser was accessed in implementation/tests.

## Confirmation and publication

The [confirmed file exporter](passwords-csv-save.md) runs with the actual selected source
verifier and protected roots. It presents a fresh destination-specific warning that anyone
with file access can read the plaintext output, explains temporary plaintext staging,
and identifies replacement of an existing file. No local file is created before approval.
The public operation preserves the caller's synchronization context for this warning when
available; a UI host without a context must dispatch its own dialog. Native capture/auth
work stays off the UI thread. The app never collects OS passwords.

After approval, private staging, exact formatted-byte verification, destination fingerprint
checks, combined source revalidation, cancellation checks and atomic publication apply.
Decline, stale data, unsupported projection or cancellation preserves an existing destination
and removes owned temporary output. Cleanup failures remain explicit. There is no automatic
raw fallback for CSV and no reuse of consent on later operations. Raw/password archive
contracts and protected-default behavior are unchanged.

## Validation and remaining integration

Twelve synthetic end-to-end cases cover both provider fixtures and platform line endings,
full capture/cache/group/projection/save output, source byte preservation, safe counts,
key disposal before confirmation, incompatible versions before access, absent explicit
choice, pre-cancellation, decline, source changes, header-only emptiness, missing cache,
unsupported named notes, cancellation in the warning, and protected destination rejection.
They use only disposable synthetic profile/output folders and injected test keys.

Next work: actual UI controls/authentication explanations, supported-version/product
compatibility validation, broader URL/Android support, native Windows/keyring checks,
and separately confirmed CSV entries inside encrypted/unencrypted archives. The current
UI remains disabled for password CSV until that flow is wired and reviewed. Restore is
not introduced.
