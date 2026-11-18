# Linux bookmarks restore backend

The internal planner authenticates encrypted archives and validates the archive contract
before reading native bookmark snapshots. The caller supplies the reviewed archive ID
and explicit archived-profile-to-destination mappings. The planner checks the digest of
the opened source before and after reading snapshots, verifies each selected payload
against its archive descriptor, and requires the transaction to freeze the same source
bytes. Final confirmation in the app must also bind the reviewed source to its subsequent
execution. An archive ID alone is insufficient to authenticate a rewritten plain archive.

The first supported shape uses the native `Bookmarks` file with version 1 and the
`bookmark_bar`, `other` and `synced` roots. Chromium uses the historical `synced`
name for the mobile root and reads separate top-level `sync_metadata`. The planner
rejects duplicate JSON property names and nonempty embedded Sync metadata in both
the source snapshot and an existing destination file. It also requires matching
recorded Brave product and version. These restrictions are compatibility gates and
still need verification against a running Brave installation on a synthetic profile.
[Chromium bookmark codec at c3817b2](https://chromium.googlesource.com/chromium/src.git/%2B/c3817b26b6bfc0d447d408f4402afda1ff5c3fd0/components/bookmarks/browser/bookmark_codec.cc)

The session installs the exact native bytes in the mapped profile's `Bookmarks` file.
Existing files retain supported Linux metadata. When the file is absent, the planner
requires an eligible `Preferences` file in that profile and uses its metadata as a
template. The transaction keeps the actual original absence for rollback. The per-file
limit is 64 MiB and the session bounds aggregate bytes. Other profile files are outside
the mutation plan. The existing encrypted safety, journaling and browser-state checks
govern every write.

Linux integration tests build actual plain and encrypted archives from synthetic bookmark
data. They verify exact bytes, restoration into an existing or absent file, rollback,
preservation of Preferences, and rejection of wrong archive IDs, incompatible versions,
running Brave, Sync metadata and duplicate keys. The feature is an internal backend
while the approved final confirmation and lifecycle controls remain pending. Native
Windows support is outside this checkpoint.
