# Supported web-origin CSV projection

`BravePasswordCsvOrigin.Project` implements a conservative subset of browser origin display
for **already-canonical** ASCII HTTP/HTTPS URLs. It does not canonicalize arbitrary input,
establish credential groups, derive affiliation data or turn a full local password database
into a complete Brave export. No CSV save or UI control is enabled here.

## Pinned source evidence

[`CredentialUIEntry::GetAffiliatedDomains`](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/password_manager/core/browser/ui/credential_ui_entry.cc)
uses the facet URL for a web CSV URL. Its name comes from origin display with
`OMIT_CRYPTOGRAPHIC`, not the database's display-name field. Credential sort keys use
`OMIT_HTTP_AND_HTTPS` plus a separate scheme. The source's
[`FormatOriginForSecurityDisplay`](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/url_formatter/elide_url.cc)
preserves the displayed host, omits cryptographic schemes, includes a nonzero non-default
port and applies browser-specific IDN/Bidi display checks.

For the supported subset:

| Input | CSV name | Group-origin component | Scheme component |
| --- | --- | --- | --- |
| `https://synthetic.invalid/login` | `synthetic.invalid` | `synthetic.invalid` | `https` |
| `http://synthetic.invalid/login` | `http://synthetic.invalid` | `synthetic.invalid` | `http` |
| `https://www.synthetic.invalid:8443/` | `www.synthetic.invalid:8443` | `www.synthetic.invalid:8443` | `https` |

`www` is preserved. Paths, query and fragments do not enter the name or origin-sort
component. The returned `Url` is the exact original string; the caller must already have
established that it is a canonical browser web facet. The group-origin/scheme values are
components only, not sufficient proof that two credentials belong to one browser group.

## Conservative scope and failure handling

Accept lowercase HTTP/HTTPS schemes, lowercase ASCII DNS labels (including localhost),
canonical dotted-decimal IPv4, ordinary root/path/query/fragment URLs and non-default
ports 1–65535. The grammar is deliberately narrower than all legal Chromium URLs:

- Require a root path slash; reject userinfo, empty/invalid DNS labels, trailing dots,
  uppercase host/scheme spelling, IDNs/punycode and IPv6.
- Reject short, integer, octal, hexadecimal and other legacy IPv4 interpretations.
- Reject explicit default/zero/leading-zero/invalid ports rather than normalize them.
- Require valid percent triplets and the documented ASCII URI character subset; reject
  raw Unicode, whitespace, controls/NUL, backslashes and unverified special characters.
- Reject literal or percent-encoded dot path segments rather than implement GURL path
  normalization. Dot-like segments in a query are retained as query data.

These exclusions prevent substituting generic .NET IDN/URL normalization for Chromium's
security display. They do not imply these URLs are invalid in Brave. Unsupported input
fails with `passwords-csv-origin-unsupported`, a fixed message recommending the protected
portable backup. The message includes no URL or credential data. Original protected
payloads are unaffected and preserve source values. Future UI must display this failure,
not silently omit the affected credential or claim complete CSV export.

Input is bounded to 1 MiB and authorities/labels to their supported bounds. Cancellation
is checked before projection, during the bounded path scan and before return. Results
contain immutable metadata strings and redact `ToString`; source URLs/metadata cannot be
reliably erased from managed strings and must never be logged as credentials.

## Fixtures, tests and remaining work

`CsvOrigins.synthetic.json` under `tests/Core.Tests/Fixtures/Passwords` records nine
source-derived synthetic URL/name/origin/scheme vectors. These are pinned-source fixtures,
not results from a real Brave runtime. Thirty-seven tests check those vectors, URL byte
preservation, safe errors, unsupported families, cancellation and limits. No real browser,
password database or keyring was accessed.

Browser grouping, affiliation expansion, IDN/IPv6/Android handling, CSV archive contracts
and consent, complete export UI and real native authentication compatibility remain pending.
The existing canonical-note projection remains separate; it refuses nonempty named notes
rather than losing them. Existing backup formats and writer behavior are unchanged.
