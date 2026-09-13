# -*- coding: utf-8 -*-
"""The free go/no-go gate for a candidate corpus revision. ZERO model calls.

WHAT IT IS FOR. `headroom ~= 1 - ALLgold_retrieval` (MEASUREMENT_STATUS 88.12: median residual 0.000
across 35 shapes) is computable from a corpus alone. So a proposed revision can be checked for
discriminating power BEFORE it is generated into the repo and BEFORE a probe run is paid for. This
converts a ~200-500 call gamble into a free measurement plus a run whose RETRIEVAL result is
already known.

🔴 IT DOES NOT KNOW WHETHER THE CORPUS STILL WORKS, and that distinction cost a corpus.
ALLgold measures how hard gold is to FIND. A change that makes gold harder to find and a change
that makes the answer impossible to DERIVE move it in the same direction, so this gate reports both
as progress. Only V1 -- the gold-only ceiling, which costs model calls -- says the questions are
still answerable. On 2026-09-13 an E1-b re-form of semantic/source-attribution passed here at
0.733 -> 0.200 and the probe then read V1 15/15 -> 10/15: five questions had become unanswerable,
and part of the improvement this tool reported was the corpus breaking (MEASUREMENT_STATUS 88.42).

Usage:
    python tools/typedmemeval_precheck.py <vertical> <candidate-root>

`candidate-root` is a directory holding `<vertical>/agenteval-typedmemeval-<vertical>-v5.json`,
e.g. the scratch tree a generator was pointed at by rebinding `tmc.DATA_ROOT`.

------------------------------------------------------------------------------------------------
🔴 THE CONFOUND THIS TOOL EXISTS TO DISCLOSE, and it bit me before I noticed it.
------------------------------------------------------------------------------------------------
Regenerating with `--recalibrate` re-searches the ECHO knob, which weaves question vocabulary into
distractors so they compete for the top-K budget. Echo is therefore a direct driver of ALLgold:

    LOWER echo  ->  weaker distractors  ->  gold easier to retrieve  ->  HIGHER ALLgold

So a candidate and its shipped predecessor usually differ in TWO variables, not one, and a raw
before/after ALLgold comparison is not a controlled experiment. Measured on two real arcs:

    episodic   E1-b            echo 0.6667 -> 0.3333
    bitemporal corrections 1->3 echo 0.6406 -> 0.3750

In the bitemporal candidate `correction-depth/valid` at correction-rung 3 read ALLgold 0.500 shipped
and 0.750 candidate -- the SAME rung, moved by echo alone. Read as a rung effect it would have been a
fabricated mechanism.

WHY THE CONFOUND IS SAFE FOR A *GO* DECISION -- and note the scope of that claim. The confound
pushes ALLgold UP, i.e. it works AGAINST the change looking good. A candidate that clears the floor
DESPITE a lower echo clears it conservatively, and the true RETRIEVAL effect of the design change is
at least what was measured. Episodic's E1-b pre-check (ALLgold 1.00 -> 0.27) was trustworthy in
exactly that sense and its arc succeeded.

⚠ Safe against the ECHO confound is not the same as safe to ship. Semantic's re-form was equally
clean on this axis -- the echo fell 0.667 -> 0.292, so the pass was conservative -- and it was still
the wrong corpus. The two statements are about different things and this file used to run them
together.

WHAT IT CANNOT DO.
  1. A MARGINAL pass cannot be attributed. If the candidate lands near the floor, the honest reading
     is "not established", not "small improvement" -- the echo delta is easily that large on its
     own. This tool prints both echoes and refuses to call a marginal result a pass.
  2. It cannot see ANSWERABILITY at all, for the reason above. A PASS here licenses the SPEND and
     nothing after it: V1, V8 and V6 are what decide whether the candidate ships, and all three
     cost calls.
"""
import collections
import json
import os
import sys


# A GATE MUST NOT DIE ON ITS OWN WARNING TEXT.
#
# The findings here are marked with U+1F534 and U+26A0, and Windows hands a bare `python x.py`
# a cp1252 stdout that cannot encode either. Every line that carries one sits on a branch that
# only fires when something is WRONG -- an undeclared absence shape, an exemption that is an
# artefact of the wrong ceiling, the bar-supplied caveat -- so the tool ran green for as long as
# it had nothing to say and raised UnicodeEncodeError, mid-report, the first time it did. It did
# exactly that on 2026-09-13, after `prospective/not-yet-true` became a declared exception with
# V8 > V1 and reached the second of those branches for the first time.
#
# A traceback is not a finding. Reconfiguring is preferred over rewriting the six strings in
# ASCII because the next warning someone adds will carry a marker too, and a convention nobody
# can see is a convention that lapses. `errors="replace"` keeps the line readable on a console
# that still cannot render the glyph.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import typedmemeval_common as tmc  # noqa: E402

SHIPPED_ROOT = os.path.join(os.path.dirname(HERE), 'src', 'AgentEval.Memory', 'Data', 'typedmemeval')
FLOOR = 0.15
#: Below this margin over the floor, an echo change alone could account for the result.
MARGIN = 0.10


def _paths(root, vertical):
    base = os.path.join(root, vertical, 'agenteval-typedmemeval-%s-v5' % vertical)
    return base + '.json', base + '.meta.json'


def _echo(meta_path):
    if not os.path.exists(meta_path):
        return None
    return (json.load(open(meta_path, encoding='utf-8')).get('coverage') or {}).get('echo')


def _allgold(corpus_path):
    entries = json.load(open(corpus_path, encoding='utf-8'))
    cells = collections.defaultdict(lambda: [0, 0])
    text = lambda s: ' '.join(t.get('content', '') for t in s)  # noqa: E731
    for q in entries:
        x = q.get('typedmemeval') or {}
        ids = q.get('haystack_session_ids') or []
        gold = [g for g in (q.get('answer_session_ids') or []) if g in ids]
        if not gold:
            continue
        top = set(tmc.bm25_rank(q['question'],
                                [text(s) for s in q['haystack_sessions']])[:tmc.K_REF])
        key = (x.get('shape'), x.get('clock'))
        cells[key][1] += 1
        cells[key][0] += {ids.index(g) for g in gold}.issubset(top)
    return cells


def main():
    if len(sys.argv) < 3:
        print(__doc__.strip().splitlines()[0])
        print('usage: python tools/typedmemeval_precheck.py <vertical> <candidate-root>')
        return 1
    vertical, cand_root = sys.argv[1], sys.argv[2]
    sc, sm = _paths(SHIPPED_ROOT, vertical)
    cc, cm = _paths(cand_root, vertical)
    for p in (sc, cc):
        if not os.path.exists(p):
            print('missing corpus: %s' % p)
            return 1

    e_ship, e_cand = _echo(sm), _echo(cm)
    ship, cand = _allgold(sc), _allgold(cc)

    print('PRE-CHECK  %s   (K_ref=%d, 0 model calls)' % (vertical, tmc.K_REF))
    print()
    print('  echo knob   shipped %-8s candidate %-8s %s'
          % (e_ship, e_cand,
             '' if e_ship == e_cand else '<== TWO VARIABLES MOVED, see the confound note below'))
    print()
    print('  %-36s %10s %11s %14s' % ('stratum', 'shipped', 'candidate', 'pred headroom'))
    # FAIL CLOSED ON A MISSING STRATUM. `_allgold` returns a defaultdict, so a shipped stratum
    # absent from the candidate used to materialise as [0, 0] -> b = nan -> and EVERY later
    # comparison against nan is False, so no floor flag fired and the tool printed PASS for a
    # candidate corpus that had dropped a whole stratum. A go/no-go gate whose job is to decide
    # whether to spend thousands of calls must refuse what it cannot see, not wave it through.
    missing = sorted(set(ship) - set(cand), key=str)
    if missing:
        raise SystemExit(
            'DO NOT SPEND: the candidate is missing %d stratum/strata the shipped corpus has -- %s. '
            'A comparison cannot be made over questions that are not there, and the previous '
            'behaviour was to report PASS.'
            % (len(missing), ', '.join('/'.join(str(k) for k in m if k is not None) for m in missing)))

    verdicts = []
    for key in sorted(set(ship) | set(cand), key=str):
        a = ship[key][0] / ship[key][1] if ship[key][1] else float('nan')
        b = cand[key][0] / cand[key][1] if cand[key][1] else float('nan')
        pred = 1.0 - b
        name = '/'.join(str(k) for k in key if k is not None)
        flag = ''
        if pred < FLOOR:
            flag = '  BELOW FLOOR'
        elif pred < FLOOR + MARGIN:
            flag = '  MARGINAL'
        verdicts.append((name, a, b, pred, flag))
        print('  %-36s %10.3f %11.3f %+14.3f%s' % (name, a, b, pred, flag))

    below = [v for v in verdicts if v[3] < FLOOR]
    marginal = [v for v in verdicts if FLOOR <= v[3] < FLOOR + MARGIN]
    worse = [v for v in verdicts if v[2] > v[1] + 0.01]

    print()
    if e_ship != e_cand:
        print('  🔴 THE ECHO KNOB MOVED (%s -> %s), so this is NOT a controlled comparison.'
              % (e_ship, e_cand))
        print('     Echo drives ALLgold directly: lower echo -> weaker distractors -> higher')
        print('     ALLgold. The confound pushes AGAINST the change looking good, so a')
        print('     COMFORTABLE pass is conservative and trustworthy. A MARGINAL one is not')
        print('     attributable -- an echo delta alone is easily that large.')
        print()
    if worse:
        print('  ⚠ STRATA THAT GOT WORSE (candidate retrieves gold MORE easily):')
        for name, a, b, pred, _ in worse:
            print('      %-34s ALLgold %.3f -> %.3f  (pred headroom %+.3f)' % (name, a, b, pred))
        print()

    if below:
        print('  VERDICT: DO NOT SPEND. %d stratum/strata predicted below the %.2f floor.'
              % (len(below), FLOOR))
        return 2
    if marginal:
        names = ', '.join(v[0] for v in marginal)
        if e_ship != e_cand:
            # The echo moved, so a thin margin cannot be attributed to the design at all.
            print('  VERDICT: NOT ESTABLISHED. %d stratum/strata land within %.2f of the floor (%s).'
                  % (len(marginal), MARGIN, names))
            print('           The echo knob ALSO moved, so that margin is not attributable to the')
            print('           design change. Treat as "not shown to work", NOT a small improvement.')
            return 3
        # Echo pinned: the comparison IS controlled, so a thin margin is real but small. Saying
        # "not attributable" here would be false -- it is attributable, and modest.
        print('  VERDICT: ATTRIBUTABLE BUT THIN. The echo knob is PINNED (%s), so this is a'
              % e_cand)
        print('           controlled comparison and the movement IS the design change. %d'
              % len(marginal))
        print('           stratum/strata still land within %.2f of the floor (%s).'
              % (MARGIN, names))
        print('           Judge it on the DEFECT it repairs, not on the margin it leaves: a')
        print('           stratum measured BELOW the floor being lifted above it is a real fix;')
        print('           a stratum already above the floor gaining a little is not worth a run.')
        return 4
    print('  VERDICT: PASS. Every stratum clears the floor with margin, so the SPEND is')
    print('           justified -- and nothing after it.')
    print()
    print('           This reads ALLgold, which is RETRIEVAL DIFFICULTY. A change that makes')
    print('           gold harder to FIND and a change that makes the answer impossible to')
    print('           DERIVE move it the same way, so this gate reports both as progress.')
    print('           Only the probe sees V1, and V1 is whether the corpus still works.')
    print('           On 2026-09-13 a re-form passed here at 0.733 -> 0.200 and the probe')
    print('           then read V1 15/15 -> 10/15 (MEASUREMENT_STATUS 88.42).')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
