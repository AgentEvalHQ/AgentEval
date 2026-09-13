# -*- coding: utf-8 -*-
"""Every git tag must have a §0 entry in the status doc. The check that did not exist.

WHY
---
The rule "no tag without a §0 entry" has now failed FIVE times: 0.29, 0.30 and 0.31 (recorded in
§0w, which was written specifically to close it) and then 0.32 and 0.33, which shipped AFTER the
rule was written down and were logged retroactively on 2026-09-12.

§0w diagnosed it correctly and the diagnosis did not save it:

    "The CHANGELOG never drifted because CI READS IT; this doc drifted because nothing does."

Writing a rule into a header enforces nothing. This is the missing reader.

⚠ WHAT THIS CAN AND CANNOT DO. The status doc lives under `strategy/`, which is gitignored — a
public repository must not carry it. So this CANNOT run in CI against a fresh clone, and pretending
otherwise would be worse than not having it: it would pass vacuously on every CI machine and give
the rule a green tick it has not earned.

Instead it FAILS LOUDLY when the doc is absent rather than skipping. A maintainer runs it locally
before tagging. That is weaker than a CI gate and it is what the file layout allows; the honest
move is to say so here rather than to ship a check that cannot see its subject.
"""
import argparse
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOC = os.path.join(ROOT, 'strategy', 'AgentEval-Status-and-Plan-Forward.md')

#: The §0 log's first entry. Tags before this predate the rule.
LOG_BEGINS = '2026-07-17'


def tags():
    out = subprocess.run(['git', 'tag'], cwd=ROOT, capture_output=True, text=True, check=True)
    return sorted(t.strip() for t in out.stdout.splitlines() if t.strip())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--allow-missing-doc', action='store_true',
                    help='exit 0 when the status doc is absent. For a CI machine that legitimately '
                         'has no strategy/ checkout — NEVER as a way past a real failure.')
    args = ap.parse_args()

    if not os.path.exists(DOC):
        message = ('the status doc is not present at strategy/AgentEval-Status-and-Plan-Forward.md, '
                   'so this check cannot see its subject')
        if args.allow_missing_doc:
            print('SKIPPED: %s' % message)
            return 0
        print('CANNOT CHECK: %s' % message)
        print('  This is a failure, not a pass. Run it where the doc lives, or pass '
              '--allow-missing-doc if this machine legitimately has no strategy/ checkout.')
        return 2

    text = open(DOC, encoding='utf-8').read()
    all_tags = tags()

    # POSITIVE CONTROL. "every tag is logged" is also true of a repository with no tags, and of a
    # doc this failed to read.
    if not all_tags:
        print('CANNOT CHECK: the repository has no tags, so this measured nothing')
        return 2
    headings = re.findall(r'^## 0[a-z-]*\.', text, flags=re.MULTILINE)
    if len(headings) < 5:
        print('CANNOT CHECK: found only %d §0 headings; the doc is not the one this expects'
              % len(headings))
        return 2

    # SCOPE: the §0 log begins 2026-07-17. Tags older than that predate the rule and are not
    # failures — flagging 25 of them would make this check noise, and a check people scroll past
    # is a check that is not running. Scope by tag DATE, read from git, not by a hand-kept list.
    in_scope, pre_rule = [], []
    for tag in all_tags:
        when = subprocess.run(['git', 'log', '-1', '--format=%cs', tag],
                              cwd=ROOT, capture_output=True, text=True).stdout.strip()
        (in_scope if when >= LOG_BEGINS else pre_rule).append(tag)

    # MATCH INSIDE A §0 HEADING, NOT ANYWHERE IN THE DOCUMENT. `t not in text` passed on a tag
    # merely MENTIONED in narrative prose, and on any tag that is a prefix of another (no pair
    # collides today -- checked -- but "no collision currently exists" is not a property a gate
    # should depend on). The rule is "no tag without a §0 ENTRY", so the lookup now reads the
    # headings it is about, with a boundary so a prefix cannot satisfy a longer tag.
    # A §0 SECTION -- not a heading, and not the whole document.
    #
    # `t not in text` passed a tag merely MENTIONED in narrative prose elsewhere in the plan,
    # and passed any tag that is a prefix of another. Tightening it to headings went too far and
    # the tags said so immediately: §0w is ONE entry written deliberately to cover v0.29, v0.30
    # and v0.31 together, so a heading-per-tag rule failed four releases that ARE logged.
    #
    # The section is the unit the rule is actually about: 'no tag without a §0 ENTRY'.
    sections = re.split(r'^(?=## 0[a-z-]*\.)', text, flags=re.MULTILINE)[1:]
    logged = {t for section in sections for t in in_scope
              if re.search(r'(?<![\w.-])%s(?![\w.-])' % re.escape(t), section)}
    missing = [t for t in in_scope if t not in logged]
    print('%d tags total; %d predate the §0 log (%s) and are out of scope; %d in scope; %d §0 headings'
          % (len(all_tags), len(pre_rule), LOG_BEGINS, len(in_scope), len(headings)))
    if missing:
        print('\nTAGS WITH NO §0 ENTRY:')
        for t in missing:
            print('  - %s' % t)
        print('\nThe rule is "no tag without a §0 entry". It has failed five times; this check '
              'exists so the sixth is caught before the tag, not months later.')
        return 1
    print('every tag is named in the status doc')
    return 0


sys.exit(main())
