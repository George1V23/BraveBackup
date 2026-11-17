# Source-write-free affiliation cache capture

`BraveAffiliationSnapshot.Read(profile, observeBrowserState, token)` captures the pinned
profile cache for an explicit future CSV operation. It adds no discovery/startup/UI caller
and performs no password decryption, keyring access, network call, archive change or save.

The pinned [filename constant](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/affiliations/core/browser/affiliation_constants.h)
is `Affiliation Database`; the
[factory](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/chrome/browser/affiliations/affiliation_service_factory.cc)
places it under the selected profile directory. Read only that file and its `-wal`,
`-shm`, `-journal` companions. Unknown siblings, cookies, Local State and OS key stores
are excluded. A missing main file is an error, including orphan companions; the API does
not assume a missing file is an initialized empty browser cache.

## Capture and validation

Require Brave observed stopped throughout capture and again before later publication.
A running/unknown process result or failed observation requests close/retry. This is the
initial safe path while source-write-free live SQLite capture remains unproven. User
cancellation propagates normally, including when a source verification is requested later.

Every ancestor must be a safe directory, and known source entries must be regular files
without symbolic links/reparse points, FIFOs, devices or sockets. Reuse Platform's raw
read-only opening; no source filename is passed to SQLite. Check lengths/timestamps,
file sets and source SHA-256 before accepting the capture. Same-length/same-timestamp
changes still fail. These are conservative snapshot checks, not a guarantee against
hostile concurrent directory replacement.

Copy available known files into owned memory. Reject a nonempty rollback journal without
attempting recovery or changing the source. An existing zero-length journal participates
in the source set and remains subject to change checks. Assemble the main database plus
committed WAL frames with Core's tested `SqliteSnapshotImage`, then pass only that image
to the [schema-v7 cache reader](passwords-affiliation-cache.md). The source SHM file is
never mapped or modified by SQLite. Clear the assembled image on success or failure.

Bounds: 32 MiB per source file, 64 MiB across the known source set, and the reader's
32 MiB assembled-image limit plus its row/text/SQL bounds. Oversized or corrupt input
fails before a grouping result is returned. All copied source arrays are cleared on
failure/cancellation and disposal. `Cache` returns immutable metadata, `CapturedUtc`
records the successful capture, and `VerifyUnchanged` rechecks the entire source and
process state before future CSV publication. Disposed access fails. Borrowed metadata
strings cannot be reliably erased; never log site associations. `ToString` is redacted.

## Fixtures and validation

The affiliation fixture generator now creates a private synthetic WAL-mode database,
commits an update to group metadata without checkpointing it, and freezes main/WAL/SHM
files under `tests/Core.Tests/Fixtures/Affiliations/Wal`. The connection remains open only
during fixture generation; it never points at a real profile. WAL salts and SHM bytes
are runtime-generated, not fixed format claims. The upstream schema/license provenance
is unchanged.

Seventeen tests cover ordinary and WAL capture, visible committed WAL updates, byte/hash
preservation of source main/WAL/SHM, missing/orphan data, stopped/unknown/failed process
observations, late starts, same-stamp content/set changes, in-capture mutation, cancellation,
journals, unsafe source/profile paths, limits, disposed access and safe diagnostics.
No real browser profile, database or keyring was inspected. Actual Windows filesystem
behavior still needs native execution.

## Remaining integration

This provides raw cached groups only. Canonical facet construction, public-suffix/private
registry matching and cached PSL-extension related-group merging are still required
before these data can supply the CSV projector's verified-group resolver. Both password
and affiliation captures must remain valid through the eventual CSV save. Full CSV UI,
CSV archive contracts/consent and real native authentication checks remain pending.
Existing password backup/archive behavior is unchanged.
