# Offline affiliation cache: schema-v7 reader

`BraveAffiliationDatabase.Read` loads an **already-isolated SQLite image** into the existing
read-only Platform `SqliteMemoryImage`. It does not open a source filename, inspect a real
profile, create/migrate a database, start a browser, call an affiliation service or use the
network. No package dependency is added. The resulting raw cache is not yet a final CSV
credential-group resolver.

## What the pinned source establishes

Chromium revision `b8c2be851f62f6a394ec57955d7f31335d7cb6d4` provides:

- [Affiliation database](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/affiliations/core/browser/affiliation_database.cc): schema version 7, compatible version 1, table evolution and exact `GetGroup` query.
- [Upstream v7 SQL fixture](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/test/data/password_manager/affiliation_db_v7.sql): concrete table/column/index shape and synthetic examples.
- [Backend](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/affiliations/core/browser/affiliation_backend.cc): grouping queries read the cache, skip already-returned facets and return cached groups.
- [Service implementation](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/affiliations/core/browser/affiliation_service_impl.cc): `GetGroupingInfo` dispatches a local backend operation.
- [Desktop factory](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/chrome/browser/affiliations/affiliation_service_factory.cc): the cache is initialized under the profile directory using the upstream filename constant. This checkpoint does not locate/read that file.

A refinement to the earlier CSV research: absent facets are not always an unknowable
relationship. `AffiliationDatabase::GetGroup` explicitly returns a singleton for a facet
missing from the cache. However, that is **before** related-domain merging. The
[password grouper](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/password_manager/core/browser/ui/passwords_grouper.cc)
then calls `MergeRelatedGroups` with PSL-extension information. The
[merge implementation](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/affiliations/core/browser/affiliation_utils.cc)
uses stored main-domain overrides or a public-suffix-derived registrable domain, including
private registries, extended by cached PSL extensions. Missing-cache singletons therefore
must not be used as final per-origin groups without that merge step.

## Reader contract

Require exact meta strings `version=7`, `last_compatible_version=1` and real `meta`,
`eq_classes`, `eq_class_members`, `eq_class_groups`, `psl_extensions` tables. Reject views,
triggers, virtual tables, failed integrity checks, unsupported schemas, dangling set
references, invalid row storage types and unsupported limits. No migrations or recovery
run. Class metadata, membership/change-password columns and foreign-key relationships are
checked even where branding is not exposed in the result.

Return immutable `BraveAffiliationCache.Groups` and `PslExtensions`. Facets retain exact
stored URI text and nullable `main_domain`; no speculative URI normalization occurs.
`GetGroup(canonicalFacet)` performs an exact ordinal lookup. If several cached sets
contain that facet, the lowest set ID wins, matching the source query's `ORDER BY c.id`.
Duplicate stored facet rows remain preserved within the cached group. A missing facet
returns one facet with `CachedSetId=null`; this is an explicitly uncached singleton,
not a claim that final browser grouping is complete. The fixture includes group-only
facets not present in `eq_class_members`; this is valid and retained.

Limits: 32 MiB input image, 64 SQLite schema objects, 100,000 rows per class/member/group
collection, 10,000 PSL extensions, 4096 characters per metadata field and 16 MiB aggregate
UTF-8 metadata. Fixed SQL and the native adapter's execution/column/row limits bound
untrusted processing. Cancellation interrupts loops/native work. Expected failures expose
fixed content-free errors. Input bytes remain caller-owned and unchanged; the native
copy is cleared on disposal. Returned metadata consists of managed strings that cannot
be reliably erased, may reveal site relationships and must not be logged. All result
`ToString` methods redact their contents.

## Fixtures, validation and remaining work

The exact upstream SQL and BSD license live under
`tests/Core.Tests/Fixtures/Affiliations`. Python SQLite generates the synthetic binary
fixture. Nineteen tests cover real fixture shape, nullable metadata, PSL values, unchanged
input, cached/missing/duplicate lookups, empty cache, unsupported schemas, corrupt/unsafe
objects, invalid types/references, bounds, cancellation and safe diagnostics. Mutations
use disposable test copies; no live profile/keyring was accessed.

[Safe main/WAL source capture](passwords-affiliation-snapshot.md) and source revalidation
are now implemented separately. Canonical facet construction, public-suffix/private-registry
matching and related-group merging remain pending before this cache can supply the CSV projector's verified resolver. There is no
claim of fresh server equivalence: this is offline cached state. Existing password/archive
behavior is unchanged, and CSV UI remains disabled.
