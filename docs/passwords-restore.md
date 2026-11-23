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

## Disposable Linux writer compatibility probe

The native probe passed with Chromium `154.0.8037.98` in Brave on Linux. It
created an isolated D-Bus session, Secret Service keyring and temporary browser
profile. The probe explicitly selected `--password-store=gnome-libsecret` and
disabled Sync. It checked that Brave created a local `Login Data` database at
schema 43, compatible version 40, and encrypted both the synthetic password
and its note with the isolated v11 key.

Run the probe on Linux with Python 3.11 or later, Brave, `dbus-run-session`,
`gnome-keyring-daemon`, `secret-tool` and OpenSSL available.

```sh
python3 tests/Manual/PasswordRestoreProbe/probe.py /absolute/path/to/brave
```

The probe closes Brave before working on a copy of the database. It rejects
nonempty WAL, shared-memory and rollback-journal sidecars. It confirms that
collapsing two native form keys to one portable identity raises a SQLite
uniqueness error. A successful staged replacement removes the fixture logins
and their related note and insecure-credential rows, retains a blocked entry
and an unrelated stats row, and passes SQLite integrity and foreign-key checks.
Brave reopens the replacement and returns the inserted Unicode password and
note, as well as a login with empty values. A second staged replacement
verifies that an empty archived collection can clear the saved logins while
retaining the blocked entry and stats row in this fixture. The probe publishes
only into its disposable profile.

The native run also confirmed that Brave stores password ciphertext as a
SQLite BLOB and note ciphertext as TEXT. Capture now reads note TEXT as raw
bytes inside SQLite, before the managed provider can decode it. It still
accepts BLOB notes and rejects other SQLite storage classes. Regression tests
cover native-style TEXT decryption, source preservation and malformed values.
[Chromium password note storage](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/password_manager/core/browser/password_store/password_notes_table.cc).

## Linux provider selection

The same probe now writes a fresh synthetic login and note through the
supplied launcher under three isolated launch configurations. It inspects the
new rows after Brave closes. Results for Chromium `154.0.8037.98` are below.

| Launch configuration | Provider used for the new password and note |
| --- | --- |
| GNOME desktop selection, private Secret Service available, no password-store override | v11, verified against the private Secret Service key |
| Same launcher and browser profile, with `--password-store=basic` | v10 |
| Explicit `--password-store=gnome-libsecret`, fresh profile, session bus unavailable | v10 |

BraveBackup discovery records the launcher path, version and expected user-data
directory. Those fields cannot establish the effective writer. An existing
v11 row describes a previous write. The probe shows that even an explicit
libsecret flag can result in a different provider when service availability
changes. Chromium also registers the Secret Portal provider with higher
priority when the relevant features are enabled.
[BraveBackup launcher discovery](../src/Backup/BraveAppDetector.cs),
[Chromium Linux password storage](https://chromium.googlesource.com/chromium/src/+/319607cda8fc17d4033b74ec565728f3579e82ee/docs/linux/password_storage.md),
[Chromium provider registration](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/chrome/browser/browser_process_impl.cc).

## Next writer checkpoint

Build the staging component around explicit, operation-scoped provider
evidence. It must also validate the entire destination schema, sidecar state,
portable identity collisions, excluded credential classes and Sync state.
Before enabling profile writes, establish that the provider evidence applies
to the selected destination and is still valid when preparing the replacement.
The writer still needs final review and integration with the encrypted safety
transaction. Passwords remains disabled in Restore.
