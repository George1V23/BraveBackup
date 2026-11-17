# Confirmed standalone password CSV save

`PasswordCsvFileExport.SaveAsync` implements the local save transaction for
[already-projected CSV rows](passwords-csv.md). It does not access browser credentials,
project/group database rows, enable an Export control or save CSV inside an archive.
No real credentials were read or saved while implementing it.

## Explicit choice and fresh warning

The caller must explicitly supply `csvExplicitlyChosen=true`; protected-default selection
or disabling archive encryption does not authorize this API. Each invocation calls the
host's confirmation callback with a new `PasswordCsvExportWarning`, naming the normalized
full destination and whether its existing file will be replaced. The warning states that
anyone with file access can read saved credentials and that a temporary plaintext file
will also be used in that folder. It includes no credential values. Consent is never
cached, including when repeating the same destination. This warning applies to standalone
plaintext files only; it must not be reused to describe encrypted-container output.

Before confirmation, the transaction freezes row/root collections, checks cancellation,
validates the destination, calls the supplied source verifier on a worker, and fingerprints
any existing destination. It creates no files or folders. Decline, cancellation, callback
failure or an initial source failure prevents any write. The confirmation callback resumes
on the caller's captured context when available; hosts without a UI context must dispatch
their own dialog. Row secret owners remain borrowed/alive until the operation finishes.

## Staging and publication

After confirmation, worker-thread code rechecks destination safety/fingerprint and sources,
formats the CSV into an owned memory buffer, and creates an exclusive sibling temporary
file using Platform's existing private-file helper. Linux permissions are owner read/write;
Windows uses a current-user-only ACL. It writes in bounded chunks, flushes to disk, reopens
and hashes the staged file, then compares that hash with the exact formatted payload.

Before atomic rename publication it checks source stability, destination safety and the
original destination fingerprint again, then cancellation. Same-size/same-timestamp
changes are detected by SHA-256. A previously absent file appearing also requires a fresh
operation/warning. These are best-effort race checks, not a filesystem compare-and-swap
primitive against hostile concurrent directory manipulation. Standard and supplied custom
browser roots, links/reparse paths and non-regular destinations are rejected. The caller
must supply all custom browser roots and a verifier for the actual selected profile.

An existing destination folder is required, a `.csv` extension is required, and an existing
file above the formatter's 64 MiB limit is refused. The operation does not create browser
files, modify profiles or create destination directories. Payload and hashing buffers are
cleared on exit. Successful results include only destination, row count and byte length.

Failure/cancellation before publication preserves the original destination and removes the
owned temporary file. If removing temporary plaintext fails, an explicit cleanup failure
tells the user to check that folder. Deletion is not guaranteed secure erasure. The host
must show this failure and must not report a successful export. No automated retry bypasses
fresh confirmation.

## Validation and remaining work

Sixteen synthetic transaction tests cover exact output, fresh/repeated consent, explicit
selection, replacement details, owner-only Linux permissions, decline/cancellation, UI
callback failure, source failures before/after staging, same-stamp destination changes,
changes during final source verification, invalid rows/destinations and temporary cleanup.
All files belong to disposable synthetic test folders; native Windows ACL validation
remains pending.

Browser CSV projection and credential grouping/affiliation semantics remain unfinished.
The API accepts prepared rows so that file safety and consent can be verified independently;
it does not claim that a raw database-row dump matches Brave's export. UI integration,
encrypted-archive CSV consent, portable password archive saving, explicit raw-file fallback
and real native authentication validation remain pending. Password export remains disabled
in the UI until the full flow is implemented and reviewed.
