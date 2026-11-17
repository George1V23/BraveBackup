# Windows v10 key-access checkpoint

`BraveWindowsPasswordKey.Unlock` parses supplied captured `Local State` bytes and calls
an explicit unprotector only after validating the existing `os_crypt.encrypted_key`
base64 envelope and its `DPAPI` prefix. It accepts exactly a 32-byte unwrapped key for
[Windows v10 decoding](passwords-decoding.md). Missing/duplicate fields, invalid JSON,
unknown prefixes and invalid returned key lengths fail explicitly. Local State is limited
to 4 MiB/depth 64 and the wrapped DPAPI blob to 64 KiB. Other Local State properties are
ignored; nothing from this file is added to a backup.

`WindowsDpapi.Unprotect` lives in Platform and calls native `CryptUnprotectData` under the
current Windows logon context, with no entropy, deprecated prompt structure or UI suppression.
It does not promise fresh OS authentication and never asks for OS passwords. This follows
Chromium's [pinned DPAPI provider](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/os_crypt/async/browser/dpapi_key_provider.cc)
and Microsoft's [native API contract](https://learn.microsoft.com/windows/win32/api/dpapi/nf-dpapi-cryptunprotectdata).
The native output is cleared before `LocalFree`; native input and temporary managed
buffers are cleared on success and failure. The caller owns and clears a direct adapter
result; the Brave envelope reader clears its unprotector result after copying it into a
`PasswordKeyMaterial` owner. The injected unprotector must return owned bytes and must not
retain input or output copies. Expected key-access failures expose fixed messages without
underlying secret-bearing exceptions.

These APIs do not read files, create keys, launch Brave, save plaintext, or access anything
on discovery/navigation. The operation orchestrator still needs to capture and verify
Local State, require the explicit password operation, explain authentication limits and
handle cancellation/failure with the approved raw-file-or-cancel choice. App-bound v20
and Linux keyring access remain unsupported. No attempt to bypass their protection occurs.

## Validation

Twenty synthetic tests cover envelope parsing, duplicate/unsupported rejection, bounded
inputs, cancellation, cleanup, failure redaction and a complete synthetic key-to-password
vector. Two additional native Windows tests create only a fresh synthetic DPAPI blob and
check round-trip/failure behavior. They are explicitly skipped on Linux; native Windows
execution and real Brave compatibility are still pending. No real Local State, password
database or OS key store was read during development.
