"""Refresh lesson README code excerpts from the marked source regions and Cypher snapshots they name.

Usage (from the repository root):  python workshop/GraphBridge.Workshop/fill-excerpts.py [--check]

A README excerpt is a ```csharp block directly after <!-- region: Name --> (the lesson's Lesson.cs)
or <!-- region: Domain/File.cs#Name -->. A ```cypher block directly after <!-- snapshot: name --> shows the reviewed
snapshots/name.cypher. The workshop tests fail if an excerpt differs from its source, so lesson scripts always show the
code that runs and the Cypher it produces. --check reports differences without writing.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
# The body is whole lines up to the block's own closing fence, so an empty block never reaches into the next one.
EXCERPT = re.compile(r"(<!-- region: (?P<ref>\S+) -->[ \t]*\n```csharp\n)(?P<code>(?:(?!```).*\n)*?)(```)")
SNAPSHOT = re.compile(r"(<!-- snapshot: (?P<name>[\w.-]+) -->[ \t]*\n```cypher\n)(?P<code>(?:(?!```).*\n)*?)(```)")


def region(file: Path, name: str) -> str:
    lines = file.read_text(encoding="utf-8").replace("\r", "").split("\n")
    start = next((i for i, l in enumerate(lines) if l.strip() == f"// workshop:start {name}"), None)
    end = next((i for i, l in enumerate(lines) if l.strip() == f"// workshop:end {name}"), None)
    if start is None or end is None or end <= start:
        raise SystemExit(f"{file}: region '{name}' needs '// workshop:start {name}' and a later '// workshop:end {name}'.")
    body = lines[start + 1:end]
    indent = min((len(l) - len(l.lstrip()) for l in body if l.strip()), default=0)
    return "\n".join(l[indent:] if len(l) >= indent else l.lstrip() for l in body)


def main() -> int:
    arguments = sys.argv[1:]
    if any(a in ("-h", "--help") for a in arguments):
        print(__doc__.strip())
        return 0
    unknown = [a for a in arguments if a != "--check"]
    if unknown:
        print(f"Unknown argument: {unknown[0]}", file=sys.stderr)
        print("Usage (from the repository root):  python workshop/GraphBridge.Workshop/fill-excerpts.py [--check]", file=sys.stderr)
        return 2
    check = "--check" in arguments
    stale = 0
    for readme in sorted((ROOT / "Lessons").glob("*/README.md")):
        text = readme.read_text(encoding="utf-8").replace("\r", "")

        def fill(match: re.Match) -> str:
            ref = match["ref"]
            file, name = (ROOT / ref.split("#")[0], ref.split("#")[1]) if "#" in ref else (readme.parent / "Lesson.cs", ref)
            return match.group(1) + region(file, name) + "\n" + match.group(4)

        def cypher(match: re.Match) -> str:
            snapshot = (ROOT / "snapshots" / (match["name"] + ".cypher")).read_text(encoding="utf-8").replace("\r", "")
            return match.group(1) + snapshot.rstrip("\n") + "\n" + match.group(4)

        updated = SNAPSHOT.sub(cypher, EXCERPT.sub(fill, text))
        if updated != text:
            stale += 1
            print(("stale: " if check else "updated: ") + str(readme.relative_to(ROOT)))
            if not check:
                readme.write_text(updated, encoding="utf-8", newline="\n")
    return 1 if check and stale else 0


if __name__ == "__main__":
    sys.exit(main())
