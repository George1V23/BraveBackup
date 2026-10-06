namespace BraveBackup.Core;

public enum BraveAppChannel
{
    Stable, //(Release)
    Beta,
    Nightly //(Dev) 
}

public sealed record BraveAppInfo(
    BraveAppChannel Channel,
    string Version,
    string ProgramPath,
    string UserDataDirectory);
