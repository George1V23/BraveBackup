# Restore Settings on Linux

Select **Settings** in the Restore tab after mapping an archived profile to an existing
Linux Brave profile with the same recorded product and browser version. Settings works
alone or alongside Bookmarks. Review shows the exact destination `Preferences` file.
Confirmation uses the same stopped-browser checks, verified encrypted safety backup,
journal, pause, resume and rollback as [Bookmarks restore](restore-bookmarks-execution.md).
The final report identifies each completed category for its installation and profile.

## What transfers

The writer uses the existing Settings v1 payload. It applies the stored bookmark bar,
Home and startup choices listed in the [Settings backup contract](settings-backup.md).
It preserves explicitly stored false values, empty strings or lists, and startup address
order. If the archive records a choice as absent, the destination keeps its existing
value. Other preference values in the file are preserved. Saving can change JSON
formatting. Rollback recovers the original file bytes and supported Linux metadata.

The destination needs a readable, existing `Preferences` JSON object. The entire plan
stops before writing if a selected Settings payload contains no stored choices, the
existing file is missing or malformed, a selected path has an incompatible parent or
value, or the same selected key is found in `Secure Preferences`. If a Settings
selection blocks the plan, choose another destination or clear that category and review
the remaining changes. The native preference file limit is 32 MiB. Confirmation displays
sanitized errors. Settings addresses are kept out of review and completion reports.

The operation changes only the selected values in the local `Preferences` file.
`Secure Preferences`, integrity metadata, browser policy, and account or Sync data remain
outside its scope. Browser policy and Sync can affect the effective choices. Startup
configuration applies to the startup mode and addresses, while session windows and tabs
have their own restore category. Windows execution remains pending.

## Research and verification

Chromium's preference factory identifies the Home and startup paths as tracked and
selects telemetry-only tracking on Linux in the inspected source. Its protected store
uses a separate file on platforms with enforcement. The Brave override inspected adds
a tracked Shields preference outside this restore scope. The writer updates only the
existing allowlisted paths in the plain dictionary and leaves the existing protection
metadata intact. See the [Chromium preference factory, blob 4c737923](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/chrome/browser/prefs/chrome_pref_service_factory.cc#255),
[Chromium's tracked preference documentation](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/services/preferences/tracked/README.md),
and [Brave's preference factory patch, target blob 642a359e](https://raw.githubusercontent.com/brave/brave-core/master/patches/chrome-browser-prefs-chrome_pref_service_factory.cc.patch).
The existing [source research](settings-preferences-research.md) records the preference
names and types used by Settings v1.

Linux integration tests use synthetic Preferences dictionaries in
`tests/Linux.Tests/BraveSettingsRestoreTests.cs`. They exercise a plain Settings-only
archive, an encrypted mixed archive, exact rollback, preserved neighbors and rejection
of an unsafe Settings selection before any category publishes files. The Avalonia smoke
runner uses `tests/Core.Tests/Fixtures/Preferences.settings.json` and confirms both
categories through the Restore tab. A separate local check on Linux copied the restored
synthetic Preferences file into a disposable Brave profile and verified all seven
values through the settings service in Brave 1.96.61 (Chromium 154). That browser check
used an isolated profile and establishes behavior for the tested build and values.
