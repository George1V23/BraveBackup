using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class RestoreRecoveryEvidenceTests
{
    [Fact]
    public void ImageFrameBindsExistenceAndSeparatesContentsFromMetadata()
    {
        using var missing = new RestoreFileImage(false, []);
        using var empty = new RestoreFileImage(true, []);
        using var image = new RestoreFileImage(true, "new"u8, "id"u8);
        using var differentBoundary = new RestoreFileImage(true, "newi"u8, "d"u8);

        Assert.Equal("f2939d0ca58a9c38fa757a5ae39ae2c382d7c5254bedf4d0a54403c3d19df0ed", RestoreRecoveryEvidence.Fingerprint(image));
        Assert.NotEqual(RestoreRecoveryEvidence.Fingerprint(missing), RestoreRecoveryEvidence.Fingerprint(empty));
        Assert.NotEqual(RestoreRecoveryEvidence.Fingerprint(image), RestoreRecoveryEvidence.Fingerprint(differentBoundary));
    }

    [Theory]
    [InlineData(RestoreEntryState.Intent)]
    [InlineData(RestoreEntryState.Applied)]
    public void TouchedEntriesAllowOnlyOriginalOrVerifiedReplacement(RestoreEntryState state)
    {
        using var original = new RestoreFileImage(false, []);
        using var replacement = new RestoreFileImage(true, "new"u8, "metadata"u8);
        using var externalEdit = new RestoreFileImage(true, "new"u8, "different metadata"u8);
        var before = RestoreRecoveryEvidence.Fingerprint(original);
        var after = RestoreRecoveryEvidence.Fingerprint(replacement);

        Assert.Equal(RestoreRollbackDecision.AlreadyOriginal, RestoreRecoveryEvidence.AssessRollback(state, original, before, after));
        Assert.Equal(RestoreRollbackDecision.RestoreOriginal, RestoreRecoveryEvidence.AssessRollback(state, replacement, before, after));
        Assert.Equal(RestoreRollbackDecision.Conflict, RestoreRecoveryEvidence.AssessRollback(state, externalEdit, before, after));
        Assert.Equal(RestoreRollbackDecision.AlreadyOriginal, RestoreRecoveryEvidence.AssessRollback(state, original, before, before));
    }

    [Theory]
    [InlineData(RestoreEntryState.Untouched)]
    [InlineData(RestoreEntryState.Recovered)]
    public void SkippedEntriesReportConflictsWhenCurrentImageDisagreesWithJournal(RestoreEntryState state)
    {
        using var original = new RestoreFileImage(false, []);
        using var replacement = new RestoreFileImage(true, "new"u8);
        using var externalEdit = new RestoreFileImage(true, "external"u8);

        var before = RestoreRecoveryEvidence.Fingerprint(original);
        var after = RestoreRecoveryEvidence.Fingerprint(replacement);
        Assert.Equal(RestoreRollbackDecision.Skip, RestoreRecoveryEvidence.AssessRollback(state, original, before, after));
        Assert.Equal(RestoreRollbackDecision.Conflict, RestoreRecoveryEvidence.AssessRollback(state, replacement, before, after));
        Assert.Equal(RestoreRollbackDecision.Conflict, RestoreRecoveryEvidence.AssessRollback(state, externalEdit, before, after));
    }

    [Fact]
    public void RejectsInvalidFingerprintsAndUnknownStates()
    {
        using var image = new RestoreFileImage(true, "new"u8);
        var valid = RestoreRecoveryEvidence.Fingerprint(image);

        Assert.Throws<InvalidDataException>(() => RestoreRecoveryEvidence.AssessRollback(
            RestoreEntryState.Intent, image, valid, "invalid"));
        Assert.Throws<InvalidDataException>(() => RestoreRecoveryEvidence.AssessRollback(
            (RestoreEntryState)255, image, valid, valid));
    }
}
