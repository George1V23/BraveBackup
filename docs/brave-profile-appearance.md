# Brave profile avatars and colors

## Metadata and profile discovery

`BraveProfileFinder` reads appearance metadata from each profile entry in the existing `Local State` file. It uses `profile.info_cache` when that object is present, otherwise `profile.profiles_attributes_storage`. Profiles are ordered by `profile.profiles_order` when available, with remaining entries appended. If `Local State` is absent, invalid, or lacks both profile metadata objects, discovery falls back to profile directories; those fallback records use the default avatar index and have no stored appearance colors or picture path.

Avatar selection comes from the numeric suffix of an `avatar_icon` value beginning exactly with `chrome://theme/IDR_PROFILE_AVATAR_`. Only decimal indices 0–89 are supported. Missing, malformed, or out-of-range values select index 26, the generated placeholder.

| Resource index | Presentation |
| --- | --- |
| 0–25, 27–55 | Existing Chromium-compatible bundled avatar images |
| 26 | Generated placeholder using the profile's available fill and stroke colors |
| 56–89 | Brave-specific bundled images, in Brave's upstream `avatarlist` order |

## Colors and account pictures

The profile name uses `profile_highlight_color` when it is a valid JSON integer. The signed 32-bit value is reinterpreted as its unsigned ARGB bit pattern for Avalonia. Missing or invalid color metadata leaves the name's foreground unset so the UI's default text color is used.

The generated placeholder uses `default_avatar_fill_color`, falling back to the profile highlight color and then `#E8EAED`. Its stroke uses `default_avatar_stroke_color`, falling back to the highlight color and then `#5F6368`. These colors affect the generated placeholder, not a loaded built-in or account-picture image.

The finder considers `gaia_picture_file_name` only when `use_gaia_picture` or `is_using_default_avatar` is the JSON literal `true`. The filename must be non-empty, relative, contain no directory separators, resolve to a direct child of the profile directory, and exist. This is path validation based on normalized path strings; it does not resolve or reject filesystem symlinks. The loader tries that picture first, then the selected bundled avatar. If neither image can be decoded—or the selected index is the placeholder index—the profile remains available and the UI displays its generated placeholder.

## Safety, assets, and tests

Appearance reads are local and read-only. Missing or malformed optional appearance data does not by itself hide a discovered profile, and no images are downloaded at runtime. Finder tests are in `tests/Brave.Tests/BraveProfileFinderTests.cs`; UI loading and presentation tests are in `tests/Core.Tests/MainViewModelTests.cs`.

The legacy avatar PNGs are sourced from Chromium's `chrome/app/theme/default_100_percent/common` resources and distributed under the Chromium BSD-3-Clause license; attribution and terms are in `docs/licenses/CHROMIUM-LICENSE.txt`. Brave's avatar PNGs are sourced from [`brave-core/app/theme/default_100_percent/common/avatars`](https://github.com/brave/brave-core/tree/d36e8c924b742f48fdf42f1f2c44641619136578/app/theme/default_100_percent/common/avatars) at revision `d36e8c924b742f48fdf42f1f2c44641619136578` and are distributed under the Mozilla Public License 2.0; attribution is in `docs/licenses/BRAVE-AVATARS-LICENSE.txt`. The index mapping comes from Brave's [theme resource manifest](https://github.com/brave/brave-core/blob/d36e8c924b742f48fdf42f1f2c44641619136578/app/theme/brave_theme_resources.grd) and [`avatarlist`](https://github.com/brave/brave-core/blob/d36e8c924b742f48fdf42f1f2c44641619136578/app/theme/default_100_percent/common/avatars/avatarlist).