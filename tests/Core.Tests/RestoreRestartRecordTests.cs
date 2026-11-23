using System.Security.Cryptography;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class RestoreRestartRecordTests
{
    private const string Password = "synthetic-restart-password";

    [Fact]
    public void BoundArtifactsReloadTheReviewedTargetsAndJournalState()
    {
        using var fixture = new Evidence();
        using var record = RestoreRestartRecordFormat.Encode(fixture.Plan);
        using var journal = fixture.EncodeJournal(record);

        var inspection = Inspect(record, fixture.Safety, journal);

        Assert.Equal(fixture.Plan.OperationId, inspection.Record.OperationId);
        Assert.Equal(fixture.Plan.SafetyFingerprint, inspection.Record.SafetyFingerprint);
        Assert.Equal(fixture.Plan.Targets.ToArray(), inspection.Record.Targets.ToArray());
        Assert.Equal(4, inspection.Journal.Revision);
        Assert.Equal(RestoreTransactionState.Paused, inspection.Journal.State);
        Assert.Equal(RestoreEntryState.Intent, Assert.Single(inspection.Journal.Entries).State);
        Assert.DoesNotContain(fixture.Plan.Targets[0].ProfileDirectory, inspection.ToString());
    }

    [Fact]
    public void IdenticalOperationWithAuthenticatedButChangedMappingCannotUseOldJournal()
    {
        using var fixture = new Evidence();
        using var originalRecord = RestoreRestartRecordFormat.Encode(fixture.Plan);
        using var journal = fixture.EncodeJournal(originalRecord);
        var moved = fixture.Plan.Targets[0] with { ProfileDirectory = "/profiles/different" };
        using var differentRecord = RestoreRestartRecordFormat.Encode(fixture.Plan with { Targets = [moved] });

        Assert.Throws<InvalidDataException>(() => Inspect(differentRecord, fixture.Safety, journal));
    }

    [Fact]
    public void AuthenticatedSafetyWithSameIdsButDifferentOriginalBytesIsRejected()
    {
        using var fixture = new Evidence();
        using var record = RestoreRestartRecordFormat.Encode(fixture.Plan);
        using var journal = fixture.EncodeJournal(record);
        using var changedImage = new RestoreFileImage(true, "different"u8);
        using var differentSafety = fixture.EncodeSafety(changedImage);

        Assert.Throws<InvalidDataException>(() => Inspect(record, differentSafety, journal));
    }

    [Fact]
    public void MatchingSafetyHashDoesNotOverrideAnIncorrectOriginalImageFingerprint()
    {
        using var fixture = new Evidence();
        var target = fixture.Plan.Targets[0] with { OriginalFingerprint = new string('0', 64) };
        using var record = RestoreRestartRecordFormat.Encode(fixture.Plan with { Targets = [target] });
        using var journal = fixture.EncodeJournal(record);

        Assert.Throws<InvalidDataException>(() => Inspect(record, fixture.Safety, journal));
    }

    [Fact]
    public void VersionOneJournalCannotAuthorizeRestartInspection()
    {
        using var fixture = new Evidence();
        using var record = RestoreRestartRecordFormat.Encode(fixture.Plan);
        using var journal = new SensitiveBytes(RestoreJournalFormat.Encode(fixture.Plan.OperationId, 4,
            RestoreTransactionState.Paused, [(fixture.Plan.Targets[0].MutationId, RestoreEntryState.Intent)]));

        Assert.Throws<InvalidDataException>(() => Inspect(record, fixture.Safety, journal));
    }

    [Theory]
    [InlineData("../Login Data")]
    [InlineData("Sessions/../Login Data")]
    [InlineData("/Login Data")]
    [InlineData("C:Login Data")]
    [InlineData("Sessions\\Login Data")]
    public void RejectsUnsafeRelativeMappings(string name)
    {
        using var fixture = new Evidence();
        var target = fixture.Plan.Targets[0] with { RelativeTarget = name };
        Assert.Throws<InvalidDataException>(() => RestoreRestartRecordFormat.Encode(fixture.Plan with { Targets = [target] }));
    }

    [Fact]
    public void RejectsDuplicateDestinationsAndTrailingOrTruncatedRecords()
    {
        using var fixture = new Evidence();
        var target = fixture.Plan.Targets[0];
        Assert.Throws<InvalidDataException>(() => RestoreRestartRecordFormat.Encode(
            fixture.Plan with { Targets = [target, target with { MutationId = Guid.NewGuid() }] }));
        using var valid = RestoreRestartRecordFormat.Encode(fixture.Plan);
        using var truncated = new SensitiveBytes(valid.Bytes.Span[..^1].ToArray());
        using var trailing = new SensitiveBytes([.. valid.Bytes.Span, 0]);
        Assert.Throws<InvalidDataException>(() => RestoreRestartRecordFormat.Decode(truncated));
        Assert.Throws<InvalidDataException>(() => RestoreRestartRecordFormat.Decode(trailing));
        Assert.Throws<InvalidDataException>(() => RestoreRestartRecordFormat.EncodeJournal(valid, 2,
            RestoreTransactionState.Paused, [(Guid.NewGuid(), RestoreEntryState.Intent)]));
    }

    [Fact]
    public void AuthenticationFailureAndCancellationAreReported()
    {
        using var fixture = new Evidence();
        using var record = RestoreRestartRecordFormat.Encode(fixture.Plan);
        using var journal = fixture.EncodeJournal(record);
        using var encryptedRecord = Encrypt(record);
        using var encryptedSafety = Encrypt(fixture.Safety);
        using var encryptedJournal = Encrypt(journal);

        Assert.Throws<CryptographicException>(() => RestoreRestartRecordFormat.Inspect(
            encryptedRecord, encryptedSafety, encryptedJournal, "wrong"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => RestoreRestartRecordFormat.Inspect(
            encryptedRecord, encryptedSafety, encryptedJournal, Password, cancellation.Token));
    }

    private static RestoreRestartInspection Inspect(SensitiveBytes record, SensitiveBytes safety, SensitiveBytes journal)
    {
        using var encryptedRecord = Encrypt(record);
        using var encryptedSafety = Encrypt(safety);
        using var encryptedJournal = Encrypt(journal);
        return RestoreRestartRecordFormat.Inspect(encryptedRecord, encryptedSafety, encryptedJournal, Password);
    }

    private static MemoryStream Encrypt(SensitiveBytes bytes)
    {
        var stream = new MemoryStream();
        using (var writer = ArchiveEncryption.CreateWriter(stream, Password))
            writer.Write(bytes.Bytes.Span);
        return stream;
    }

    private sealed class Evidence : IDisposable
    {
        private readonly Guid _operation = Guid.NewGuid();
        private readonly Guid _mutation = Guid.NewGuid();
        private readonly Guid _installation = Guid.NewGuid();
        private readonly Guid _profile = Guid.NewGuid();
        internal SensitiveBytes Safety { get; }
        internal RestoreRestartRecord Plan { get; }

        internal Evidence()
        {
            using var original = new RestoreFileImage(true, "synthetic original"u8);
            using var replacement = new RestoreFileImage(true, "synthetic replacement"u8);
            Safety = EncodeSafety(original);
            Plan = new(_operation, Convert.ToHexStringLower(SHA256.HashData(Safety.Bytes.Span)),
                [new(_mutation, RestoreRestartTargetKind.File, "/profiles", "/profiles/Default",
                    "Preferences", "synthetic directory evidence",
                    RestoreRecoveryEvidence.Fingerprint(original), RestoreRecoveryEvidence.Fingerprint(replacement))]);
        }

        internal SensitiveBytes EncodeSafety(RestoreFileImage original)
        {
            using var snapshot = new RestoreSafetySnapshot(_operation,
                [new(_mutation, _installation, _profile, "settings", original.Clone())]);
            return RestoreSafetyFormat.Encode(snapshot);
        }

        internal SensitiveBytes EncodeJournal(SensitiveBytes record) =>
            RestoreRestartRecordFormat.EncodeJournal(record, 4, RestoreTransactionState.Paused,
                [(_mutation, RestoreEntryState.Intent)]);

        public void Dispose() => Safety.Dispose();
    }
}
