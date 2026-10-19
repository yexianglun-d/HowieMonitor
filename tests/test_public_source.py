import importlib.util
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location('public_source', Path(__file__).resolve().parents[1] / 'scripts' / 'prepare-public-source.py')
EXPORTER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(EXPORTER)


class PublicSourceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name).resolve()
        for name in EXPORTER.ROOT_FILES:
            (self.root / name).write_text('public\n', encoding='utf-8')
        (self.root / 'README.md').write_text('private operational notes', encoding='utf-8')
        for folder in EXPORTER.DIRECTORIES:
            (self.root / folder).mkdir(parents=True, exist_ok=True)
        (self.root / 'scripts').mkdir()
        for name in EXPORTER.SCRIPTS:
            (self.root / 'scripts' / name).write_text('source\n', encoding='utf-8')

    def tearDown(self):
        self.temporary.cleanup()

    def test_internal_readme_and_runtime_directories_are_excluded(self):
        (self.root / 'src' / 'App.cs').write_text('class App {}', encoding='utf-8')
        private = self.root / 'src' / 'obj'
        private.mkdir()
        (private / 'agent.json').write_text('private', encoding='utf-8')
        files = EXPORTER.collect(self.root)
        self.assertEqual(files['README.md'].decode().splitlines(), ['public'])
        self.assertIn('src/App.cs', files)
        self.assertFalse(any('/obj/' in path or path.startswith('docs/') for path in files))

    def test_runtime_json_in_source_is_rejected(self):
        (self.root / 'src' / 'agent.json').write_text('{}', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'JSON'):
            EXPORTER.collect(self.root)

    def test_reviewed_favicon_is_allowed_without_allowing_other_svg_files(self):
        relative = 'src/Howie.Monitor.Web/public/favicon.svg'
        favicon = self.root / relative
        favicon.parent.mkdir(parents=True)
        favicon.write_text('<svg xmlns="http://www.w3.org/2000/svg"/>', encoding='utf-8')
        self.assertEqual(EXPORTER.collect(self.root)[relative], favicon.read_bytes())
        (favicon.parent / 'private.svg').write_text('<svg/>', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'file type'):
            EXPORTER.collect(self.root)

    def test_tokens_are_rejected_without_echoing_values(self):
        token = 'ghp_' + 'A' * 40
        (self.root / 'src' / 'App.cs').write_text(token, encoding='utf-8')
        with self.assertRaises(ValueError) as result:
            EXPORTER.collect(self.root)
        self.assertNotIn(token, str(result.exception))

    def test_database_and_binary_are_rejected(self):
        for name in ['monitor.db', 'Agent.exe']:
            path = self.root / 'src' / name
            path.write_bytes(b'private')
            with self.assertRaisesRegex(ValueError, 'file type'):
                EXPORTER.collect(self.root)
            path.unlink()

    def test_links_are_rejected(self):
        target = self.root / 'target.cs'
        target.write_text('source', encoding='utf-8')
        link = self.root / 'src' / 'App.cs'
        try:
            link.symlink_to(target)
        except OSError as error:
            self.skipTest(str(error))
        with self.assertRaisesRegex(ValueError, 'Links'):
            EXPORTER.collect(self.root)

    def test_export_cannot_overwrite_or_leave_private_staging(self):
        outside = self.root / 'public'
        with self.assertRaises(ValueError):
            EXPORTER.export(self.root, outside)
        destination = self.root / '.local' / 'public-source' / 'review'
        self.assertGreater(EXPORTER.export(self.root, destination), 0)
        self.assertEqual((destination / 'README.md').read_text(), 'public\n')
        self.assertTrue((destination / 'SOURCE-MANIFEST.sha256').is_file())
        with self.assertRaises(ValueError):
            EXPORTER.export(self.root, destination)


if __name__ == '__main__':
    unittest.main()
