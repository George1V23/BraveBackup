# Researched browser CSV note projection

Pinned Chromium
[`StoredCredential::GetPasswordNote`](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/password_manager/core/browser/password_store/stored_credential.cc)
selects the note whose unique display name is empty. Other named notes are not returned
by that browser accessor. The grouped
[`CredentialUIEntry` constructor](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/password_manager/core/browser/ui/credential_ui_entry.cc)
collects those canonical values, removes empty values, sorts/deduplicates as UTF-16 strings,
and joins distinct values with LF. This is distinct from the CSV writer's final UTF-8
row sorting. Embedded newlines in note values remain unchanged.

`BravePasswordCsvNotes.Combine` implements this rule for an **already-established credential
group**. It does not establish grouping, derive browser display origins, expand affiliations
or turn local database rows into a complete browser export. Duplicate canonical-note
identities within one login are rejected rather than choosing one arbitrarily.

## No silent omission

The accepted application scope requires preserving supported notes. A nonempty named
note therefore produces `passwords-csv-notes-unsupported` rather than being silently
excluded to mimic the browser accessor. The content-free message recommends a protected
portable backup, which retains every note separately. Empty named notes contribute no
CSV text; an absent/empty canonical note yields empty text. Timestamps, names and flags
remain metadata of the protected payload; a CSV cell cannot preserve their structure.
This component does not invoke a raw fallback or save a CSV file on failure.

Secret values are copied into owned character arrays for UTF-16 ordinal sorting without
creating plaintext strings. Arrays are cleared in finally blocks, including failure and
cancellation. Distinct sorted values are encoded into an exact-size temporary UTF-8
buffer, copied into the returned `SensitiveUtf8` owner, and the temporary is cleared.
Input secret owners remain unchanged and caller-owned; all owners must stay alive and
unmodified during projection. Disposing the result clears its borrowed bytes.

Bounds are 100,000 grouped logins, 100,000 total notes, 32 MiB input note bytes, and 4 MiB
combined UTF-8 including separators. The bounded sort itself is not interrupted;
cancellation is checked around sorting and per login, note and output value. There are
no OS calls, file writes, authentication, credential logging or save-consent shortcuts.

## Fixtures and validation

`tests/Core.Tests/Fixtures/Passwords/CsvNotes.synthetic.json` independently computes its
expected set/join result with Python: distinct nonempty strings sorted by their UTF-16BE
encoding, then joined with LF. Values include supplementary Unicode, BMP Unicode,
duplicates, quotes and CR/LF. All data is synthetic.

Twelve tests cover the independent fixture, canonical empty/missing handling, deduplication,
UTF-16 ordering versus UTF-8 ordering, embedded newlines/NUL/quotes, named-note refusal,
duplicate identity refusal, cancellation, disposed inputs, output ownership, group limits
and combined-size limits including separators. No real password or keyring was accessed.

## Remaining projection research

Browser domain naming calls `FormatOriginForSecurityDisplay` with `OMIT_CRYPTOGRAPHIC`,
not simply a raw display-name database column. The pinned
[URL formatter](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/url_formatter/elide_url.cc)
handles schemes, ports, IDNs and security display. Credential grouping and available
facets also affect exported rows. The [ASCII HTTP/HTTPS subset](passwords-csv-origins.md) is now implemented separately.
Full domain support and grouping remain unimplemented; no equivalence claim is made
for arbitrary Brave exports. CSV archive contracts/consent, complete export UI
and real native authentication checks remain pending. Existing archives are unchanged.
