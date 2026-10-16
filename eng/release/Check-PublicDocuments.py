import json
from pathlib import Path
import re
import subprocess
from urllib.parse import unquote, urlsplit

root = Path(__file__).resolve().parents[2]
tracked = subprocess.check_output(['git', 'ls-files'], cwd=root, text=True).splitlines()
files = set(tracked)
errors = []
checked = 0
for name in tracked:
    if not name.endswith('.md') or name == 'AGENTS.md':
        continue
    source = root / name
    text = source.read_text(encoding='utf-8-sig')
    targets = re.findall(r'\]\(([^\s)]+)(?:\s+"[^"]*")?\)', text)
    targets += re.findall(r'^\[[^\]]+\]:\s*(\S+)', text, re.MULTILINE)
    for target in targets:
        target = target.strip('<>')
        link = urlsplit(target)
        if link.scheme or target.startswith('#') or not link.path:
            continue
        resolved = (source.parent / unquote(link.path)).resolve()
        checked += 1
        if not resolved.is_relative_to(root):
            errors.append(f'{name}: link leaves repository: {target}')
            continue
        relative = resolved.relative_to(root).as_posix()
        if relative.startswith(('strategy/', '.artifacts/')) or not resolved.exists():
            errors.append(f'{name}: private/missing target: {target}')
        elif relative not in files and not any(item.startswith(relative.rstrip('/') + '/') for item in files):
            errors.append(f'{name}: untracked target: {target}')
print(json.dumps({'sourceCommit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
                  'checkedLocalLinks': checked, 'errors': errors}, indent=2))
raise SystemExit(bool(errors))
