# Backup tab: profile-driven steps and categories

Two behaviors of the Backup tab, both read-only and independent of what a backup later does:

## Steps 2 and below wait for a profile

"1 Select profiles" is always shown. "2 Select data to include", "3 Backup options", "4 Backup summary", the
"About the backup" card and the Reset/Create Backup panel appear only while at least one profile is selected
(`ShellViewModel.HasSelectedBackupProfiles`). The last panel also stays visible while a backup runs or its
result/report is on screen (`ShowBackupActions`), so deselecting a profile never hides a result.

## Categories follow the selected profiles

`BraveProfileCategoryAvailability.Detect` reports which categories a profile has any data for, checking only that
files or folders exist (never reading contents; links and unreadable paths count as absent):

| Category | Present when |
|---|---|
| bookmarks | the profile's `Bookmarks` file exists |
| settings | `Preferences` exists |
| passwords | `Login Data` or `Login Data For Account` exists |
| extensions | `Extensions`, `Local Extension Settings` or `Sync Extension Settings` has entries |
| sessions | `Current/Last Session|Tabs` exists, or `Sessions`/`Sessions_Encrypted` holds `Session_*`/`Tabs_*` files |
| tabmanagerplus | `Local Extension Settings/<official extension id>` has entries |

With profiles selected, `BackupOptionsViewModel.ApplyAvailability` lists a category if **any** selected profile has it and
lets it be checked only if **every** selected profile has it; otherwise it is disabled with "Not found in: <profiles>".
A category that becomes unavailable is unchecked, an unavailable category cannot be checked even programmatically,
and "Select all categories" covers only checkable ones. If no listed category exists, the card says the selected
profiles contain nothing that can be backed up. With no profile selected nothing is restricted (the steps are hidden).

Why "every": backup of a category absent from one selected profile could fail the whole operation, so it is offered
only when it cannot. Detection runs on each selection change; data created while the page is open is noticed at the
next selection change. Existing tests that ticked categories on empty fixtures now create the data first.

Tests: `BackupCategoryAvailabilityTests` (detector, step visibility, mixed-profile availability, select-all) and
native smoke checks in `tests/Manual/PasswordUiSmoke`. Full run: 1011 Core, 48 Brave, 52 Linux (4 skipped),
14 Windows-labelled (7 skipped) tests passed.
