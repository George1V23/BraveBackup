namespace BraveBackup.Core;

/// <summary>Source-derived facet construction for the already-supported canonical web-realm subset.</summary>
public static class BravePasswordCsvFacet
{
    /// <summary>Strips realm path/query/fragment; canonical HTTPS facets omit the slash, invalid HTTP facets retain it as upstream does.</summary>
    public static string FromRealm(string signonRealm, CancellationToken token = default)
    {
        var origin = BravePasswordCsvOrigin.Project(signonRealm, token);
        return origin.Scheme + "://" + origin.GroupOrigin + (origin.Scheme == "https" ? "" : "/");
    }
}
