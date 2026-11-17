# Verified-group CSV credential projection

`BravePasswordCsvProjection.Create` connects the supported web-origin projector and
canonical-note combiner to a complete row-projection algorithm **when verified browser
group identities are supplied**. It does not load affiliation caches or enable a complete
password export operation by itself. No network, browser, keyring or file-save call occurs.

## Source evidence and unresolved input

Pinned Chromium's
[`SavedPasswordsPresenter::GetSavedCredentials`](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/password_manager/core/browser/ui/saved_passwords_presenter.cc)
uses `PasswordsGrouper::GetAllCredentials` for the desktop targets. The
[`PasswordsGrouper` implementation](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/password_manager/core/browser/ui/passwords_grouper.cc)
gets grouping information through the affiliation service, merges related groups using
its PSL-extension information, and buckets stored credentials by group identity and
username/federation identity, then actual password equality. The approved captured scope
already excludes federated/blocked entries and passkeys.

Login Data alone does not prove all those relationships. Therefore the caller must supply
`verifiedGroup(login)` using verified grouping information for every credential. The API
passes the original login metadata, including realm and URL, to that resolver; it does
not guess facet canonicalization from an arbitrary hostname/suffix. The returned opaque
group identity must be nonempty and at most 256 characters. A missing resolver or any
unresolved identity fails with `passwords-csv-grouping-unavailable`, without partial output.
Group identities must not contain secrets. The resolver must not use network requests
for the application's offline export workflow; offline affiliation-source integration
remains pending.

## Projection and ownership

For each eligible locally captured login:

1. Apply [supported ASCII web-origin display](passwords-csv-origins.md); unsupported
   URL families fail instead of being omitted or formatted with a guessed IDN rule.
2. Resolve its verified group, then bucket by group identity and username (null maps to
   the browser empty value). Within that bucket compare actual UTF-8 password bytes with
   fixed-time equality; different passwords remain different credential groups.
3. Combine [canonical notes](passwords-csv-notes.md) across every member of the group,
   including records from both local stores. Nonempty named notes fail explicitly.
4. Emit one row for each distinct captured canonical URL in the group. The URL/name
   comes from its original supported web facet. No new domain/affiliation is invented.
   The [CSV formatter](passwords-csv.md) separately sorts the final rows by UTF-8 fields.

This follows the grouped `CredentialUIEntry` note behavior and `GetAffiliatedDomains`
URL deduplication for the demonstrated web subset. Android facets and their affiliated
web realms are not supported. It does not invent cloud-only records, strip notes, or
assert that all possible browser exports are reproduced.

The returned `PasswordCsvProjection` owns independent copied passwords and combined note
values. Rows borrow those owners, potentially sharing one owner across several URLs:
do not dispose row values independently. Disposing the projection clears every owned
value and invalidates `Rows`; source documents can be disposed independently after
successful projection. Metadata remains managed strings and must not be logged. Counts
are source-login count and credential-group count, not secret values. `ToString` is redacted.

Bounds are one or two source stores, 100,000 source logins, 100,000 notes, 32 MiB combined
input secret bytes and 1,000,000 within-bucket password comparisons. The comparison ceiling
bounds pathological many-password buckets without storing unkeyed password hashes or
plaintext password strings as dictionary keys. Oversized/unsupported input fails the whole
projection. Cancellation is checked through input, group comparison and output work;
partial secret copies are disposed on any failure. Output quoting/size bounds remain the
formatter's responsibility. Original sources must be revalidated before publication.

## Fixtures and validation

`CsvProjection.synthetic.json` under `tests/Core.Tests/Fixtures/Passwords` independently
projects explicit synthetic groups in Python, deduplicates captured URLs, combines notes
with UTF-16 ordering and sorts final CSV fields by UTF-8. It includes cross-URL grouping,
duplicate URLs, different usernames/passwords and exact expected CSV text. The supplied
group IDs are synthetic test input, not observations from a real browser affiliation cache.

Twelve tests cover that fixture, both local stores, grouping boundaries, URL deduplication,
null/empty usernames, missing/unresolved groups, unsupported URLs/notes, independent
ownership/disposal, empty collections, cancellation and pathological/scope resource bounds.
No real credentials, profile, browser or keyring was accessed.

Offline affiliation/group-source loading, broader origin/Android support, CSV archive
contracts/consent and the complete Passwords UI remain unfinished. The host must not
silently choose a trivial per-origin resolver and label it verified browser grouping.
Protected portable/raw archive backup behavior is unchanged.
