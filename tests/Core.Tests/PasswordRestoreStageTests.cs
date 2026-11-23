using BraveBackup.Platform;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class PasswordRestoreStageTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"BraveRestoreStage-{Guid.NewGuid():N}.db");
    private static readonly DateTimeOffset Created = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacesLocalLoginsAndPreservesExcludedRowsAndStats(bool empty)
    {
        var original = CreateDestination();
        using var key = PasswordKeyMaterial.FromLinuxKeyringSecret("stage-fixture"u8);
        using var source = Document(empty ? [] : [Login(99, "new"), Login(100, "empty", empty: true)]);
        using var payload = PasswordPayloadFormat.Encode(source);

        var image = BravePasswordRestoreStage.Prepare(original, payload.Bytes.Span, key, Created);

        Assert.Equal(original, File.ReadAllBytes(_path));
        Assert.True(image.AsSpan().IndexOf("synthetic-password-✓"u8) < 0);
        Assert.True(image.AsSpan().IndexOf("synthetic-note-✓"u8) < 0);
        using var staged = BravePasswordDatabase.Read(image);
        Assert.Equal(1, staged.BlockedEntries);
        Assert.Equal(1, staged.FederatedEntries);
        Assert.Equal(empty ? 0 : 2, staged.Rows.Count);
        if (!empty)
        {
            var row = staged.Rows[0];
            Assert.Equal("new", row.Username);
            Assert.NotEqual(99, row.Id);
            Assert.Equal("v11"u8.ToArray(), row.EncryptedValue[..3]);
            using var password = BravePasswordCrypto.Decode(row.EncryptedValue, key);
            Assert.Equal("synthetic-password-✓"u8.ToArray(), password.Utf8.ToArray());
            var note = Assert.Single(row.Notes);
            Assert.Equal("user note", note.Name);
            Assert.Equal(42, note.Created);
            Assert.True(note.Confidential);
            using var noteValue = BravePasswordCrypto.Decode(note.EncryptedValue, key);
            Assert.Equal("synthetic-note-✓"u8.ToArray(), noteValue.Utf8.ToArray());
            Assert.Empty(staged.Rows[1].EncryptedValue);
            Assert.Empty(Assert.Single(staged.Rows[1].Notes).EncryptedValue);
        }

        using var db = new SqliteMemoryImage(image);
        using var command = db.Connection.CreateCommand();
        command.CommandText = "SELECT dismissal_count FROM stats";
        Assert.Equal(3L, command.ExecuteScalar());
        command.CommandText = "SELECT parent_id FROM insecure_credentials";
        Assert.Equal(2L, command.ExecuteScalar());
        command.CommandText = "SELECT key FROM password_notes WHERE parent_id = 2";
        Assert.Equal("blocked-note", command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM password_notes WHERE typeof(value) = 'text'";
        Assert.Equal(empty ? 0L : 2L, command.ExecuteScalar());
    }

    [Fact]
    public void SerializedImageCanGrowPastTheOriginalPageCount()
    {
        var original = CreateDestination();
        using var key = PasswordKeyMaterial.FromLinuxKeyringSecret("stage-fixture"u8);
        using var source = Document([new PortablePasswordLogin(1, "https://restore.example/", "https://restore.example/",
            "large", null, new SensitiveUtf8(Enumerable.Repeat((byte)'p', 128 * 1024).ToArray()), [])]);
        using var payload = PasswordPayloadFormat.Encode(source);

        var image = BravePasswordRestoreStage.Prepare(original, payload.Bytes.Span, key, Created);

        Assert.True(image.Length > original.Length);
        using var staged = BravePasswordDatabase.Read(image);
        using var password = BravePasswordCrypto.Decode(Assert.Single(staged.Rows).EncryptedValue, key);
        Assert.Equal(128 * 1024, password.Utf8.Length);
        Assert.Equal(original, File.ReadAllBytes(_path));
    }

    [Theory]
    [InlineData("CREATE TABLE additional(value)", "definition")]
    [InlineData("INSERT INTO sync_model_metadata(model_metadata) VALUES ('synthetic')", "Sync")]
    [InlineData("INSERT INTO insecure_credentials(parent_id,insecurity_type,create_time) VALUES (99,0,0)", "references")]
    public void RejectsUnverifiedDestinationState(string alteration, string expectedMessage)
    {
        var original = CreateDestination(alteration);
        using var key = PasswordKeyMaterial.FromLinuxKeyringSecret("stage-fixture"u8);
        using var source = Document([]);
        using var payload = PasswordPayloadFormat.Encode(source);

        var error = Assert.Throws<BackupOperationException>(() =>
            BravePasswordRestoreStage.Prepare(original, payload.Bytes.Span, key, Created));

        Assert.Contains(expectedMessage, error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(original, File.ReadAllBytes(_path));
    }

    [Fact]
    public void RejectsCollidingIdentitiesIncludingNullVersusEmptyUsername()
    {
        var original = CreateDestination();
        using var key = PasswordKeyMaterial.FromLinuxKeyringSecret("stage-fixture"u8);
        using var source = Document([Login(11, null), Login(12, "")]);
        using var payload = PasswordPayloadFormat.Encode(source);
        var error = Assert.Throws<BackupOperationException>(() =>
            BravePasswordRestoreStage.Prepare(original, payload.Bytes.Span, key, Created));
        Assert.Contains("same native form identity", error.Message);
        Assert.Equal(original, File.ReadAllBytes(_path));
    }

    [Fact]
    public void RejectsAccountPayloadAndUnsupportedProvider()
    {
        var original = CreateDestination();
        using var linux = PasswordKeyMaterial.FromLinuxKeyringSecret("stage-fixture"u8);
        using var windows = new PasswordKeyMaterial(PasswordEncryptionProvider.WindowsDpapiV10, new byte[32]);
        using var source = new PortablePasswordDocument([new PortablePasswordStore(PortablePasswordStoreKind.Account, 0, 0, [])]);
        using var payload = PasswordPayloadFormat.Encode(source);
        Assert.Contains("single local profile store", Assert.Throws<BackupOperationException>(() =>
            BravePasswordRestoreStage.Prepare(original, payload.Bytes.Span, linux, Created)).Message);
        Assert.Contains("v11", Assert.Throws<BackupOperationException>(() =>
            BravePasswordRestoreStage.Prepare(original, payload.Bytes.Span, windows, Created)).Message);
    }

    [Fact]
    public void CancellationStopsPreparation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var key = PasswordKeyMaterial.FromLinuxKeyringSecret("stage-fixture"u8);
        Assert.Throws<OperationCanceledException>(() =>
            BravePasswordRestoreStage.Prepare([], [], key, Created, cancellation.Token));
    }

    private byte[] CreateDestination(string? alteration = null)
    {
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "Passwords", "schema-v43-brave-154.sql"));
            command.ExecuteNonQuery();
            command.CommandText = """
                PRAGMA foreign_keys=OFF;
                INSERT INTO logins(id,origin_url,username_value,signon_realm,date_created,blacklisted_by_user,scheme,password_value,federation_url)
                VALUES (1,'https://old.example/','old','https://old.example/',0,0,0,X'',''),
                       (2,'https://blocked.example/','','https://blocked.example/',0,1,0,X'',''),
                       (3,'https://federated.example/','','federation://federated.example/id.example',0,0,0,X'','https://id.example/');
                INSERT INTO password_notes(parent_id,key,value,date_created,confidential)
                VALUES (1,'old-note',X'',0,0), (2,'blocked-note',X'',0,0);
                INSERT INTO insecure_credentials(parent_id,insecurity_type,create_time) VALUES (1,0,0),(2,0,0);
                INSERT INTO stats VALUES ('https://stats.example/','fixture',3,0);
                """;
            command.ExecuteNonQuery();
            if (alteration is not null)
            {
                command.CommandText = alteration;
                command.ExecuteNonQuery();
            }
        }

        return File.ReadAllBytes(_path);
    }

    private static PortablePasswordLogin Login(long id, string? username, bool empty = false) =>
        new(id, "https://restore.example/login", "https://restore.example/", username, "Display",
            new SensitiveUtf8(empty ? [] : "synthetic-password-✓"u8),
            [new PortablePasswordNote("user note", 42, true, new SensitiveUtf8(empty ? [] : "synthetic-note-✓"u8))]);

    private static PortablePasswordDocument Document(IEnumerable<PortablePasswordLogin> logins) =>
        new([new PortablePasswordStore(PortablePasswordStoreKind.Profile, 0, 0, logins)]);

    public void Dispose() => File.Delete(_path);
}
