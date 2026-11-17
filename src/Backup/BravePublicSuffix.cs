namespace BraveBackup.Core;

/// <summary>Pinned offline ASCII PSL matching including private registries, wildcards, exceptions and unknown suffixes.</summary>
public static class BravePublicSuffix
{
    private sealed record Rules(HashSet<string> Exact, HashSet<string> Wildcard, HashSet<string> Exception);
    private static readonly Lazy<Rules> Pinned = new(Load);

    /// <summary>Returns the registrable domain, then applies exact cached PSL-extension expansion. Empty means no registrable domain.</summary>
    public static string ExtendedDomain(string canonicalHost, IReadOnlyCollection<string> pslExtensions,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalHost); ArgumentNullException.ThrowIfNull(pslExtensions);
        token.ThrowIfCancellationRequested();
        if (pslExtensions.Count > 10_000 || canonicalHost.Contains(':')) throw Invalid();
        // Reuse conservative host validation; neither IDN display nor arbitrary URL canonicalization is substituted.
        var origin = BravePasswordCsvOrigin.Project("https://" + canonicalHost + "/", token);
        if (origin.GroupOrigin != canonicalHost) throw Invalid();
        if (canonicalHost.All(c => c is >= '0' and <= '9' or '.')) return "";
        var labels = canonicalHost.Split('.');
        if (labels.Length < 2) return "";
        var rules = Pinned.Value;
        var suffixLabels = 1; // Chromium includes unknown registries.
        var exceptionLabels = 0;
        for (var index = 0; index < labels.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var candidate = string.Join('.', labels, index, labels.Length - index);
            var count = labels.Length - index;
            if (rules.Exception.Contains(candidate)) exceptionLabels = Math.Max(exceptionLabels, count - 1);
            if (rules.Exact.Contains(candidate)) suffixLabels = Math.Max(suffixLabels, count);
            if (index > 0 && rules.Wildcard.Contains(candidate)) suffixLabels = Math.Max(suffixLabels, count + 1);
        }
        if (exceptionLabels != 0) suffixLabels = exceptionLabels;
        if (labels.Length <= suffixLabels) return "";
        var first = labels.Length - suffixLabels - 1;
        var domain = string.Join('.', labels, first, labels.Length - first);
        // Only our immutable prepared set is reused; arbitrary caller sets may use a different comparer.
        var extensions = pslExtensions is PreparedPslExtensions prepared ? prepared.Values : new HashSet<string>(pslExtensions, StringComparer.Ordinal);
        while (first > 0 && extensions.Contains(domain))
        {
            token.ThrowIfCancellationRequested(); first--;
            domain = string.Join('.', labels, first, labels.Length - first);
        }
        return domain;
    }

    private static Rules Load()
    {
        using var stream = typeof(BravePublicSuffix).Assembly.GetManifestResourceStream("BraveBackup.PublicSuffixRules")
            ?? throw new InvalidOperationException("Pinned public-suffix data is unavailable.");
        using var reader = new StreamReader(stream);
        var exact = new HashSet<string>(StringComparer.Ordinal); var wildcard = new HashSet<string>(StringComparer.Ordinal); var exception = new HashSet<string>(StringComparer.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            // Unicode/punycode hosts are rejected by the origin layer. Unicode-only rules cannot match supported ASCII hosts.
            if (line.Any(c => c > 127)) continue;
            if (line.StartsWith('!')) exception.Add(line[1..]);
            else if (line.StartsWith("*.", StringComparison.Ordinal)) wildcard.Add(line[2..]);
            else exact.Add(line);
        }
        if (exact.Count < 1000 || wildcard.Count == 0 || exception.Count == 0) throw new InvalidOperationException("Pinned public-suffix data is incomplete.");
        return new Rules(exact, wildcard, exception);
    }
    private static BackupOperationException Invalid() => new("passwords-csv-domain-unsupported",
        "Related-domain matching is outside supported offline public-suffix limits; grouping was not guessed.");
}

/// <summary>Operation-local immutable ordinal extensions, avoiding repeated set allocations for each cached facet.</summary>
internal sealed class PreparedPslExtensions(IEnumerable<string> values) : IReadOnlyCollection<string>
{
    internal HashSet<string> Values { get; } = new(values, StringComparer.Ordinal);
    public int Count => Values.Count;
    public IEnumerator<string> GetEnumerator() => Values.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
