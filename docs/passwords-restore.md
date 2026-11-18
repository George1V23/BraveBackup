# Password restore research and source inspection

Restore now shows what the selected backup contains without exposing websites,
usernames, passwords or note contents. For encrypted archives, the inventory
appears after a successful unlock.

| Backup representation | Restore preview |
| --- | --- |
| Protected portable payload in v6 or v7 | Profile store and local account store, with saved login, note and excluded-entry counts. An empty store is shown as zero rather than omitted. |
| Raw native files in v7 | Count of captured files and whether a main database is missing from the set. This is preservation evidence. Credential recovery requires separate evidence. |
| CSV in v8 | Count of validated data rows. Multiple rows can represent one source login. The CSV is readable by anyone with access to an extracted file. |

`BackupArchive.Validate` derives the inventory during its existing validation
pass. Its optional callback receives only counts, store kinds and the archived
profile ID. Callers discard results if the archive fails validation.
`BraveArchiveInspection.Read` exposes those results to Restore only after
archive validation and source identity checks finish. The existing Passwords
checkbox stays disabled and explains the limit of its representation. Mapping and
Recheck need no password data from the destination.

## Destination write requirements

Chromium's `LoginDatabase::AddLogin` checks the form, encrypts the password
through the store's configured encryptor, binds the form fields, and handles
related notes and insecure-credential rows by the destination primary key.
Its schema has a composite login key and separate note and Sync metadata tables.
BraveBackup's portable schema captures a subset of these fields. In particular,
the origin, realm, username and source row ID cannot reproduce every native
form identity. Treat the stored ID as source metadata. A direct writer needs
explicit rules for missing form fields, collisions, note ownership and Sync
state before it can safely replace records.
[Chromium LoginDatabase write path](https://chromium.googlesource.com/chromium/src/+/1d6ef28c92d6ce31bd84532f8a1dbd8698e727a9/components/password_manager/core/browser/password_store/login_database.cc#1360),
[repository's pinned source and v43 fixture provenance](passwords-research.md),
[portable password fields](passwords-payload.md#binary-specification).

An in-memory SQLite probe loaded the checked-in v43 schema and inserted two
synthetic logins that differed only in their username form element. Both had
the same portable origin, realm and username. When the probe replaced both
form elements with the same empty values, SQLite rejected the update as a
duplicate. The probe used only the synthetic fixture.
[Synthetic v43 schema](../tests/Core.Tests/Fixtures/Passwords/schema-v43.sql).

The existing capture implementation can read specific provider variants. A
writer must separately establish the provider the destination Brave build will
use to write new rows and access that provider through the normal authorized
OS flow. Verify the provider from the destination configuration. Raw copies may
depend on protection unavailable on the target and may contain inconsistently
captured or missing database files. A validated raw index confirms which bytes
were captured. Database usability and decryption require separate evidence.
[Linux provider evidence](passwords-linux-key.md),
[Windows provider evidence](passwords-windows-key.md),
[raw-file preservation](passwords-raw-fallback.md).

Brave documents an interactive CSV import from Password Manager. Chromium's
importer submits additions and updates separately, can stop for conflict
resolution, and rejects some values, including empty passwords. This belongs in the separate Import workflow. Any later import feature needs
its own review, including a fresh confirmation
before BraveBackup saves plaintext CSV.
[Brave Help Center](https://support.brave.app/hc/en-us/articles/360019782291-How-do-I-import-or-export-browsing-data),
[Chromium CSV importer](https://chromium.googlesource.com/chromium/src/+/HEAD/components/password_manager/core/browser/import/password_importer.cc).

## Next writer checkpoint

Start with an existing disposable Linux profile store that uses the researched
schema and a verified destination provider. Use native Brave with synthetic data
to verify that it can reopen the staged database and read the inserted login and
notes. Include colliding logins, empty values, sidecars, related records, and
unsupported stores in the fixtures. Establish how an explicitly empty archive
store affects the supported rows while preserving excluded classes and unrelated
tables. Leave account stores, active Sync, unknown protection providers and raw
file replacement disabled until their effects can be demonstrated. Only then
connect an eligible writer to final review and the encrypted safety transaction.

Current coverage exercises encrypted portable v6 and v7, v7 raw capture with
and without a main database, and v8 CSV in Restore. Tests use synthetic data to
verify counts, unlock behavior, preview redaction and the disabled Passwords
selection after Recheck. Destination write compatibility is pending.
