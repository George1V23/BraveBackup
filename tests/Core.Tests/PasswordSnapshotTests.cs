using System.Buffers.Binary;
using System.Security.Cryptography;
using BraveBackup.Platform;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class PasswordSnapshotTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "PasswordSnapshot_" + Guid.NewGuid().ToString("N"));

    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Passwords");
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(Fixtures, name));

    private void Prepare(string fixture = "LoginData.v43.windows.sqlite")
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "Login Data"), Fixture(fixture));
    }

    private BravePasswordSnapshot Read() => BravePasswordSnapshot.Read(_root, () => ProcessPresence.Stopped);

    [Theory]
    [InlineData("windows", "v10")]
    [InlineData("linux", "v11")]
    public void ReadsEncryptedRowsNotesAndExclusionCountsWithoutChangingSource(string os, string prefix)
    {
        Prepare("LoginData.v43." + os + ".sqlite");
        var original = File.ReadAllBytes(Path.Combine(_root, "Login Data"));
        using var capture = Read();
        var data = Assert.Single(capture.Databases).Value;
        Assert.Equal(1, data.BlockedEntries);
        Assert.Equal(1, data.FederatedEntries);
        var row = Assert.Single(data.Rows);
        Assert.Equal("fixture-user", row.Username);
        Assert.Equal(System.Text.Encoding.ASCII.GetBytes(prefix), row.EncryptedValue[..3]);
        Assert.Equal(System.Text.Encoding.ASCII.GetBytes(prefix), Assert.Single(row.Notes).EncryptedValue[..3]);
        Assert.DoesNotContain("fixture", row.ToString());
        capture.VerifyUnchanged();
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_root, "Login Data")));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void WalCommitIsAppliedWithoutOpeningOrChangingSharedMemory()
    {
        Directory.CreateDirectory(_root);
        foreach (var file in Directory.GetFiles(Path.Combine(Fixtures, "Wal")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
        var before = Hashes();
        using var capture = Read();
        Assert.Equal("fixture-WAL-new", Assert.Single(capture.Databases["Login Data"].Rows).Username);
        Assert.Equal(before.OrderBy(p => p.Key), Hashes().OrderBy(p => p.Key));
    }

    [Fact]
    public void BothLocalStoresAreReadAndEmptyIsNotMissing()
    {
        Prepare("LoginData.v43.empty.sqlite");
        File.WriteAllBytes(Path.Combine(_root, "Login Data For Account"), Fixture("LoginData.v43.linux.sqlite"));
        using var capture = Read();
        Assert.Empty(capture.Databases["Login Data"].Rows);
        Assert.Single(capture.Databases["Login Data For Account"].Rows);
        capture.Dispose();
        File.Delete(Path.Combine(_root, "Login Data"));
        File.Delete(Path.Combine(_root, "Login Data For Account"));
        Assert.Equal("passwords-source-missing", Assert.Throws<BackupOperationException>(() => Read()).Code);
    }

    [Theory]
    [InlineData(ProcessPresence.Running)]
    [InlineData(ProcessPresence.Unknown)]
    public void InitialPathRequiresClosedBrowser(ProcessPresence state)
    {
        Prepare();
        var before = Hashes();
        Assert.Equal("passwords-close-brave",
            Assert.Throws<BackupOperationException>(() => BravePasswordSnapshot.Read(_root, () => state)).Code);
        Assert.Equal(before.OrderBy(p => p.Key), Hashes().OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData("LoginData.unsupported.sqlite")]
    [InlineData("LoginData.corrupt.bin")]
    public void UnsupportedOrCorruptIsNotReportedAsEmpty(string name)
    {
        Prepare(name);
        var error = Assert.Throws<BackupOperationException>(() => Read());
        Assert.DoesNotContain(_root, error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("SELECT 1", true)]
    [InlineData("CREATE TABLE forbidden(value)", false)]
    public void NativeImageUsesReadOnlyMemory(string sql, bool allowed)
    {
        var bytes = Fixture("LoginData.v43.empty.sqlite");
        using var db = new SqliteMemoryImage(bytes);
        using var command = db.Connection.CreateCommand();
        command.CommandText = sql;
        if (allowed) Assert.Equal(1L, command.ExecuteScalar());
        else Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(bytes, Fixture("LoginData.v43.empty.sqlite"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(45)]
    public void CorruptWalChecksumAndFramingFailClosed(int offset)
    {
        var database = Fixture("Wal/Login Data");
        var wal = Fixture("Wal/Login Data-wal");
        wal[offset] ^= 1;
        Assert.Throws<InvalidDataException>(() => SqliteSnapshotImage.Create(database, wal));
    }

    [Fact]
    public void EveryPartialWalFrameIsRejected()
    {
        var database = Fixture("Wal/Login Data");
        var wal = Fixture("Wal/Login Data-wal");
        var pageSize = (int)BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(8));
        for (var length = 33; length < Math.Min(wal.Length, pageSize + 56); length++)
            Assert.Throws<InvalidDataException>(() => SqliteSnapshotImage.Create(database, wal.AsSpan(0, length)));
    }

    [Fact]
    public void SameSizeChangeAndAddedCompanionBlockFinalVerification()
    {
        Prepare();
        using var capture = Read();
        var path = Path.Combine(_root, "Login Data");
        var time = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, time);
        Assert.Equal("passwords-source-changed",
            Assert.Throws<BackupOperationException>(() => capture.VerifyUnchanged()).Code);
    }

    [Fact]
    public void NonemptyJournalNeverTriggersRecoveryAgainstSource()
    {
        Prepare();
        File.WriteAllText(Path.Combine(_root, "Login Data-journal"), "synthetic journal");
        var before = Hashes();
        Assert.Equal("passwords-journal-unsupported", Assert.Throws<BackupOperationException>(() => Read()).Code);
        Assert.Equal(before.OrderBy(p => p.Key), Hashes().OrderBy(p => p.Key));
    }

    [Fact]
    public void OwnedBuffersAreClearedAndDisposedObjectsRejectAccess()
    {
        Prepare();
        var capture = Read();
        var row = capture.Databases["Login Data"].Rows[0];
        var value = row.EncryptedValue;
        var note = row.Notes[0].EncryptedValue;
        capture.Dispose();
        capture.Dispose();
        Assert.All(value, b => Assert.Equal(0, b));
        Assert.All(note, b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => capture.Databases);
        Assert.Throws<ObjectDisposedException>(() => capture.VerifyUnchanged());
    }

    [Fact]
    public void CancellationAndObserverErrorsFailClosed()
    {
        Prepare();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            BravePasswordSnapshot.Read(_root, () => ProcessPresence.Stopped, cancel.Token));
        var error = Assert.Throws<BackupOperationException>(() =>
            BravePasswordSnapshot.Read(_root, () => throw new Exception("secret")));
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Theory]
    [InlineData("UPDATE logins SET scheme=1 WHERE id=1")]
    [InlineData("UPDATE logins SET password_value='not-a-blob' WHERE id=1")]
    [InlineData("PRAGMA foreign_keys=OFF; UPDATE password_notes SET parent_id=999")]
    [InlineData("UPDATE password_notes SET confidential=2")]
    [InlineData("DROP TABLE password_notes")]
    [InlineData("ALTER TABLE logins RENAME TO original; CREATE VIEW logins AS SELECT * FROM original")]
    public void InvalidTypesOrUntestedSchemesAreNotSilentlySkipped(string sql)
    {
        Prepare();
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(_root, "Login Data") + ";Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        var error = Assert.Throws<BackupOperationException>(() => Read());
        Assert.DoesNotContain("fixture", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void OpaqueFederationMarkerIsNotMisclassifiedAsFederated()
    {
        Prepare();
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(_root, "Login Data") + ";Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE logins SET federation_url='null' WHERE id=1";
            command.ExecuteNonQuery();
        }

        using var snapshot = Read();
        Assert.Single(snapshot.Databases["Login Data"].Rows);
    }

    [Fact]
    public void MissingWalMainAndUnsafeSourcesFailRatherThanGuessing()
    {
        Prepare();
        File.Delete(Path.Combine(_root, "Login Data"));
        File.WriteAllText(Path.Combine(_root, "Login Data-wal"), "orphan");
        Assert.Throws<BackupOperationException>(() => Read());
        File.Delete(Path.Combine(_root, "Login Data-wal"));
        Directory.CreateDirectory(Path.Combine(_root, "Login Data"));
        Assert.Throws<BackupOperationException>(() => Read());
    }

    [Fact]
    public void LinkedSourceIsRejectedOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;
        Prepare();
        File.Move(Path.Combine(_root, "Login Data"), Path.Combine(_root, "outside"));
        File.CreateSymbolicLink(Path.Combine(_root, "Login Data"), Path.Combine(_root, "outside"));
        Assert.Throws<BackupOperationException>(() => Read());
    }

    [Fact]
    public void AddedCompanionAndLateBrowserStartBlockVerification()
    {
        Prepare();
        var state = ProcessPresence.Stopped;
        using var snapshot = BravePasswordSnapshot.Read(_root, () => state);
        File.WriteAllBytes(Path.Combine(_root, "Login Data-journal"), Array.Empty<byte>());
        Assert.Equal("passwords-source-changed",
            Assert.Throws<BackupOperationException>(() => snapshot.VerifyUnchanged()).Code);
        File.Delete(Path.Combine(_root, "Login Data-journal"));
        state = ProcessPresence.Running;
        Assert.Equal("passwords-close-brave",
            Assert.Throws<BackupOperationException>(() => snapshot.VerifyUnchanged()).Code);
    }

    [Fact]
    public void NativeExecutionHonorsCancellationAndDisallowsAttachedFiles()
    {
        using var cancel = new CancellationTokenSource();
        using var db = new SqliteMemoryImage(Fixture("LoginData.v43.empty.sqlite"), cancel.Token);
        using var command = db.Connection.CreateCommand();
        command.CommandText = "ATTACH DATABASE ':memory:' AS other";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        cancel.Cancel();
        command.CommandText =
            "WITH RECURSIVE n(x) AS (VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<1000000) SELECT sum(x) FROM n";
        Assert.Throws<SqliteException>(() => command.ExecuteScalar());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothDocumentedChecksumByteOrdersProduceSameImage(bool bigEndian)
    {
        var db = Fixture("Wal/Login Data");
        var wal = Fixture("Wal/Login Data-wal");
        var expected = SqliteSnapshotImage.Create(db, wal);
        BinaryPrimitives.WriteUInt32BigEndian(wal, bigEndian ? 0x377f0683U : 0x377f0682U);
        Rechecksum(wal, bigEndian);
        Assert.Equal(expected, SqliteSnapshotImage.Create(db, wal));
    }

    [Fact]
    public void ValidUncommittedTailIsNotApplied()
    {
        var db = Fixture("Wal/Login Data");
        var original = Fixture("Wal/Login Data-wal");
        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(8));
        var wal = new byte[original.Length + 24 + size];
        original.CopyTo(wal, 0);
        original.AsSpan(32, 24 + size).CopyTo(wal.AsSpan(original.Length));
        BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(original.Length + 4), 0);
        wal[^1] ^= 1;
        Rechecksum(wal, (wal[3] & 1) != 0);
        Assert.Equal(SqliteSnapshotImage.Create(db, original), SqliteSnapshotImage.Create(db, wal));
    }

    [Fact]
    public void ValidWalWithoutAnyCommitRetainsMainDatabase()
    {
        var db = Fixture("Wal/Login Data");
        var wal = Fixture("Wal/Login Data-wal");
        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(8));
        for (var offset = 32; offset < wal.Length; offset += 24 + size)
            BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(offset + 4), 0);
        Rechecksum(wal, (wal[3] & 1) != 0);
        Assert.Equal(db, SqliteSnapshotImage.Create(db, wal));
    }

    // Independent test helper follows SQLite's published rolling-checksum equations.
    private static void Rechecksum(byte[] wal, bool bigEndian)
    {
        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(8));
        uint a = 0, b = 0;
        Update(0, 24);
        Store(24);
        for (var offset = 32; offset < wal.Length; offset += 24 + size)
        {
            Update(offset, 8);
            Update(offset + 24, size);
            Store(offset + 16);
        }

        void Update(int offset, int length)
        {
            for (var end = offset + length; offset < end; offset += 8)
            {
                var x = bigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset))
                    : BinaryPrimitives.ReadUInt32LittleEndian(wal.AsSpan(offset));
                var y = bigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset + 4))
                    : BinaryPrimitives.ReadUInt32LittleEndian(wal.AsSpan(offset + 4));
                a = unchecked(a + b + x);
                b = unchecked(b + a + y);
            }
        }

        void Store(int offset)
        {
            BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(offset), a);
            BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(offset + 4), b);
        }
    }

    private Dictionary<string, string> Hashes() => Directory.GetFiles(_root).ToDictionary(p => Path.GetFileName(p),
        p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

    [Fact]
    public void OversizedSourceIsRejectedBeforeImageAllocation()
    {
        Prepare();
        using (var file = new FileStream(Path.Combine(_root, "Login Data"), FileMode.Open, FileAccess.Write))
            file.SetLength((long)SqliteSnapshotImage.MaximumBytes + 1);
        Assert.Equal("passwords-source-invalid", Assert.Throws<BackupOperationException>(() => Read()).Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}