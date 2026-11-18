# Linux extension storage directory recovery

The Linux restore session treats an existing `Local Extension Settings/<id>`
directory as one transaction target. The [Extensions workflow](restore-extensions.md)
uses the adapter after verifying the archive and matching the registered package.
The final review lists each authorized target directory.

## Publication and rollback

The transaction captures the original directory and its regular files. It flushes
the encrypted safety snapshot and journal directory entries and authenticates
readback before publication. It records intent before starting a directory exchange.
The adapter writes the complete replacement under a private staging directory on
the destination filesystem, checks all bytes and supported metadata, and flushes
the staged contents. It rechecks the context and the observed directory states
before atomically exchanging the two directories.

Publication verifies the complete incoming and displaced images. The adapter
removes only verified files in its own staging workspace. A conflict can leave a
private `.restore-directory-*` staging directory beside the target for inspection.
Rollback uses the verified original image and the same whole-directory exchange.
It refuses an externally changed target. Original directory and file bytes,
ordinary permission modes and last-write times are verified on readback.

The Linux implementation uses directory-relative operations and
`renameat2(RENAME_EXCHANGE)` under an advisory lock. The target and staging parent
directories are flushed after the exchange. Unsupported filesystems fail the
operation. This provides atomic visibility of one directory, while a transaction
with multiple targets still progresses between targets. Process observations and
identity checks do not prevent changes by noncooperating same-user processes.
Automatic restart recovery remains pending.
[Linux rename manual](https://man7.org/linux/man-pages/man2/rename.2.html#DESCRIPTION),
[Linux fsync manual](https://man7.org/linux/man-pages/man2/fsync.2.html#DESCRIPTION).

## Directory image

The existing authenticated safety snapshot and archive versions are unchanged.
For a directory target, the image content is an app-owned `BBKLD001` frame.
Integers are signed 32-bit little-endian values. After the eight-byte magic and
file count, each file stores its UTF-8 name length, content length, name bytes,
28-byte `BBKLM002` Linux file metadata and content bytes. Entries are ordered by
ordinal filename. There are no nested paths or trailing bytes. The outer image
metadata uses `BBKDM001` with ordinary mode, UTC last-write ticks and effective
UID/GID in the same layout as file metadata.

Images are limited to 32 MiB including framing, 4,096 files and 255 UTF-8 bytes
per name. An existing empty directory is supported. Linked or nested entries,
other ownership, unsupported modes and extended metadata block the operation.
The adapter requires an existing directory and matching effective UID/GID. For
archive-based replacements, directory metadata comes from the reviewed destination.
Restored files use private mode `0600` and inherit the destination directory's
ownership and timestamp because the archive contains bytes without OS metadata.
The rollback image retains each original file's supported metadata. Installation,
Sync storage and creating a missing Local store remain pending.

## Verification

`LinuxRestoreDirectoryTransactionTests` covers mixed file and directory targets,
pause at a directory boundary, exact rollback with the source removed, original
empty state, browser-state guards, external changes, malformed images and retained
staging on conflict. Tests inspect the decrypted safety image of the original
directory using synthetic inputs.

A separate local probe opened two disposable Linux profiles under Brave 1.96.61
with the same synthetic extension, wrote distinct values through
`chrome.storage.local`, and closed both processes. The adapter exchanged and
rolled back their actual browser-generated stores. After a fresh apply, the
destination Brave profile reopened and returned the source value. This probe
exercised the internal writer; it did not use an Extensions archive or the
Restore tab to authorize the operation.
