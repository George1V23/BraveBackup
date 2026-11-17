# Approved password-protection UI checkpoint

The Backup category panel now shows the already-approved **Protect passwords** checkbox
when Passwords is selected. It defaults to checked; Reset restores checked. Selecting
categories or changing this control performs no credential, keyring or source-file read.

Checked means the protected portable option requires whole-archive encryption. Turning
Encrypt backup off never unchecks protection or silently chooses CSV; an actionable
validation message asks to re-enable encryption. Unchecking protection explicitly chooses
the CSV representation. The later [CSV archive UI integration](passwords-csv-archive-ui.md)
connects that explicit choice to the v8 writer with fresh destination-specific consent,
supported-source selection and researched-version checks.

The existing CSV warning now uses the actual representation/container choice. When CSV
is explicitly chosen it describes either readability after archive decryption/extraction,
or readability by anyone with file access for an unencrypted container. The warning is
informational only and does not grant save consent. Fresh destination-specific confirmation
remains mandatory at the actual save. Protected data with encryption disabled displays
the protection error rather than claiming readable CSV was automatically selected.

Shell availability surfaces these mode errors before the generic unsupported-category
message. Protected password actions now use the [approved explicit provider selector](passwords-ui-provider.md)
and remain blocked until its required selection/protection conditions are satisfied. Existing working categories,
bookmark exports and archive
operations remain unchanged. No native credential UI was opened for this work.

## Provider control review completed

The user asked to decide each visible control. The newly needed **Password source** selector
was therefore presented for review, with two concrete alternatives: add an explicit
supported-provider selector initially unselected, or show capability information and keep
password operations blocked. The user chose the initially unselected selector. It is now implemented and connected
for protected backup: [provider UI checkpoint](passwords-ui-provider.md). No source is
inferred automatically. This is the user's phase-1 review requirement, not an AGENTS.md approval rule.

## Validation

Three new tests cover default/reset/encryption independence, explicit CSV warnings under
both container modes, property-change notifications and shell blocking/explanations without
file creation. Existing option and shell tests remain enabled; one previous warning test
was updated to require the approved explicit CSV choice. Compiled Avalonia bindings are
verified by the build. No live profile, keyring or credential was accessed; a native visual
preview was not performed in this checkpoint.
