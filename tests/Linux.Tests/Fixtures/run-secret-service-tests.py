#!/usr/bin/env python3
"""Run native tests on a private D-Bus with a synthetic, lookup-only Secret Service.
Invoke with dbus-run-session -- python3 tests/Linux.Tests/Fixtures/run-secret-service-tests.py.
Requires Python dbus/gi and libsecret runtime; never runs against the desktop bus.
"""
import os
import subprocess
import threading
from pathlib import Path
import dbus
import dbus.service
from dbus.mainloop.glib import DBusGMainLoop
from gi.repository import GLib

if Path(os.readlink(f'/proc/{os.getppid()}/exe')).name != 'dbus-run-session':
    raise SystemExit('Must be launched by dbus-run-session on a private bus.')
DBusGMainLoop(set_as_default=True)
bus = dbus.SessionBus()
name = dbus.service.BusName('org.freedesktop.secrets', bus, do_not_queue=True)
SERVICE = 'org.freedesktop.Secret.Service'
COLLECTION = 'org.freedesktop.Secret.Collection'
ITEM = 'org.freedesktop.Secret.Item'
SESSION = 'org.freedesktop.Secret.Session'
PROPS = 'org.freedesktop.DBus.Properties'
ROOT = '/org/freedesktop/secrets'
COL = ROOT + '/collection/synthetic'
SESS = ROOT + '/session/synthetic'
APPS = ['brave', 'empty', 'binary', 'ambiguous', 'locked', 'oversized']

class Properties(dbus.service.Object):
    @dbus.service.method(PROPS, in_signature='s', out_signature='a{sv}')
    def GetAll(self, interface):
        return self.properties(interface)

    @dbus.service.method(PROPS, in_signature='ss', out_signature='v')
    def Get(self, interface, key):
        return self.properties(interface)[key]

class Item(Properties):
    def __init__(self, app, suffix=''):
        self.app = app
        self.path = COL + '/' + app + suffix
        super().__init__(bus, self.path)

    def properties(self, interface):
        assert interface == ITEM
        return {'Locked': dbus.Boolean(self.app == 'locked'),
                'Attributes': dbus.Dictionary({'application': self.app}, signature='ss'),
                'Label': 'Synthetic test item', 'Created': dbus.UInt64(1), 'Modified': dbus.UInt64(1)}

class Collection(Properties):
    def properties(self, interface):
        assert interface == COLLECTION
        return {'Locked': dbus.Boolean(False), 'Items': dbus.Array([i.path for i in items], signature='o'),
                'Label': 'Synthetic test collection', 'Created': dbus.UInt64(1), 'Modified': dbus.UInt64(1)}

    @dbus.service.method(COLLECTION, in_signature='a{ss}', out_signature='ao', async_callbacks=('respond', 'error'))
    def SearchItems(self, attributes, respond, error):
        assert set(attributes) == {'application'}
        result = dbus.Array([i.path for i in items if i.app == attributes['application']], signature='o')
        if attributes['application'] == 'slow':
            GLib.timeout_add(2000, lambda: (respond(result), False)[1])
        else:
            respond(result)

class Session(dbus.service.Object):
    @dbus.service.method(SESSION, in_signature='', out_signature='')
    def Close(self):
        pass

class Service(Properties):
    def properties(self, interface):
        assert interface == SERVICE
        return {'Collections': dbus.Array([COL], signature='o')}

    @dbus.service.method(SERVICE, in_signature='sv', out_signature='vo')
    def OpenSession(self, algorithm, value):
        if algorithm != 'plain':
            raise dbus.exceptions.DBusException('Synthetic fixture supports plain only', name='org.freedesktop.DBus.Error.NotSupported')
        return dbus.String('', variant_level=1), dbus.ObjectPath(SESS)

    @dbus.service.method(SERVICE, in_signature='s', out_signature='o')
    def ReadAlias(self, alias):
        assert alias == 'default'
        return dbus.ObjectPath(COL)

    @dbus.service.method(SERVICE, in_signature='ao', out_signature='aoo')
    def Unlock(self, paths):
        return dbus.Array([p for p in paths if not str(p).endswith('/locked')], signature='o'), dbus.ObjectPath('/')

    @dbus.service.method(SERVICE, in_signature='aoo', out_signature='a{o(oayays)}')
    def GetSecrets(self, paths, session):
        assert session == SESS
        result = dbus.Dictionary(signature='o(oayays)')
        for path in paths:
            item = next(i for i in items if i.path == path)
            assert item.app != 'locked'
            value = {'empty': b'', 'binary': b'\x00\xff\x01\x00', 'oversized': b'x' * 65537}.get(item.app, b'synthetic-keyring-secret-not-real')
            result[path] = dbus.Struct((dbus.ObjectPath(SESS), dbus.ByteArray(b''), dbus.ByteArray(value), 'text/plain'))
        return result

items = [Item(app) for app in APPS] + [Item('ambiguous', '2')]
collection = Collection(bus, COL)
service = Service(bus, ROOT)
session = Session(bus, SESS)
loop = GLib.MainLoop()
status = [1]
def run():
    env = dict(os.environ, BRAVEBACKUP_SYNTHETIC_SECRET_BUS='1')
    root = Path(__file__).resolve().parents[3]
    try:
        status[0] = subprocess.run(['dotnet', 'test', 'tests/Linux.Tests/Linux.Tests.csproj', '--no-restore',
                                    '--filter', 'FullyQualifiedName~LinuxSecretServiceTests', '--verbosity', 'minimal'],
                                   cwd=root, env=env, timeout=90).returncode
    finally:
        GLib.idle_add(loop.quit)
threading.Thread(target=run, daemon=True).start()
loop.run()
raise SystemExit(status[0])
