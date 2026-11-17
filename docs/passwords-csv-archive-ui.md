# Password CSV inside backups: UI integration

The approved **Protect passwords** control now connects both representations to the
archive writer. It remains checked by default, and Reset restores it. Disabling archive
encryption never unchecks protection. A protected selection still requires encryption.

Explicitly unchecking protection chooses CSV entries, one per selected profile, inside
the backup. The already-approved password source selector must be chosen explicitly.
CSV backup is limited to the researched Brave 1.102.13 compatibility profile, including
1.102.13.0, with the existing URL/note/provider restrictions. Unsupported selected browser
versions display an explanation and disable Create backup; protected backup remains available.
Installation selection, category changes and readiness checks do not read credentials.

## Fresh consent and execution

The shell's `ConfirmPasswordCsvArchiveSave` callback is required for CSV backup. Its setter
updates availability and command state; an absent callback blocks CSV. The main window
supplies an owned, UI-thread-dispatched dialog titled **Save password CSV inside backup?**
with **Cancel backup** and **Save backup with CSV** actions.

The dialog shows the actual full archive destination, selected profile count, existing-file
replacement state and plaintext risk. It explains readability by anyone with file access
to an unencrypted archive, or after decryption for an encrypted archive, and describes
temporary staging. Existing-file replacement approval remains separate from CSV consent.
Each save requires fresh approval before archive staging or credential unlock. Closing the
dialog, cancelling the operation or declining consent does not approve saving. Operation
cancellation also closes the dialog. There is no remembered approval or credential preview.

The shell freezes categories, profile scope, destination, encryption choice, source provider,
CSV/protected mode and confirmation callback before awaiting. CSV uses LF on Linux and
CRLF on Windows, matching the standalone exporter. Existing busy interlocks freeze navigation,
refresh and competing exports while keeping cancellation available. Completion displays the
backend's value-free password summary and report; encryption form text is cleared after
completion, failure or cancellation. Disposal cancels pending work and releases the callback.

CSV capture/projection failures stop the backup, clean staging and preserve the old destination.
CSV mode does not substitute raw files or omit unsupported credentials silently. The existing
protected-backup path can offer explicit encrypted raw preservation when eligible. Neither
mode restores or imports passwords in this checkpoint.

## Validation

Seven shell test cases cover both archive encryption states, repeated consent, safe completion,
missing callback and unsupported-version blocking, declined consent, cancellation, disposal
and frozen choices during approval. Existing option/shell/backend tests remain enabled.

The opt-in [Linux desktop smoke runner](passwords-ui-smoke.md) also drives the real archive
dialog: full-path/risk text, Cancel, window-close decline, approval under both encryption
states, v8 archive validation, cancellation closing the dialog and source-byte preservation.
It uses synthetic keys and source files. Native file-picker, actual OS unlock prompts and
Windows desktop execution remain untested.

On 2026-11-17, the full solution test run passed 1,009 tests with six environment-dependent
tests skipped. The extended Linux desktop runner passed all 28 assertions with no Avalonia
warning output. Only synthetic credentials were used.

See [archive v8](archive-format-v8.md) for the backend contract and
[confirmation screenshot](screenshots/password-csv-archive-confirmation.png).
