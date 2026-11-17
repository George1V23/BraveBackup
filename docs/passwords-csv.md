# Password CSV formatting checkpoint

`PasswordCsvFormat.Encode` in Core formats **already-projected** `PasswordCsvRow` values
using the researched browser CSV syntax. It performs no browser access, grouping, file
save, archive write, authentication or consent handling. Password backup/export UI is
not enabled by this component.

The format follows Chromium's pinned
[password CSV writer](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/password_manager/core/browser/export/password_csv_writer.cc)
and [CSV quoting implementation](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/password_manager/core/browser/export/csv_writer.cc).
See [password research](passwords-research.md) for fixture provenance and projection limits.

## Contract

- Columns are exactly `name,url,username,password,note`, UTF-8 without a BOM.
- Rows sort lexicographically by all five columns, comparing UTF-8 bytes as unsigned
  values, matching the source's byte-string order. Input order is not changed; duplicates
  are retained. UTF-16 ordinal string comparison would differ for some Unicode values.
- Fields containing comma, quote, CR or LF are quoted; embedded quotes are doubled.
  Other bytes, including Unicode, NUL and formula-like prefixes, are preserved without
  normalization or spreadsheet-oriented changes to credential values.
- `PasswordCsvLineEnding` explicitly selects Linux LF or Windows CRLF. Every record,
  including the header and final record, ends with that separator. Embedded field
  newlines are preserved. An empty collection produces the header and terminator.
- Null username becomes the browser CSV's empty field; name/URL and secret owners are
  required. Malformed UTF-16 metadata is rejected instead of replacement-encoded.
- Maximums are 100,000 rows, 1 MiB UTF-8 per metadata field, 4 MiB per individual secret
  through `SensitiveUtf8`, and 64 MiB total escaped output. Preflight measures quoted
  lengths before allocating the exact-size output buffer.

`PasswordCsvRow` borrows the caller's password/note owners; it does not dispose them.
Keep them alive and unchanged throughout formatting. Temporary encoded metadata is
cleared on every exit. Password/note values are read directly as UTF-8 bytes, without
creating plaintext strings. The returned `SensitiveBytes` independently owns the entire
plaintext CSV and clears it on disposal; failed/cancelled output buffers are also cleared.
Cancellation is checked per row/field, around the bounded sort, and every 64 KiB while
writing fields. The bounded sort itself is not interrupted. Metadata supplied as managed
strings cannot be reliably erased. All diagnostic `ToString` methods are redacted.

## What remains before export

Rows must first be correctly projected from captured credentials. Browser grouping,
shown-domain name derivation, affiliated-domain expansion, Android facets and combining
distinct nonempty notes are not implemented by this formatter. A direct database dump
must not be labeled identical to Brave's complete export. No raw/native password files
are represented as successful CSV output. The portable protected payload retains separate
notes and metadata that CSV cannot preserve.

Each local CSV save still requires an explicit CSV choice and fresh confirmation for
its actual destination. Standalone/unencrypted saves must warn that anyone with file
access can read credentials. An encrypted-container warning must explain readability
after decryption/extraction. Formatting bytes in memory does not grant save consent.
[Confirmed standalone saving](passwords-csv-save.md) now implements destination handling
and source revalidation as a separate component. Browser projection, archive integration,
reports and UI controls remain pending, as do real native authentication checks.

## Tests

Seventeen synthetic tests compare exact bytes with independently generated Linux/Windows
fixtures under `tests/Core.Tests/Fixtures/Passwords`, then cover quoting, CR/LF, Unicode,
embedded NUL, unchanged formula prefixes, five-column sorting, UTF-8 versus UTF-16 order,
duplicates, header-only output, null usernames, ownership/disposal, redacted diagnostics,
malformed/oversized input, row/output limits and cancellation. No real passwords or
keyrings are accessed, and no plaintext credentials are saved by the implementation.
