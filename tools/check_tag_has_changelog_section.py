# -*- coding: utf-8 -*-
"""Every git tag must have a CHANGELOG section of its own. The check whose absence was ASSUMED away.

WHY
---
`check_tag_has_status_entry.py` beside this file was built because "no tag without a §0 entry" had
failed five times. Its docstring quotes the diagnosis it was built on, and the diagnosis contains a
claim about a DIFFERENT artifact that nobody measured:

    "The CHANGELOG never drifted because CI READS IT; this doc drifted because nothing does."

🔴 **Nothing reads it.** No workflow under `.github/workflows/` mentions the CHANGELOG at all, and
no test asserts a section per version. The sentence was the reason the CHANGELOG was left ungated,
and it was false when it was written.

**`v0.35.0-beta` is the proof, and it was already released when this was found.** Tagged
2026-09-08; `git show v0.35.0-beta:CHANGELOG.md` has `## [Unreleased]` followed straight by
`## [0.34.0-beta]`. Its notes were never cut, so they sat in `[Unreleased]` and the NEXT release
would have shipped them a second time under its own number -- attributing work to a version that
did not do it, which is the intent-scope-vs-diff-scope error this project has already made twice in
release disclosures.

This is the missing reader, and unlike its sibling it CAN run in CI: `CHANGELOG.md` is tracked, so
a fresh clone can see its subject and there is no `--allow-missing-doc` escape to write.

WHAT IT CHECKS
--------------
For every tag at or after :data:`LOG_BEGINS`, a `## [<version>]` heading exists, where `<version>`
is the tag with its leading `v` removed. It does NOT check the section's contents: a gate that
graded prose would be a gate nobody could satisfy twice the same way. Presence is the property that
failed, so presence is what is asserted.
"""
import argparse
import os
import re
import subprocess  # DevSkim: ignore DS107369 - reads this repository's own tags
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CHANGELOG = os.path.join(ROOT, 'CHANGELOG.md')

#: Tags before this predate Keep-a-Changelog discipline in this repo. Matches the §0 checker's
#: scope date deliberately: two gates on the same release act with two different scopes is how one
#: of them quietly stops covering what the other does.
LOG_BEGINS = '2026-07-17'


def tags():
    out = subprocess.run(['git', 'tag'],  # DevSkim: ignore DS107369 - fixed argv
                         cwd=ROOT, capture_output=True, text=True, check=True)
    return sorted(t.strip() for t in out.stdout.splitlines() if t.strip())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--ablate-drop-section', metavar='VERSION',
                    help='pretend this version has no section, to prove the check can fail. '
                         'Prints the heading count first so an emptied CHANGELOG cannot '
                         'masquerade as a drift.')
    args = ap.parse_args()

    if not os.path.exists(CHANGELOG):
        print('CANNOT CHECK: no CHANGELOG.md at the repository root')
        return 2

    text = open(CHANGELOG, encoding='utf-8-sig').read()
    all_tags = tags()

    # POSITIVE CONTROL, both operands. "every tag has a section" is also true of a repository with
    # no tags and of a CHANGELOG this failed to parse -- and the second is the one that would pass
    # silently, because an empty findall returns cleanly.
    if not all_tags:
        print('CANNOT CHECK: the repository has no tags, so this measured nothing')
        return 2
    headings = re.findall(r'^## \[([^\]]+)\]', text, flags=re.MULTILINE)
    versions = {h for h in headings if h.lower() != 'unreleased'}
    if len(versions) < 5:
        print('CANNOT CHECK: found only %d versioned headings; this is not the file the check '
              'expects' % len(versions))
        return 2

    if args.ablate_drop_section:
        dropped = args.ablate_drop_section.lstrip('v')
        print('ABLATION: %d versioned headings read; dropping [%s] from the set'
              % (len(versions), dropped))
        versions.discard(dropped)

    in_scope, pre_rule = [], []
    for tag in all_tags:
        when = subprocess.run(['git', 'log', '-1', '--format=%cs', tag],  # DevSkim: ignore DS107369
                              cwd=ROOT, capture_output=True, text=True).stdout.strip()
        (in_scope if when >= LOG_BEGINS else pre_rule).append(tag)

    missing = [t for t in in_scope if t.lstrip('v') not in versions]
    print('%d tags total; %d predate %s and are out of scope; %d in scope; %d versioned sections'
          % (len(all_tags), len(pre_rule), LOG_BEGINS, len(in_scope), len(versions)))
    if missing:
        print('\nTAGS WITH NO CHANGELOG SECTION:')
        for t in missing:
            print('  - %s' % t)
        print('\nA release whose notes stay in [Unreleased] ships them AGAIN under the next '
              'version, which attributes work to a release that did not do it. Cut the section '
              'before tagging.')
        return 1
    print('every tag has a CHANGELOG section of its own')
    return 0


sys.exit(main())
