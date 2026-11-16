using System.Runtime.CompilerServices;
using Avalonia;
using UI;

namespace BraveBackup.Core.Tests;

/// <summary>Initializes Avalonia once before parallel tests can create dispatcher-backed presentation objects.</summary>
internal static class AvaloniaTestBootstrap
{
    /// <summary>Sets up the shared test application before any test class runs.</summary>
    [ModuleInitializer]
    internal static void Initialize() => AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
}