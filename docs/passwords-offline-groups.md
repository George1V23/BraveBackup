# Offline canonical facets and related-group merging

`BraveOfflineCsvGroups.Create(document, cache)` now supplies the CSV projector's resolver
from already-captured local password metadata and a validated affiliation cache. It calls
no browser, service, network, keyring or file-saving API. Source snapshots must remain
owned and be revalidated before eventual publication; the complete capture-to-save/UI
operation is still separate work.

## Source-backed rules

The pinned [facet construction](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/password_manager/core/browser/ui/passwords_grouper.cc)
parses the non-federated signon realm, strips its path/query/fragment, then passes it through
`FacetURI`. The pinned [facet implementation](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/affiliations/core/browser/affiliation_utils.cc)
canonicalizes supported HTTPS web facets without the trailing slash; HTTP is not a valid
web facet type and retains the supplied scheme/authority URL with slash. The new
`BravePasswordCsvFacet.FromRealm` follows this distinction for the existing conservative
ASCII HTTP/HTTPS subset. It is not a general URL/Android/IDN canonicalizer.

The [backend](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/affiliations/core/browser/affiliation_backend.cc)
requests only groups containing the requested facets, skips facets already present in a
returned group and uses the database's singleton result for absent facets. The
[related-group merger](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/affiliations/core/browser/affiliation_utils.cc)
merges groups sharing a stored nonempty `main_domain`, or a computed extended registrable
domain when the override is empty. Android cached facets don't contribute domain keys.
Cached group membership itself is retained, including unrequested facets in a returned
group; unreturned cache groups cannot supply speculative bridges.

The implementation selects exactly those cache/singleton groups, then uses a disjoint-set
union to merge equal domain keys. Stored overrides are compared exactly, as source strings,
without inventing normalization. For computed domains, `BravePublicSuffix.ExtendedDomain`
implements the pinned PSL rules including private registries, wildcard/exception precedence
and unknown registries. IPv4/single-label/public-suffix-only hosts have no registrable-domain
merge key. Cached PSL extensions repeatedly add one preceding host label until the domain
is no longer extended or the whole host is reached. Cached extension matching is ordinal.

`OfflineCsvGroups.Resolve` maps a login's realm facet to an opaque operation-local group
identifier. Unknown facets return null. This resolves relationships, not credential data:
the existing projector still separately groups by username/password, combines canonical
notes, deduplicates captured URLs, and fails on unsupported origins/nonempty named notes.
A cached Android facet can preserve an existing association but does not add an Android
CSV row or merge unrelated web groups by an Android `main_domain` field.

## Pinned offline data and limits

The unmodified 334130-byte
[PSL data at the researched Chromium revision](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/net/base/registry_controlled_domains/effective_tld_names.dat)
is embedded from `src/Backup/Resources/PublicSuffix`. It is MPL-2.0 licensed; the full
license, provenance and source-code access notice accompany build/publish output. No
NuGet dependency or runtime download was added. Tests pin the resource SHA-256. Both
ICANN and private rules are loaded. Unicode-only rules cannot match accepted ASCII hosts;
IDN/punycode hosts remain explicitly unsupported rather than applying a different IDN
security policy. Equality with another Brave/Chromium release's PSL is not claimed.

Operation-local ordinal extension sets avoid repeated allocations while matching many
facets. Bounds are one/two input stores, 100,000 input logins, 16 MiB cumulative realm
characters and 100,000 selected facets, plus cache-reader/PSL-extension/origin limits.
Cancellation is checked throughout group selection, suffix matching, union and result
construction. A partial overlap between nonidentical returned cached groups fails
explicitly: request-order-dependent ambiguous cache states are outside demonstrated
support. No hostname-only fallback is silently substituted.

Results contain managed facet/domain metadata, not passwords. No secret values are read
for grouping itself. Metadata cannot be reliably erased and must not be logged; result
`ToString` is redacted. Protected portable/raw backup behavior and archive formats remain
unchanged.

## Validation and next work

Twenty PSL/facet cases cover pinned-data integrity, ICANN/private rules, wildcards,
exceptions, unknown suffixes, IP/single-label/suffix-only hosts, extension chaining,
ordinal matching, canonical HTTPS/HTTP facets, unsupported hosts and cancellation.
Nine merger cases cover source-defined singleton merging, private/extension boundaries,
stored overrides and unrequested returned-facet bridges, excluding unreturned groups,
IP/local hosts, unresolved lookups, ambiguous overlaps, Android nonparticipation and
empty/cancelled scopes. All data/credentials are synthetic; no live profile was read.

A [single-profile CSV capture/projection/save operation](passwords-csv-operation.md) now
connects these components behind a conservative researched-version gate. Broader browser-release validation,
broader origin/Android support, CSV archive contracts/consent and Passwords UI remain
unfinished. Native real-keyring/Windows validation is still pending. Safe password and
affiliation source verification must bracket the eventual confirmed save.
