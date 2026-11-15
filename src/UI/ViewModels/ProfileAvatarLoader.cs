using System;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BraveBackup.Core;

namespace UI.ViewModels;

/// <summary>Loads profile-local account pictures and bundled Chromium-compatible or Brave profile-avatar images.</summary>
public static class ProfileAvatarLoader
{
    // Avatar resource indices are 0–89; index 26 is rendered by the UI as a generated placeholder.
    private const int DefaultAvatarIconCount = 90;
    private const int PlaceholderAvatarIconIndex = 26;
    private const int BraveAvatarIndexOffset = 56;

    /// <summary>Brave avatar filenames in upstream <c>avatarlist</c> order, corresponding to indices 56–89.</summary>
    private static readonly string[] BraveAvatarAssetNames =
    {
        "avatar_edgy_brave.png",
        "avatar_edgy_ocean.png",
        "avatar_edgy_grass.png",
        "avatar_edgy_corn.png",
        "avatar_edgy_royal.png",
        "avatar_edgy_ninja.png",
        "avatar_paladin_rewards.png",
        "avatar_paladin_brave.png",
        "avatar_paladin_ocean.png",
        "avatar_paladin_grass.png",
        "avatar_paladin_corn.png",
        "avatar_paladin_royal.png",
        "avatar_paladin_ninja.png",
        "avatar_gleam_rewards.png",
        "avatar_gleam_brave.png",
        "avatar_gleam_ocean.png",
        "avatar_gleam_grass.png",
        "avatar_gleam_corn.png",
        "avatar_gleam_royal.png",
        "avatar_gleam_ninja.png",
        "avatar_oval_rewards.png",
        "avatar_oval_brave.png",
        "avatar_oval_ocean.png",
        "avatar_oval_grass.png",
        "avatar_oval_corn.png",
        "avatar_oval_royal.png",
        "avatar_oval_ninja.png",
        "avatar_bonbon_rewards.png",
        "avatar_bonbon_brave.png",
        "avatar_bonbon_ocean.png",
        "avatar_bonbon_grass.png",
        "avatar_bonbon_corn.png",
        "avatar_bonbon_royal.png",
        "avatar_bonbon_ninja.png"
    };

    /// <summary>
    /// Loads the selected account picture when available, otherwise the bundled image for the selected avatar.
    /// </summary>
    /// <param name="profileInfo">The profile whose avatar should be loaded.</param>
    /// <returns>The decoded avatar, or <c>null</c> when the profile uses a generated placeholder or no image can be loaded.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="profileInfo"/> is null.</exception>
    public static Bitmap? Load(BraveProfileInfo profileInfo)
    {
        ArgumentNullException.ThrowIfNull(profileInfo);

        if (!string.IsNullOrWhiteSpace(profileInfo.AvatarPicturePath))
        {
            var picture = TryLoad(() => new Bitmap(profileInfo.AvatarPicturePath));
            if (picture != null)
            {
                return picture;
            }
        }

        var index = profileInfo.AvatarIconIndex;
        if (index < 0 || index >= DefaultAvatarIconCount || index == PlaceholderAvatarIconIndex)
        {
            return null;
        }

        var assetUri = index >= BraveAvatarIndexOffset
            ? new Uri($"avares://UI/Assets/BraveAvatars/{BraveAvatarAssetNames[index - BraveAvatarIndexOffset]}")
            : new Uri($"avares://UI/Assets/ProfileAvatars/avatar-{index:00}.png");
        return TryLoad(() =>
        {
            using var stream = AssetLoader.Open(assetUri);
            return new Bitmap(stream);
        });
    }

    /// <summary>Attempts to decode an image and returns <see langword="null"/> for expected I/O, authorization, argument, invalid-operation, or unsupported-operation failures.</summary>
    /// <param name="load">The deferred image-loading operation.</param>
    /// <returns>The decoded bitmap, or <see langword="null"/> when the image cannot be loaded.</returns>
    private static Bitmap? TryLoad(Func<Bitmap> load)
    {
        try
        {
            return load();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}