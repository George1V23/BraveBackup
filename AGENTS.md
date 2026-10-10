# Project: BraveBackup

BraveBackup is a cross-platform application for Brave browser which can
backup/restore the profiles detected with bookmarks, passwords, settings,
session, windows, tabs, extensions in a locally format and can sync/merge
data between profiles without the need of internet.

## Architecture

Language:
- C#
- .NET 10

UI:
- Avalonia
- MVVM

Targets:
- Windows 10/11
- Linux

Rules:
1. Must be platform-independent with no Windows/Linux APIs in Core project.
2. Brave-specific implementation belongs in Backup project.
3. OS-specific implementation belongs in Platform project.
4. TabManagerPlus belongs in its own project inside Extensions folder.
5. CLI project must remain usable without the UI project.
6. Never directly modify a live Brave profile without an explicit restore operation.
7. Never restore into a running Brave profile.
8. Restore must create a safety backup before modifying files.
9. Never log passwords, encryption keys, cookies, or secrets.
10. Never store plaintext passwords in the backup.
11. Do not invent undocumented Brave/Chromium formats. 
12. If a format is undocumented, create a research note and test fixture. 
13. All new functionality requires tests. 
14. Do not add NuGet packages without explaining their purpose. 
15. Do not change backup format version without updating documentation. 
16. Preserve backward compatibility with previous backup versions.

## Development rules

- After implementing new classes and methods write documentation.
- When making documentation, ensure it is clear, concise, and up-to-date.