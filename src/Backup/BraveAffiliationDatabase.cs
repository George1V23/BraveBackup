using BraveBackup.Platform;
using Microsoft.Data.Sqlite;

namespace BraveBackup.Core;

/// <summary>One stored grouping facet. Metadata may identify websites; never log it.</summary>
public sealed class CachedAffiliationFacet(string uri, string? mainDomain)
{
    public string Uri { get; } = uri;
    public string? MainDomain { get; } = mainDomain;
    public override string ToString() => "Cached affiliation facet (contents hidden)";
}

/// <summary>Cached native group, or the upstream-defined singleton result for an absent facet.</summary>
public sealed class CachedAffiliationGroup
{
    public long? CachedSetId { get; }
    public IReadOnlyList<CachedAffiliationFacet> Facets { get; }
    internal CachedAffiliationGroup(long? id, IEnumerable<CachedAffiliationFacet> facets)
    { CachedSetId = id; Facets = Array.AsReadOnly(facets.ToArray()); }
    public override string ToString() => "Cached affiliation group (contents hidden)";
}

/// <summary>Immutable schema-v7 cache data. Raw cache groups are not final browser groups until related-domain merging.</summary>
public sealed class BraveAffiliationCache
{
    private readonly Dictionary<string, CachedAffiliationGroup> _firstGroup;
    public IReadOnlyList<CachedAffiliationGroup> Groups { get; }
    public IReadOnlyList<string> PslExtensions { get; }
    internal BraveAffiliationCache(IEnumerable<CachedAffiliationGroup> groups, IEnumerable<string> extensions)
    {
        Groups = Array.AsReadOnly(groups.ToArray()); PslExtensions = Array.AsReadOnly(extensions.ToArray());
        _firstGroup = new(StringComparer.Ordinal);
        foreach (var group in Groups)
        foreach (var facet in group.Facets) _firstGroup.TryAdd(facet.Uri, group);
    }
    /// <summary>Matches exact canonical facet text; the lowest set ID wins as in the pinned GetGroup SQL.</summary>
    public CachedAffiliationGroup GetGroup(string canonicalFacet)
    {
        ArgumentException.ThrowIfNullOrEmpty(canonicalFacet);
        if (canonicalFacet.Length > 4096) throw new ArgumentException("Facet exceeds its supported limit.", nameof(canonicalFacet));
        return _firstGroup.TryGetValue(canonicalFacet, out var group) ? group :
            new CachedAffiliationGroup(null, [new CachedAffiliationFacet(canonicalFacet, null)]);
    }
    public override string ToString() => "Affiliation cache (contents hidden)";
}

/// <summary>Reads a supplied isolated schema-v7 image without source paths, migrations, network or browser services.</summary>
public static class BraveAffiliationDatabase
{
    /// <summary>Validates bounded native cache rows and loads raw groups/PSL extensions for later offline grouping.</summary>
    public static BraveAffiliationCache Read(byte[] image, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(image); token.ThrowIfCancellationRequested();
        if (image.Length is < 512 or > 32 * 1024 * 1024) throw Invalid();
        try
        {
            using var memory = new SqliteMemoryImage(image, token);
            var db = memory.Connection;
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT name,type,rootpage FROM sqlite_schema LIMIT 65";
                using var reader = command.ExecuteReader(); var count = 0; var tables = new HashSet<string>(StringComparer.Ordinal);
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (++count > 64 || reader.GetString(1) is "view" or "trigger" ||
                        (reader.GetString(1) == "table" && reader.GetInt64(2) == 0)) throw Invalid();
                    if (reader.GetString(1) == "table") tables.Add(reader.GetString(0));
                }
                if (!new[] { "meta", "eq_classes", "eq_class_members", "eq_class_groups", "psl_extensions" }.All(tables.Contains)) throw Invalid();
            }
            using (var command = db.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check";
                using var reader = command.ExecuteReader();
                if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read()) throw Invalid();
            }
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT key,value FROM meta WHERE key IN ('version','last_compatible_version')";
                using var reader = command.ExecuteReader(); var versions = new Dictionary<string, string>(StringComparer.Ordinal);
                while (reader.Read())
                    if (reader.GetValue(0) is not string key || reader.GetValue(1) is not string value || !versions.TryAdd(key, value)) throw Invalid();
                if (versions.Count != 2 || versions.GetValueOrDefault("version") != "7" || versions.GetValueOrDefault("last_compatible_version") != "1")
                    throw new BackupOperationException("passwords-affiliation-schema-unsupported", "The offline affiliation cache schema is unsupported; grouping was not guessed.");
            }
            long textBytes = 0;
            var classes = new HashSet<long>();
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT id,last_update_time,group_display_name,group_icon_url FROM eq_classes ORDER BY id LIMIT 100001";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var id = Integer(reader, 0);
                    if (id <= 0 || classes.Count >= 100_000 || !classes.Add(id) || (!reader.IsDBNull(1) && reader.GetValue(1) is not long)) throw Invalid();
                    _ = Text(reader, 2, true); _ = Text(reader, 3, true);
                }
            }
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT id,facet_uri,set_id,facet_display_name,facet_icon_url,change_password_url FROM eq_class_members LIMIT 100001";
                using var reader = command.ExecuteReader(); var ids = new HashSet<long>(); var facets = new HashSet<string>(StringComparer.Ordinal);
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var id = Integer(reader, 0); var facet = Text(reader, 1, false)!;
                    if (ids.Count >= 100_000 || id <= 0 || !ids.Add(id) || facet.Length == 0 || !facets.Add(facet) || !classes.Contains(Integer(reader, 2))) throw Invalid();
                    _ = Text(reader, 3, true); _ = Text(reader, 4, true); _ = Text(reader, 5, true);
                }
            }
            var groups = new SortedDictionary<long, List<CachedAffiliationFacet>>();
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT id,facet_uri,set_id,main_domain,change_password_url FROM eq_class_groups ORDER BY set_id,id LIMIT 100001";
                using var reader = command.ExecuteReader(); var ids = new HashSet<long>();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var id = Integer(reader, 0); var facet = Text(reader, 1, false)!; var set = Integer(reader, 2);
                    var domain = Text(reader, 3, true); _ = Text(reader, 4, true);
                    if (ids.Count >= 100_000 || id <= 0 || !ids.Add(id) || facet.Length == 0 || !classes.Contains(set)) throw Invalid();
                    if (!groups.TryGetValue(set, out var list)) { list = []; groups.Add(set, list); }
                    list.Add(new CachedAffiliationFacet(facet, domain));
                }
            }
            var extensions = new HashSet<string>(StringComparer.Ordinal);
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT domain FROM psl_extensions ORDER BY domain LIMIT 10001";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var domain = Text(reader, 0, false)!;
                    if (extensions.Count >= 10_000 || domain.Length == 0 || !extensions.Add(domain)) throw Invalid();
                }
            }
            token.ThrowIfCancellationRequested();
            return new BraveAffiliationCache(groups.Select(pair => new CachedAffiliationGroup(pair.Key, pair.Value)), extensions.Order(StringComparer.Ordinal));

            string? Text(SqliteDataReader reader, int column, bool nullable)
            {
                if (reader.IsDBNull(column)) { if (nullable) return null; throw Invalid(); }
                if (reader.GetValue(column) is not string value || value.Length > 4096) throw Invalid();
                textBytes += System.Text.Encoding.UTF8.GetByteCount(value);
                if (textBytes > 16L * 1024 * 1024) throw Invalid();
                return value;
            }
        }
        catch (Exception error) when (error is SqliteException or InvalidDataException or ArgumentException or OverflowException)
        { token.ThrowIfCancellationRequested(); throw Invalid(); }
    }
    private static long Integer(SqliteDataReader reader, int column) => reader.GetValue(column) is long value ? value : throw Invalid();
    private static BackupOperationException Invalid() => new("passwords-affiliation-invalid",
        "The offline affiliation cache is corrupt, unsafe, unsupported or over-limit; grouping did not complete.");
}
