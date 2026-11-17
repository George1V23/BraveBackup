# Protected portable password archive writing

`BraveArchiveBackup.CreateAsync` now accepts an explicit `PasswordBackupOptions` in its
advanced overload. Existing entry points remain available and do not enable password
capture: passing no password options still rejects the Passwords category. The UI remains
disabled until its full controls/provider/fallback flow is connected.

`ProtectPasswords` defaults to true. This writer supports **protected portable data only**:
archive encryption is mandatory, an empty/missing encryption password fails before
provider/key access, and unchecking protection is rejected rather than silently selecting
CSV. CSV and raw/native archive saving need their separately documented contracts/consent.

`GetProvider` is mandatory and establishes the researched provider/backend per selected
installation/profile; no OS-only or prefix-only guess is made. `ObserveBrowserState` can
supply the operation process observer. The optional `Unlock` adapter receives a profile,
borrowed captured Local State bytes where applicable, and cancellation; it must return
an owned key and retain no copies. Without that adapter, native researched key access is
used. Options store no keys or archive password. Provider failures stop this writer;
the separate raw-or-cancel capture component is not silently substituted into its payload.

## Capture, streaming and publication

The operation freezes scope/categories, validates protection and destinations, and handles
overwrite consent before credential access. On its worker it captures each selected
profile, decodes every eligible password/note, creates an independent portable document,
and writes `passwords/portable-v1.bin` directly into the ZIP over the existing BBKENC1
writer. No plaintext file staging or extracted password database is created. Both locally
present stores are retained by the [portable payload contract](passwords-payload.md).

A password operation emits [archive version 6](archive-format-v6.md) and canonical
`passwords-v1`, optionally alongside existing categories. Password-free operations keep
their previous version-selection behavior. Completed password archives are reopened and
validated by the Core/Brave readers before publication.

Captured sources stay owned until final hash/file-set checks. The operation bounds retained
password source bytes to 128 MiB and serialized password payload bytes to 128 MiB across
profiles, in addition to the per-capture/payload and whole-archive limits. A new capture
can temporarily coexist with prior captures while the aggregate limit is checked; it is
immediately disposed if refused. Existing format/entry/resource ceilings still apply.

After verification, destination safety/fingerprints and sources are checked again. Password
operations repeat the destination check after source verification, then check stopped-browser
state and cancellation immediately before atomic rename. These remain conservative race
checks, not a filesystem compare-and-swap guarantee against hostile concurrent changes.
Any failure/cancellation clears owned credential/key/source buffers, removes the owned
encrypted temporary file and preserves an existing destination. Cleanup failures remain
explicit. Restore is not introduced.

`BackupResult.PasswordsSummary` and the report contain profile, credential, note and
blocked/federated exclusion counts plus protected portable capture status. They contain
no saved usernames, passwords, sites, notes, keys or source paths. Counts reflect locally
captured records, not deduplicated browser UI groups or cloud completeness.

## Validation and remaining integration

Twelve synthetic integration cases cover Windows-v10/Linux-v11 provider fixtures,
password-only and mixed bookmark/password archives, every selected profile, v6 reopening,
secret-free reports, unchanged sources, key disposal, protected empty collections, encryption
and provider guards, unsupported modes, declined overwrite, corrupt data, source/destination
changes, cancellation and encrypted-temporary cleanup. No real credentials or native keys
were accessed. The full regression suite protects existing categories and versions.

The semantic refactoring tool updated advanced call sites to pass no password options;
a compatibility overload preserves the existing source/API entry point. UI selection and
credential discovery remain unchanged. Native Windows/keyring validation, raw/CSV archive
contracts, browser CSV projection, full fallback controls and the Passwords UI remain pending.
