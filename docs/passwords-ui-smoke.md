# Synthetic Linux password UI smoke test

The opt-in runner at `tests/Manual/PasswordUiSmoke` opens the actual Avalonia main
window and owned confirmation dialogs on a Linux desktop. It is separate from the
headless test suite and requires a working graphical display and .NET 10.

From the repository root:

```sh
dotnet run --project tests/Manual/PasswordUiSmoke/PasswordUiSmoke.csproj
```

Alternatively, pass the absolute repository path after `--`. No additional NuGet
packages are introduced; the runner references the existing UI project. A failed
assertion closes the application with a nonzero exit code.

## Scope and isolation

Before the main window opens, the runner removes its discovery handler and replaces
the default shell with injected synthetic installation/profile metadata. It copies
checked-in SQLite/bookmark fixtures to a unique temporary directory and supplies a
synthetic encryption key. It does not discover real installations, access a real
password store, or invoke the OS keyring. The native file picker is replaced by a
test callback. Temporary outputs contain synthetic credentials only and are retained
for inspection under `bravebackup-password-ui-preview-*` in the OS temporary folder.

The runner drives actual selector bindings and dialog button handlers, checking:

- The password source starts unselected and export becomes enabled after selection.
- The CSV section has rendered bounds below the bookmark exporter.
- The real plaintext warning displays the full destination and readability warning.
- Cancel publishes no CSV; a separate approval saves the synthetic CSV.
- The completion report contains counts only and the source files remain unchanged.
- A deliberately corrupt synthetic password database prompts for raw preservation.
- Explicit preservation creates a valid encrypted version-7 backup.

It saves a rendered main-window image to
[`screenshots/password-csv-section.png`](screenshots/password-csv-section.png).
This screenshot contains synthetic metadata and paths, not credential values.

## Recorded result and limits

On 2026-11-17, the runner passed all 14 assertions using Linux/X11 software rendering
at 1160 × 860. No Avalonia warning output was emitted. The section's destination,
readiness, action and completion feedback were visible in the rendered image.

This validates native Avalonia layout, bindings and dialog interaction. It does not
validate manual mouse/keyboard accessibility, other display scales/window sizes,
the native file picker, actual OS unlock prompts, or execution on Windows. The
existing unit/integration tests remain responsible for backend format and failure
coverage. CSV entries inside backup archives remain unfinished.
