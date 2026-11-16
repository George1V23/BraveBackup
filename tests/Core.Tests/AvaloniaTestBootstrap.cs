using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Threading;
using UI;

namespace BraveBackup.Core.Tests;

/// <summary>Initializes Avalonia once before parallel tests can create dispatcher-backed presentation objects.</summary>
internal static class AvaloniaTestBootstrap
{
    /// <summary>Sets up the shared test application before any test class runs.</summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        // These tests inspect view models and bitmaps without running an Avalonia event loop.
        // Leave asynchronous continuations on the test runner's synchronization context.
        AvaloniaSynchronizationContext.AutoInstall = false;
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
    }
}