using BraveBackup.Core;

namespace UI.ViewModels;

/// <summary>A supported explicit source choice; selecting it never reads credentials or probes a key store.</summary>
public sealed class PasswordSourceOption
{
    public string Label { get; }
    public string Description { get; }
    public PasswordEncryptionProvider Provider { get; }
    internal PasswordSourceOption(string label, string description, PasswordEncryptionProvider provider)
    { Label = label; Description = description; Provider = provider; }
    public override string ToString() => Label;
}
