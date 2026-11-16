# Backup interface completion

Historical UI-only increment. The subsequent [archive-v1 implementation](archive-format-v1.md)
now enables bookmarks-only creation; the controls below describe their earlier idle state.

This is increment 1 of [the completion plan](backup-completion-plan.md). It adds no
archive writer and never writes a browser profile. Existing bookmark HTML/JSON export
continues through the independent Export tab. No packages or format versions changed.

## Behavior

- Sessions is selectable for open windows, tabs and tab groups. Native session files is
  optional and enabled only with Sessions. Deselecting Sessions clears the native choice;
  selecting Sessions again does not silently restore it. Reset clears both choices.
- The summary follows the current single-installation or Export All scope, listing each
  installation and its selected profiles, categories, native-file choice, encryption,
  compression and complete proposed destination. Refresh preserves surviving choices.
- The full destination is resolved for display only. Invalid/empty paths have an explicit
  message. Destination safety and save validation remain requirements of the future writer.
- Approximate archive size says Unavailable because the archive contract/category sizes
  have not been defined. It does not substitute profile-directory size for archive size.
- About the backup reflects the encryption checkbox and describes current restore limits.
  The B graphic uses placeholder branding and has no interaction.
- Create Backup is disabled with a visible explanation. Idle progress, Cancel and completion
  layouts are hidden; no simulated progress, success, folder opening or report is supplied.
- The status bar shows actual discovery/export activity or errors, Ready otherwise, the
  active installation version and the host OS. Detailed export feedback stays on Export.

## Implementation

`BackupOptionsViewModel.SessionsSelected` and `IncludeNativeSessionFiles` maintain the
session dependency. `FullBackupPath` resolves the proposed destination without creating
files; `ProtectionDescription` describes the encryption choice without secret text.
`ShellViewModel.BackupSelectionSummary` and `BackupCategorySummary` derive their values
from the current scope. Profile/option changes and refresh notify the summary bindings.
Shell subscriptions are detached on disposal. `StatusText` observes discovery and the
embedded exporter; `ActiveVersionText` follows navigation and refreshed metadata.
`HostEnvironment.OperatingSystemDescription` keeps host metadata in Platform.

## Validation

- All 156 solution tests passed on Linux: 89 Core, 48 Brave, 13 Linux and 6 Windows test
  cases. Native Windows UI behavior was not exercised on this Linux host.
- New tests cover native-session enablement/clearing/reset, safe destination display,
  encryption descriptions, scope summaries, refresh preservation, reset and status state.
  Existing HTML/JSON export and profile-protection tests still pass.
- A temporary Linux desktop fixture exercised the actual native-session checkbox,
  disabled Create Backup button and hidden idle operation panels. It verified that
  source bookmark data stayed unchanged and emitted no binding warnings.
- [Review screenshot](screenshots/backup-summary.png) shows synthetic profile data.

The next checkpoint is user review of this increment, then a concrete archive-format and
encryption-contract proposal before implementing archive creation.
