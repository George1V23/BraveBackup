# Phase 3: approved shell and selection form

This increment implements the controls approved in [ui-decisions.md](ui-decisions.md).
It does not implement the future archive writer or the deferred review items.

## Using the shell

- The native title is **Brave browsers Backup & Restore custom data**. The application
  uses a dark theme, purple accents and a placeholder icon.
- Click an installation header to activate it. Double-click it to activate it and toggle
  its Export All checkmark. Shift+Space on the focused header is the keyboard equivalent.
- The separate chevron expands installation details and profile navigation. Directory
  and executable paths are selectable read-only text, with no replacement directory picker.
- Clicking a sidebar profile opens it in Profiles. It does not check the profile for backup.
- Export All appears only when installations are checked. It opens the Backup tab with
  profile groups from those installations. Clicking a header returns to individual scope
  while preserving bulk checkmarks and remembered profile selections.
- Profile select all affects only profiles in the current backup scope. A mixed selection
  is shown as indeterminate. Category choices are shared across the scoped profiles.
- Refresh performs detection/count reads off the UI thread. It preserves matching
  activation, expanded headers, bulk checks, focused profile and backup checkboxes;
  newly discovered profiles start unchecked. Product, channel, executable path and data
  root identify an installation; changing version alone does not lose its selection.
- Disappeared installations/profiles are removed. A failed overall scan retains existing
  results, and an unreadable installation shows its own discovery message.

## Counts and appearance

`BraveProfileSummaryReader.Read` in Backup reuses the established readers. It counts URL
bookmarks recursively (not folders), and counts discovered extension directories with
version subdirectories (not enabled extensions). Missing/malformed bookmark data is
Unavailable rather than an invented zero; valid empty bookmarks are zero. Categories
are read independently. The last-used column remains Unavailable because no verified
last-use metadata reader has been implemented. No timestamp format was invented.

A Default badge means the profile directory is named `Default`; it does not mean the
profile was most recently used. Avatars, account-picture fallback, generated placeholders
and profile highlight colors reuse the existing presentation behavior.

## Options and secrets

- Default filename: `BraveBackup_yyyy-MM-dd_HHmmss.bravebackup`, using local time. The path
  is editable and Browse chooses a destination. These controls do not write files.
- Bookmarks is initially selected; other categories start unchecked. Sessions and native
  session files are omitted pending their deferred review.
- Encryption starts checked. Password and confirmation are masked, with a shared reveal
  control, matching validation and a labelled heuristic strength estimate. The estimate
  considers length/character variety; it is not a cryptographic guarantee.
- Selecting Passwords with encryption unchecked displays the plaintext CSV warning. The
  required save confirmation must be implemented with the future save operation; there
  is no plaintext credential extraction or save operation in this increment.
- Disabling encryption, Reset and closing the shell clear the form's secret strings.
  They are never persisted or logged. Clearing references does not promise secure erasure
  of immutable .NET strings from memory.
- Compression choices are None, Fast, Balanced (default), High.
- Reset clears profile selections and restores option defaults without changing the
  active installation, sidebar checks, expansion, or current individual/bulk scope.

## Existing export and deferred pages

**Open bookmark JSON exporter** opens `BookmarkExportWindow` for the active installation.
It preserves the existing selection, destination picker and schema-version-1 JSON export
using `MainViewModel` and the phase-2 destination protections. The custom archive's
filename, encryption and category controls are separate and do not affect this JSON export.
The exporter remains unencrypted bookmark-only JSON, not a `.bravebackup` archive.

Restore, Backups, Settings and About have navigation shells only. Profiles provides
read-only profile navigation/metadata and identifies where Merge/Sync will belong; no
merge behavior has been invented. Summary, size estimates, Create Backup, progress,
completion, status-bar behavior and illustration remain deferred. The bottom status-bar
area is reserved without invented status data.

## View-model responsibilities

- `ShellViewModel`: injectable discovery, activation, bulk scope, profile selection,
  refresh preservation, picker delegation and reset coordination. Construction does not
  scan the machine; the window calls RefreshAsync when opened. Dispose releases owned
  avatar bitmaps and clears option secrets; a finishing scan cannot repopulate a closed shell.
- `InstallationViewModel`: independent active, expanded and bulk-check states; profile list
  and navigation commands. Dispose releases profile presentation resources.
- `ProfileSelectionViewModel`: existing ProfileItem presentation plus count strings,
  default-directory badge, independent backup checkbox and profile-navigation command.
- `BackupOptionsViewModel`: category aggregation, filename, compression and in-memory
  encryption choices. Reset and ClearSecrets manage defaults and secret references.
- `BackupCategoryItem`: category identity, description and observable selection.
- `MainWindow`: control layout, native destination dialog and input/window routing.
  `BookmarkExportWindow`: preserved existing JSON workflow.

No NuGet package was added. Tests use injected discovery/clock/picker delegates and isolated
files. `AvaloniaTestBootstrap` initializes Avalonia before parallel test classes run,
preventing dispatcher initialization races in the existing avatar/view-model tests.

## Validation and review

- All 118 tests passed (33 Brave.Tests, 66 Core.Tests, 13 Linux.Tests, 6 Windows.Tests).
  Command: `dotnet test BraveBackup.slnx --no-restore --verbosity minimal -m:1 -nr:false`.
  The build reported no warnings/errors. The 104 phase-2 tests remain covered.
- A temporary Linux desktop runner rendered the real compiled windows with synthetic
  discovery data. It verified header command binding, Shift+Space bulk selection,
  profile/category checkbox bindings, mixed selection, password field updates/clearing,
  opening the existing exporter and navigation visibility at 1060×680.
- The final smoke run produced no binding errors. The desktop emitted a DBusMenu service
  warning when the secondary test window closed; this was an unavailable desktop service,
  not an application binding failure. Native Windows UI validation is still pending.
- Review captures use synthetic names, versions, counts and directories, not personal
  browser data: [shell](screenshots/phase3-shell.png), [backup options](screenshots/phase3-options.png).
- To review locally: `dotnet run --project src/UI/UI.csproj`. Select an installation,
  exercise the sidebar/checkboxes, refresh and Reset, and use the existing JSON exporter
  if needed. The future archive form never writes a `.bravebackup` file.
- Next checkpoint: user review of this implementation, then return to the deferred
  controls and unreviewed page behavior. No deferred workflow is implicitly approved.
