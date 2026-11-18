# Restore persisted Sessions on Linux

1. Open a backup created with **Sessions** and **Include native session files**.
   Restore preview shows the archived window and tab counts and whether the verified
   native snapshot is present.
2. Map the archived profile to an existing profile on a 64-bit little-endian Linux
   installation with the same recorded Brave product and version. Close Brave,
   choose **Recheck**, and select **Sessions** if the destination is eligible.
3. Choose an encrypted safety backup location and password. **Review restore**
   shows the exact `Sessions/Session_<number>` file to replace.
4. **Confirm restore** validates the archive and destination again, verifies the
   encrypted safety backup, and atomically replaces that file. Pause, explicit
   resume and rollback use the existing Restore controls.

The writer uses the native SNSS v3 file that the archive identifies as the source of
its verified portable window and tab projection. It replaces the destination file
with the greatest canonical numeric Session suffix, using the destination filename
and supported file metadata. The app never selects an older file when the newest is
unsupported. Review binds the archive digest, exact target, and original contents and
metadata. Forward publication and resume recheck the destination file selection and
Brave process state. Rollback can use the original encrypted image after the source
archive is unavailable, provided the target and browser state allow recovery.

One mapped profile's selected Session file is one transaction target. Other
`Session_*` files, `Tabs_*` closed-tab data, legacy session files, and startup
preferences remain outside the write plan. Brave controls which windows reopen at
startup, and it may update or remove old files when it next runs. This restores the
last persisted snapshot. The app keeps archived tab URLs, titles and page state
out of review and reports.

Portable-only archives can be inspected. Restoring them would require reconstructing
native commands from a projection that deliberately omits data. The app also blocks
restore when the destination contains encrypted session-family files, the latest
plaintext snapshot is missing or unsupported, or file names or paths are unsafe.
Each native source and destination file is limited to 64 MiB for restore. Existing
archive capture limits and format versions are unchanged. Windows, encrypted
native files and creating a missing Session file remain unsupported.

## Research and validation

Chromium orders modern Session candidates by the timestamp encoded in their names,
then checks the initial-state marker. Chromium can prefer the encrypted store when
both representations coexist. The app requires the newest existing plaintext file
to pass full replay and rejects encrypted coexistence.
[Chromium session backend](https://chromium.googlesource.com/chromium/src/+/HEAD/components/sessions/core/command_storage_backend.cc#788),
[Chromium encrypted-session selection](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/components/sessions/core/command_storage_manager.cc#119),
[pinned backup research and fixtures](sessions-research.md).

The integration tests use the independent SNSS v3 fixtures in
`tests/Core.Tests/Fixtures/Sessions`. They check exact reviewed paths and bytes,
portable-only, invalid and encrypted storage gates, changed and newer destination
files, pause and explicit resume, and rollback without the source archive. The
Avalonia smoke test exercises Recheck, Review and Confirm against a synthetic
profile.

A separate probe used two disposable profiles in Brave 1.96.61 (Chromium 154).
Both wrote plaintext SNSS v3 with their default session configuration. The probe
created an encrypted archive from the source, applied the native restore, and
verified exact rollback of the destination. It reapplied the restore and launched
the disposable destination with Brave's last-session startup option. Brave reopened
the archived synthetic tab. The application itself does not start Brave or change
startup preferences during restore.
