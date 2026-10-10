namespace BraveBackup.Core;

/// <summary>
/// Specifies the release channel of a Brave Browser installation.
/// </summary>
public enum BraveAppChannel
{
    /// <summary>
    /// Stable release channel.
    /// </summary>
    Stable,

    /// <summary>
    /// Beta pre-release channel.
    /// </summary>
    Beta,

    /// <summary>
    /// Nightly developer channel.
    /// </summary>
    Nightly
}

/// <summary>
/// Contains metadata about a detected Brave Browser installation.
/// </summary>
/// <param name="Channel">The browser channel (Stable, Beta, Nightly).</param>
/// <param name="Version">The detected version string of the browser executable.</param>
/// <param name="ProgramPath">The filesystem path to the browser binary executable.</param>
/// <param name="UserDataDirectory">The filesystem path to the user data root directory.</param>
public sealed record BraveAppInfo(
    BraveAppChannel Channel,
    string Version,
    string ProgramPath,
    string UserDataDirectory);
