# Phase 3: approved shell and selection form

This increment implements the controls approved in [ui-decisions.md](ui-decisions.md).
The later [archive-v1 increment](archive-format-v1.md) now enables bookmarks-only backup
creation. Workflows on unreviewed pages remain unavailable.

## Using the shell

- The native title is **Brave browsers Backup & Restore custom data**. The application
  uses a dark theme, purple accents and a placeholder icon. The sidebar starts with
  installation controls; the static branding header was removed following user review.
- Tabs are visible only while an installation is active, including after Refresh removes an active installation.
- The main workspace begins with Backup, Restore, Export, Import, Profiles, Backups,
  Settings and About. The former workspace title and description were removed following review.
- Click an installation header to activate it. Double-click it to activate it and toggle
  its Export All checkmark. Shift+Space on the focused header is the keyboard equivalent.
- The separate chevron expands installation details and profile navigation. Directory
  and executable paths are selectable read-only text, with no replacement directory picker.
- Clicking a sidebar profile opens it in Profiles. It does not check the profile for backup.
- Export All appears only when installations are checked. It opens the Backup tab with
  profile groups from those installations. If Refresh removed the active installation,
  Export All activates the first checked installation so the workspace can be shown. Clicking a header returns to individual scope
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
- Bookmarks is initially selected; other implemented categories start unchecked. The later
  review approved Sessions (open windows, tabs and tab groups) and optional native session
  files enabled only with Sessions. Their selection controls are implemented; session data handling is not implemented yet.
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

**Backup** contains only the custom archive form. Its former exporter-launcher row is removed.
**Restore** is reserved for restoring backup files; it does not combine import operations.
**Export** directly embeds `BookmarkExportView`, with profile selection, destination text,
HTML/JSON format choice, native Browse, export action and operation feedback. Panel 2 includes a selectable,
wrapping full destination path, so long paths and resolved relative input remain visible. There is no separate exporter
window in the shell workflow. The selected sidebar installation supplies the profile list;
no duplicate installation selector can diverge from the sidebar. Custom export paths and
surviving profile selections are preserved through refresh; removed active installations
clear the exporter and disable its action. There is no second installation scan on the UI
thread: the embedded exporter consumes the shell's read-only discovery snapshot.

The default destination is `Documents/BraveBackup/Exports/brave-bookmarks-<profile>.html`,
falling back to `~/BraveBackup/Exports` when Documents is unavailable. The default is
absolute. Relative filenames resolve against that same stable export directory, not the
process launch directory. Selecting a profile or destination does not create files or
folders. The Export action writes to the full path shown in panel 2 and reports it on
success. Invalid destinations, folder paths, file-write failures and save-picker failures
produce visible feedback. Custom paths are preserved when switching profiles or refreshing.

HTML is primary and selected by default, using Brave's browser-compatible bookmark
format. JSON remains selectable and writes the unchanged schema-version-1 format.
See [HTML format research and limits](brave-bookmarks-html.md), including the absence of
optional favicon images. Both choices retain phase-2 destination protections. The custom
archive's filename, categories, encryption and compression do not affect these exports.

**Import** is a separate navigation page for future import operations; its supported
formats and behavior still require review. Restore, Import, Backups, Settings and About
currently have navigation shells only. Profiles provides read-only navigation/metadata
and identifies where Merge/Sync will belong. The subsequent review approved the backup
summary, size estimate and Create Backup action. The interface increment now includes these
controls, the explanatory panel, placeholder-brand illustration and actual status bar.
Create Backup now enables for a valid bookmarks-only request. Progress/cancel and
completion layouts are connected to actual archive work; other categories block creation.
See [Backup interface completion](backup-interface-completion.md).

`BookmarkExportWindow` remains a compatibility host for the same reusable control, but
there is no launcher for it in the shell. Both hosts release their exporter-owned avatars
when closed. This preserves the existing standalone type without duplicate form markup.

## View-model responsibilities

- `ShellViewModel`: injectable discovery, activation, bulk scope, profile selection,
  refresh preservation, picker delegation and reset coordination. Construction does not
  scan the machine; the window calls RefreshAsync when opened. Dispose releases owned
  avatar bitmaps, disposes the embedded exporter and clears option secrets; a finishing scan cannot repopulate a closed shell.
- `InstallationViewModel`: independent active, expanded and bulk-check states; profile list
  and navigation commands. Dispose releases profile presentation resources.
- `ProfileSelectionViewModel`: existing ProfileItem presentation plus count strings,
  default-directory badge, independent backup checkbox and profile-navigation command.
- `BackupOptionsViewModel`: category aggregation, filename, compression and in-memory
  encryption choices. Reset and ClearSecrets manage defaults and secret references.
- `BackupCategoryItem`: category identity, description and observable selection.
- `MainViewModel`: the established JSON export behavior, now with an additional constructor
  accepting discovery delegates so the embedded host can share shell results. Existing
  constructors retain native detection. A further constructor accepts an explicit export
  base for isolated tests/hosts. `FullDestinationPath` resolves input, validation explains
  invalid input, and suggested filenames are tracked separately from custom user paths.
  Dispose and profile replacement release owned avatars.
- `MainWindow`: layout, native archive/JSON pickers and input routing.
  `BookmarkExportView`: reusable, host-supplied export controls without window creation.
  `BookmarkExportWindow`: compatibility host for that control.
- `BookmarkExportFormatOption`: immutable HTML/JSON choice metadata, shared by the form
  and native file-picker filters. HTML is first/default; choice changes preserve filename
  location/basename while updating its extension and clear obsolete operation feedback.
- Shell tab-position constants keep profile navigation correct after adding Export and Import.

No NuGet package was added. Tests use injected discovery/clock/picker delegates and isolated
files. `AvaloniaTestBootstrap` initializes Avalonia before parallel test classes run,
preventing dispatcher initialization races in the existing avatar/view-model tests. It
disables automatic Avalonia synchronization-context installation: these tests have no UI
event loop, so asynchronous continuations must remain on the test runner rather than
queueing indefinitely on an idle dispatcher.

## Validation and review

- All 152 tests passed (48 Brave.Tests, 85 Core.Tests, 13 Linux.Tests, 6 Windows.Tests).
  Command: `dotnet test BraveBackup.slnx --no-restore --verbosity minimal -m:1 -nr:false`.
  The build reported no warnings/errors. The existing regression tests remain covered.
- The latest Linux desktop fixture run checked all eight tab labels/order, hidden tabs
  without activation, profile navigation, direct Export hosting, no secondary window,
  successful fixture HTML/JSON exports, installation switching and minimum-width navigation.
  It produced no binding errors or runtime warnings. Native Windows UI validation is pending.
- A subsequent real-button check verified that a long destination wraps fully in panel 2,
  that the default field is absolute, and that clicking Export creates valid schema-1 JSON
  at exactly the displayed path while leaving the source fixture unchanged. See
  [export destination fix](export-destination-fix.md) for the reproduction and regression details.
- Earlier desktop checks covered Shift+Space bulk selection, scoped profile/category
  checkboxes, mixed selection and password field updates/clearing.
- Review captures use synthetic names, versions, counts and directories, not personal
  browser data: [Backup](screenshots/phase3-shell.png),
  [Export](screenshots/export-tab.png), [backup options](screenshots/phase3-options.png).
- To review locally: `dotnet run --project src/UI/UI.csproj`. Select an installation,
  exercise the tabs, sidebar/checkboxes, refresh and Reset, and use Export for bookmark
  HTML or JSON. The Backup form now writes verified bookmarks-only `.bravebackup` files.
- Next checkpoint: user review of bookmarks-only archive creation, then choose the next
  category increment. Unreviewed page workflows remain pending.

## Settings backup category

The Backup form also supports the approved limited Settings category, alone or alongside
Bookmarks. Its description lists stored bookmark-bar, Home and startup preferences.
Completion reports stored/absent counts; unsupported categories still block creation.
This does not implement the separate Settings page. See [Settings backup and archive v2](settings-backup.md).
