# Approved explicit Password source UI

The user chose an initially unselected **Password source** selector. It now appears under
Backup's selected Passwords category, alongside the previously approved Protect passwords
control. It lists only the supported native provider for the current target host:

- Windows: DPAPI v10, with a current-logon-context explanation and no guaranteed fresh
  prompt; app-bound variants remain unsupported.
- Linux: Secret Service v11, explicitly requiring that the selected browsers actually use
  that backend. Native keyring unlocking can prompt. Direct KWallet/basic/portal backends
  are not automatically tried or claimed supported.

The selector is metadata only: selecting, resetting or navigating performs no password,
keyring or database access. It starts unselected, Reset clears it, and changing/rediscovering
the installation backup scope clears it to avoid reusing another scope's backend assertion.
Reactivating an unchanged scope preserves the choice. The description applies the explicit
choice to every installation in the current bulk scope; mixed/unknown backends must not
be treated as known Secret Service merely because the host is Linux.

## Protected backup wiring

Password backup becomes available only with a selected supported source, Protect passwords
checked, archive encryption enabled, valid matching archive passwords, a valid destination
and selected profiles. CSV-in-archive mode remains explicitly blocked. Existing non-password
categories and bookmark export remain available as before.

Starting the explicit backup freezes the selected source, protection choice, scope and host
callbacks before worker/native access. It supplies advanced `PasswordBackupOptions` to the
existing verified v6/v7 writer. Default host behavior uses native key access and real process
observation. Test/alternate hosts can inject controlled observation and an owned-key adapter;
no keys or OS passwords are stored in view-model state. Password backup still requires Brave
observed stopped, and unsupported protection does not trigger another algorithm/provider.

Portable failures can invoke the approved fresh **Preserve raw files / Cancel backup**
dialog. The window dispatches worker callbacks to Avalonia's UI thread, shows a sanitized
failure explanation and selected profile path, states that the archive stays encrypted and
that raw data may be incomplete/nonportable/unrecoverable, and closes without approval on
cancellation. Closing/declining cancels the operation; no implicit raw success occurs. The
raw warning carries no credential values or underlying native exception text. Completion
reports retain the existing separate portable/raw counts and unknown raw-credential status.

## Validation and remaining UI work

Four new tests cover initially unselected/reset metadata, explicit protected backup through
the shell with synthetic Windows/Linux provider data, encrypted v6 reopening and safe
reports, explicit host raw fallback to encrypted v7, and scope/rediscovery reset behavior.
Existing option/shell/capture tests remain active; one previous unsupported-category message
assertion now expects the missing-source explanation. Avalonia bindings and dialog code
compile in the full build. No actual OS keyring, real credentials or native visual dialog
was accessed/opened during these tests; native window interaction remains unverified.

Standalone password CSV Export controls, CSV archive entries/consent, broader provider/URL
compatibility, and native Windows/keyring runtime validation remain pending. The approved
source selector does not imply those unsupported paths are enabled. No restore operation
is introduced.
