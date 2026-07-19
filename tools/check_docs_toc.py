#!/usr/bin/env python3
"""
Doc-nav completeness check (BUG found 2026-07-19): a new docs/**/*.md file that never gets added to
docs/toc.yml (the real docfx site navigation) is invisible in the site sidebar even though it may still
be linked from docs/index.md's separately-maintained landing-page card list, or from another doc's
in-content cross-link. Both docs/agent-skills.md and the freshly-written
docs/gatekeeper/explainability-and-trust.md slipped through exactly this way in the same session, found only
by manual audit. This script is the automated version of that audit: fail CI if any doc file isn't reachable
from toc.yml.

Individual docs/adr/*.md files are intentionally excluded — they're indexed via docs/adr/README.md's own
table (a hub-page pattern), not listed individually in toc.yml; that's a deliberate, reasonable structure,
not a gap. See docs/adr/README.md's "## Index" table.

Usage: python3 tools/check_docs_toc.py [--docs-dir docs]
Exit code 0 if every doc is reachable; 1 (with a list) otherwise.
"""

import argparse
import pathlib
import re
import sys


def find_hrefs(toc_path: pathlib.Path) -> set[str]:
    text = toc_path.read_text(encoding="utf-8")
    # toc.yml hrefs are plain "href: <path>" lines (no quoting needed for the paths used in this file).
    return {m.strip() for m in re.findall(r"^\s*href:\s*(\S+)\s*$", text, re.MULTILINE)}


def find_doc_files(docs_dir: pathlib.Path) -> set[str]:
    excluded_dir_names = {"_site", "templates", "adr"}
    files = set()
    for path in docs_dir.rglob("*.md"):
        rel = path.relative_to(docs_dir)
        if rel.parts[0] in excluded_dir_names:
            continue
        files.add(str(rel).replace("\\", "/"))
    return files


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--docs-dir", default="docs", help="Path to the docs/ directory (default: docs)")
    args = parser.parse_args()

    docs_dir = pathlib.Path(args.docs_dir)
    toc_path = docs_dir / "toc.yml"

    if not toc_path.exists():
        print(f"ERROR: {toc_path} not found.", file=sys.stderr)
        return 1

    hrefs = find_hrefs(toc_path)
    doc_files = find_doc_files(docs_dir)

    # An href may carry an anchor (path.md#section) or already be a bare relative path — doc_files never
    # has anchors, so strip them before comparing.
    href_paths = {h.split("#", 1)[0] for h in hrefs}

    orphans = sorted(f for f in doc_files if f not in href_paths)

    if orphans:
        print(f"FAIL: {len(orphans)} doc file(s) under {docs_dir}/ are not reachable from {toc_path} "
              "(the real site navigation — docs/index.md's landing-page list does NOT count, it's a "
              "separate, driftable list):\n")
        for o in orphans:
            print(f"  - {docs_dir}/{o}")
        print(f"\nAdd each one to {toc_path}, or add its containing directory to the excluded_dir_names "
              "list in this script if it's deliberately excluded (with a comment explaining why — see the "
              "docs/adr/ precedent in this script's own docstring).")
        return 1

    print(f"OK: all {len(doc_files)} doc file(s) under {docs_dir}/ are reachable from {toc_path}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
