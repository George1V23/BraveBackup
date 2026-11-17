# Password decoding checkpoint

Implemented under the accepted [Passwords scope](passwords-backup-proposal.md), after
[encrypted database capture](passwords-snapshot.md). Format evidence and independently
generated OpenSSL vectors are recorded in [password research](passwords-research.md).

## API and ownership

`PasswordKeyMaterial` copies an explicitly supplied provider key and clears it on disposal.
`FromLinuxKeyringSecret` derives the researched v11 key using Chromium's PBKDF2-HMAC-SHA1,
`saltysalt`, one iteration and 16 output bytes. These legacy compatibility parameters are
not a recommendation for new encryption. No key lookup, key creation or prompt occurs.
The caller must clear its original input; no missing-secret or alternate-provider fallback
is attempted. Already-started decoding owns a separate temporary key copy.

`BravePasswordCrypto.Decode` supports Windows DPAPI-provider v10 AES-256-GCM (12-byte nonce,
16-byte tag, empty associated data) and Linux keyring v11 AES-128-CBC (space IV, PKCS7).
Provider context is mandatory: the v10 prefix alone cannot identify a cross-platform
algorithm. Other providers, including app-bound v20 and portal v12, fail explicitly.
CBC padding is not authentication: this decoder cannot guarantee detection of every wrong
key or ciphertext alteration. Actual empty encrypted blobs decode to empty values;
truncated nonempty frames and invalid UTF-8 fail rather than becoming empty passwords.

`SensitiveUtf8` validates and owns up to 4 MiB of UTF-8 bytes without creating plaintext
strings. Its borrowed memory is cleared on disposal; callers must not retain copies or
use it concurrently with disposal. Redacted `ToString` methods do not authorize logging
metadata or copying secrets. Temporary key and plaintext arrays are cleared in finally
blocks, including failures.

`DecodeDatabase` decodes every eligible login and attached note or fails the whole result.
`DecodedPasswordDatabase` owns the secret values and clears them on disposal. Rows and
notes preserve source metadata, and exclusion counts remain available. Limits are 100,000
rows, 100,000 notes and 32 MiB of decoded secret bytes. Crypto/encoding failures use fixed,
content-free messages; partial results are disposed. Source ciphertext is not changed.

## Validation and remaining work

Twenty tests cover the four frozen OpenSSL vectors, Linux key derivation, both synthetic
SQLite stores, password/note ownership, altered GCM frames, unsupported providers,
truncation, authenticated invalid UTF-8, note failure, cancellation and disposal.

This checkpoint performs no OS credential access, browser automation or local plaintext
save. Native Windows key access and Linux keyring prompting remain unimplemented and
untested. Passwords UI, archive integration, fresh CSV-save consent and explicit raw-file
fallback remain pending. Existing archive versions and category behavior are unchanged.
