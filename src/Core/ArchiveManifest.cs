using System.Text.Json;

namespace BraveBackup.Core;

/// <summary>Versioned browser-independent archive metadata; never contains passwords or absolute source paths.</summary>
public sealed record ArchiveManifest(
    string Format,
    int FormatVersion,
    Guid ArchiveId,
    DateTimeOffset CreatedUtc,
    string ApplicationVersion,
    string[] RequiredCapabilities,
    string Compression,
    ArchiveInstallation[] Installations)
{
    /// <summary>Gets the canonical archive JSON settings, separate from legacy bookmark JSON.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, RespectRequiredConstructorParameters = true };
}

/// <summary>Archive-local installation identity and informational browser metadata.</summary>
public sealed record ArchiveInstallation(
    Guid Id,
    string Product,
    string Channel,
    string BrowserVersion,
    ArchiveProfile[] Profiles);

/// <summary>Archive-local profile identity; directory name is informational, never a restore destination.</summary>
public sealed record ArchiveProfile(Guid Id, string DisplayName, string DirectoryName, ArchiveCategory[] Categories);

/// <summary>A required versioned data category and its payloads.</summary>
public sealed record ArchiveCategory(string Id, int Version, ArchiveFile[] Files);

/// <summary>Payload identity, role, uncompressed byte count and SHA-256 digest.</summary>
public sealed record ArchiveFile(string Path, string Role, long Length, string Sha256);

/// <summary>Resource ceilings applied before allocating or decompressing untrusted archive data.</summary>
public static class ArchiveLimits
{
    public const long MaxBytes = 100L * 1024 * 1024 * 1024;
    public const int MaxEntries = 100_000;
    public const int MaxManifestBytes = 16 * 1024 * 1024;
    public const int MaxPreferencesBytes = 32 * 1024 * 1024;
    public const int MaxSettingsBytes = 1024 * 1024;
    public const int MaxBookmarkBytes = 256 * 1024 * 1024;
}