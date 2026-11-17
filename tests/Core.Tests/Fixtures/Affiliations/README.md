# Synthetic affiliation-cache fixture

`affiliation_db_v7.sql` is the exact public Chromium test fixture at revision
`b8c2be851f62f6a394ec57955d7f31335d7cb6d4`, path
`components/test/data/password_manager/affiliation_db_v7.sql`.
Source: https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/test/data/password_manager/affiliation_db_v7.sql

All sample associations are upstream synthetic example/test data, not browser credentials.
`UPSTREAM-LICENSE.txt` contains Chromium's BSD license. The SQL is used as a format fixture,
not runtime implementation code. `Affiliation.v7.synthetic.sqlite` is generated locally
from this SQL with Python SQLite, without a browser/profile/keyring. Regenerate with:

```sh
python3 tests/Core.Tests/Fixtures/Affiliations/generate-fixture.py
```

Tests modify private disposable copies, never this fixture or a live database. This does
not prove native Brave runtime cache compatibility, freshness, PSL merging or source
snapshot consistency; those remain separate checks.

The generator also freezes a synthetic committed WAL update under `Wal/` while its private
SQLite connection is open. Reader tests require `main_domain=wal.synthetic.invalid` from
the committed WAL and compare source main/WAL/SHM hashes before/after capture. Runtime
WAL salts and SHM values can change on regeneration; none comes from a live profile.
