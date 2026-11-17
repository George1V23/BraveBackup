using Microsoft.Data.Sqlite;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class OfflineCsvGroupsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OfflineGroups_" + Guid.NewGuid().ToString("N"));
    private readonly List<SensitiveUtf8> _values = [];
    private PortablePasswordLogin Login(long id, string url)
    {
        var secret = new SensitiveUtf8("synthetic-password"u8); _values.Add(secret);
        return new PortablePasswordLogin(id, url, url, "user", null, secret, []);
    }
    private static PortablePasswordDocument Document(params PortablePasswordLogin[] logins) => new([new PortablePasswordStore(PortablePasswordStoreKind.Profile, 0, 0, logins)]);
    private BraveAffiliationCache Cache(string additions = "", bool empty = true)
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".sqlite");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Affiliations", "Affiliation.v7.synthetic.sqlite"), path);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=OFF; " +
                (empty ? "DELETE FROM eq_class_members; DELETE FROM eq_class_groups; DELETE FROM eq_classes; DELETE FROM psl_extensions; " : "") + additions;
            command.ExecuteNonQuery();
        }
        return BraveAffiliationDatabase.Read(File.ReadAllBytes(path));
    }

    [Fact]
    public void SourceSingletonsMergeByRegistrableDomainAcrossSchemesAndPorts()
    {
        var first = Login(1, "https://a.example.com/login"); var second = Login(2, "http://b.example.com:8080/login");
        using var document = Document(first, second); var groups = BraveOfflineCsvGroups.Create(document, Cache());
        Assert.Equal(1, groups.GroupCount); Assert.Equal(groups.Resolve(first), groups.Resolve(second));
        using var projected = BravePasswordCsvProjection.Create(document, groups.Resolve);
        Assert.Equal(1, projected.CredentialGroupCount); Assert.Equal(2, projected.Rows.Count);
    }

    [Fact]
    public void PrivateRegistryBoundariesAndCachedExtensionsPreventFalseMerges()
    {
        var first = Login(1, "https://a.blogspot.com/"); var second = Login(2, "https://b.blogspot.com/");
        using var privateDocument = Document(first, second);
        Assert.Equal(2, BraveOfflineCsvGroups.Create(privateDocument, Cache()).GroupCount);
        using var extended = Document(Login(3, "https://a.example.com/"), Login(4, "https://b.example.com/"));
        Assert.Equal(2, BraveOfflineCsvGroups.Create(extended, Cache("INSERT INTO psl_extensions(domain) VALUES('example.com');")).GroupCount);
    }

    [Fact]
    public void StoredOverridesAndCachedUnrequestedFacetsCanBridgeGroups()
    {
        var cache = Cache("INSERT INTO eq_classes(id) VALUES(1),(2); " +
            "INSERT INTO eq_class_groups(facet_uri,set_id,main_domain) VALUES" +
            "('https://a.one.invalid',1,'one.invalid'),('https://bridge.two.invalid',1,'bridge-domain'),('https://b.two.invalid',2,'bridge-domain');");
        var first = Login(1, "https://a.one.invalid/"); var second = Login(2, "https://b.two.invalid/");
        using var document = Document(first, second); var groups = BraveOfflineCsvGroups.Create(document, cache);
        Assert.Equal(1, groups.GroupCount); Assert.Equal(3, groups.FacetCount);
        using var rows = BravePasswordCsvProjection.Create(document, groups.Resolve);
        Assert.Equal(2, rows.Rows.Count); Assert.DoesNotContain(rows.Rows, row => row.Url.Contains("bridge."));
    }

    [Fact]
    public void UnrequestedUnreturnedGroupsDoNotBridgeRequestedSingletons()
    {
        var cache = Cache("INSERT INTO eq_classes(id) VALUES(1); INSERT INTO eq_class_groups(facet_uri,set_id,main_domain) VALUES('https://unrequested.invalid',1,'one.invalid');");
        using var document = Document(Login(1, "https://a.one.invalid/"), Login(2, "https://b.two.invalid/"));
        Assert.Equal(2, BraveOfflineCsvGroups.Create(document, cache).GroupCount);
    }

    [Fact]
    public void IpAndSingleLabelHostsDoNotInventRegistrableDomains()
    {
        using var document = Document(Login(1, "https://127.0.0.1/"), Login(2, "https://localhost/"));
        Assert.Equal(2, BraveOfflineCsvGroups.Create(document, Cache()).GroupCount);
    }

    [Fact]
    public void UnknownFacetRemainsUnresolvedAndMetadataIsRedacted()
    {
        using var document = Document(Login(1, "https://a.example.com/")); var groups = BraveOfflineCsvGroups.Create(document, Cache());
        Assert.Null(groups.Resolve(Login(2, "https://unrequested.invalid/")));
        Assert.DoesNotContain("example.com", groups.ToString());
    }

    [Fact]
    public void PartialCachedGroupOverlapFailsRatherThanUsingRequestOrder()
    {
        var cache = Cache("INSERT INTO eq_classes(id) VALUES(1),(2); INSERT INTO eq_class_groups(facet_uri,set_id) VALUES" +
            "('https://a.one.invalid',1),('https://overlap.invalid',1),('https://b.two.invalid',2),('https://overlap.invalid',2);");
        using var document = Document(Login(1, "https://a.one.invalid/"), Login(2, "https://b.two.invalid/"));
        Assert.Equal("passwords-csv-offline-groups-unsupported", Assert.Throws<BackupOperationException>(() => BraveOfflineCsvGroups.Create(document, cache)).Code);
    }

    [Fact]
    public void CancellationAndEmptyCollectionAreHandled()
    {
        using var document = Document(); var cache = Cache();
        var groups = BraveOfflineCsvGroups.Create(document, cache); Assert.Equal(0, groups.GroupCount);
        Assert.Throws<OperationCanceledException>(() => BraveOfflineCsvGroups.Create(document, cache, new CancellationToken(true)));
    }

    [Fact]
    public void CachedAndroidFacetDoesNotSupplyADomainMergeKey()
    {
        var cache = Cache("INSERT INTO eq_classes(id) VALUES(1),(2); INSERT INTO eq_class_groups(facet_uri,set_id,main_domain) VALUES" +
            "('https://a.one.invalid',1,'one.invalid'),('android://hash@com.synthetic.app',1,'two.invalid'),('https://b.two.invalid',2,'two.invalid');");
        using var document = Document(Login(1, "https://a.one.invalid/"), Login(2, "https://b.two.invalid/"));
        var groups = BraveOfflineCsvGroups.Create(document, cache); Assert.Equal(2, groups.GroupCount);
        using var rows = BravePasswordCsvProjection.Create(document, groups.Resolve); Assert.Equal(2, rows.Rows.Count);
    }

    public void Dispose() { foreach (var value in _values) value.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
