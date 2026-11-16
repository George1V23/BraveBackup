# Approved UI decisions

Source: the supplied UI reference image and the user's subsequent control-by-control decisions.
The long shared conversation is not a requirements source; it could not be read and the user
decided it was unnecessary. The first implementation increment was approved on 2026-11-16.

## Shell and installation navigation

- Keep the dark design and purple accents. Use a placeholder icon and the name **Brave browsers Backup & Restore custom
  data**.
- The static branding header is removed from the sidebar; the application name remains in the window title.
- Each detected Brave installation has a sidebar header. Single-click activates the
  installation and updates the main workspace. Double-click also toggles a visible
  bulk-export checkmark. A separate chevron expands installation details and profiles.
- Details show product, channel, version, executable path and user-data directory.
  The directory is read-only information, never an editable replacement or custom picker.
- Sidebar profiles show avatar/name; clicking one opens it in the Profiles tab.
- Remove the static main-workspace title and description; place tabs at the top.
- Main workspace tabs are hidden when no installation is active.
- Main workspace tabs: Backup, Restore, Export, Import, Profiles, Backups, Settings, About.
  Backup is for custom backup archives; Restore is for restoration from backup files.
  Export directly embeds bookmark export with browser-compatible HTML as the primary/default
  option and existing schema-1 JSON as a secondary option. Import is a distinct page for future import operations.
  Export panel 2 shows the complete resolved destination path with wrapping; output must
  be written to that displayed path with explicit success/error feedback.
  The former exporter-launcher row is removed from Backup; no separate window is opened from the shell.
  Merge/Sync belongs within Profiles. Unreviewed page contents remain undecided.
- Refresh rescans installations/profiles and preserves active/checked entries that still exist.
- Export All appears when at least one installation is checked. It opens profile and
  category selection in the main workspace for those installations; it does not immediately save.

## Backup selection

- Multiple profile checkboxes and select all. Bulk selection groups profiles by installation.
- Profile information includes avatar, name, directory, default badge, bookmark and
  extension counts, and last used. Unknown/unreliable values appear as Unavailable.
- Categories: Bookmarks, Passwords, Extensions (including supported settings/data),
  Settings (supported profile preferences), TabManagerPlus (supported windows/groups/data).
- Sessions is approved: include open windows, tabs and tab groups from selected profiles.
- Include native session files is approved as an optional additional inclusion for best-effort
  restoration, enabled only when Sessions is selected. These two controls were explicitly
  kept during the subsequent deferred-control review; implementation is still pending.
- Select all categories supports a mixed state. Choices apply across selected profiles;
  differences are intended for the later summary.

## Password exception

- Protection is checked by default. The user can explicitly choose plaintext CSV,
  including inside an unencrypted backup.
- Warn about readable credentials and require explicit confirmation before saving locally.
- This is the user-approved exception to the former blanket prohibition in AGENTS.md rule 10.
  Secret logging remains prohibited. No password extraction/export is implemented in phase 2.

## Backup options

- Editable destination plus Browse; timestamped, editable `.bravebackup` filename.
- Whole-archive encryption checked by default. Disabling it with passwords selected
  requires the plaintext warning and confirmation before saving.
- Masked encryption password, visibility toggle, confirmation and strength indicator;
  a password is required when encryption is enabled.
- Compression: None, Fast, Balanced (default), High.
- Reset restores form defaults, preserving active installation and sidebar bulk selections.

## Approved backup summary and action

- Backup summary: show selected installations, profiles, categories, encryption,
  compression and destination, updating when choices change.
- Estimated size: show an approximate size, with Calculating or Unavailable states
  when appropriate.
- Create Backup: validate selections and destination, obtain the required confirmation
  for plaintext passwords, then create the `.bravebackup` file. Never silently omit
  selected data. These controls are approved; implementation is still pending.

## Approved backup feedback controls

- About the backup panel: explain the selected encryption state and supported restoration limits.
- Progress/cancellation: show the current stage and progress; Cancel safely cleans up incomplete output.
- Completion feedback: show success or actionable errors, with options to open the destination
  folder and view an operation report containing no secrets.
- Bottom status bar: show Ready/current operation/error, the active Brave version and OS.
- Decorative illustration: keep a reference-style backup graphic adapted to placeholder branding;
  it is decorative only.

All five controls were kept during the subsequent review. Their implementation is pending.
The Backup page's previously deferred controls have now been reviewed; this does not approve
unreviewed behavior for Restore, Import, Backups, Settings, About or Merge/Sync.

## Delivery checkpoints

1. Approved first portion of specification (this document).
2. Protect existing export behavior and align Core / Backup / Platform; review before UI work.
3. Implement approved UI controls and selection state; no new archive-writing workflow yet.
4. Revisit deferred controls and the remaining phased completion plan with the user.

Each implementation phase includes tests and documentation. Do not invent decisions for
the unreviewed pages or promise cross-machine credential portability without validation.

## Phase 3 implementation

The approved shell and selection form are implemented. See [ui-phase3.md](ui-phase3.md)
for behavior, review screenshots, validation and remaining limits. The previously deferred Backup-page controls have now been reviewed and kept.
See [the next implementation checkpoints](backup-completion-plan.md); the remaining
pages still require their own review.
