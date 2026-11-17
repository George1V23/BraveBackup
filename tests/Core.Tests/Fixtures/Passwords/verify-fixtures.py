"""Verify frozen research fixtures with SQLite/Python, without loading application code.
Only private temporary copies of the WAL files are opened by SQLite.
"""
import csv
from contextlib import closing
import hashlib
import io
import json
from pathlib import Path
import shutil
import sqlite3
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent


class ResearchFixtures(unittest.TestCase):
    def test_frozen_hashes(self):
        for name, expected in json.loads((ROOT / "sha256.json").read_text()).items():
            self.assertEqual(hashlib.sha256((ROOT / name).read_bytes()).hexdigest(), expected, name)

    def test_source_defined_schema_and_encrypted_note_relationship(self):
        for platform, prefix in [("windows", b"v10"), ("linux", b"v11")]:
            with closing(sqlite3.connect((ROOT / f"LoginData.v43.{platform}.sqlite").as_uri() + "?mode=ro", uri=True)) as db:
                self.assertEqual(db.execute("PRAGMA integrity_check").fetchone()[0], "ok")
                self.assertEqual(db.execute("SELECT value FROM meta WHERE key='version'").fetchone()[0], "43")
                self.assertEqual(db.execute("SELECT count(*) FROM logins").fetchone()[0], 3)
                rows = db.execute("SELECT password_value FROM logins WHERE blacklisted_by_user=0 AND federation_url='' ").fetchall()
                self.assertEqual(len(rows), 1)
                self.assertTrue(rows[0][0].startswith(prefix))
                self.assertTrue(db.execute("SELECT value FROM password_notes WHERE parent_id=1").fetchone()[0].startswith(prefix))

    def test_empty_is_distinct_from_unsupported_and_corrupt(self):
        with closing(sqlite3.connect((ROOT / "LoginData.v43.empty.sqlite").as_uri() + "?mode=ro", uri=True)) as db:
            self.assertEqual(db.execute("SELECT count(*) FROM logins").fetchone()[0], 0)
        with closing(sqlite3.connect((ROOT / "LoginData.unsupported.sqlite").as_uri() + "?mode=ro", uri=True)) as db:
            self.assertEqual(db.execute("SELECT value FROM meta WHERE key='version'").fetchone()[0], "999")
        with closing(sqlite3.connect((ROOT / "LoginData.corrupt.bin").as_uri() + "?mode=ro", uri=True)) as db:
            with self.assertRaises(sqlite3.DatabaseError):
                db.execute("SELECT * FROM logins").fetchall()

    def test_csv_columns_encoding_quotes_and_newlines(self):
        expected = json.loads((ROOT / "Crypto.synthetic.json").read_text())
        for platform, ending in [("windows", b"\r\n"), ("linux", b"\n")]:
            data = (ROOT / f"Passwords.synthetic.{platform}.csv").read_bytes()
            self.assertTrue(data.endswith(ending))
            self.assertFalse(data.startswith(b"\xef\xbb\xbf"))
            rows = list(csv.reader(io.StringIO(data.decode(), newline="")))
            self.assertEqual(rows[0], ["name", "url", "username", "password", "note"])
            self.assertEqual(rows[1][3], expected["passwordUtf8"])
            self.assertEqual(rows[1][4], expected["noteUtf8"])
            self.assertEqual(len(rows), 2)

    def test_wal_companion_contains_newer_committed_value(self):
        with tempfile.TemporaryDirectory(prefix="verify-password-wal-") as folder:
            root = Path(folder)
            for source in (ROOT / "Wal").iterdir():
                shutil.copyfile(source, root / source.name)
            with closing(sqlite3.connect((root / "Login Data").as_uri() + "?mode=ro", uri=True)) as db:
                self.assertEqual(db.execute("SELECT username_value FROM logins WHERE id=1").fetchone()[0], "fixture-WAL-new")
            alone = root / "alone.sqlite"
            shutil.copyfile(ROOT / "Wal/Login Data", alone)
            with closing(sqlite3.connect(alone.as_uri() + "?mode=ro", uri=True)) as db:
                self.assertEqual(db.execute("SELECT username_value FROM logins WHERE id=1").fetchone()[0], "fixture-user")

    def test_recorded_live_experiment_distinguishes_snapshot_from_source_write_safety(self):
        result = json.loads((ROOT / "Wal.experiment.json").read_text())
        self.assertTrue(result["pinnedReadTransactionSawOldValue"])
        self.assertTrue(result["newSnapshotSawCommittedWalValue"])
        self.assertTrue(result["databaseBytesUnchangedByRead"])
        self.assertTrue(result["walBytesUnchangedByRead"])
        # This observation is evidence for the implementation gate, not an app feature.
        self.assertFalse(result["shmBytesUnchangedByRead"])


if __name__ == "__main__":
    unittest.main()
