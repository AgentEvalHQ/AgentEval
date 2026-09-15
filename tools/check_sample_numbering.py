# -*- coding: utf-8 -*-
"""The documented `dotnet run -- <n>` numbers must match the dispatcher's own flattening.

WHY THIS EXISTS. `Program.RunLegacyNumber` resolves `<n>` against
`Groups.SelectMany(g => g.Samples)`, so a sample inserted anywhere but the tail renumbers every
sample after it. The numbers in the docs are hand-maintained, and they had ALREADY drifted in two
independent places before this checker was written -- `dotnet run -- 43  # Performance (H2)` sent
the reader to `Registry Discovery`. Nothing failed; the command ran, and it ran the wrong sample.

WHAT MAKES IT CHECKABLE. The docs already carry the stable coordinate in the comment: `(H2)` is
group H, second sample. A group letter plus a position does not move when an EARLIER group gains
a sample -- only the flat number does. So the doc line contains both the claim and the thing that
identifies it, and the two can be compared without anybody maintaining a second list.

THREE WAYS THIS CHECK COULD LIE, AND WHAT STOPS EACH:

1. **It could pass by finding nothing.** A regex change, or a docs reformat, makes every command
   unrecognisable and the checker prints "0 failed" while CI goes green -- the exact silent-
   coverage failure it exists to catch. So ZERO CHECKED REFERENCES IS A FAILURE, and
   `--ablate-blind` proves that guard fires.
2. **Its own file list could go stale.** The first version hard-coded three paths and missed
   `docs/redteam.md`, which carried two stale labelled references -- the checker reproducing, in
   its own configuration, the defect it polices. The set is now DISCOVERED from `git ls-files`.
3. **It could check the wrong project.** Several docs run `samples/Galaxus.RecommendationAgent.Evals`
   or a `$VAR` project, whose numbering is unrelated. Only references that resolve to
   `AgentEval.Samples` are checked; anything naming another project is skipped and counted.

Unlabelled references cannot be checked this way. They are COUNTED AND REPORTED, never passed
over in silence.

    python tools/check_sample_numbering.py
    python tools/check_sample_numbering.py --ablate-shift   # self-test, must FAIL
    python tools/check_sample_numbering.py --ablate-blind   # self-test, must FAIL
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROGRAM = os.path.join(REPO, 'samples', 'AgentEval.Samples', 'Program.cs')

# The dispatcher these numbers address. A reference naming any other project is not ours.
OUR_PROJECT = 'samples/AgentEval.Samples'

# A group header `new('H', "Benchmarks", ...)` or a sample `new("Name", "desc", X.RunAsync)`.
# NOT re.VERBOSE: the pattern matches literal spaces inside the C# argument lists, and VERBOSE
# would silently strip them.
_TOKEN = re.compile(
    r"""new\('(?P<group>[A-Z])'\s*,\s*"(?P<title>[^"]*)\""""
    r"""|new\("(?P<name>[^"]+)"\s*,\s*"[^"]*"\s*,\s*[A-Za-z0-9_]+\.RunAsync""")

# `dotnet run ... -- 45   # Performance  (H2)`
_DOC_LINE = re.compile(r'dotnet run\b(?P<mid>[^\n]*?)--\s+(?P<num>\d+)\b(?P<rest>[^\n]*)')
_LABEL = re.compile(r'\((?P<group>[A-Z])(?P<pos>\d+)\)')
_PROJECT = re.compile(r'--project\s+(?P<path>\S+)')


def build_index(source: str) -> dict:
    """group letter -> list of (flat_index, sample_name), in declaration order."""
    flat = 0
    current = None
    groups: dict[str, list] = {}
    for m in _TOKEN.finditer(source):
        if m.group('group'):
            current = m.group('group')
            groups.setdefault(current, [])
        elif m.group('name'):
            flat += 1
            if current is not None:
                groups[current].append((flat, m.group('name')))
    return groups


def discover_docs() -> list:
    """Every tracked markdown file, from git -- never a hand-maintained list (see §2 above)."""
    out = subprocess.run(['git', '-C', REPO, 'ls-files', '--', '*.md'],
                         capture_output=True, encoding='utf-8', errors='replace')
    if out.returncode != 0:
        print('FAIL: could not list tracked files (%s)' % out.stderr.strip()[:200])
        return []
    return [p for p in out.stdout.splitlines() if p.strip()]


def targets_our_dispatcher(mid: str) -> bool:
    """True when this `dotnet run` addresses AgentEval.Samples.

    A bare `dotnet run -- 3` (no --project) is ours: the docs `cd` into the sample directory
    first. A `--project` naming anything else -- Galaxus, or a `$VAR` -- is not.
    """
    project = _PROJECT.search(mid)
    if project is None:
        return True
    return project.group('path').replace('\\', '/').endswith(OUR_PROJECT)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--ablate-shift', action='store_true',
                    help='shift every flat index by one. Every labelled number then points at the '
                         'wrong sample, so a working checker MUST fail.')
    ap.add_argument('--ablate-blind', action='store_true',
                    help='make the line pattern match nothing, simulating a docs reformat or a '
                         'regex change. The checker MUST fail on zero coverage rather than '
                         'reporting "0 failed" and going green.')
    args = ap.parse_args()

    with open(PROGRAM, encoding='utf-8-sig') as fh:
        source = fh.read()

    groups = build_index(source)
    if not groups:
        print('FAIL: parsed 0 groups from Program.cs -- the dispatcher shape changed.')
        return 1

    total = sum(len(v) for v in groups.values())
    docs = discover_docs()
    print('Dispatcher: %d groups, %d samples (flat 1..%d)' % (len(groups), total, total))
    print('Scanning %d tracked markdown file(s).' % len(docs))

    if args.ablate_shift:
        groups = {g: [(i + 1, n) for (i, n) in items] for g, items in groups.items()}
        print('ABLATION: every flat index shifted by +1.')

    line_pattern = _DOC_LINE
    if args.ablate_blind:
        line_pattern = re.compile(r'(?!x)x(?P<mid>)(?P<num>)(?P<rest>)')   # matches nothing
        print('ABLATION: the reference pattern now matches nothing.')

    checked = failures = 0
    unlabelled: list[str] = []
    other_project = 0
    files_with_refs = 0

    for rel in docs:
        path = os.path.join(REPO, rel)
        try:
            with open(path, encoding='utf-8-sig') as fh:
                lines = fh.read().splitlines()
        except OSError:
            continue

        saw = False
        for lineno, line in enumerate(lines, 1):
            m = line_pattern.search(line)
            if not m:
                continue
            if not targets_our_dispatcher(m.group('mid')):
                other_project += 1
                continue
            saw = True

            num = int(m.group('num'))
            label = _LABEL.search(m.group('rest'))
            if not label:
                unlabelled.append('%s:%d  -- %d' % (rel, lineno, num))
                continue

            checked += 1
            g, pos = label.group('group'), int(label.group('pos'))
            items = groups.get(g)
            if not items or pos < 1 or pos > len(items):
                print('  FAIL %s:%d  label (%s%d) names no sample' % (rel, lineno, g, pos))
                failures += 1
                continue

            want, name = items[pos - 1]
            if want != num:
                print('  FAIL %s:%d  says %d, but (%s%d) "%s" is %d  [%d runs %s]'
                      % (rel, lineno, num, g, pos, name, want, num,
                         where(groups, num) or '(out of range)'))
                failures += 1
        files_with_refs += 1 if saw else 0

    print()
    print('Checked %d labelled reference(s) across %d file(s); %d failed.'
          % (checked, files_with_refs, failures))
    if other_project:
        print('%d reference(s) name a different project and were skipped.' % other_project)
    if unlabelled:
        # NO SILENT CAPS. These carry a number and no coordinate, so nothing anchors them.
        print('%d reference(s) carry no (Gn) label and were NOT checked:' % len(unlabelled))
        for u in unlabelled[:12]:
            print('    %s' % u)
        if len(unlabelled) > 12:
            print('    ... and %d more' % (len(unlabelled) - 12))

    # A CHECK THAT FOUND NOTHING HAS NOT PASSED. This is the guard `--ablate-blind` exercises.
    if checked == 0:
        print()
        print('FAIL: zero labelled references were found. Either the docs no longer carry them, '
              'or the pattern stopped matching. A checker that finds nothing has not verified '
              'anything -- it must not report success.')
        return 1

    if args.ablate_shift or args.ablate_blind:
        if failures or checked == 0:
            print('\nABLATION PASSED: the defect was detected.')
            return 0
        print('\nABLATION FAILED: the injected defect produced no failure. '
              'The checker is not reading what it claims to read.')
        return 1

    if failures:
        print('\nFAIL: a documented number runs a different sample than the doc claims.')
        return 1
    print('\nPASS: every labelled number resolves to the sample its label names.')
    return 0


def where(groups: dict, flat: int):
    for g, items in groups.items():
        for pos, (idx, name) in enumerate(items, 1):
            if idx == flat:
                return '"%s" (%s%d)' % (name, g, pos)
    return None


if __name__ == '__main__':
    sys.exit(main())
