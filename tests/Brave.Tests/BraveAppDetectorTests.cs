using BraveBackup.Core;
using Xunit;

namespace Brave.Tests;

public class BraveAppDetectorTests
{
    [Fact]
    public void Windows_DetectsAllProductsEvenWhenRegistryFindsOne()
    {
        var root = Path.Combine("test", "local");
        var programFiles = Path.Combine("test", "programs");
        var names = new[] { "Brave-Browser", "Brave-Browser-Beta", "Brave-Browser-Nightly", "Brave-Origin", "Brave-Origin-Beta", "Brave-Origin-Nightly" };
        var executables = names.Select(name => Path.Combine(programFiles, "BraveSoftware", name, "Application", "brave.exe")).ToHashSet();
        var apps = BraveAppDetector.DetectWindowsApps(root, programFiles, "", (key, value) =>
            key.EndsWith("Brave-Browser") && value == "installation directory"
                ? Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application") : null,
            executables.Contains, _ => "1.0");

        Assert.Equal(6, apps.Count);
        foreach (var name in names)
            Assert.Contains(apps, app => app.UserDataDirectory == Path.Combine(root, "BraveSoftware", name, "User Data"));
        Assert.Equal(3, apps.Count(app => app.Product == BraveAppProduct.Origin));
    }

    [Fact]
    public void Linux_DetectsChannelsAndUsesConfiguredDataRoot()
    {
        var paths = new[] { "/test/bin/brave-browser", "/test/bin/brave-browser-beta", "/test/bin/brave-browser-nightly", "/test/bin/brave-origin" }.ToHashSet();
        var apps = BraveAppDetector.DetectLinuxApps(new[] { "/test/bin" }, "/custom/config", paths.Contains, _ => "1.0");

        Assert.Equal(4, apps.Count);
        Assert.Contains(apps, app => app.Channel == BraveAppChannel.Beta && app.UserDataDirectory == "/custom/config/BraveSoftware/Brave-Browser-Beta");
        Assert.Contains(apps, app => app.Product == BraveAppProduct.Origin && app.UserDataDirectory == "/custom/config/BraveSoftware/Brave-Origin");
        Assert.Equal(4, apps.Select(app => app.UserDataDirectory).Distinct().Count());
    }

    [Fact]
    public void Linux_DetectsInstalledArchPackageLaunchersWithoutDuplicates()
    {
        var executables = new[] { "/usr/bin/brave", "/usr/bin/brave-beta", "/usr/bin/brave-browser-beta",
            "/usr/bin/brave-nightly", "/usr/bin/brave-browser-nightly", "/usr/bin/brave-origin" }.ToHashSet();
        var apps = BraveAppDetector.DetectLinuxApps(Array.Empty<string>(), "/config", executables.Contains, _ => "1.0");

        Assert.Equal(4, apps.Count);
        Assert.Contains(apps, app => app.Product == BraveAppProduct.Browser && app.Channel == BraveAppChannel.Stable &&
            app.ProgramPath == "/usr/bin/brave" && app.UserDataDirectory == "/config/BraveSoftware/Brave-Browser");
        Assert.Contains(apps, app => app.Product == BraveAppProduct.Browser && app.Channel == BraveAppChannel.Beta &&
            app.UserDataDirectory == "/config/BraveSoftware/Brave-Browser-Beta");
        Assert.Contains(apps, app => app.Product == BraveAppProduct.Browser && app.Channel == BraveAppChannel.Nightly &&
            app.UserDataDirectory == "/config/BraveSoftware/Brave-Browser-Nightly");
        Assert.Contains(apps, app => app.Product == BraveAppProduct.Origin && app.Channel == BraveAppChannel.Stable &&
            app.UserDataDirectory == "/config/BraveSoftware/Brave-Origin");
    }

    [Theory]
    [InlineData("brave", BraveAppChannel.Stable)]
    [InlineData("brave-beta", BraveAppChannel.Beta)]
    [InlineData("brave-nightly", BraveAppChannel.Nightly)]
    public void Linux_DetectsAlternateChannelLauncher(string launcher, BraveAppChannel channel)
    {
        var executable = "/test/bin/" + launcher;
        var apps = BraveAppDetector.DetectLinuxApps(new[] { "/test/bin" }, "/config", path => path == executable, _ => "1.0");

        var app = Assert.Single(apps);
        Assert.Equal(BraveAppProduct.Browser, app.Product);
        Assert.Equal(channel, app.Channel);
        Assert.Equal(executable, app.ProgramPath);
    }

    [Fact]
    public void Linux_PrefersSystemLauncherOverPathWrapper()
    {
        var executables = new[] { "/home/user/.local/bin/brave", "/usr/bin/brave" }.ToHashSet();
        var apps = BraveAppDetector.DetectLinuxApps(new[] { "/home/user/.local/bin", "/usr/bin" }, "/config", executables.Contains, _ => "1.0");

        Assert.Equal("/usr/bin/brave", Assert.Single(apps).ProgramPath);
    }

    [Fact]
    public void Detection_ReturnsEmptyWhenNoExecutablesExist()
    {
        Assert.Empty(BraveAppDetector.DetectLinuxApps(Array.Empty<string>(), "/config", _ => false, _ => "Unknown"));
        Assert.Empty(BraveAppDetector.DetectWindowsApps("local", "programs", "", (_, _) => null, _ => false, _ => "Unknown"));
    }
}