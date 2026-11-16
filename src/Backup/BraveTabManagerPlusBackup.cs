using System.IO.Compression;
using System.Security.Cryptography;
using BraveBackup.Extensions.TabManagerPlus;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Per-operation host callbacks for saved-window process checks and explicit unencrypted-save consent.</summary>
public sealed class TabManagerPlusBackupOptions
{
    public Func<ProcessPresence>? ObserveBrowserState { get; init; }
    public Func<string, Task<bool>>? ConfirmUnencryptedData { get; init; }
}

/// <summary>Applies researched extension identity/version, typed saved-window semantics and ordering to archives.</summary>
public static class BraveTabManagerPlusBackup
{
    /// <summary>Validates saved-window semantics after Core has verified container structure and payload hashes.</summary>
    public static void ValidateCategory(ZipArchive zip, ArchiveCategory category, CancellationToken token)
    {
        var file = category.Files.Single();
        if (file.Length > SavedWindowPayload.MaximumBytes) throw new InvalidDataException("Oversized saved-window payload.");
        var bytes = new byte[checked((int)file.Length)];
        try
        {
            using var input = zip.GetEntry(file.Path)!.Open();
            for (var offset = 0; offset < bytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                var count = input.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                if (count == 0) throw new InvalidDataException("Truncated saved-window payload.");
                offset += count;
            }
            SavedWindowPayload.Parse(bytes, token);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

/// <summary>Owns captured profile models until final source verification, with aggregate operation resource limits.</summary>
internal sealed class TabManagerPlusCaptureSession : IDisposable
{
    private readonly List<BraveTabManagerPlusCapture> _captures = new();
    private readonly Func<ProcessPresence> _observe;
    private readonly CancellationToken _token;
    public long Bytes { get; private set; }
    public int WindowCount { get; private set; }
    public int TabCount { get; private set; }
    public int SourceFiles { get; private set; }
    public int Profiles => _captures.Count;

    public TabManagerPlusCaptureSession(IReadOnlyList<BraveBackupSelection> scope,
        TabManagerPlusBackupOptions options, CancellationToken token)
    {
        _token = token;
        var prefixes = new[] { "brave" }.Concat(scope.Select(item =>
                Path.GetFileNameWithoutExtension(item.Installation.ProgramPath)))
            .Where(name => !string.IsNullOrEmpty(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _observe = options.ObserveBrowserState ?? (() => ProcessPresenceProbe.Observe(prefixes, token));
    }

    public void EnsureStopped()
    {
        _token.ThrowIfCancellationRequested();
        ProcessPresence state;
        try { state = _observe(); }
        catch (OperationCanceledException) { throw; }
        catch { state = ProcessPresence.Unknown; }
        if (state != ProcessPresence.Stopped)
            throw new BackupOperationException("tabmanagerplus-browser-not-stopped",
                "Close all Brave instances before backing up TabManagerPlus. Its saved windows require an established stopped state.");
    }

    public ArchiveCategory Capture(string profile, string prefix, ZipArchive zip, string compression, int availableEntries)
    {
        if (availableEntries < 1) throw Limit();
        EnsureStopped();
        var capture = BraveTabManagerPlusCapture.Read(profile, _observe, _token);
        byte[]? bytes = null;
        try
        {
            WindowCount = checked(WindowCount + capture.Data.Windows.Length);
            TabCount = checked(TabCount + capture.Data.TabCount);
            SourceFiles = checked(SourceFiles + capture.SourceFileCount);
            if (WindowCount > 100_000 || TabCount > 100_000 || SourceFiles > 100_000) throw Limit();
            bytes = SavedWindowPayload.Serialize(capture.Data, capture.CapturedUtc, capture.SourceFileCount, _token);
            Bytes += bytes.Length;
            if (Bytes > 256L * 1024 * 1024) throw Limit();
            var file = BackupArchive.WriteFile(zip, prefix + "saved-windows-v1.json", "portable-extension-windows",
                bytes, compression, _token);
            _captures.Add(capture);
            capture = null;
            return new ArchiveCategory("tabmanagerplus", 1, new[] { file });
        }
        finally
        {
            capture?.Dispose();
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void VerifySources()
    {
        foreach (var capture in _captures) capture.VerifyUnchanged();
        EnsureStopped();
    }

    public void Dispose()
    {
        foreach (var capture in _captures) capture.Dispose();
        _captures.Clear();
    }

    private static BackupOperationException Limit() => new("tabmanagerplus-limit",
        "Selected saved-window data exceeds supported archive operation limits.");
}
