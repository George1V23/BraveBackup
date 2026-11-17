#!/usr/bin/env python3
"""Generate only synthetic upstream schema-v7 affiliation data; never opens a browser cache."""
from pathlib import Path
import sqlite3
root=Path(__file__).parent
output=root/'Affiliation.v7.synthetic.sqlite'
output.unlink(missing_ok=True)
with sqlite3.connect(output) as connection:
    connection.executescript((root/'affiliation_db_v7.sql').read_text())

# Freeze a committed WAL update while the synthetic generator connection stays open.
import shutil
import tempfile
with tempfile.TemporaryDirectory() as work:
    source=Path(work)/'Affiliation Database'
    shutil.copyfile(output,source)
    connection=sqlite3.connect(source)
    try:
        connection.execute('PRAGMA journal_mode=WAL')
        connection.execute('PRAGMA wal_autocheckpoint=0')
        connection.execute('PRAGMA wal_checkpoint(TRUNCATE)')
        connection.execute("UPDATE eq_class_groups SET main_domain='wal.synthetic.invalid' WHERE set_id=1")
        connection.commit()
        target=root/'Wal'
        target.mkdir(exist_ok=True)
        for suffix in ['', '-wal', '-shm']:
            shutil.copyfile(str(source)+suffix,target/('Affiliation Database'+suffix))
    finally:
        connection.close()
