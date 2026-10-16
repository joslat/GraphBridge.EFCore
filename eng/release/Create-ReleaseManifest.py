"""Prepare a byte-identified release candidate report; never publish, tag or authorize it.

Receipt JSON: check, status, sourceCommit, sourceDirty (false), evidencePath (repo-relative),
evidenceSha256. Package consumer receipts also carry packageSha256 (all candidate packages).
Checks are maintainer-supplied attestations backed by hashed logs, not cryptographic signatures.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET
import zipfile

REQUIRED_CHECKS = (
    'locked-build', 'provider-unit', 'provider-live', 'specification', 'tooling', 'generator',
    'template-consumer', 'combined-package-consumer', 'fresh-sample', 'secret-scan',
    'dependency-audit', 'public-documents', 'windows-consumer', 'linux-consumer',
)
PACKAGE_CHECKS = {'template-consumer', 'combined-package-consumer', 'windows-consumer', 'linux-consumer'}
PACKAGE_IDS = {'GraphBridge.EntityFrameworkCore', 'GraphBridge.Tooling', 'GraphBridge.Generators', 'GraphBridge.Templates'}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def git(repo, *args):
    return subprocess.run(['git', '-C', str(repo), *args], check=True, capture_output=True).stdout


def normalized(data):
    return data.decode('utf-8-sig').replace('\r\n', '\n')


def package(path, commit, version, attribution):
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError('Duplicate ZIP entries in candidate package')
        manifests = [n for n in names if n.endswith('.nuspec') and '/' not in n]
        if len(manifests) != 1:
            raise ValueError('Expected exactly one package manifest')
        metadata = ET.fromstring(archive.read(manifests[0])).find('{*}metadata')
        if metadata is None:
            raise ValueError('Missing package metadata')
        identity = metadata.findtext('{*}id')
        if identity not in PACKAGE_IDS or metadata.findtext('{*}version') != version:
            raise ValueError('Candidate package identity/version mismatch')
        repository = metadata.find('{*}repository')
        if repository is None or repository.get('commit') != commit or repository.get('type') != 'git' \
                or repository.get('url') != 'https://github.com/joslat/GraphBridge.EFCore':
            raise ValueError('Candidate package source repository/commit mismatch')
        license_node = metadata.find('{*}license')
        if license_node is None or license_node.get('type') != 'expression' or license_node.text != 'MIT':
            raise ValueError('Candidate package license mismatch')
        for name, expected in attribution.items():
            if name not in names or normalized(archive.read(name)) != normalized(expected):
                raise ValueError('Candidate attribution payload missing or different: ' + name)
        return dict(id=identity, version=version, file=path.name, sha256=sha(path.read_bytes()),
                    dependencies=sorted([dict(id=d.get('id'), range=d.get('version'))
                                         for d in metadata.findall('.//{*}dependency')], key=lambda d: d['id']))


def receipt(data, repo, commit, package_hashes):
    check = data.get('check')
    if check not in REQUIRED_CHECKS or data.get('status') not in {'pass', 'fail', 'blocked', 'not-run'}:
        raise ValueError('Unknown receipt check/status')
    if data.get('sourceCommit') != commit or data.get('sourceDirty') is not False:
        raise ValueError('Receipt is for a different or dirty source revision')
    relative = Path(data.get('evidencePath', ''))
    evidence = (repo / relative).resolve()
    if relative.is_absolute() or not evidence.is_relative_to(repo.resolve()) or not evidence.is_file():
        raise ValueError('Receipt needs an existing repository-relative evidence file')
    digest = sha(evidence.read_bytes())
    if data.get('evidenceSha256') != digest:
        raise ValueError('Receipt evidence hash mismatch')
    if check in PACKAGE_CHECKS and data.get('status') == 'pass' \
            and sorted(data.get('packageSha256', [])) != sorted(package_hashes):
        raise ValueError('Consumer receipt does not identify these candidate package bytes')
    # Never copy arbitrary receipt messages, environment variables or raw log contents.
    return dict(check=check, status=data['status'], evidencePath=relative.as_posix(), evidenceSha256=digest)


def qualify(checks):
    if len({c['check'] for c in checks}) != len(checks):
        raise ValueError('Duplicate check receipts')
    by_id = {c['check']: c for c in checks}
    result = [by_id.get(name, dict(check=name, status='not-run')) for name in REQUIRED_CHECKS]
    return result, 'ready-for-maintainer-review' if all(c['status'] == 'pass' for c in result) else 'incomplete'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--package', action='append', type=Path, required=True)
    parser.add_argument('--receipt', action='append', type=Path, default=[])
    parser.add_argument('--output-dir', type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[2]
    commit = git(repo, 'rev-parse', args.commit + '^{commit}').decode().strip()
    if git(repo, 'rev-parse', 'HEAD').decode().strip() != commit or git(repo, 'status', '--porcelain').strip():
        raise ValueError('Freeze a clean checkout of the selected commit before qualification')
    output = args.output_dir.resolve()
    if not output.is_relative_to((repo / '.artifacts').resolve()) or output.exists():
        raise ValueError('Choose a new output directory beneath this repository .artifacts')
    sources = {'LICENSE': 'LICENSE', 'THIRD_PARTY_NOTICES.md': 'eng/PACKAGE_NOTICES.md',
               'DEPENDENCY-INVENTORY.json': 'eng/dependencies/packages.json'}
    for path in git(repo, 'ls-tree', '-r', '--name-only', commit, 'eng/dependencies/licenses').decode().splitlines():
        if path.endswith('.txt'):
            sources['notices/' + Path(path).name] = path
    attribution = {name: git(repo, 'show', f'{commit}:{source}') for name, source in sources.items()}
    packages = sorted([package(p, commit, args.version, attribution) for p in args.package], key=lambda p: p['id'])
    if len({p['id'] for p in packages}) != len(packages) or {p['id'] for p in packages} != PACKAGE_IDS:
        raise ValueError('Select exactly the four current distribution package identities')
    hashes = [p['sha256'] for p in packages]
    checks, status = qualify([receipt(json.loads(p.read_text(encoding='utf-8-sig')), repo, commit, hashes) for p in args.receipt])
    source = git(repo, 'archive', '--format=zip', commit)
    output.mkdir(parents=True)
    (output / 'source.zip').write_bytes(source)
    report = dict(formatVersion=1, sourceCommit=commit, version=args.version,
                  sourceArchive=dict(file='source.zip', sha256=sha(source)), packages=packages,
                  checks=checks, qualification=status, published=False, publicationAuthorized=False,
                  evidenceBoundary='Hashed maintainer receipts; no automated signature, publication or cross-version support claim.')
    (output / 'manifest.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f'{status}: {output / "manifest.json"}. Nothing published or authorized.')
    return 0 if status == 'ready-for-maintainer-review' else 3


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (ValueError, OSError, ET.ParseError, zipfile.BadZipFile, subprocess.CalledProcessError) as error:
        raise SystemExit(f'Candidate preparation rejected ({type(error).__name__}): {error}')
