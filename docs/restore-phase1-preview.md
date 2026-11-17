# Restore phase 1: read-only archive inspection

The reviewed source selector, encrypted-archive unlock and contents preview are implemented.
This phase has no destination writer, restore action, safety-backup write or rollback.
It does not access live profile credentials or the OS keyring.

## Backend

`BraveArchiveInspection.Read` opens an existing regular `.bravebackup` through Platform's
safe reader, rejecting final symlinks, special files and oversized inputs. An encrypted
source without a supplied password returns a locked result without an inventory. Unlocked
sources use the existing Core/Brave validators for versions 1–8, including authentication,
structure, category contracts and hashes. Source hashes before/after validation and a final
reopened-path comparison reject observed changes/replacements. This does not authorize a
later restore: execution must validate its source again.

No extraction or file creation occurs. Validation-owned decryption streams are disposed
before returning metadata; no decrypted archive/key is retained. Large archives require
full validation plus hash passes, performed on a worker with cancellation checks.

## UI and ownership

`RestorePreviewViewModel` owns each request and cancellation source. File selection or
entering Restore detects encryption and validates unencrypted contents. Encrypted sources
show a masked password field and **Unlock backup**. Typing does not read the archive;
unlocking is explicit. The source field/native Browse callback has a resolved full-path
display. No source profile is mapped to a destination.

The preview shows creation time, app/browser versions, archive format/protection,
installations, profiles and categories. Password representations distinguish portable,
raw and CSV with relevant limitations, without credential payload values. Inclusion does
not imply tested restore compatibility. Errors do not echo exceptions/native details.

Switching files cancels and invalidates old work. Leaving Restore, losing the active
installation or disposing the shell clears password/preview state and invalidates pending
results. Returning reinspects and requires encrypted sources to be unlocked again. Late
picker results are ignored. Password form text clears when unlocking starts; managed
password strings cannot be guaranteed physically erased, but are never logged/persisted.
Each worker disposes its own cancellation source, including superseded requests.

## Validation

The solution build passed with zero warnings/errors. The full regression run passed
1,024 tests with six environment-dependent tests skipped, including the initial 15 Restore
cases; two additional pending-reader lifecycle cases were then added and checked separately.
Coverage includes existing independent v1–v5 fixtures, synthetic v6–v8 archives, wrong
passwords, corrupt sources, representation descriptions, switching files, stale picker
results, tab departure and disposal. No real credentials were used.

The Linux native smoke runner passed all 36 checks, including explicit unlock, masked
input, rendered inventory/representation text and clearing on tab departure. Synthetic
profile bytes remained unchanged. See [preview screenshot](screenshots/restore-phase1-preview.png).
The native open picker is wired but was not manually driven; Windows desktop execution
remains untested. No packages or archive-format versions changed.

Phase 2 adds source choices and destination mapping. Restoration stays unavailable until
the safety engine and category writers are implemented and tested.
