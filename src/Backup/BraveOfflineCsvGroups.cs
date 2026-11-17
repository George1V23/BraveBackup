using System.Globalization;

namespace BraveBackup.Core;

/// <summary>Immutable operation-local offline group identities; includes no passwords and never logs facet metadata.</summary>
public sealed class OfflineCsvGroups
{
    private readonly Dictionary<string, string> _groups;
    public int GroupCount { get; }
    public int FacetCount => _groups.Count;
    internal OfflineCsvGroups(Dictionary<string, string> groups, int groupCount) { _groups = groups; GroupCount = groupCount; }
    /// <summary>Resolves the source-derived realm facet; an unknown facet remains unresolved, never a guessed group.</summary>
    public string? Resolve(PortablePasswordLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        return _groups.GetValueOrDefault(BravePasswordCsvFacet.FromRealm(login.Realm));
    }
    public override string ToString() => "Offline CSV groups (contents hidden)";
}

/// <summary>Combines the pinned cache lookup, realm-facet and related-domain rules for the supported web subset.</summary>
public static class BraveOfflineCsvGroups
{
    /// <summary>Selects requested cached/singleton groups and merges them using stored overrides, PSL/private rules and extensions.</summary>
    /// <remarks>Uses only supplied immutable cache data. Caller must retain/revalidate password and cache snapshots before saving.</remarks>
    public static OfflineCsvGroups Create(PortablePasswordDocument document, BraveAffiliationCache cache,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(cache); token.ThrowIfCancellationRequested();
        if (document.Stores.Count is < 1 or > 2 || document.Stores.Sum(store => (long)store.Logins.Count) > 100_000) throw Invalid();
        var selected = new List<CachedAffiliationGroup>();
        var facetToGroup = new Dictionary<string, int>(StringComparer.Ordinal);
        long realmCharacters = 0;
        foreach (var store in document.Stores)
        foreach (var login in store.Logins)
        {
            token.ThrowIfCancellationRequested();
            realmCharacters += login.Realm.Length;
            if (realmCharacters > 16L * 1024 * 1024) throw Invalid();
            var requested = BravePasswordCsvFacet.FromRealm(login.Realm, token);
            if (facetToGroup.ContainsKey(requested)) continue;
            var group = cache.GetGroup(requested);
            var index = selected.Count;
            foreach (var facet in group.Facets)
            {
                token.ThrowIfCancellationRequested();
                // Partial overlaps are outside demonstrated coherent-cache state; don't depend on request ordering.
                if (facetToGroup.TryGetValue(facet.Uri, out var existing) && existing != index) throw Invalid();
                facetToGroup[facet.Uri] = index;
                if (facetToGroup.Count > 100_000) throw Invalid();
            }
            if (!facetToGroup.ContainsKey(requested)) throw Invalid();
            selected.Add(group);
        }
        var parents = Enumerable.Range(0, selected.Count).ToArray();
        var domainToGroup = new Dictionary<string, int>(StringComparer.Ordinal);
        var extensions = new PreparedPslExtensions(cache.PslExtensions);
        for (var index = 0; index < selected.Count; index++)
        foreach (var facet in selected[index].Facets)
        {
            token.ThrowIfCancellationRequested();
            // Source cached facets use FromCanonicalSpec: Android scheme facets don't participate in domain merging.
            if (facet.Uri.StartsWith("android://", StringComparison.Ordinal)) continue;
            var url = facet.Uri.StartsWith("https://", StringComparison.Ordinal) ? facet.Uri + "/" : facet.Uri;
            var origin = BravePasswordCsvOrigin.Project(url, token);
            if (BravePasswordCsvFacet.FromRealm(url, token) != facet.Uri) throw Invalid();
            var host = origin.GroupOrigin.Split(':')[0]; // Only the supported DNS/IPv4 subset; IPv6 is rejected upstream.
            var domain = string.IsNullOrEmpty(facet.MainDomain) ? BravePublicSuffix.ExtendedDomain(host, extensions, token) : facet.MainDomain;
            if (domain.Length == 0) continue;
            if (domainToGroup.TryGetValue(domain, out var other))
            { var first = Find(index); var second = Find(other); parents[Math.Max(first, second)] = Math.Min(first, second); }
            else domainToGroup.Add(domain, index);
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var roots = new HashSet<int>();
        foreach (var pair in facetToGroup)
        {
            token.ThrowIfCancellationRequested(); var root = Find(pair.Value); roots.Add(root);
            result.Add(pair.Key, "offline-group-" + root.ToString(CultureInfo.InvariantCulture));
        }
        return new OfflineCsvGroups(result, roots.Count);

        int Find(int index)
        {
            while (parents[index] != index) { parents[index] = parents[parents[index]]; index = parents[index]; }
            return index;
        }
    }
    private static BackupOperationException Invalid() => new("passwords-csv-offline-groups-unsupported",
        "Offline grouping data is ambiguous, unsupported or over-limit; credential relationships were not guessed.");
}
