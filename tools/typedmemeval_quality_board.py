# -*- coding: utf-8 -*-
"""The quality board, DERIVED. Every vertical score computed from measured fields. 0 model calls.

WHY THIS EXISTS. The family publishes a per-vertical "quality score" -- mean 8.45, Episodic 7.5,
Conjunction and Procedural 9.5 -- and cites it as the headline in the plan, the README and the
status-and-plan-forward doc. Searched on 2026-09-12: there is **no rubric, no score table with a
derivation, and no tool that computes any of it**. The numbers exist only as prose assertions.

That is the family's most-cited figure resting on nothing an instrument produced -- claim-without-
instrument, on the one number a reader is most likely to quote. This file is that instrument.

WHAT IT DOES NOT DO. It does not try to reproduce 8.45. A rubric tuned until it re-derived the
hand-assigned numbers would be fitted to them, and would inherit whatever they were worth. The
criteria below were chosen for being measurable and load-bearing, then computed once. **The scores
this prints are NOT comparable to the published ones and supersede them.**

THE RUBRIC. Each shape is scored on the criteria that apply to it. Every criterion is binary, reads
a field the probe run already writes, and states its own threshold:

  C1 discriminates        headroom_perfect_selector >= 0.15  -- the shape can rank two systems.
                          ADR-028's floor; the whole point of a shape.
  C2 answerable           v1_passed / v1_applicable >= 0.90  -- a perfect selector can answer it.
                          Below this the shape is asking something its own gold cannot settle.
  C3 reachable            headroom_reachable >= 0.15         -- a REAL retriever can win some of the
                          headroom, not only a perfect one. Separates retrieval-limited from
                          reasoning-limited, which V1-V9 alone cannot.
  C4 floor disclosed      a closed-choice shape publishes chance_floor. Applies only where the
                          corpus declares one; open questions are NOT penalised for lacking it.
  C5 headroom is skill,   v9_above_chance >= 0. Applies only where a floor exists. A lexical
     not floor            baseline scoring BELOW guessing means part of the published headroom is
                          the floor rather than retrieval skill. ⚠ Failing C5 is NOT a corpus
                          defect -- the sidecar already discloses it via `v9_above_chance` and
                          `chance_floor_reading`. It is a CAVEAT on the headroom figure, scored
                          because the goal is that every published number be defensible, and a
                          headroom that is partly floor cannot be quoted bare.

  vertical score = 10 x (criteria passed / criteria applicable)

A shape declared exempt in the discrimination baseline (no gold, or a control arm) is excluded from
C1/C3 with its reason, never silently scored as a pass.

🔴 THE RUBRIC'S OWN LIMITATION, stated rather than papered over. Every criterion is a BINARY floor,
so it measures "does this shape clear every declared threshold" and NOT "by how much". A shape at
headroom 0.16 and one at 0.90 both pass C1 identically. That is deliberate -- the thresholds are
declared elsewhere (ADR-028) and this file must not invent new ones to manufacture a spread -- but it
means a 10.00 says *no floor is breached*, never *there is comfortable margin*. The `min headroom`
column carries the margin so a reader sees both, and the per-shape distribution lives in
`typedmemeval_shape_profile.py`. Do not read this score as a quality ranking between two verticals
that both clear every floor.

🔴🔴 READ THIS BEFORE QUOTING THE MEAN. **This rubric is bar-supplied.** The family's targets
("mean >= 9.0, none < 8.5") were written against the OLD hand-assigned numbers, and this file
replaces the measuring device those targets were calibrated on. A score from it clearing those
thresholds therefore **does NOT mean the targets are met** -- it means a different instrument,
written by the same agent that wanted them met, reports a different number. That is precisely the
gate self-examination failure this family exists to catch: never let the artifact under test supply
any input to its own pass mark.

What this file legitimately establishes is narrower and still worth having:
  * the published board had **no instrument at all**, which is a defect independent of any score;
  * these specific, named criterion failures are real and actionable whatever the scale
    (`forgetting/still-valid` V1 13/15; five shapes whose lexical baseline scores below chance).
**Re-anchoring the numeric targets is the maintainer's call, not this tool's.**

Usage:  python tools/typedmemeval_quality_board.py [--check] [-v]
        --check exits non-zero if any vertical is below MIN_VERTICAL or the mean below MIN_MEAN.
        ⚠ --check is a REGRESSION guard against this rubric's own thresholds; it is not evidence
        that the family's declared goals are satisfied. See the bar-supplied warning above.
"""
import collections
import glob
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(os.path.dirname(HERE), 'src', 'AgentEval.Memory', 'Data', 'typedmemeval')
BASELINE = os.path.join(HERE, 'typedmemeval-discrimination-baseline.json')

DISCRIMINATION_FLOOR = 0.15      # ADR-028
V1_FLOOR = 0.90
MIN_VERTICAL = 8.5               # the goal's per-vertical target
MIN_MEAN = 9.0                   # the goal's family target

#: SHAPES WHOSE ANSWER ASSERTS AN ABSENCE, declared here rather than detected from the run.
#:
#: For these, V1 (gold only) is NOT a valid ceiling. Gold can contain what IS; it cannot contain what
#: ISN'T, and an absence is established only by the whole record. So `headroom_perfect_selector`
#: (V1-V9) understates the shape and `headroom_reachable` (V8-V9) is the statistic that applies.
#:
#: ⚠ DECLARED FROM THE QUESTION, NEVER FROM THE MEASUREMENT. `V8 > V1` is the signature of this
#: class and is printed below as a diagnostic, but keying the RULE on it would take applicability
#: from the RESULT instead of the INPUT -- gate self-examination shape 7, the silent-`{}` defect this
#: family has already shipped once. The reason each shape is here is readable off its own question
#: text. A shape whose question asserts an absence and which does NOT show V8 > V1 stays declared:
#: that would mean the model got lucky, not that the ceiling became valid.
ABSENCE_SHAPES = {
    'forgetting/still-valid':
        'asks "say whether that is still current" -- the answer asserts that NOTHING in the record '
        'cancelled the fact. Gold holds the statement and its reaffirmation; it cannot hold the '
        'absence of a later cancellation.',
    'forgetting/never-known':
        'asks about a fact never stated. The extreme case: it has NO gold at all, which is why it '
        'is scored on abstention (V10/V11) instead. Same phenomenon, taken to its limit.',
    'prospective/not-yet-true':
        'asks whether something has become true YET -- the answer asserts the triggering event has '
        'not occurred anywhere in the record.',
}


#: THE PUBLISHED SCALE, RECOVERED. Anchored to the maintainer's own numbers, NOT to anything I chose.
#:
#: The published board (mean 8.45, Episodic 7.5 lowest, Conjunction/Procedural 9.5 highest) had no
#: written rubric -- but it was not arbitrary. Regressing the three SOURCED per-vertical scores
#: against each vertical's MEAN HEADROOM reproduces all three within 0.10:
#:
#:     episodic (pre-E1-b) 0.322 -> 7.51   published 7.5
#:     conjunction         0.754 -> 9.40   published 9.5
#:     procedural          0.800 -> 9.59   published 9.5
#:
#:     score = 6.106 + 4.362 x mean_headroom
#:
#: This matters because the criteria score above is BAR-SUPPLIED and this is not: its calibration
#: points are the maintainer's published judgements. The two disagree sharply -- the criteria score
#: ranks Episodic 8th of 10 and Conjunction 2nd, exactly inverting the published order -- because they
#: answer different questions. The criteria score asks "is every declared floor cleared"; the
#: recovered scale asks "how much room does a better system have", which is what the original tracked.
#:
#: ⚠ FRAGILITY, stated: three anchors but only TWO distinct published values, so this is effectively
#: a two-point fit. It also under-predicts the published family mean (8.25 reconstructed against 8.45
#: published), so mean headroom explains the ORDERING and the anchors but is not the whole story.
#: Treat it as the best available reconstruction of the published scale, not as a validated rubric.
RECOVERED_INTERCEPT = 6.106
RECOVERED_SLOPE = 4.362


def recovered_score(mean_headroom):
    """The published scale, reconstructed from its own anchors. See RECOVERED_INTERCEPT."""
    return RECOVERED_INTERCEPT + RECOVERED_SLOPE * mean_headroom


def exempt_shapes():
    """Shapes the family has already declared non-discriminating, with written reasons."""
    if not os.path.exists(BASELINE):
        return {}
    shapes = json.load(open(BASELINE, encoding='utf-8'))['shapes']
    return {k: v.get('reason', '') for k, v in shapes.items()
            if v.get('discriminates') is not True}


def score_shape(vertical, shape, blk, exempt):
    """Returns (passed, applicable, [(criterion, ok_or_None, detail)])."""
    key = '%s/%s' % (vertical, shape)
    is_exempt = key in exempt
    rows = []

    h = blk.get('headroom_perfect_selector')
    if is_exempt or h is None:
        rows.append(('C1 discriminates', None, 'exempt: %s' % (exempt.get(key, 'no gold') or '')[:60]))
    else:
        rows.append(('C1 discriminates', h >= DISCRIMINATION_FLOOR, 'headroom %.3f' % h))

    v1n, v1p = blk.get('v1_applicable'), blk.get('v1_passed')
    if v1n:
        rows.append(('C2 answerable', v1p / v1n >= V1_FLOOR, 'V1 %d/%d' % (v1p, v1n)))
    else:
        rows.append(('C2 answerable', None, 'no V1 (no gold)'))

    hr = blk.get('headroom_reachable')
    if is_exempt or hr is None:
        rows.append(('C3 reachable', None, 'exempt or undefined'))
    else:
        rows.append(('C3 reachable', hr >= DISCRIMINATION_FLOOR, 'reachable %.3f' % hr))

    floor = blk.get('chance_floor')
    ac = blk.get('v9_above_chance')
    if floor is None:
        rows.append(('C4 floor disclosed', None, 'open question, no floor to declare'))
        rows.append(('C5 baseline >= chance', None, 'no floor'))
    else:
        rows.append(('C4 floor disclosed', True, 'floor %.3f' % floor))
        rows.append(('C5 baseline >= chance', (ac is not None and ac >= 0),
                     'v9_above_chance %s' % ('%+.3f' % ac if ac is not None else 'MISSING')))

    passed = sum(1 for _, ok, _ in rows if ok is True)
    applicable = sum(1 for _, ok, _ in rows if ok is not None)
    return passed, applicable, rows


def board():
    exempt = exempt_shapes()
    out = {}
    for corpus in sorted(glob.glob(os.path.join(ROOT, '*', '*-v5.json'))):
        vertical = os.path.basename(os.path.dirname(corpus))
        meta = corpus.replace('-v5.json', '-v5.meta.json')
        if not os.path.exists(meta):
            continue
        by_shape = (json.load(open(meta, encoding='utf-8')).get('probes') or {}).get('by_shape') or {}
        if not by_shape:
            # NEVER SILENTLY SKIP. A vertical whose sidecar carries no probe results used to drop
            # out of this board entirely, and the mean was then computed over the SURVIVORS -- it
            # printed "mean 9.32 over 9 verticals" with a tenth vertical sitting unmeasured in the
            # tree. That is the diluted-denominator defect in the instrument that exists to catch it.
            # Recorded as unmeasured so the caller must deal with it.
            out[vertical] = {'unmeasured': True}
            continue
        shapes = {}
        tp = ta = 0
        for shape, blk in sorted(by_shape.items()):
            p, a, rows = score_shape(vertical, shape, blk, exempt)
            shapes[shape] = (p, a, rows)
            tp += p
            ta += a
        beaten = {}
        for shape, blk in sorted(by_shape.items()):
            n1, p1 = blk.get('v1_applicable'), blk.get('v1_passed')
            n8, p8 = blk.get('v8_applicable'), blk.get('v8_passed')
            n9, p9 = blk.get('v9_applicable'), blk.get('v9_passed')
            if n1 and n8 and n9 and p8 / n8 > p1 / n1 + 1e-9:
                beaten[shape] = (p1 / n1, p8 / n8, p9 / n9)
        hs = [b.get('headroom_perfect_selector') for b in by_shape.values()
              if b.get('headroom_perfect_selector') is not None]
        # MEAN HEADROOM APPLIES THE ABSENCE-SHAPE CEILING (§88.16). For a shape whose answer asserts
        # an absence, V1 is not a valid ceiling -- gold cannot hold an absence -- so V1-V9 understates
        # it and V8-V9 is the statistic that applies. The rule was declared and then NOT applied
        # here, which left `forgetting` and `prospective` reported against a ceiling this file itself
        # says is wrong for them.
        #
        # ⚠ THIS CORRECTION MOVES THE HEADLINE UP (family 8.39 -> 8.43, forgetting 7.23 -> 7.52,
        # prospective 8.12 -> 8.26), which is the FLATTERING direction and therefore the one to
        # justify hardest. It is applied because the rule is independently established and keyed on
        # the QUESTION, not because of where it moves the number -- and it changes NO verdict: six
        # verticals are below 8.5 before and after.
        # EXEMPT SHAPES ARE EXCLUDED FROM THE VERTICAL MEAN. A shape the family has DECLARED
        # non-discriminating (a control arm, or one with no gold) is not trying to rank systems, so
        # averaging it into the vertical's discriminating-power mean understates the vertical. It
        # bites exactly one vertical -- `forgetting`, whose `still-valid` control is deliberately
        # easy -- and it moved its mean headroom 0.325 -> 0.450, i.e. from "below par" to "at par".
        # Same class as the absence-ceiling error just above: reading a number against a shape that
        # the design says should not produce it.
        ceil = []
        for shape, b in by_shape.items():
            h = b.get('headroom_perfect_selector')
            if h is None:
                continue
            key = '%s/%s' % (vertical, shape)
            if key in exempt:
                continue
            hr = b.get('headroom_reachable')
            ceil.append(hr if (key in ABSENCE_SHAPES and hr is not None) else h)
        mean_h = (sum(ceil) / len(ceil)) if ceil else None
        with open(corpus, encoding='utf-8') as fh:
            _entries = json.load(fh)
        _dep = [len([g for g in (q.get('answer_session_ids') or [])
                     if g in (q.get('haystack_session_ids') or [])])
                for q in _entries]
        _dep = [x for x in _dep if x]
        mean_depth = (sum(_dep) / len(_dep)) if _dep else None
        out[vertical] = {'score': 10.0 * tp / ta if ta else float('nan'),
                         'passed': tp, 'applicable': ta, 'shapes': shapes,
                         'beaten': beaten,
                         'mean_depth': mean_depth,
                         'mean_headroom': mean_h,
                         'recovered': recovered_score(mean_h) if mean_h is not None else None,
                         'min_headroom': min(hs) if hs else None}
    return out


def main():
    check = '--check' in sys.argv
    verbose = '-v' in sys.argv or '--verbose' in sys.argv
    b = board()
    if not b:
        print('no probed sidecars found under %s' % ROOT)
        return 1

    unmeasured = sorted(v for v, d in b.items() if d.get('unmeasured'))
    b = {v: d for v, d in b.items() if not d.get('unmeasured')}
    if unmeasured:
        print('🔴 UNMEASURED VERTICALS -- corpus present, NO probe results in the sidecar:')
        for v in unmeasured:
            print('     %s' % v)
        print('   Every figure below is computed over the REMAINING %d verticals and is not a'
              % len(b))
        print('   family number. Re-probe before quoting anything here.')
        print()

    print('TYPEDMEMEVAL QUALITY BOARD, derived  (0 model calls)')
    print('  score = 10 x (criteria passed / criteria applicable); rubric in this file\'s docstring')
    print()
    print('  %-15s %7s %10s %8s %9s   %s'
          % ('vertical', 'score', 'passed', 'shapes', 'min head', 'failing criteria'))
    scores = []
    for vertical, d in sorted(b.items(), key=lambda kv: kv[1]['score']):
        fails = []
        for shape, (p, a, rows) in sorted(d['shapes'].items()):
            for name, ok, detail in rows:
                if ok is False:
                    fails.append('%s %s (%s)' % (shape, name.split()[0], detail))
        scores.append(d['score'])
        mh = d.get('min_headroom')
        print('  %-15s %7.2f %10s %8d %9s   %s'
              % (vertical, d['score'], '%d/%d' % (d['passed'], d['applicable']),
                 len(d['shapes']), ('%+.3f' % mh) if mh is not None else '-',
                 '; '.join(fails[:2]) + ('; ...' if len(fails) > 2 else '')))

    mean = sum(scores) / len(scores)
    below = [v for v, d in b.items() if d['score'] < MIN_VERTICAL]

    # THE SECOND READING: the published scale, recovered from its own anchors rather than chosen.
    print()
    print('  THE PUBLISHED SCALE, RECOVERED (score = %.3f + %.3f x mean headroom; anchors within 0.10)'
          % (RECOVERED_INTERCEPT, RECOVERED_SLOPE))
    print('  %-15s %14s %10s' % ('vertical', 'mean headroom', 'recovered'))
    rec = []
    for vert, d in sorted(b.items(), key=lambda kv: (kv[1]['recovered'] is None,
                                                     kv[1]['recovered'] or 0)):
        if d['recovered'] is None:
            continue
        rec.append(d['recovered'])
        print('  %-15s %14.3f %10.2f %s'
              % (vert, d['mean_headroom'], d['recovered'],
                 '' if d['recovered'] >= MIN_VERTICAL else '*** BELOW %.1f ***' % MIN_VERTICAL))
    if rec:
        rbelow = [v for v, d in b.items()
                  if d['recovered'] is not None and d['recovered'] < MIN_VERTICAL]
        print('  recovered mean %.2f (target >=%.1f); below %.1f: %s'
              % (sum(rec) / len(rec), MIN_MEAN, MIN_VERTICAL,
                 ', '.join(sorted(rbelow)) if rbelow else 'none'))
        print("  ^ NOT bar-supplied: calibrated on the maintainer's own published numbers. It")
        print('    disagrees sharply with the criteria score above because they ask different')
        print('    questions -- floors cleared, versus room a better system has.')
    print()
    print('  mean %.2f over %d verticals   |   below %.1f: %s'
          % (mean, len(scores), MIN_VERTICAL, ', '.join(sorted(below)) if below else 'none'))

    if verbose:
        for vertical, d in sorted(b.items()):
            print()
            print('  %s' % vertical)
            for shape, (p, a, rows) in sorted(d['shapes'].items()):
                print('    %-24s %d/%d' % (shape, p, a))
                for name, ok, detail in rows:
                    mark = {True: 'pass', False: 'FAIL', None: ' -- '}[ok]
                    print('        %-22s %-4s %s' % (name, mark, detail))

    print()
    # THE DIAGNOSTIC for C2 failures: a perfect gold selector BEATEN by the full haystack is
    # impossible unless the answer needs information gold cannot hold.
    beaten = [(v, shape) + vals
              for v, d in sorted(b.items())
              for shape, vals in sorted(d.get('beaten', {}).items())]
    if beaten:
        print()
        print('  V8 > V1 -- the full haystack BEATS a perfect gold selector. Impossible unless the')
        print('  answer needs what gold cannot hold, so V1 is not a valid ceiling for these shapes:')
        for vertical, shape, r1, r8, r9 in beaten:
            key = '%s/%s' % (vertical, shape)
            declared = key in ABSENCE_SHAPES
            print('      %-13s %-20s V1 %.3f  V8 %.3f  |  V1-V9 %+.3f  V8-V9 %+.3f'
                  % (vertical, shape, r1, r8, r1 - r9, r8 - r9))
            if not declared:
                print('        🔴 NOT declared in ABSENCE_SHAPES -- either the question asserts an')
                print('           absence and belongs there, or something else is wrong. Do not')
                print('           add it to silence this line; read the question first.')
            elif (r1 - r9) < DISCRIMINATION_FLOOR <= (r8 - r9):
                print('        ⚠ declared non-discriminating on V1-V9 = %.3f, but V8-V9 = %.3f'
                      % (r1 - r9, r8 - r9))
                print('          CLEARS the %.2f floor. The exemption is an artefact of the wrong'
                      % DISCRIMINATION_FLOOR)
                print('          ceiling, not a property of the shape.')

    # DEPTH-ADJUSTED RESIDUAL: the part of a vertical's headroom that is about QUALITY rather than
    # about what it measures. 47% of the raw spread is construct depth (§88.31) -- a distance ladder
    # needs gold depth 1 and a multi-hop join needs 4-5, so comparing their raw headroom compares
    # constructs. Regressing headroom on depth and reading the RESIDUAL removes that confound.
    pts = [(v, d['mean_depth'], d['mean_headroom']) for v, d in b.items()
           if d.get('mean_depth') and d.get('mean_headroom') is not None]
    if len(pts) >= 4:
        n_ = len(pts)
        mdp = sum(p[1] for p in pts) / n_
        mhr = sum(p[2] for p in pts) / n_
        sxx_ = sum((p[1] - mdp) ** 2 for p in pts)
        slp = sum((p[1] - mdp) * (p[2] - mhr) for p in pts) / sxx_ if sxx_ else 0.0
        itc = mhr - slp * mdp
        resid = [(v, hh - (itc + slp * dd)) for v, dd, hh in pts]
        sig = (sum(r * r for _, r in resid) / max(1, n_ - 2)) ** 0.5
        print()
        print('  DEPTH-ADJUSTED QUALITY  (headroom = %.3f + %.3f x depth; sigma %.3f)'
              % (itc, slp, sig))
        print('  Raw headroom is ~47% construct. The residual is the part that is about quality.')
        print('  %-15s %7s %10s %9s %7s' % ('vertical', 'depth', 'headroom', 'residual', 'z'))
        for v, r in sorted(resid, key=lambda x: x[1]):
            dd = dict((p[0], p[1]) for p in pts)[v]
            hh = dict((p[0], p[2]) for p in pts)[v]
            z = r / sig if sig else 0.0
            tag = ''
            if z <= -2:
                tag = '  *** BELOW PAR for its construct ***'
            elif z >= 2:
                tag = '  best-in-class for its construct'
            print('  %-15s %7.2f %10.3f %+9.3f %7.2f%s' % (v, dd, hh, r, z, tag))
        # NAMED `below_par`, never `below`. The first version of this block called it `below` and
        # SHADOWED the criteria-score list built above -- so `--check` read the residual list
        # (empty) and printed "every vertical >= 8.5" while `forgetting` sat at 7.50. A gate turned
        # green by a variable name, introduced by the block that was meant to make the board more
        # honest. Caught because the headline and the check disagreed in the same run.
        below_par = [v for v, r in resid if (r / sig if sig else 0) <= -2]
        print('  below par beyond 2 sigma: %s'
              % (', '.join(sorted(below_par)) if below_par else 'NONE'))

    thin = sorted((v, d['min_headroom']) for v, d in b.items()
                  if d.get('min_headroom') is not None and d['min_headroom'] < 0.35)
    if thin:
        print()
        print('  ⚠ MARGIN, which the score cannot show: these clear every floor but not by much.')
        for v, m in thin:
            print('      %-15s thinnest shape headroom %+.3f (floor %.2f)'
                  % (v, m, DISCRIMINATION_FLOOR))
    print()
    print('  ⚠ These scores are DERIVED and supersede the hand-assigned board (mean 8.45, Episodic')
    print('    7.5, Conjunction/Procedural 9.5), which no rubric, table or tool ever produced. They')
    print('    are NOT comparable to it: this rubric was not fitted to reproduce those numbers.')
    print()
    print('  🔴 BAR-SUPPLIED. This rubric was written by the same agent working toward the targets')
    print('     it is measured against, and it replaces the device those targets were calibrated on.')
    print("     Clearing them here is NOT evidence the family's goals are met. What IS established:")
    print('     the published board had no instrument, and the named criterion failures above are')
    print("     real whatever the scale. Re-anchoring the targets is the maintainer's call.")

    if check:
        problems = []
        if unmeasured:
            problems.append('%d vertical(s) carry a corpus with NO probe results: %s'
                            % (len(unmeasured), ', '.join(unmeasured)))
        if below:
            problems.append('%d vertical(s) below %.1f: %s'
                            % (len(below), MIN_VERTICAL, ', '.join(sorted(below))))
        if mean < MIN_MEAN:
            problems.append('mean %.2f is below the %.1f target' % (mean, MIN_MEAN))
        print()
        if problems:
            for p in problems:
                print('FAIL: %s' % p)
            return 2
        print('OK: every vertical >= %.1f and mean %.2f >= %.1f' % (MIN_VERTICAL, mean, MIN_MEAN))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
