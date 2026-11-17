#!/usr/bin/env python3
"""Independent specification fixture; all values are synthetic, never browser credentials."""
from pathlib import Path
import struct

def i(value): return struct.pack('<i', value)
def q(value): return struct.pack('<q', value)
def blob(value): return i(len(value)) + value
def text(value): return i(-1) if value is None else blob(value.encode('utf-8'))

payload = b'BBKPW001' + i(2)
payload += i(0) + i(1) + i(2) + i(1)
payload += q(7) + text('https://synthetic.invalid/') + text('https://synthetic.invalid/')
payload += text(None) + text('') + blob('synthetic, "secret"\n☃\0'.encode()) + i(2)
payload += text('first') + q(123456789) + i(1) + blob(b'synthetic note\r\nline two')
payload += text('second') + q(0) + i(0) + blob(b'')
payload += i(1) + i(0) + i(0) + i(0)
Path(__file__).with_name('Payload.v1.synthetic.bin').write_bytes(payload)
