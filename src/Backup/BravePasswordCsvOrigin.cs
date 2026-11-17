using System.Globalization;

namespace BraveBackup.Core;

/// <summary>Source-derived browser CSV naming for a supported canonical web facet; contains no password.</summary>
public sealed class PasswordCsvWebOrigin
{
    public string Name { get; }
    public string Url { get; }
    public string GroupOrigin { get; }
    public string Scheme { get; }
    internal PasswordCsvWebOrigin(string name, string url, string groupOrigin, string scheme)
    { Name = name; Url = url; GroupOrigin = groupOrigin; Scheme = scheme; }
    public override string ToString() => "Password CSV web origin (contents hidden)";
}

/// <summary>Conservative researched ASCII HTTP/HTTPS origin display. Unsupported URL families fail, never get guessed.</summary>
public static class BravePasswordCsvOrigin
{
    /// <summary>Projects an already-canonical supported origin URL without changing its CSV URL bytes.</summary>
    /// <remarks>Does not establish credential groups, process Android facets or emulate Chromium IDN spoof/Bidi checks.</remarks>
    public static PasswordCsvWebOrigin Project(string originUrl, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(originUrl);
        token.ThrowIfCancellationRequested();
        if (originUrl.Length > 1024 * 1024) throw Unsupported();
        var secure = originUrl.StartsWith("https://", StringComparison.Ordinal);
        var scheme = secure ? "https" : originUrl.StartsWith("http://", StringComparison.Ordinal) ? "http" : null;
        if (scheme is null) throw Unsupported();
        var start = secure ? 8 : 7;
        var slash = originUrl.IndexOf('/', start);
        if (slash < 0) throw Unsupported(); // GURL's canonical HTTP URL includes a root path slash.
        var authority = originUrl[start..slash];
        if (authority.Length is 0 or > 260) throw Unsupported();
        var colon = authority.IndexOf(':');
        var host = colon < 0 ? authority : authority[..colon];
        if (!SupportedHost(host)) throw Unsupported();
        if (colon >= 0)
        {
            var port = authority.AsSpan(colon + 1);
            if (port.Length is 0 or > 5 || port[0] == '0' || !AllDecimal(port) ||
                !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number > 65535 ||
                number == (secure ? 443 : 80)) throw Unsupported();
        }
        // Deliberately accept only the canonical ASCII URI subset whose path/query/fragment bytes are preserved.
        for (var index = slash; index < originUrl.Length; index++)
        {
            if ((index & 65535) == 0) token.ThrowIfCancellationRequested();
            var value = originUrl[index];
            if (value == '%')
            {
                if (index + 2 >= originUrl.Length || !Hex(originUrl[index + 1]) || !Hex(originUrl[index + 2])) throw Unsupported();
                index += 2;
            }
            else if (!AsciiLetterOrDigit(value) && "/?:@!$&()*+,;=#-._~".IndexOf(value) < 0) throw Unsupported();
        }
        // Dot-segment normalization is a GURL responsibility; do not export an unnormalized path as canonical.
        var pathEnd = originUrl.IndexOfAny(['?', '#'], slash);
        var path = originUrl.AsSpan(slash, (pathEnd < 0 ? originUrl.Length : pathEnd) - slash);
        foreach (var segment in path.ToString().Split('/'))
            if (segment is "." or ".." || EncodedDotSegment(segment)) throw Unsupported();
        token.ThrowIfCancellationRequested();
        return new PasswordCsvWebOrigin(secure ? authority : "http://" + authority, originUrl, authority, scheme);
    }

    private static bool SupportedHost(string host)
    {
        if (host.Length is 0 or > 253 || host.EndsWith('.')) return false;
        var labels = host.Split('.');
        if (host.All(value => value is >= '0' and <= '9' or '.'))
        {
            if (labels.Length != 4) return false;
            return labels.All(part => part.Length is > 0 and <= 3 && (part.Length == 1 || part[0] != '0') &&
                int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= 255);
        }
        // A numeric final label can invoke Chromium's legacy IPv4 parser. That subset is not emulated here.
        if (labels[^1].All(value => value is >= '0' and <= '9') || labels[^1].StartsWith("0x", StringComparison.Ordinal)) return false;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.StartsWith("xn--", StringComparison.Ordinal)) return false;
            if (label.Any(value => value is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-')) return false;
        }
        return true;
    }
    private static bool EncodedDotSegment(string value)
    {
        var decoded = value.Replace("%2e", ".", StringComparison.OrdinalIgnoreCase);
        return decoded is "." or "..";
    }
    private static bool AllDecimal(ReadOnlySpan<char> value)
    { foreach (var item in value) if (item is < '0' or > '9') return false; return true; }
    private static bool AsciiLetterOrDigit(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
    private static bool Hex(char value) => value is >= 'a' and <= 'f' or >= 'A' and <= 'F' or >= '0' and <= '9';
    private static BackupOperationException Unsupported() => new("passwords-csv-origin-unsupported",
        "A credential URL is outside verified browser CSV origin-display support. Use a protected portable backup to retain its original data.");
}
