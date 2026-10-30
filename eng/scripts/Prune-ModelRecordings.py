"""Removes recorded model calls that no test uses any more.

Recorded calls are keyed by a hash of each request, so a request that changes is recorded again and its old entry stays
behind. To prune them, replay every suite that reads the recordings with GRAPHBRIDGE_AI_USED_KEYS naming a file, check that
the run passed, then:

    python eng/scripts/Prune-ModelRecordings.py <used-keys file> [--apply]

Without --apply it only reports. Only the samples' recordings files named in the used-keys file are changed; a file the run
didn't read is left alone. The JSON is rewritten in the form the recorder writes, so the diff shows only removed entries.
"""
import io
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def main(argv):
    if len(argv) not in (1, 2) or len(argv) == 2 and argv[1] != "--apply":
        print(__doc__)
        return 2
    used = {}
    for line in Path(argv[0]).read_text(encoding="utf-8").splitlines():
        name, kind, key = line.split("\t")
        used.setdefault(name, {}).setdefault(kind, set()).add(key)
    for path in sorted(ROOT.glob("samples/*/data/*-recordings.json")):
        if path.name not in used:
            print(f"{path.relative_to(ROOT)}: not read by the run, left alone")
            continue
        raw = io.open(path, encoding="utf-8", newline="").read()
        data = json.loads(raw)
        removed = {}
        for kind in ("chat", "embeddings"):
            keep = used[path.name].get(kind, set())
            removed[kind] = [entry["request"] for entry in data[kind] if entry["request"] not in keep]
            data[kind] = [entry for entry in data[kind] if entry["request"] in keep]
        print(f"{path.relative_to(ROOT)}: {len(removed['chat'])} chat and {len(removed['embeddings'])} embedding entries unused")
        if len(argv) == 2 and any(removed.values()):
            newline = "\r\n" if "\r\n" in raw else "\n"
            ending = raw[len(raw.rstrip("\r\n")):]
            io.open(path, "w", encoding="utf-8", newline="").write(json.dumps(data, indent=2, ensure_ascii=False).replace("\n", newline) + ending)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
