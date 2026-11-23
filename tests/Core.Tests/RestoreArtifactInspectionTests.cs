using System.Security.Cryptography;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class RestoreArtifactInspectionTests
{
    private const string Password = "synthetic-artifact-password";

    [Fact]
    public void SelectedEncryptedFilesReturnLogicalInventoryOnly()
    {
        var operation = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var directory = Directory.CreateTempSubdirectory("restore-inspection-");
        try
        {
            var safetyPath = Path.Combine(directory.FullName, "operation.safety.encrypted");
            var journalPath = Path.Combine(directory.FullName, "operation.journal.encrypted");
            var safety = Safety(operation, first, second);
            var journal = Journal(operation, first, second);
            File.WriteAllBytes(safetyPath, safety);
            File.WriteAllBytes(journalPath, journal);

            var inspection = RestoreArtifactStorage.Inspect(safetyPath, journalPath, Password);

            Assert.Equal(operation, inspection.OperationId);
            Assert.Equal(3, inspection.Revision);
            Assert.Equal(RestoreTransactionState.Paused, inspection.State);
            Assert.Collection(inspection.Entries,
                item => Assert.Equal((first, "bookmarks", true, RestoreEntryState.Applied),
                    (item.MutationId, item.Category, item.OriginallyExisted, item.State)),
                item => Assert.Equal((second, "settings", false, RestoreEntryState.Intent),
                    (item.MutationId, item.Category, item.OriginallyExisted, item.State)));
            Assert.Equal(safety, File.ReadAllBytes(safetyPath));
            Assert.Equal(journal, File.ReadAllBytes(journalPath));
            Assert.Equal(2, Directory.GetFiles(directory.FullName).Length);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthenticatedArtifactsWithMismatchedOperationOrEntryOrderAreRejected(bool differentOperation)
    {
        var operation = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using var safety = new MemoryStream(Safety(operation, first, second));
        using var journal = new MemoryStream(differentOperation
            ? Journal(Guid.NewGuid(), first, second)
            : Journal(operation, second, first));
        Assert.Throws<InvalidDataException>(() => RestoreArtifactInspection.Read(safety, journal, Password));
    }

    [Fact]
    public void WrongPasswordAndTamperedSecondaryArtifactAreRejected()
    {
        var operation = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var safetyBytes = Safety(operation, first, second);
        using var safety = new MemoryStream(safetyBytes);
        using var journal = new MemoryStream(Journal(operation, first, second));
        Assert.Throws<CryptographicException>(() => RestoreArtifactInspection.Read(safety, journal, "incorrect"));
        safetyBytes[^1] ^= 1;
        Assert.Throws<CryptographicException>(() => RestoreArtifactInspection.Read(safety, journal, Password));
    }

    [Fact]
    public void CancellationStopsInspectionBeforeReading()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var empty = new MemoryStream();
        Assert.Throws<OperationCanceledException>(() =>
            RestoreArtifactInspection.Read(empty, empty, Password, cancellation.Token));
    }

    private static byte[] Safety(Guid operation, Guid first, Guid second)
    {
        var installation = Guid.NewGuid();
        var profile = Guid.NewGuid();
        using var snapshot = new RestoreSafetySnapshot(operation,
        [
            new(first, installation, profile, "bookmarks", new(true, "synthetic-original"u8)),
            new(second, installation, profile, "settings", new(false, []))
        ]);
        using var plaintext = RestoreSafetyFormat.Encode(snapshot);
        return Encrypt(plaintext.Bytes.Span);
    }

    private static byte[] Journal(Guid operation, Guid first, Guid second) =>
        Encrypt(RestoreJournalFormat.Encode(operation, 3, RestoreTransactionState.Paused,
            [(first, RestoreEntryState.Applied), (second, RestoreEntryState.Intent)]));

    private static byte[] Encrypt(ReadOnlySpan<byte> content)
    {
        using var output = new MemoryStream();
        using (var writer = ArchiveEncryption.CreateWriter(output, Password))
            writer.Write(content);
        return output.ToArray();
    }
}
