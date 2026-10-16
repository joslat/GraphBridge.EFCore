"""MkDocs hook: links from docs/ to files elsewhere in the repository (samples, workshop, tests, source) point to GitHub.

Inside the repository those links are relative and checked by eng/release/Check-PublicDocuments.py; on the site they
would leave docs_dir, so they are rewritten to the matching blob or tree URL of the main branch.
"""
import os
import posixpath
import re

LINK = re.compile(r"(\]\()([^)\s]+)(\))")


def on_page_markdown(markdown, page, config, files):
    docs_dir = config["docs_dir"]
    root = os.path.dirname(os.path.abspath(docs_dir))
    base = config["repo_url"].rstrip("/")
    page_dir = posixpath.dirname(page.file.src_uri)

    def rewrite(match):
        target = match.group(2)
        if re.match(r"^[a-z][a-z0-9+.-]*:", target) or target.startswith(("#", "/")):
            return match.group(0)
        path, sep, anchor = target.partition("#")
        resolved = posixpath.normpath(posixpath.join("docs", page_dir, path))
        if resolved == "docs" or resolved.startswith("docs/"):
            return match.group(0)
        kind = "tree" if os.path.isdir(os.path.join(root, resolved)) else "blob"
        url = f"{base}/{kind}/main/{resolved}" + (f"#{anchor}" if sep else "")
        return match.group(1) + url + match.group(3)

    return LINK.sub(rewrite, markdown)
