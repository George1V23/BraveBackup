# Bookmarks restore review

Select archived profiles, map each to an existing profile, and select Bookmarks for
each mapping. Bookmarks is selectable on Linux when the inspected archive and target
have the same recorded Brave product and browser version. Other categories remain
available for inspection.

With a recovery folder and matching safety passwords, **Review restore** displays the
source archive, each source and destination profile, the exact Bookmarks file to replace,
and the planned encrypted safety backup and journal paths. It keeps the archive digest
from inspection and an independent copy of the mappings. Review does not create files
or retain either password in the review record. Cancelling or changing the mapping,
source or safety settings discards the review.

The backend accepts a reviewed digest and operation ID. It rejects a source with changed
bytes even if its archive ID still matches. After opening a session, the safety and journal
paths carry the operation ID shown in the review. The UI checkpoint is read-only. Executing
the confirmed operation and retaining a paused session for resume or rollback are pending.

Tests cover the exact reviewed paths and digest, mismatching safety passwords, write-free
review, invalidated reviews, and backend rejection of a mismatched digest.
