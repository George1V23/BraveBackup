using Microsoft.Data.Sqlite;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class AffiliationDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AffiliationReader_" + Guid.NewGuid().ToString("N"));
    private static byte[] Fixture => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Affiliations", "Affiliation.v7.synthetic.sqlite"));
    private byte[] Mutate(string sql, string? text = null)
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".sqlite");
        File.WriteAllBytes(path, Fixture);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=OFF; " + sql;
            if (text is not null) command.Parameters.AddWithValue("$text", text);
            command.ExecuteNonQuery();
        }
        return File.ReadAllBytes(path);
    }

    [Fact]
    public void ReadsExactUpstreamV7GroupsExtensionsAndNullableMetadataWithoutChangingInput()
    {
        var bytes = Fixture; var before = bytes.ToArray();
        var cache = BraveAffiliationDatabase.Read(bytes);
        Assert.Equal(before, bytes); Assert.Equal(3, cache.Groups.Count);
        var group = cache.GetGroup("https://alpha.example.com");
        Assert.Equal(1L, group.CachedSetId);
        Assert.Equal(new[] { "https://alpha.example.com", "https://beta.example.com", "https://gamma.example.com" }, group.Facets.Select(f => f.Uri));
        Assert.All(group.Facets, f => Assert.Equal("example.com", f.MainDomain));
        Assert.Equal(3, cache.GetGroup("https://delta.example.com").Facets.Count);
        Assert.Null(cache.GetGroup("https://delta.example.com").Facets[0].MainDomain);
        Assert.Equal(new[] { "app.com", "example.com", "news.com" }, cache.PslExtensions);
        Assert.DoesNotContain("example.com", cache.ToString()); Assert.DoesNotContain("example.com", group.ToString());
        Assert.DoesNotContain("example.com", group.Facets[0].ToString());
    }

    [Fact]
    public void MissingFacetReturnsTheSourceDefinedSingletonWithoutClaimingCachedIdentity()
    {
        var cache = BraveAffiliationDatabase.Read(Fixture);
        var missing = cache.GetGroup("https://absent.synthetic.invalid");
        Assert.Null(missing.CachedSetId);
        Assert.Equal("https://absent.synthetic.invalid", Assert.Single(missing.Facets).Uri);
        Assert.Null(missing.Facets[0].MainDomain);
    }

    [Fact]
    public void LowestCachedSetWinsForRepeatedFacetAcrossGroups()
    {
        var image = Mutate("INSERT INTO eq_class_groups(facet_uri,set_id,main_domain) VALUES('https://alpha.example.com',2,'different.invalid')");
        var cache = BraveAffiliationDatabase.Read(image);
        Assert.Equal(1L, cache.GetGroup("https://alpha.example.com").CachedSetId);
    }

    [Fact]
    public void DuplicateNativeFacetRowsRemainUninterpretedMembersOfTheirCachedGroup()
    {
        var image = Mutate("INSERT INTO eq_class_groups(facet_uri,set_id) VALUES('https://alpha.example.com',1)");
        Assert.Equal(4, BraveAffiliationDatabase.Read(image).GetGroup("https://alpha.example.com").Facets.Count);
    }

    [Fact]
    public void ValidatedEmptyCacheStillUsesMissingFacetSingleton()
    {
        var image = Mutate("DELETE FROM eq_class_members; DELETE FROM eq_class_groups; DELETE FROM eq_classes; DELETE FROM psl_extensions");
        var cache = BraveAffiliationDatabase.Read(image);
        Assert.Empty(cache.Groups); Assert.Empty(cache.PslExtensions);
        Assert.Null(cache.GetGroup("https://absent.synthetic.invalid").CachedSetId);
    }

    [Theory]
    [InlineData("UPDATE meta SET value='8' WHERE key='version'")]
    [InlineData("UPDATE meta SET value='2' WHERE key='last_compatible_version'")]
    [InlineData("DELETE FROM meta WHERE key='version'")]
    public void UnsupportedSchemaNeverMigratesOrGuesses(string sql)
    {
        var error = Assert.Throws<BackupOperationException>(() => BraveAffiliationDatabase.Read(Mutate(sql)));
        Assert.Equal("passwords-affiliation-schema-unsupported", error.Code);
    }

    [Theory]
    [InlineData("DROP TABLE psl_extensions")]
    [InlineData("DROP TABLE eq_class_groups; CREATE VIEW eq_class_groups AS SELECT id,facet_uri,set_id,NULL AS main_domain,NULL AS change_password_url FROM eq_class_members")]
    [InlineData("CREATE TRIGGER unexpected AFTER INSERT ON eq_classes BEGIN SELECT 1; END")]
    [InlineData("UPDATE eq_class_groups SET set_id=999")]
    [InlineData("UPDATE eq_class_members SET set_id=999")]
    [InlineData("UPDATE eq_classes SET last_update_time='wrong-type'")]
    [InlineData("UPDATE eq_class_groups SET set_id='wrong-type'")]
    [InlineData("UPDATE eq_class_groups SET facet_uri='' WHERE id=1")]
    [InlineData("DELETE FROM psl_extensions; INSERT INTO psl_extensions(domain) VALUES('')")]
    public void MalformedCacheFailsWithSafeError(string sql)
    {
        var error = Assert.Throws<BackupOperationException>(() => BraveAffiliationDatabase.Read(Mutate(sql)));
        Assert.Equal("passwords-affiliation-invalid", error.Code);
        Assert.DoesNotContain("example.com", error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public void OversizedTextImageAndCorruptInputAreBounded()
    {
        Assert.Throws<BackupOperationException>(() => BraveAffiliationDatabase.Read(Mutate("UPDATE eq_class_groups SET facet_uri=$text WHERE id=1", new string('x', 4097))));
        Assert.Throws<BackupOperationException>(() => BraveAffiliationDatabase.Read(new byte[32 * 1024 * 1024 + 1]));
        Assert.Throws<BackupOperationException>(() => BraveAffiliationDatabase.Read("synthetic corrupt data"u8.ToArray()));
    }

    [Fact]
    public void CancellationAndInvalidLookupInputsAreHandled()
    {
        Assert.Throws<OperationCanceledException>(() => BraveAffiliationDatabase.Read(Fixture, new CancellationToken(true)));
        var cache = BraveAffiliationDatabase.Read(Fixture);
        Assert.Throws<ArgumentException>(() => cache.GetGroup(""));
        Assert.Throws<ArgumentException>(() => cache.GetGroup(new string('x', 4097)));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
