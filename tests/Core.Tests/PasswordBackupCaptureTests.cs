using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class PasswordBackupCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FallbackPasswords_" + Guid.NewGuid().ToString("N"));
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Passwords");
    public PasswordBackupCaptureTests() => Directory.CreateDirectory(_root);
    private void Prepare(string fixture = "LoginData.corrupt.bin") => File.Copy(Path.Combine(Fixtures, fixture), Path.Combine(_root, "Login Data"));
    private Task<PasswordBackupCapture> Read(Func<PasswordRawFallbackWarning, CancellationToken, Task<bool>> choose,
        Func<ProcessPresence>? observe = null, CancellationToken token = default) => PasswordBackupCapture.ReadAsync(_root,
        PasswordEncryptionProvider.LinuxKeyringV11, choose,
        (_, _) => throw new BackupOperationException("passwords-key-unlock-failed", "Synthetic key access failure"),
        observe ?? (() => ProcessPresence.Stopped), token);

    [Fact]
    public async Task ExplicitFallbackPreservesCorruptBytesWithoutClaimingPortableSuccess()
    {
        Prepare(); var before = File.ReadAllBytes(Path.Combine(_root, "Login Data")); var prompts = 0;
        using var result = await Read((warning, _) =>
        {
            prompts++; Assert.Equal(_root, warning.ProfileDirectory);
            Assert.Contains("not guaranteed portable or recoverable", warning.Message);
            Assert.Contains("does not authorize an unencrypted save", warning.Message);
            Assert.DoesNotContain("Synthetic key access", warning.Message);
            return Task.FromResult(true);
        });
        Assert.Equal(1, prompts); Assert.Equal(PasswordBackupCaptureKind.RawNative, result.Kind);
        Assert.NotNull(result.PortableFailureCode); Assert.Null(result.CredentialCount);
        Assert.Equal(before, result.Raw.Files["Login Data"].ToArray());
        Assert.Throws<InvalidOperationException>(() => result.Portable);
        result.VerifyUnchanged(); var borrowed = result.Raw.Files["Login Data"];
        result.Dispose(); Assert.All(borrowed.ToArray(), b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => result.Raw);
        Assert.Throws<ObjectDisposedException>(() => result.CredentialCount);
    }

    [Fact]
    public async Task DecliningOrCancellingChoiceDoesNotReturnRawSuccess()
    {
        Prepare();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read((_, _) => Task.FromResult(false)));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read((_, _) =>
        { cancellation.Cancel(); return Task.FromResult(true); }, token: cancellation.Token));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task PortableEmptySuccessNeverOffersRawFallback()
    {
        Prepare("LoginData.v43.empty.sqlite");
        using var result = await Read((_, _) => throw new Exception("Must not ask"));
        Assert.Equal(PasswordBackupCaptureKind.Portable, result.Kind); Assert.Equal(0, result.CredentialCount);
        Assert.Null(result.PortableFailureCode); Assert.Empty(result.Portable.Databases["Login Data"].Rows);
        Assert.Throws<InvalidOperationException>(() => result.Raw);
    }

    [Fact]
    public async Task FailedUnlockOffersExplicitRawChoice()
    {
        Prepare("LoginData.v43.linux.sqlite");
        using var result = await Read((warning, _) =>
        { Assert.Equal("passwords-key-unlock-failed", warning.ReasonCode); return Task.FromResult(true); });
        Assert.Equal(PasswordBackupCaptureKind.RawNative, result.Kind);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("missing")]
    [InlineData("cancelled")]
    public async Task CloseRetryMissingAndCancelledDoNotOfferFallback(string reason)
    {
        if (reason != "missing") Prepare();
        var prompts = 0;
        var exception = await Record.ExceptionAsync(() => Read((_, _) => { prompts++; return Task.FromResult(true); },
            observe: () => reason == "running" ? ProcessPresence.Running : ProcessPresence.Stopped,
            token: new CancellationToken(reason == "cancelled")));
        Assert.NotNull(exception); Assert.Equal(0, prompts);
    }

    [Fact]
    public async Task RawReadFailureAfterChoiceIsExplicitAndDoesNotRetryOrBecomeEmpty()
    {
        Prepare(); var prompts = 0;
        var error = await Assert.ThrowsAsync<BackupOperationException>(() => Read((_, _) =>
        { prompts++; File.Delete(Path.Combine(_root, "Login Data")); return Task.FromResult(true); }));
        Assert.Equal("passwords-raw-source-missing", error.Code); Assert.Equal(1, prompts);
    }

    [Fact]
    public async Task ChangedSourceRequiresRetryWithoutRawPrompt()
    {
        Prepare("LoginData.v43.empty.sqlite");
        var observations = 0; var prompts = 0;
        var error = await Assert.ThrowsAsync<BackupOperationException>(() => Read((_, _) =>
        { prompts++; return Task.FromResult(true); }, observe: () =>
        {
            if (++observations == 3)
            {
                var path = Path.Combine(_root, "Login Data"); var stamp = File.GetLastWriteTimeUtc(path);
                var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1;
                File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, stamp);
            }
            return ProcessPresence.Stopped;
        }));
        Assert.Equal("passwords-source-changed", error.Code); Assert.Equal(0, prompts);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
