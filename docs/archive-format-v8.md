# Archive v8: explicit password CSV entries

Version 8 adds a distinct CSV password category. Versions 1–7 remain readable and
unchanged. Protected portable-only operations still emit v6, protected raw-preservation
operations emit v7, and operations without passwords keep their existing version rules.
The BBKENC1 encryption envelope is unchanged.

## Manifest and entry contract

The canonical required-capability subsequence is:

```text
bookmarks-v1, settings-v1, extensions-v1, sessions-v1, tabmanagerplus-v1, passwords-v3
```

`passwords-v3` is required. Each profile has category `passwords`, version `3`;
all other category versions remain `1`. Unknown, duplicate, reordered or mismatched
capabilities/category versions are rejected. Every profile's password category contains
exactly one descriptor:

- Path: `installations/{installation UUID}/profiles/{profile UUID}/passwords/passwords.csv`
- Role: `plaintext-password-csv`
- Length: positive, at most 64 MiB, matching the ZIP entry length
- SHA-256: 64 lowercase hexadecimal characters, matching the entry bytes

No binary portable password payload, raw native database/index, extra descriptor or
unreferenced ZIP entry is allowed in this form. CSV and protected forms cannot be mixed
within one operation. Unencrypted ZIP and authenticated encrypted BBKENC1 envelopes are
both supported for v8; v6/v7 still require the original encrypted envelope. Selecting CSV
does not weaken protected payload validation.

## CSV payload validation

The header is exactly `name,url,username,password,note` followed by LF or CRLF.
Payloads use strict UTF-8 without a BOM, exactly five columns per data record and a
final record separator, including header-only exports. Separators are uniform; CR/LF
inside quoted fields are preserved. Quotes inside quoted fields are doubled. Unquoted
quotes, unterminated quoted fields, trailing text after closing quotes, missing/extra
columns and invalid UTF-8 are rejected.

There are at most 100,000 data rows. The first three fields are each at most 1 MiB of
decoded UTF-8 bytes; password and note fields are each at most 4 MiB. Validation counts
bytes after doubled-quote unescaping without creating credential strings, performs
cancellation checks and clears the archive payload buffer on success or failure.
Hash/syntax validation does not establish browser identity, correct grouping, authentic
credentials or user consent. It does not sort or change entry bytes.

## Writer API and consent

The advanced `BraveArchiveBackup.CreateAsync` overload accepts `PasswordBackupOptions`
with `ProtectPasswords = false`, an explicit `CsvLineEnding`, an explicit provider,
and a fresh `ConfirmCsvArchiveSave` callback. Absence of the callback rejects the operation.
The protected default and older overloads remain unchanged.

After destination/replacement checks, the callback receives a `PasswordCsvArchiveWarning`
with the full destination, encryption state, replacement state and selected profile count.
It explains that credentials are readable by anyone with file access to an unencrypted
archive, or after decrypting an encrypted archive. It also describes temporary staging.
Approval is operation-scoped and never cached. Declining, cancellation, callback failure
or destination changes before staging prevent writes and credential unlock. The application
must dispatch this callback to its UI thread when needed.

After approval, source capture uses the existing conservative Brave 1.102.13 gate,
explicit provider, closed-browser observation, supported URLs/notes and offline affiliation
grouping. Profiles must be immediate children of their installation's user-data directory.
The same projection and encoding are used for standalone CSV. Empty credential collections
emit the header without keyring/cache access. Unsupported capture/projection fails the whole
operation; CSV mode never silently substitutes raw password files or a partial CSV.

Password and affiliation source images remain owned and bounded to 128 MiB across the
operation; serialized password payloads also have a 128 MiB aggregate limit. Keys are scoped
to capture; projected secrets/payloads are disposed and cleared after each entry. Sources,
destination and stopped-browser state are checked before publication. The writer validates
the completed archive and atomically publishes it; failures clean up private staging and
preserve the existing destination. Unencrypted archive staging contains readable CSV after
approval. Cleanup cannot promise secure physical erasure or protection against other
processes with sufficient filesystem privileges.

## Checkpoint and coverage

This checkpoint implements the backend/API and validators. The existing UI still blocks
CSV-in-backup operations until the fresh dialog is connected. No restore/import is added.
Standalone CSV UI behavior remains unchanged. Backend tests cover both envelope states,
source preservation, fresh consent, cancellation, source/cache/destination changes, cleanup,
and malformed CSV/manifest/category rejection. Protected v6/v7 regression tests remain in
the suite.

On 2026-11-17, `dotnet test BraveBackup.slnx --no-restore` passed 1,002 tests
with six environment-dependent native tests skipped (934 Core, 48 Brave, 14 Linux
and six Windows-labelled tests passed). All new tests use synthetic credentials;
no real OS keyring or Windows unlock prompt was accessed.
