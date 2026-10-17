"""Export a reviewed source tree without private deployment state or documents."""
import argparse
import hashlib
import os
from pathlib import Path
import re
import shutil
import stat
import time

ROOT_FILES = ['.gitignore', 'Directory.Build.props', 'Howie.Monitor.slnx',
              'NuGet.Config', 'global.json', 'LICENSE', 'README.public.md',
              'PRIVACY.md', 'CODE_SIGNING.md', 'BUILDING.md']
SCRIPTS = ['publish-monitor.ps1', 'start-local.ps1', 'stop-local.ps1',
           'verify-signed-setup.ps1', 'prepare-public-source.py']
DIRECTORIES = ['src', 'tests', 'deploy', 'licenses', '.github/workflows']
EXCLUDED = {'bin', 'obj', 'node_modules', 'wwwroot', '__pycache__', '.git',
            '.local', 'reports', 'artifacts', 'output', 'TestResults'}
EXTENSIONS = {'.cs', '.csproj', '.json', '.manifest', '.html', '.ts', '.vue',
              '.css', '.py', '.template', '.txt', '.xml', '.yml', '.yaml'}
JSON_FILES = {'packages.lock.json', 'package.json', 'package-lock.json',
              'tsconfig.json', 'tsconfig.app.json', 'tsconfig.node.json',
              'agent.example.json'}
SECRET_PATTERNS = [
    re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----\s+[A-Za-z0-9+/=]{40}'),
    re.compile(r'\bgh[pousr]_[A-Za-z0-9]{30,}\b'),
    re.compile(r'\bgithub_pat_[A-Za-z0-9_]{40,}\b'),
    re.compile(r'\bSCT[A-Za-z0-9]{25,}\b'),
    re.compile(r'\bAKIA[0-9A-Z]{16}\b'),
    re.compile(r'[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s]+[\\/]', re.IGNORECASE),
]


def regular(path):
    info = path.lstat()
    if path.is_symlink() or getattr(info, 'st_file_attributes', 0) & 0x400:
        raise ValueError('Links and reparse points cannot enter public source: ' + path.name)
    return stat.S_ISREG(info.st_mode)


def collect(root):
    root = root.resolve()
    selected = [root / name for name in ROOT_FILES]
    selected.extend(root / 'scripts' / name for name in SCRIPTS)
    for folder in DIRECTORIES:
        base = root / folder
        if not base.is_dir():
            raise ValueError('Required source directory is missing: ' + folder)
        regular(base)
        for directory, subdirectories, files in os.walk(base, followlinks=False):
            subdirectories[:] = sorted(n for n in subdirectories if n not in EXCLUDED)
            for name in subdirectories:
                regular(Path(directory) / name)
            for name in sorted(files):
                path = Path(directory) / name
                if path.suffix not in EXTENSIONS:
                    raise ValueError('Unexpected source file type: ' + str(path.relative_to(root)))
                if path.suffix == '.json' and path.name not in JSON_FILES:
                    raise ValueError('Unexpected JSON configuration: ' + str(path.relative_to(root)))
                selected.append(path)
    result = {}
    for path in selected:
        if not regular(path):
            raise ValueError('Expected a source file: ' + path.name)
        relative = path.relative_to(root).as_posix()
        if any(ord(character) < 32 for character in relative):
            raise ValueError('Control character in source path')
        content = path.read_bytes()
        text = content.decode('utf-8-sig')
        if any(pattern.search(text) for pattern in SECRET_PATTERNS):
            raise ValueError('Private data marker requires review: ' + relative)
        result[relative] = content
    # 工作目录的 README 含内部验收链接；公开入口使用单独维护的说明。
    result['README.md'] = result['README.public.md']
    return result


def export(root, destination):
    root = root.resolve()
    destination = destination.absolute()
    parent = root / '.local' / 'public-source'
    destination.resolve().relative_to(parent.resolve())
    if destination == parent or destination.exists():
        raise ValueError('Use a new directory below .local/public-source')
    sources = collect(root)
    # 校验完成后才创建新目录；任何已有公开副本都不覆盖或清理。
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.parent.resolve() != destination.parent:
        raise ValueError('Export parent must not traverse a link')
    destination.mkdir()
    manifest = []
    for relative, content in sorted(sources.items()):
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open('xb') as stream:
            stream.write(content)
        manifest.append(hashlib.sha256(content).hexdigest() + '  ' + relative)
    (destination / 'SOURCE-MANIFEST.sha256').write_text('\n'.join(manifest) + '\n', encoding='utf-8')
    return len(sources)


if __name__ == '__main__':
    project = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--destination', type=Path,
                        default=project / '.local' / 'public-source' / ('HowieMonitor-' + time.strftime('%Y%m%d-%H%M%S', time.gmtime())))
    options = parser.parse_args()
    count = export(project, options.destination)
    print('Prepared {} public source files at {}'.format(count, options.destination))
