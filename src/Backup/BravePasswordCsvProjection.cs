using System.Security.Cryptography;

namespace BraveBackup.Core;

/// <summary>Owns independently copied CSV passwords and combined notes; rows borrow these owners until disposal.</summary>
public sealed class PasswordCsvProjection : IDisposable
{
    private IReadOnlyList<PasswordCsvRow>? _rows;
    private readonly List<SensitiveUtf8> _values;
    public IReadOnlyList<PasswordCsvRow> Rows => _rows ?? throw new ObjectDisposedException(nameof(PasswordCsvProjection));
    public int SourceLoginCount { get; }
    public int CredentialGroupCount { get; }
    internal PasswordCsvProjection(List<PasswordCsvRow> rows, List<SensitiveUtf8> values, int sourceCount, int groups)
    { _rows = rows.AsReadOnly(); _values = values; SourceLoginCount = sourceCount; CredentialGroupCount = groups; }
    /// <summary>Clears all owned values; never dispose the secret owners borrowed by individual rows separately.</summary>
    public void Dispose() { foreach (var value in _values) value.Dispose(); _values.Clear(); _rows = null; }
    public override string ToString() => "Password CSV projection (contents hidden)";
}

/// <summary>Projects supported web credentials using established affiliation-group identities, never guessing those relationships.</summary>
public static class BravePasswordCsvProjection
{
    /// <summary>Groups by verified identity, username and password, combines notes and emits each group's distinct captured URLs.</summary>
    /// <remarks>
    /// The resolver must use verified affiliation/grouping information for every login, not infer equality from a site suffix.
    /// No browser cache, network, keyring, file save or consent operation occurs here. Input must remain alive/unchanged.
    /// </remarks>
    public static PasswordCsvProjection Create(PortablePasswordDocument document,
        Func<PortablePasswordLogin, string?>? verifiedGroup, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        token.ThrowIfCancellationRequested();
        if (verifiedGroup is null) throw Unavailable();
        var owned = new List<SensitiveUtf8>();
        try
        {
            var buckets = new Dictionary<(string Identity, string Username), List<Group>>();
            var groups = new List<Group>();
            if (document.Stores.Count is < 1 or > 2 || document.Stores.Sum(store => (long)store.Logins.Count) > 100_000) throw Limit();
            var sourceCount = 0;
            var noteCount = 0;
            var comparisons = 0;
            long secretBytes = 0;
            foreach (var store in document.Stores)
            foreach (var login in store.Logins)
            {
                token.ThrowIfCancellationRequested();
                if (++sourceCount > 100_000) throw Limit();
                secretBytes += login.Password.Utf8.Length;
                foreach (var note in login.Notes)
                {
                    token.ThrowIfCancellationRequested();
                    if (++noteCount > 100_000) throw Limit();
                    secretBytes += note.Value.Utf8.Length;
                }
                if (secretBytes > 32L * 1024 * 1024) throw Limit();
                var origin = BravePasswordCsvOrigin.Project(login.Origin, token);
                var identity = verifiedGroup(login);
                if (string.IsNullOrEmpty(identity) || identity.Length > 256) throw Unavailable();
                var key = (identity, login.Username ?? "");
                if (!buckets.TryGetValue(key, out var candidates)) { candidates = []; buckets.Add(key, candidates); }
                Group? group = null;
                foreach (var candidate in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    if (++comparisons > 1_000_000) throw Limit();
                    if (CryptographicOperations.FixedTimeEquals(candidate.Logins[0].Password.Utf8.Span, login.Password.Utf8.Span))
                    { group = candidate; break; }
                }
                if (group is null) { group = new Group(key.Item2); candidates.Add(group); groups.Add(group); }
                group.Logins.Add(login);
                if (group.Urls.Add(origin.Url)) group.Origins.Add(origin);
            }
            var rows = new List<PasswordCsvRow>();
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                var note = BravePasswordCsvNotes.Combine(group.Logins, token); owned.Add(note);
                var password = new SensitiveUtf8(group.Logins[0].Password.Utf8.Span); owned.Add(password);
                foreach (var origin in group.Origins)
                {
                    token.ThrowIfCancellationRequested();
                    rows.Add(new PasswordCsvRow(origin.Name, origin.Url, group.Username, password, note));
                }
            }
            token.ThrowIfCancellationRequested();
            return new PasswordCsvProjection(rows, owned, sourceCount, groups.Count);
        }
        catch { foreach (var value in owned) value.Dispose(); throw; }
    }
    private sealed class Group(string username)
    {
        public string Username { get; } = username;
        public List<PortablePasswordLogin> Logins { get; } = [];
        public HashSet<string> Urls { get; } = new(StringComparer.Ordinal);
        public List<PasswordCsvWebOrigin> Origins { get; } = [];
    }
    private static BackupOperationException Unavailable() => new("passwords-csv-grouping-unavailable",
        "Verified browser affiliation/grouping information is required for every credential. No CSV grouping was guessed.");
    private static BackupOperationException Limit() => new("passwords-csv-projection-limit",
        "CSV credential projection exceeds supported resource limits; no credential was silently omitted.");
}
