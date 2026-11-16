# Export destination visibility and output

This note records the original JSON destination fix. The later [HTML export](brave-bookmarks-html.md)
uses the same destination rules and is now the default UI choice; JSON remains available.

## Reproduced behavior

The old suggested destination was a bare filename such as `brave-bookmarks-personal.json`.
`BraveBookmarksExporter` resolved it using the process working directory, while the form
and success message showed only that filename. The earlier tests and desktop preview
used explicit absolute destinations, so they did not cover this default-path behavior.

A controlled Linux desktop run used the real button's `OnClick` handler with an isolated
profile fixture and an unchanged default filename. The button was enabled, the success
message echoed the bare filename, and the output existed at:
`/tmp/bravebackup-export-repro-dir/brave-bookmarks-personal.json`.
The UI had not shown that directory. This confirms ambiguous working-directory output;
it does not establish every possible cause of the user's original attempt.

The debugger attach succeeded but its breakpoint could not bind the copied UI module and
its pause request timed out. Diagnosis therefore used controlled button execution,
reported destination/status and the actual output file, rather than claiming debugger
values that were unavailable. The agent breakpoint was removed and user breakpoints
were left unchanged.

## Corrected behavior

- `HostEnvironment.DocumentsDirectory` in Platform supplies the configured Documents path.
- `MainViewModel` defaults to `Documents/BraveBackup/Exports`, with home-directory fallback;
  the selected format determines whether its suggested filename ends in .html or .json.
  Its additional constructor allows tests/hosts to supply an isolated export base.
- `FullDestinationPath` resolves relative input against the stable export base. Panel 2
  shows that full path as selectable, wrapping text; no ellipsis or filename-only label
  hides the actual destination. The editable field initially contains the absolute path.
- Suggested destinations follow profile changes, while custom destinations (including
  custom names beginning with `brave-bookmarks-`) remain unchanged.
- Export snapshots the profile and resolved destination, validates it, and writes through
  the existing protected atomic exporter. The success message reports the absolute path.
  Directory creation occurs only after explicit Export, never on profile selection.
- Missing/invalid destinations and folder targets receive visible explanations. Picker
  failures are caught; unsupported nonlocal picker results are rejected instead of being
  treated as local paths. Native dialog unavailability tells the user to type a path.
- Existing profile-directory, link and overwrite protections remain intact. Schema version
  stays 1; no password or other data export is added.

## Verification

`ExportDestinationTests` covers the default destination, actual file creation, relative
resolution, suggested/custom filename behavior, invalid input, folder/I/O errors, picker
failure and path-binding notifications. All 132 solution tests passed with no build warnings.

A subsequent real-window run with an injected temporary export base verified:

- the full long path wraps onto multiple visible lines in panel 2;
- selecting a profile creates no output directories;
- the real enabled button writes JSON at exactly the displayed path;
- the output deserializes as schema version 1 and the source fixture is unchanged;
- success reports the absolute destination.

Only temporary fixture output was written during these checks. The documented default
under the user's Documents directory was not used for verification writes.
