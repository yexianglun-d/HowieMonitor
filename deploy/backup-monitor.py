#!/usr/bin/env python3
"""本项目的一致性备份及隔离恢复校验；兼容部署机 Python 3.6。"""
import argparse
import glob
import hashlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import tarfile
import tempfile
import time
import urllib.request

PRODUCT = 'HowieMonitor-93746211-9989-4cef-9d88-07870b3f35dd'
BASE = '/opt/howie-monitor'
DATA = '/var/lib/howie-monitor'
ROOT = BASE + '/backups'
TABLES = ('device', 'latest', 'heartbeat', 'history_samples', 'history_metric_buckets', 'enrollment_active', 'enrollment_pending')


def digest(path):
    value = hashlib.sha256()
    with open(path, 'rb') as source:
        for block in iter(lambda: source.read(1024 * 1024), b''): value.update(block)
    return value.hexdigest()


def files(directory, with_hash=True):
    result = {}
    for folder, directories, names in os.walk(directory, followlinks=False):
        for name in directories + names:
            path = os.path.join(folder, name)
            if os.path.islink(path): raise ValueError('Backup source contains a link')
        for name in names:
            path = os.path.join(folder, name)
            if not os.path.isfile(path): raise ValueError('Backup source contains a special file')
            value = {'bytes': os.path.getsize(path)}
            if with_hash: value['sha256'] = digest(path)
            result[os.path.relpath(path, directory).replace(os.sep, '/')] = value
    return result


def database_report(directory):
    database = os.path.join(directory, 'monitor.db')
    connection = sqlite3.connect(Path(database).resolve().as_uri() + '?mode=ro', uri=True)
    try:
        if connection.execute('PRAGMA integrity_check').fetchone()[0] != 'ok': raise ValueError('Database integrity check failed')
        result = {'tables': {table: connection.execute('SELECT count(*) FROM ' + table).fetchone()[0] for table in TABLES}}
        result['activeDevice'] = connection.execute('SELECT device FROM enrollment_active WHERE id=1').fetchone()[0]
        available = {row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        multi = ('computer_latest', 'computer_heartbeat', 'enrollment_devices')
        if any(table in available for table in multi):
            if not all(table in available for table in multi): raise ValueError('Incomplete multi-computer schema')
            for table in multi: result['tables'][table] = connection.execute('SELECT count(*) FROM ' + table).fetchone()[0]
            rows = connection.execute('SELECT payload FROM computer_latest ORDER BY device').fetchall()
            result['computerLatest'] = [{key: json.loads(row[0])[key] for key in ('deviceId', 'sessionId', 'sequence', 'sampledAt')} for row in rows]
            result['latest'] = next((item for item in result['computerLatest'] if item['deviceId'] == result['activeDevice']), None)
        else:
            row = connection.execute('SELECT payload FROM latest WHERE id=1').fetchone()
            result['latest'] = None if row is None else {key: json.loads(row[0])[key] for key in ('deviceId', 'sessionId', 'sequence', 'sampledAt')}
        return result
    finally: connection.close()


def pack(snapshot, archive, context):
    # WAL 模式的只读连接仍可能创建空日志/共享内存文件；先校验，再固定完整文件清单。
    report = database_report(snapshot)
    inventory = files(snapshot)
    required = ('monitor.db', 'user.json', 'device-credential.json', 'notifications.json')
    if any(name not in inventory for name in required) or not any(name.startswith('keys/') and name.endswith('.xml') for name in inventory):
        raise ValueError('Private configuration or protection keys are missing')
    manifest = {'product': PRODUCT, 'version': 1, 'createdAtUtc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
                'context': context, 'files': inventory, 'database': report}
    partial = archive + '.partial'
    if os.path.lexists(archive): raise ValueError('Backup already exists')
    with open(partial, 'xb') as output:
        os.chmod(partial, 0o600)
        with tarfile.open(fileobj=output, mode='w:gz', compresslevel=1) as bundle:
            bundle.add(snapshot, arcname='data', recursive=True)
            encoded = json.dumps(manifest, sort_keys=True).encode('utf-8')
            entry = tarfile.TarInfo('manifest.json'); entry.mode = 0o600; entry.size = len(encoded)
            bundle.addfile(entry, io.BytesIO(encoded))
    os.rename(partial, archive)
    with open(archive + '.sha256', 'x') as target: target.write(digest(archive) + '\n')
    os.chmod(archive + '.sha256', 0o600)
    return manifest


def verify(archive, destination):
    if os.path.lexists(destination): raise ValueError('Restore destination already exists')
    if digest(archive) != Path(archive + '.sha256').read_text().strip(): raise ValueError('Backup checksum mismatch')
    with tarfile.open(archive, 'r:gz') as bundle:
        entries = bundle.getmembers(); names = set()
        for entry in entries:
            parts = entry.name.split('/')
            if entry.name in names or entry.name.startswith('/') or any(part in ('', '.', '..') for part in parts) or '\\' in entry.name or ':' in entry.name:
                raise ValueError('Invalid archive path')
            names.add(entry.name)
            if entry.name != 'manifest.json' and parts[0] != 'data': raise ValueError('Unexpected archive entry')
            if not (entry.isfile() or entry.isdir()): raise ValueError('Archive links or special files are not allowed')
        entry = bundle.getmember('manifest.json')
        if not entry.isfile() or entry.size > 4 * 1024 * 1024: raise ValueError('Invalid manifest')
        manifest = json.load(bundle.extractfile(entry))
        if manifest.get('product') != PRODUCT or manifest.get('version') != 1: raise ValueError('Backup product mismatch')
        declared = manifest['files']
        actual = {entry.name[5:]: entry.size for entry in entries if entry.isfile() and entry.name.startswith('data/')}
        if actual != {name: value['bytes'] for name, value in declared.items()}: raise ValueError('File inventory mismatch')
        os.mkdir(destination, 0o700)
        # 路径与文件类型已逐项校验，只写入本次新建的私有恢复目录，绝不覆盖生产目录。
        bundle.extractall(destination)
    restored = os.path.join(destination, 'data')
    if files(restored) != declared: raise ValueError('Restored file hashes differ')
    for name in ('user.json', 'device-credential.json', 'notifications.json'):
        with open(os.path.join(restored, name)) as source: json.load(source)
    if database_report(restored) != manifest['database']: raise ValueError('Restored database state differs')
    return {'product': PRODUCT, 'verifiedFiles': len(declared), 'database': manifest['database'], 'directory': destination}


def run(arguments):
    return subprocess.check_output(arguments, timeout=90).decode().strip()


def health():
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen('http://127.0.0.1:5080/health/live', timeout=2) as response:
                if json.loads(response.read().decode()).get('status') == 'ok': return
        except (OSError, ValueError): pass
        time.sleep(.5)
    raise RuntimeError('Backend did not recover after backup')


def backup():
    info = json.loads(run(['docker', 'inspect', 'howie-monitor']))[0]
    expected_mounts = {(BASE + '/current', '/app'), (DATA, '/data')}
    if info['Name'] != '/howie-monitor' or not info['State']['Running'] or info['Config']['User'] != '994:990' \
            or {(item['Source'], item['Destination']) for item in info['Mounts']} != expected_mounts:
        raise ValueError('Running container does not match this project')
    if not os.path.realpath(BASE + '/current').startswith(BASE + '/releases/') or os.path.realpath(DATA) != DATA:
        raise ValueError('Project paths do not match')
    size = sum(value['bytes'] for value in files(DATA, False).values())
    if shutil.disk_usage(ROOT).free < size * 3 + 100 * 1024 * 1024: raise ValueError('Insufficient backup space')
    stamp = time.strftime('%Y%m%d-%H%M%S', time.gmtime())
    archive = os.path.join(ROOT, 'monitor-' + stamp + '.tar.gz')
    staging = tempfile.mkdtemp(prefix='.snapshot-', dir=ROOT)
    snapshot = os.path.join(staging, 'data'); stop_attempted = False
    context = {'release': os.path.realpath(BASE + '/current'), 'image': info['Config']['Image'], 'container': info['Id'],
               'backupToolSha256': digest(__file__)}
    try:
        start = time.monotonic()
        try:
            stop_attempted = True
            run(['docker', 'stop', '--time', '20', 'howie-monitor'])
            shutil.copytree(DATA, snapshot)
        finally:
            if stop_attempted:
                # 重启同一个经核对的容器，不改变镜像、凭据或其他站点配置。
                if json.loads(run(['docker', 'inspect', 'howie-monitor']))[0]['Id'] != info['Id']:
                    raise ValueError('Container changed during backup; inspect before restarting')
                run(['docker', 'start', 'howie-monitor']); health()
        context['outageSeconds'] = round(time.monotonic() - start, 2)
        manifest = pack(snapshot, archive, context)
        result = verify(archive, os.path.join(ROOT, 'verify-' + stamp))
        result['archive'] = archive; result['outageSeconds'] = context['outageSeconds']
        result['archiveSha256'] = digest(archive)
        remove_private_copy(result['directory'], 'verify-')
        result['restoredCopyRemoved'] = True
        with open(archive + '.verified.json', 'x') as output: json.dump(result, output, indent=2)
        os.chmod(archive + '.verified.json', 0o600)
        prune_backups(7)
        return result
    finally:
        remove_private_copy(staging, '.snapshot-')


def remove_private_copy(directory, prefix):
    if os.path.dirname(directory) != ROOT or not os.path.basename(directory).startswith(prefix) or os.path.realpath(directory) != directory:
        raise ValueError('Private copy cleanup path is unsafe')
    files(directory, False)
    shutil.rmtree(directory)


def prune_backups(keep):
    records = sorted(glob.glob(os.path.join(ROOT, 'monitor-????????-??????.tar.gz.verified.json')))
    for record in records[:-keep]:
        with open(record) as source: report = json.load(source)
        archive = record[:-len('.verified.json')]
        paths = (record, archive, archive + '.sha256')
        if report.get('product') != PRODUCT or report.get('archive') != archive or not report.get('restoredCopyRemoved') \
                or any(os.path.realpath(path) != path or not os.path.isfile(path) for path in paths):
            raise ValueError('Old backup ownership cannot be verified')
        if digest(archive) != report.get('archiveSha256'): raise ValueError('Old backup content changed; kept for inspection')
        for path in paths: os.remove(path)


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('action', choices=('backup', 'verify'))
    parser.add_argument('--name'); args = parser.parse_args()
    if os.name != 'posix' or os.geteuid() != 0: raise ValueError('Run on the deployment server as root')
    if os.path.realpath(BASE) != BASE or os.path.islink(ROOT): raise ValueError('Backup root path is unsafe')
    os.makedirs(ROOT, mode=0o700, exist_ok=True); os.chmod(ROOT, 0o700); os.umask(0o077)
    import fcntl
    lock_path = os.path.join(BASE, 'maintenance.lock')
    if os.path.islink(lock_path): raise ValueError('Maintenance lock path is unsafe')
    with open(lock_path, 'a') as lock:
        os.chmod(lock_path, 0o600)
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if args.action == 'backup': result = backup()
        else:
            if not args.name or not re.match(r'^monitor-\d{8}-\d{6}\.tar\.gz$', args.name): raise ValueError('Select an exact backup file name')
            import uuid
            destination = os.path.join(ROOT, 'verify-' + uuid.uuid4().hex)
            result = verify(os.path.join(ROOT, args.name), destination)
        print(json.dumps(result, sort_keys=True))


if __name__ == '__main__': main()
