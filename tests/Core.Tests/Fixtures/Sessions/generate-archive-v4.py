"""Independent application-owned archive-v4 fixture using the C++-generated native source.
Expected portable values are written explicitly, not obtained from the C# decoder.
"""
from pathlib import Path
import hashlib,json,zipfile
root=Path(__file__).parent
native=(root/'Session_10').read_bytes()
installation='11111111-1111-4111-8111-111111111111';profile='22222222-2222-4222-8222-222222222222'
prefix=f'installations/{installation}/profiles/{profile}/sessions/'
digest=hashlib.sha256(native).hexdigest()
group='112233445566778899aabbccddeeff00'
portable=dict(schemaVersion=1,captureKind='last-persisted',capturedUtc='2026-11-17T00:00:00+00:00',
 source=dict(path='Sessions/Session_10',formatVersion=3,length=len(native),sha256=digest),includeNativeFiles=True,activeWindowId=1,
 windows=[dict(id=1,type=0,selectedTabIndex=0,tabs=[dict(id=11,visualIndex=0,url='https://selected.example.test/',title='Selected ☃',pinned=True,groupId=group)],
 groups=[dict(id=group,title='Group Îñ',color=6,collapsed=True)]),
 dict(id=2,type=1,selectedTabIndex=1,tabs=[dict(id=21,visualIndex=0,url='https://popup.example.test/',title='Popup',pinned=False,groupId=None),
 dict(id=12,visualIndex=1,url='https://second.example.test/',title='Second',pinned=False,groupId=None)],groups=[])])
native_path=prefix+'native/Sessions/Session_10'
inventory=dict(schemaVersion=1,absentLegacyFiles=['Current Session','Current Tabs','Last Session','Last Tabs'],
 files=[dict(relativePath='Sessions/Session_10',archivePath=native_path,length=len(native),sha256=digest)])
encode=lambda x:json.dumps(x,ensure_ascii=False,indent=2).encode()
payloads=[(prefix+'portable-v1.json','portable-session',encode(portable)),(native_path,'native-session-file',native),
 (prefix+'native-inventory-v1.json','native-session-inventory',encode(inventory))]
files=[dict(path=path,role=role,length=len(data),sha256=hashlib.sha256(data).hexdigest()) for path,role,data in payloads]
manifest=dict(format='BraveBackupArchive',formatVersion=4,archiveId='33333333-3333-4333-8333-333333333333',createdUtc='2026-11-17T00:00:00+00:00',
 applicationVersion='fixture',requiredCapabilities=['sessions-v1'],compression='balanced',installations=[dict(id=installation,product='Browser',channel='Stable',browserVersion='fixture',
 profiles=[dict(id=profile,displayName='Fixture',directoryName='Default',categories=[dict(id='sessions',version=1,files=files)])])])
with zipfile.ZipFile(root/'Archive.v4.sessions.bravebackup','w') as archive:
 for path,_,data in payloads+[('manifest.json','',encode(manifest))]:
  entry=zipfile.ZipInfo(path,date_time=(2026,11,17,0,0,0));entry.compress_type=zipfile.ZIP_DEFLATED;archive.writestr(entry,data)
print('Generated independent session archive-v4 fixture.')
