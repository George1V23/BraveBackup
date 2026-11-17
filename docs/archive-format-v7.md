# Archive v7: explicit protected raw password preservation

Version 7 extends the password category to heterogeneous per-profile results. Versions
1–6 remain readable and unchanged. Portable-only password writing still emits v6;
operations with any explicitly chosen raw/native password fallback emit v7. Password-free
operations retain previous version selection. The BBKENC1 envelope is unchanged.

## Capabilities and category versions

The canonical required-capability subsequence is:

```text
bookmarks-v1, settings-v1, extensions-v1, sessions-v1, tabmanagerplus-v1, passwords-v2
```

`passwords-v2` is required. Every profile declares category `passwords`, version `2`;
all other category versions remain `1`. The archive writer promotes the password category
of portable profiles too when another profile uses raw preservation, so the manifest's
required capabilities consistently describe every profile. Unknown versions/capabilities,
duplicates and invalid ordering are rejected.

The original authenticated BBKENC1 encrypted envelope is mandatory for both forms.
Plain ZIP archives are rejected before password payload validation. This checkpoint does
not authorize unencrypted raw password saving or implement CSV archive entries.

## Per-profile alternatives

A category has exactly one of these forms:

1. **Portable:** the single `portable-passwords` / `portable-v1.bin` descriptor and
   [schema-1 payload](passwords-payload.md) specified in [archive v6](archive-format-v6.md).
2. **Raw/native:** one index plus one to eight allowlisted source-file descriptors below.

All paths use `installations/{installation UUID}/profiles/{profile UUID}/passwords/` as
prefix. Raw/native descriptors are:

| Role | Relative path | Maximum uncompressed length |
| --- | --- | --- |
| `raw-password-index` | `raw-index-v1.json` | 4096 bytes, nonempty |
| `native-password-file` | `raw/{source filename}` | 64 MiB per file, empty permitted |

Only `Login Data`, `Login Data For Account`, and their `-wal`, `-shm`, `-journal` companions
are accepted. Filenames are exact/case-sensitive. No paths, directories, Local State,
OS key stores, cookies or unknown companions are accepted. Native bytes total at most
128 MiB per category. Every descriptor has exact length and lowercase SHA-256; hashes,
actual ZIP entries, roles, counts, duplicate names and undeclared entries are checked.
Native file content is uninterpreted: the reader performs no SQLite recovery, decryption
or credential-count inference.

The index is an object with exactly these case-sensitive properties, without duplicates:

```json
{
  "schemaVersion": 1,
  "captureKind": "raw-native-password-files",
  "portableCaptureCompleted": false,
  "reasonCode": "passwords-database-invalid",
  "missingMainDatabases": []
}
```

`reasonCode` must be one of the supported portable-failure codes: `passwords-source-invalid`,
`passwords-database-invalid`, `passwords-schema-unsupported`, `passwords-scheme-unsupported`,
`passwords-journal-unsupported`, `passwords-encryption-unsupported`, `passwords-decode-failed`,
`passwords-key-unlock-failed`, `passwords-key-envelope-unsupported`,
`passwords-key-source-invalid`, or `passwords-capture-limit`. Source-change/close-retry and
cancellation are not raw preservation reasons.

`missingMainDatabases` must exactly match the native inventory's orphan companion stores,
in `Login Data`, then `Login Data For Account` order. No main file and no companions is
not an orphan entry. An orphan or empty/corrupt file is only preservation of available
bytes, never a recovery guarantee. Raw entries cannot claim portable completion or a
credential count. The index contains no native exception messages, saved credentials,
absolute source paths or keys. Index metadata records the writer's result classification;
it is not independent proof of human consent.

## Writer consent and source safety

`PasswordBackupOptions.ChooseRawPreservation` optionally connects the existing
[raw-or-cancel capture flow](passwords-raw-fallback.md). Absent that callback, portable
failure remains failure. The callback is fresh per failed profile/operation, receives the
safe reason/profile warning and must explicitly accept; otherwise nothing is published.
It runs on the archive worker, so a UI host must dispatch its dialog and return the
asynchronous decision. The worker awaits that decision; it does not assume elapsed time
is approval. Source/key changes, running/unknown Brave and cancellation bypass raw consent
and require stop/retry. Read failure after acceptance remains failure.

Both output forms keep password protection checked and require encryption. Raw bytes
stream directly from owned memory into the encrypted ZIP, with no plaintext disk staging.
Aggregate retained-source and serialized password-byte limits remain 128 MiB each (the
serialized limit includes raw indices). Captures remain owned through final source/process
checks. The existing verification, atomic destination publication, cancellation and cleanup
path applies. No implicit unencrypted/raw/CSV substitution is made.

Completion reports separately count portable profiles/credentials and raw profiles/files,
record missing-main-store counts, and state that raw credential counts are unknown and
portable capture incomplete. They do not expose credential values or claim recoverability.

## Validation and remaining work

Eighteen synthetic reader cases cover valid native/orphan categories, Core/Brave reader
agreement, encryption enforcement, inventory/hash/size/role/path failures, metadata claims,
duplicate/extra properties, missing-main consistency and version/capability mismatches.
Four additional archive-writer cases cover a portable/raw mixed archive, exact corrupt
file/journal preservation, excluded files, fresh acceptance/decline/cancellation, truthful
reports and changed-source cleanup. The existing portable-writer and prior-version tests
remain part of regression validation. No real credentials or OS key stores were accessed.

CSV browser projection, CSV archive consent/contracts, full Passwords UI controls and real
native authentication validation remain pending. No restore operation is introduced.
