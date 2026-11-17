# Archive v6: protected portable passwords

Reader validation and [explicit protected password writing](passwords-protected-backup.md)
support archive format version 6. Existing versions 1–5 retain their contracts and remain
readable; password-free writing keeps those versions. Password UI controls remain disabled.
No change is made to the BBKENC1 encryption envelope.

## Manifest and capability

Use `format: BraveBackupArchive`, `formatVersion: 6`, and require `passwords-v1`.
Required capabilities are the nonempty canonical subsequence of:

```text
bookmarks-v1, settings-v1, extensions-v1, sessions-v1, tabmanagerplus-v1, passwords-v1
```

The subsequence must contain `passwords-v1`, without duplicates or unknown capabilities.
Each declared profile has exactly one category for every required capability, using the
existing installation/profile UUID namespace and profile metadata rules. Passwords is
category ID `passwords`, category version `1`.

The password category currently contains exactly one descriptor:

| Field | Value |
| --- | --- |
| Path | `installations/{installation UUID}/profiles/{profile UUID}/passwords/portable-v1.bin` |
| Role | `portable-passwords` |
| Length | Exact uncompressed payload length, greater than zero and at most 64 MiB |
| SHA-256 | Exactly 64 lowercase hexadecimal characters over the payload bytes |
| Payload | [Portable password schema 1](passwords-payload.md), magic `BBKPW001` |

The descriptor and actual ZIP entry must agree. Paths, roles, schema/content, hashes,
counts and resource bounds are validated without extraction. Unknown/extra/missing
entries are rejected. Raw/native files and CSV entries are not part of this contract;
new contracts must explicitly describe their payload identity, consent and protection
rather than silently reusing the portable role.

## Mandatory protection

Any archive requiring `passwords-v1` must be inside an authenticated BBKENC1 encrypted
envelope. `BackupArchive.Validate` rejects a plain ZIP declaring the protected password
capability before reading the credential payload, even if all hashes and syntax are valid.
An encrypted ZIP entry or a binary filename does not replace the envelope requirement.

The validator does not accept a caller-supplied assertion that a plaintext stream came
from an encrypted file. Pass the original encrypted source and password. The Brave-level
validator now validates that original envelope through Core before opening its separate
reader for Brave-specific category semantics. That avoids losing the encryption context;
for encrypted input, it adds a second reader/key-derivation pass compared with validating
only a pre-decrypted stream. This is a correctness tradeoff at this checkpoint.

The protected payload has no independent encryption: passwords, notes and metadata are
plaintext inside decrypted ZIP content. Reader validation bounds its byte array, checks
its digest, decodes the entire schema, disposes secret owners, then clears the temporary
payload bytes on success/failure/cancellation. Metadata strings have the documented
managed-memory limits; no credential data is logged. Callers must not stage decrypted
archive content on disk merely to invoke validation.

## Validation and next integration

Fifteen synthetic tests cover Core/Brave acceptance of encrypted v6, both validators'
rejection of plain password ZIPs, corrupt payloads, digest/role/path/size/count failures,
missing/undeclared entries, invalid capabilities/version, wrong passwords and cancellation.
All archive generation is in memory using synthetic credentials. The full regression
suite also exercises the existing categories and versions.

Protected writing now has frozen options, source revalidation, atomic publication and
truthful counts. Explicit raw-file archive contracts and CSV archive consent remain
separate requirements. Browser CSV projection, actual UI wiring and real
native authentication validation remain pending. No restore operation is introduced.
