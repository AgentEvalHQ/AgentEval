# -*- coding: utf-8 -*-
"""The documented `dotnet run -- <n>` numbers must match the dispatcher's own flattening.

WHY THIS EXISTS. `Program.RunLegacyNumber` resolves `<n>` against
`Groups.SelectMany(g => g.Samples)`, so a sample inserted anywhere but the tail renumbers every
sample after it. The numbers in the READMEs are hand-maintained, and they had ALREADY drifted by
one before this checker was written -- `dotnet run -- 43  # Performance (H2)` sent the reader to
`Registry Discovery`. Nothing failed; the command ran, and it ran the wrong sample.

WHAT MAKES IT CHECKABLE. The docs already carry the stable coordinate in the comment: `(H2)` is
group H, second sample. A group letter plus a position does not move when an EARLIER group gains
a sample -- only the flat number does. So the doc line contains both the claim and the thing that
identifies it, and the two can be compared without anybody maintaining a second list.

An unlabelled line cannot be checked this way. Those are COUNTED AND REPORTED, never passed over
in silence: a checker that quietly skips half its inputs reads as coverage it does not have.

    python tools/check_sample_numbering.py
    python tools/check_sample_numbering.py --ablate-shift   # self-test, must FAIL
"""
from __future__ import annotations

import argparse
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROGRAM = os.path.join(REPO, 'samples', 'AgentEval.Samples', 'Program.cs')

# Docs that carry runnable `dotnet run -- <n>` lines.
DOC_GLOBS = [
    os.path.join(REPO, 'samples', 'AgentEval.Samples', 'README.md'),
    os.path.join(REPO, 'samples', 'AgentEval.Samples', 'Benchmarks', 'README.md'),
    os.path.join(REPO, 'docs', 'walkthrough.md'),
]

# A group header `new('H', "Benchmarks", ...)` or a sample `new("Name", "desc", X.RunAsync)`.
# NOT re.VERBOSE: the pattern matches literal spaces inside the C# argument lists, and VERBOSE
# would silently strip them.
_TOKEN = re.compile(
    r"""new\('(?P<group>[A-Z])'\s*,\s*"(?P<title>[^"]*)\""""
    r"""|new\("(?P<name>[^"]+)"\s*,\s*"[^"]*"\s*,\s*[A-Za-z0-9_]+\.RunAsync""")

# `dotnet run ... -- 45   # Performance  (H2)`
_DOC_LINE = re.compile(
    r'dotnet run\b[^\n]*?--\s+(?P<num>\d+)\b(?P<rest>[^\n]*)')
_LABEL = re.compile(r'\((?P<group>[A-Z])(?P<pos>\d+)\)')


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


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--ablate-shift', action='store_true',
                    help='insert a phantom sample at the head of the flattening. Every labelled '
                         'number shifts by one, so a working checker MUST fail. If it passes, the '
                         'checker is not reading what it claims to read.')
    args = ap.parse_args()

    with open(PROGRAM, encoding='utf-8-sig') as fh:
        source = fh.read()

    groups = build_index(source)
    if not groups:
        print('FAIL: parsed 0 groups from Program.cs -- the dispatcher shape changed.')
        return 1

    total = sum(len(v) for v in groups.values())
    print('Dispatcher: %d groups, %d samples (flat 1..%d)'
          % (len(groups), total, total))

    if args.ablate_shift:
        # Renumber as though one sample were inserted at the very front.
        groups = {g: [(i + 1, n) for (i, n) in items] for g, items in groups.items()}
        print('ABLATION: every flat index shifted by +1.')

    checked = failures = 0
    unlabelled: list[str] = []

    for path in DOC_GLOBS:
        if not os.path.exists(path):
            continue
        rel = os.path.relpath(path, REPO).replace('\\', '/')
        with open(path, encoding='utf-8-sig') as fh:
            lines = fh.read().splitlines()

        for lineno, line in enumerate(lines, 1):
            m = _DOC_LINE.search(line)
            if not m:
                continue
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
                actual = (groups_flat(groups, num) or '(out of range)')
                print('  FAIL %s:%d  says %d, but (%s%d) "%s" is %d  [%d currently runs %s]'
                      % (rel, lineno, num, g, pos, name, want, num, actual))
                failures += 1

    print()
    print('Checked %d labelled reference(s); %d failed.' % (checked, failures))
    if unlabelled:
        # NO SILENT CAPS. These lines carry a number and no coordinate, so nothing anchors them.
        print('%d reference(s) carry no (Gn) label and were NOT checked:' % len(unlabelled))
        for u in unlabelled[:12]:
            print('    %s' % u)
        if len(unlabelled) > 12:
            print('    ... and %d more' % (len(unlabelled) - 12))

    if args.ablate_shift:
        if failures:
            print('\nABLATION PASSED: the shift was detected (%d failures).' % failures)
            return 0
        print('\nABLATION FAILED: a +1 shift on every index produced NO failure. '
              'The checker is not reading the dispatcher.')
        return 1

    if failures:
        print('\nFAIL: a documented number runs a different sample than the doc claims.')
        return 1
    print('\nPASS: every labelled number resolves to the sample its label names.')
    return 0


def groups_flat(groups: dict, flat: int):
    for g, items in groups.items():
        for idx, name in items:
            if idx == flat:
                return '"%s" (%s%d)' % (name, g, items.index((idx, name)) + 1)
    return None


if __name__ == '__main__':
    sys.exit(main())
