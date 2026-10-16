import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location('manifest', Path(__file__).with_name('Create-ReleaseManifest.py'))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


class ReleaseManifestTests(unittest.TestCase):
    def test_missing_failed_and_blocked_checks_never_qualify(self):
        for status in ['fail', 'blocked', 'not-run']:
            checks = [dict(check=n, status='pass') for n in m.REQUIRED_CHECKS]
            checks[0]['status'] = status
            self.assertEqual('incomplete', m.qualify(checks)[1])
        self.assertEqual('incomplete', m.qualify([])[1])

    def test_complete_receipts_only_request_review_not_publication(self):
        checks = [dict(check=n, status='pass') for n in m.REQUIRED_CHECKS]
        self.assertEqual('ready-for-maintainer-review', m.qualify(checks)[1])
        with self.assertRaises(ValueError):
            m.qualify(checks + checks[:1])

    def test_receipt_rejects_wrong_revision_dirty_tree_hash_and_missing_package_identity(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'evidence.log').write_bytes(b'passed')
            valid = dict(check='combined-package-consumer', status='pass', sourceCommit='a' * 40,
                         sourceDirty=False, evidencePath='evidence.log', evidenceSha256=m.sha(b'passed'), packageSha256=['candidate'])
            result = m.receipt(valid | {'secret': 'must-not-be-retained'}, root, 'a' * 40, ['candidate'])
            self.assertNotIn('secret', json.dumps(result))
            for field, value in [('sourceCommit', 'b' * 40), ('sourceDirty', True), ('sourceDirty', None),
                                 ('evidenceSha256', 'wrong'), ('packageSha256', ['old']), ('check', 'unknown'),
                                 ('evidencePath', '../outside.log')]:
                with self.subTest(field=field), self.assertRaises(ValueError):
                    m.receipt(valid | {field: value}, root, 'a' * 40, ['candidate'])

    def test_nuspec_and_notices_are_checked_against_source(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'candidate.nupkg'
            def write(commit='a' * 40, version='0.1.0-preview.1', notice=b'reviewed'):
                with zipfile.ZipFile(path, 'w') as z:
                    z.writestr('GraphBridge.Tooling.nuspec', f'''<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>
                      <id>GraphBridge.Tooling</id><version>{version}</version><license type="expression">MIT</license>
                      <repository type="git" url="https://github.com/joslat/GraphBridge.EFCore" commit="{commit}" />
                      <dependencies><group targetFramework="net10.0"><dependency id="GraphBridge.EntityFrameworkCore" version="0.1.0-preview.1" /></group></dependencies>
                      </metadata></package>''')
                    z.writestr('LICENSE', notice)
            write()
            result = m.package(path, 'a' * 40, '0.1.0-preview.1', {'LICENSE': b'reviewed'})
            self.assertEqual('GraphBridge.Tooling', result['id'])
            self.assertEqual('0.1.0-preview.1', result['dependencies'][0]['range'])
            self.assertEqual(m.sha(path.read_bytes()), result['sha256'])
            for change in [dict(commit='b' * 40), dict(version='old'), dict(notice=b'changed')]:
                write(**change)
                with self.subTest(change=change), self.assertRaises(ValueError):
                    m.package(path, 'a' * 40, '0.1.0-preview.1', {'LICENSE': b'reviewed'})


if __name__ == '__main__':
    unittest.main()
