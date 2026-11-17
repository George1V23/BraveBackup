#!/usr/bin/env python3
"""Generate only synthetic upstream schema-v7 affiliation data; never opens a browser cache."""
from pathlib import Path
import sqlite3
root=Path(__file__).parent
output=root/'Affiliation.v7.synthetic.sqlite'
output.unlink(missing_ok=True)
with sqlite3.connect(output) as connection:
    connection.executescript((root/'affiliation_db_v7.sql').read_text())
