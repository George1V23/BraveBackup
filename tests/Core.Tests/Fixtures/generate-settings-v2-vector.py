"""Generate a synthetic archive-v2 settings fixture using Python, independently of C#.
No personal profile data or third-party Python package is used.
"""
from pathlib import Path
import hashlib
import json
import zipfile
root = Path(__file__).parent
source = json.loads((root / 'Preferences.settings.json').read_text())
keys = ['bookmark_bar.show_on_all_tabs', 'bookmark_bar.visibility_state',
        'browser.show_home_button', 'homepage_is_newtabpage', 'homepage',
        'session.restore_on_startup', 'session.startup_urls']
values = {}
for key in keys:
    value = source
    for part in key.split('.'):
        value = value[part]
    values[key] = value
payload = json.dumps(dict(schemaVersion=1, values=values, absentKeys=[]), ensure_ascii=False, indent=2).encode()
installation = '11111111-1111-4111-8111-111111111111'
profile = '22222222-2222-4222-8222-222222222222'
path = f'installations/{installation}/profiles/{profile}/settings/allowlisted-v1.json'
manifest = dict(format='BraveBackupArchive', formatVersion=2,
    archiveId='33333333-3333-4333-8333-333333333333', createdUtc='2026-11-16T00:00:00+00:00',
    applicationVersion='fixture', requiredCapabilities=['settings-v1'], compression='balanced',
    installations=[dict(id=installation, product='Browser', channel='Stable', browserVersion='fixture',
        profiles=[dict(id=profile, displayName='Fixture', directoryName='Default',
            categories=[dict(id='settings', version=1, files=[dict(path=path, role='portable-settings',
                length=len(payload), sha256=hashlib.sha256(payload).hexdigest())])])])])
with zipfile.ZipFile(root / 'Archive.v2.settings.bravebackup', 'w') as archive:
    for name, data in [(path, payload), ('manifest.json', json.dumps(manifest, indent=2).encode())]:
        entry = zipfile.ZipInfo(name, date_time=(2026, 11, 16, 0, 0, 0))
        entry.compress_type = zipfile.ZIP_DEFLATED
        archive.writestr(entry, data)
print('Generated independent archive-v2 Settings fixture.')
