import importlib.util
import io
import json
import os
from pathlib import Path
from contextlib import closing
import sqlite3
import shutil
import tarfile
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('backup_monitor', Path(__file__).resolve().parents[1] / 'deploy' / 'backup-monitor.py')
backup = importlib.util.module_from_spec(spec); spec.loader.exec_module(backup)


class BackupTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='howie-backup-tests-')
        # Windows 托管环境可能返回 8.3 临时目录名；测试须满足生产工具的规范路径前置条件。
        self.root = os.path.realpath(self.temporary.name)
        self.source = os.path.join(self.root, 'source'); os.mkdir(self.source)
        connection = sqlite3.connect(os.path.join(self.source, 'monitor.db'))
        for table in backup.TABLES:
            columns = 'id INTEGER,device TEXT,hash TEXT' if table == 'enrollment_active' else 'id INTEGER,payload TEXT'
            connection.execute('CREATE TABLE ' + table + '(' + columns + ')')
        connection.execute("INSERT INTO enrollment_active VALUES(1,'replacement','test-hash')")
        payload = {'deviceId': 'replacement', 'sessionId': 'explicit-test-session', 'sequence': 7, 'sampledAt': '2026-10-14T00:00:00Z'}
        connection.execute('INSERT INTO latest VALUES(1,?)', (json.dumps(payload),)); connection.commit(); connection.close()
        for name, value in [('user.json', {'passwordHash': 'test-only'}), ('device-credential.json', {'deviceId': 'legacy', 'token': 'test-only'}), ('notifications.json', {'enabled': False})]:
            Path(self.source, name).write_text(json.dumps(value))
        os.mkdir(os.path.join(self.source, 'keys')); Path(self.source, 'keys', 'test.xml').write_text('<key>test-only</key>')
        self.archive = os.path.join(self.root, 'backup.tar.gz'); self.destination = os.path.join(self.root, 'restored')

    def tearDown(self): self.temporary.cleanup()

    def test_full_restore_preserves_active_database_identity_and_every_private_file(self):
        manifest = backup.pack(self.source, self.archive, {'release': 'explicit-test-release'})
        result = backup.verify(self.archive, self.destination)
        self.assertEqual('replacement', result['database']['activeDevice'])
        self.assertEqual(7, result['database']['latest']['sequence'])
        self.assertEqual(manifest['files'], backup.files(os.path.join(self.destination, 'data')))

    def test_multi_computer_restore_verifies_each_live_snapshot_and_credential_table(self):
        with closing(sqlite3.connect(os.path.join(self.source, 'monitor.db'))) as connection:
            connection.execute('CREATE TABLE computer_latest(device TEXT PRIMARY KEY,payload TEXT NOT NULL)')
            connection.execute('CREATE TABLE computer_heartbeat(device TEXT PRIMARY KEY,payload TEXT NOT NULL)')
            connection.execute('CREATE TABLE enrollment_devices(device TEXT PRIMARY KEY,hash TEXT NOT NULL)')
            for device in ('replacement', 'second'):
                sample = {'deviceId': device, 'sessionId': 'same-session', 'sequence': 12, 'sampledAt': '2026-10-17T11:00:00Z'}
                connection.execute('INSERT INTO computer_latest VALUES(?,?)', (device, json.dumps(sample)))
                connection.execute('INSERT INTO computer_heartbeat VALUES(?,?)', (device, '{}'))
                connection.execute('INSERT INTO enrollment_devices VALUES(?,?)', (device, 'test-hash-' + device))
            connection.commit()
        backup.pack(self.source, self.archive, {})
        report = backup.verify(self.archive, self.destination)['database']
        self.assertEqual(12, report['latest']['sequence'])
        self.assertEqual({'replacement', 'second'}, {item['deviceId'] for item in report['computerLatest']})
        self.assertEqual(2, report['tables']['enrollment_devices'])

    def test_corrupt_archive_is_rejected_before_creating_restore_directory(self):
        backup.pack(self.source, self.archive, {})
        with open(self.archive, 'ab') as target: target.write(b'changed')
        with self.assertRaisesRegex(ValueError, 'checksum'): backup.verify(self.archive, self.destination)
        self.assertFalse(os.path.exists(self.destination))

    def test_wal_readonly_probe_files_are_included_in_manifest(self):
        connection = sqlite3.connect(os.path.join(self.source, 'monitor.db'))
        connection.execute('PRAGMA journal_mode=WAL'); connection.close()
        manifest = backup.pack(self.source, self.archive, {})
        self.assertEqual(backup.files(self.source), manifest['files'])
        result = backup.verify(self.archive, self.destination)
        self.assertEqual(manifest['database'], result['database'])

    def test_wal_without_sidecars_is_verified_without_changing_snapshot(self):
        database = os.path.join(self.source, 'monitor.db')
        with closing(sqlite3.connect(database)) as connection:
            connection.execute('PRAGMA journal_mode=WAL')
        self.assertFalse(os.path.exists(database + '-wal'))
        self.assertFalse(os.path.exists(database + '-shm'))
        before = backup.files(self.source)
        manifest = backup.pack(self.source, self.archive, {})
        self.assertEqual(before, backup.files(self.source))
        self.assertEqual(before, manifest['files'])
        result = backup.verify(self.archive, self.destination)
        self.assertEqual(manifest['database'], result['database'])
        self.assertEqual(before, backup.files(os.path.join(self.destination, 'data')))

    def test_committed_wal_data_survives_pack_and_restore_without_changing_snapshot(self):
        original = os.path.join(self.root, 'wal-source')
        shutil.copytree(self.source, original)
        with closing(sqlite3.connect(os.path.join(original, 'monitor.db'))) as connection:
            connection.execute('PRAGMA journal_mode=WAL')
            connection.execute('PRAGMA wal_autocheckpoint=0')
            payload = {'deviceId': 'replacement', 'sessionId': 'wal-session', 'sequence': 99, 'sampledAt': '2026-10-19T00:00:00Z'}
            connection.execute('UPDATE latest SET payload=? WHERE id=1', (json.dumps(payload),))
            connection.commit()
            for name in ('monitor.db', 'monitor.db-wal'):
                shutil.copyfile(os.path.join(original, name), os.path.join(self.source, name))
        self.assertGreater(os.path.getsize(os.path.join(self.source, 'monitor.db-wal')), 0)
        before = backup.files(self.source)
        manifest = backup.pack(self.source, self.archive, {})
        self.assertEqual(99, manifest['database']['latest']['sequence'])
        self.assertEqual(before, backup.files(self.source))
        self.assertEqual(before, manifest['files'])
        result = backup.verify(self.archive, self.destination)
        self.assertEqual(99, result['database']['latest']['sequence'])
        self.assertEqual(before, backup.files(os.path.join(self.destination, 'data')))

    def test_missing_database_is_not_created_by_the_probe(self):
        missing = os.path.join(self.root, 'missing')
        os.mkdir(missing)
        with self.assertRaises(FileNotFoundError): backup.database_report(missing)
        self.assertEqual([], os.listdir(missing))

    def rewritten_archive(self, extra):
        backup.pack(self.source, self.archive, {})
        with tarfile.open(self.archive, 'r:gz') as source:
            entries = [(entry, source.extractfile(entry).read() if entry.isfile() else None) for entry in source.getmembers()]
        with tarfile.open(self.archive, 'w:gz') as target:
            for entry, value in entries: target.addfile(entry, io.BytesIO(value) if value is not None else None)
            target.addfile(extra, io.BytesIO(b'x') if extra.isfile() else None)
        Path(self.archive + '.sha256').write_text(backup.digest(self.archive))

    def test_traversal_rejected_even_if_archive_checksum_is_replaced(self):
        entry = tarfile.TarInfo('data/../escape.txt'); entry.size = 1; self.rewritten_archive(entry)
        with self.assertRaisesRegex(ValueError, 'path'): backup.verify(self.archive, self.destination)
        self.assertFalse(os.path.exists(os.path.join(self.root, 'escape.txt')))

    def test_symlink_archive_rejected(self):
        entry = tarfile.TarInfo('data/unsafe'); entry.type = tarfile.SYMTYPE; entry.linkname = '/etc/passwd'; self.rewritten_archive(entry)
        with self.assertRaisesRegex(ValueError, 'links'): backup.verify(self.archive, self.destination)

    def test_missing_protection_keys_cannot_produce_complete_backup(self):
        os.remove(os.path.join(self.source, 'keys', 'test.xml'))
        with self.assertRaisesRegex(ValueError, 'keys'): backup.pack(self.source, self.archive, {})
        self.assertFalse(os.path.exists(self.archive))

    def test_existing_destination_is_never_overwritten(self):
        backup.pack(self.source, self.archive, {}); os.mkdir(self.destination)
        Path(self.destination, 'keep.txt').write_text('keep')
        with self.assertRaisesRegex(ValueError, 'already exists'): backup.verify(self.archive, self.destination)
        self.assertEqual('keep', Path(self.destination, 'keep.txt').read_text())

    def make_verified_backup(self, index):
        archive = os.path.join(self.root, 'monitor-20261014-0000{:02d}.tar.gz'.format(index))
        backup.pack(self.source, archive, {})
        destination = os.path.join(self.root, 'verify-{:02d}'.format(index))
        report = backup.verify(archive, destination)
        with patch.object(backup, 'ROOT', self.root): backup.remove_private_copy(destination, 'verify-')
        report.update(archive=archive, archiveSha256=backup.digest(archive), restoredCopyRemoved=True)
        Path(archive + '.verified.json').write_text(json.dumps(report))
        return archive

    def test_retention_keeps_seven_verified_archives(self):
        archives = [self.make_verified_backup(index) for index in range(8)]
        with patch.object(backup, 'ROOT', self.root): backup.prune_backups(7)
        self.assertFalse(os.path.exists(archives[0])); self.assertTrue(all(os.path.exists(path) for path in archives[1:]))

    def test_retention_cannot_delete_unrecognized_backup(self):
        archives = [self.make_verified_backup(index) for index in range(8)]
        record = Path(archives[0] + '.verified.json'); value = json.loads(record.read_text()); value['product'] = 'other-product'; record.write_text(json.dumps(value))
        with patch.object(backup, 'ROOT', self.root):
            with self.assertRaisesRegex(ValueError, 'ownership'): backup.prune_backups(7)
        self.assertTrue(all(os.path.exists(path) for path in archives))


if __name__ == '__main__': unittest.main()
