# Persisted session format research and independent fixtures

Inspected 2026-11-17 for the [approved Sessions scope](sessions-backup.md). No personal
session data, memory or machine key was inspected. Native formats below come from pinned
primary source; the portable JSON/inventory are separately documented application formats.

## Pinned sources

Chromium revision `c0837d415025e31505c769f6ef13ba0196feaea2`, resolved through the official
GitHub API. Pinned raw files were downloaded and inspected:

- [command_storage_backend.cc](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/components/sessions/core/command_storage_backend.cc):
  signature/header, v3 marker handling, v5 OS-backed encryption and numeric file ordering.
- [session_command.cc](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/components/sessions/core/session_command.cc):
  unencrypted records are native-endian uint16 length, uint8 ID and payload. The implemented
  slice explicitly supports little-endian sources; encrypted framing is not guessed.
- [session_service_commands.cc](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/components/sessions/core/session_service_commands.cc):
  native structs, command identities and replay/selection/pruning/group semantics.
- [base_session_service_commands.cc](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/components/sessions/core/base_session_service_commands.cc)
  and [serialized_navigation_entry.cc](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/components/sessions/core/serialized_navigation_entry.cc):
  tab/navigation Pickle layout and bounded optional historical metadata. Page state/history
  are not included in the portable projection.
- [session_types.cc](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/components/sessions/core/session_types.cc)
  and [session_id.h](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/components/sessions/core/session_id.h):
  native initialization/positive ID rules. Missing pin is false; current navigation starts
  at -1 and source replay selects the closest persisted entry. Missing required topology
  is blocked rather than invented.
- [base/pickle.cc](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/base/pickle.cc)
  and [base/token.cc](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/base/token.cc):
  4-byte alignment, string encodings and high/low token ordering were checked directly.
- [command_storage_manager.cc](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/components/sessions/core/command_storage_manager.cc)
  and [command_storage_features.h](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/components/sessions/core/command_storage_features.h):
  delayed persistence and migration-stage-dependent encrypted/plaintext behavior. Since
  the app does not know the browser's runtime stage, relevant encrypted coexistence blocks
  capture conservatively; no stale cleartext fallback is implied.
- [session_constants.cc](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/components/sessions/core/session_constants.cc)
  and [tab_group_color.h](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/components/tab_groups/tab_group_color.h):
  separate stream families/directories and persisted group color IDs 0–8.

## Replay support matrix

| Records | Projection policy |
| --- | --- |
| 0, 2, 6–9, 12, 16–17, 20, 24–25, 27 | Replay window/tab/order/navigation/closure/selection/pin/group state. |
| 5, 11 | Source-defined historical navigation pruning. |
| 10, 13–15, 18–19, 21, 23, 28–35 | Validate known layouts; ignore non-projection bounds/app/UA/storage/GUID/extra metadata. |
| 36–37 | Validate known split fields; any active split tab blocks projection. |
| 255 | Exactly one zero-content initial-state marker required. |
| Others | Reject; no partial replay success. |

Native fixed payload support is the tested 64-bit layout: closure/last-active structs 16
bytes, group/split structs 32 bytes (token at offset 8, Boolean at 24), pin/visibility
structs 8 bytes, ID/index pairs 8 bytes and bounds 24 bytes. Different native layouts are
not inferred from a filename or blindly accepted. The decoder handles source-supported
optional group/navigation fields at complete boundaries, with explicit null metadata
when none was stored. Unknown colors, malformed strings and partial fields fail.

Version 1/2/4/5/unknown framing, big-endian/untested ABI layouts, missing navigation for
associated tabs and missing required window/index data are not portable-capture support.
This can reject a native file for which Chromium would attempt a partial/best-effort
restore; that stricter behavior is deliberate under the no-silent-omission requirement.

## Fixture provenance

`tests/Core.Tests/Fixtures/Sessions/generate-v3.cpp` is an independent synthetic encoder
using the inspected layouts and static ABI-size assertions. It links no Chromium library
and calls no C# decoder. It produces the frozen SNSS-v3 framing/replay fixture, complete
empty snapshot and malformed/unsupported variants. Synthetic page-state/referrer markers
exercise exclusion, not browser page-state restoration. Small numeric suffixes test source
ordering; they are not claimed to be real browser flush timestamps.

Regenerate on the documented 64-bit little-endian host:

```sh
g++ -std=c++17 -O2 tests/Core.Tests/Fixtures/Sessions/generate-v3.cpp -o /tmp/generate-session-fixtures
/tmp/generate-session-fixtures tests/Core.Tests/Fixtures/Sessions
python3 tests/Core.Tests/Fixtures/Sessions/generate-archive-v4.py
```

The Python generator explicitly writes expected portable windows/tabs/groups and wraps
them with the C++ source into a deterministic archive-v4 ZIP. It does not derive expected
values from the production decoder. Adversarial tests rehash altered portable data and
require native replay consistency. Normal .NET tests use the frozen fixtures and need
neither compiler nor Python at runtime.

These fixtures prove framing/projection and application archive behavior, not successful
native browser restoration or all Brave-specific/version-specific session formats. Existing
arbitrary-byte GetSessionInfo tests remain inventory tests. Native/OS-encrypted restore,
live browser integration and more source versions require separate sources, fixtures,
platform validation and review. No extra package or key/decryption mechanism was introduced.
