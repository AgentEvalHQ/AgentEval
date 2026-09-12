# -*- coding: utf-8 -*-
"""What the V9 arm actually measures, and therefore what headroom actually is. ZERO model calls.

THE FINDING. Across all 35 headroom-bearing shapes, the published V9 pass rate equals the rate at
which BM25's top-K_ref contains ALL of a question's gold sessions -- not merely one of them:

    median |V9_rate - ALLgold_retrieval| = 0.000      27 of 35 shapes match to three decimals
    slope 0.887, intercept 0.069, R^2 0.818

V9 is not measuring reasoning under a lexical baseline. It is measuring whether a fixed K_ref budget
happened to hold the whole gold set. Since V1 sits at or near 1.0 on almost every shape, the
published headroom is, to within noise,

    headroom = V1 - V9  ~=  1 - ALLgold_retrieval

which makes headroom a statement about a RETRIEVAL BUDGET first, and about shape design only second.

WHY THIS MATTERS FOR `episodic/participant-attribution` (headroom -0.067, the shape that blocks
Episodic reaching 8.5). It is the ONLY shape in the family at ALLgold = 1.00. BM25 with K_ref=5
already holds every gold session on all 15 questions, so V9 saturates and there is nothing left for a
better retriever to win. The deficit is fully accounted for by this identity; no appeal to the
question's wording is required.

THIS CORRECTED AN OPERAND OF MY OWN. An earlier version of this script scored retrieval as "ANY gold
session in the top-K" (bool(gold & top)). That operand correlates with the V9 rate at -0.525 --
anti-predictive -- because for a shape needing 4 sessions, holding 1 is a failure it scored as a
success. It overstated retrieval by >=0.30 on 18 of 35 shapes. Three hypotheses about
participant-attribution were tested against that broken column (MEASUREMENT_STATUS 88.11); the
column, not merely the hypotheses, was wrong. The runner had it right all along -- it records
`v9_gold_in_context` as a COUNT (run_typedmemeval_probes.py).

FOUR SHAPES DO NOT FIT and are declared rather than smoothed over; see EXCEPTIONS below. A residual
above +0.20 means the arm scores better than its retrieval, i.e. the question is answerable from
partial gold -- itself worth knowing about a shape.

WHAT IT IS FOR. `headroom ~= 1 - ALLgold` is computable from a corpus with no model calls, so a
proposed shape revision can be checked for discriminating power BEFORE it is generated and BEFORE a
probe run is paid for. Run it against a candidate corpus and read the predicted headroom off the
retrieval column.

Usage:  python tools/typedmemeval_shape_profile.py [--check]
        --check exits non-zero if the identity stops holding or the exception set drifts.
"""
import collections
import glob
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import typedmemeval_common as tmc  # noqa: E402

ROOT = os.path.join(os.path.dirname(HERE), 'src', 'AgentEval.Memory', 'Data', 'typedmemeval')

#: Shapes whose V9 rate departs from ALLgold retrieval by more than this are NOT explained by the
#: identity and must be declared individually. Chosen as ~2 questions on the smallest shapes (n=6).
FIT_TOLERANCE = 0.20

#: The four that do not fit, with the reason each is exempt. Anything entering or leaving this set is
#: a drift that --check reports: the identity is a published claim, so its exceptions are too.
EXCEPTIONS = {
    ('conjunction', 'order-then-value'):
        'V9 0.60 vs ALLgold 0.20. The order half is answerable from the value half alone, so the '
        'arm scores without holding both gold sessions -- a partial-credit route, not a retrieval win.',
    ('prospective', 'not-yet-true'):
        'V9 0.33 vs ALLgold 0.67. The only shape scoring BELOW its retrieval: holding the gold is '
        'not sufficient, so this shape is reasoning-limited rather than retrieval-limited. n=6.',
    ('forgetting', 'still-valid'):
        'V9 0.80 vs ALLgold 0.53. Answerable from partial gold, which is why its headroom (0.067) '
        'sits below the discrimination floor for a DIFFERENT reason than participant-attribution.',
}

#: NOT an exception, and deliberately recorded as such: `temporal/occurrence-order` sits at residual
#: exactly +0.20, i.e. ON the tolerance boundary and therefore inside it. I declared it at first and
#: --check rejected the declaration as stale, which is the gate doing its job against its own author.
#: It is the shape most likely to cross on any corpus revision, so expect it here first.


def session_text(session):
    return ' '.join(t.get('content', '') for t in session)


def profile():
    rows = []
    for corpus in sorted(glob.glob(os.path.join(ROOT, '*', '*-v5.json'))):
        vertical = os.path.basename(os.path.dirname(corpus))
        meta = corpus.replace('-v5.json', '-v5.meta.json')
        if not os.path.exists(meta):
            continue
        with open(corpus, encoding='utf-8') as fh:
            entries = json.load(fh)
        with open(meta, encoding='utf-8') as fh:
            by_shape = (json.load(fh).get('probes') or {}).get('by_shape') or {}

        groups = collections.defaultdict(list)
        for e in entries:
            groups[(e.get('typedmemeval') or {}).get('shape')].append(e)

        for shape, group in sorted(groups.items()):
            blk = by_shape.get(shape) or {}
            headroom = blk.get('headroom_perfect_selector')
            if headroom is None or not blk.get('v9_applicable'):
                continue           # no-gold shapes are scored on abstention (V10/V11) instead
            any_hit = all_hit = n = over = 0
            depths = []
            for e in group:
                ids = e.get('haystack_session_ids') or []
                gold = [g for g in (e.get('answer_session_ids') or []) if g in ids]
                if not gold:
                    continue
                n += 1
                depths.append(len(gold))
                over += len(gold) > tmc.K_REF
                texts = [session_text(s) for s in e['haystack_sessions']]
                gidx = {ids.index(g) for g in gold}
                top = set(tmc.bm25_rank(e['question'], texts)[:tmc.K_REF])
                any_hit += bool(gidx & top)
                all_hit += gidx.issubset(top)
            if not n:
                continue
            rows.append({
                'vertical': vertical, 'shape': shape, 'n': n,
                'any_gold': any_hit / n, 'all_gold': all_hit / n,
                'depth_median': sorted(depths)[len(depths) // 2], 'depth_max': max(depths),
                'over_budget': over,
                'v1': blk['v1_passed'] / blk['v1_applicable'] if blk.get('v1_applicable') else None,
                'v9': blk['v9_passed'] / blk['v9_applicable'],
                'headroom': headroom, 'discriminates': blk.get('discriminates'),
            })
    return rows


def fit(xs, ys):
    n = len(xs)
    mx, my = sum(xs) / n, sum(ys) / n
    sxx = sum((a - mx) ** 2 for a in xs)
    slope = sum((a - mx) * (b - my) for a, b in zip(xs, ys)) / sxx if sxx else float('nan')
    inter = my - slope * mx
    ss_tot = sum((b - my) ** 2 for b in ys)
    ss_res = sum((b - (slope * a + inter)) ** 2 for a, b in zip(xs, ys))
    return slope, inter, (1 - ss_res / ss_tot if ss_tot else float('nan'))


def main():
    check = '--check' in sys.argv
    rows = profile()
    if not rows:
        print('no headroom-bearing shapes found under %s' % ROOT)
        return 1

    print('WHAT V9 MEASURES  (K_ref=%d, %d shapes, 0 model calls)' % (tmc.K_REF, len(rows)))
    print()
    print('  %-13s %-22s %4s %7s %8s %6s %5s %7s %9s'
          % ('vertical', 'shape', 'n', 'ANYgold', 'ALLgold', 'V9', 'depth', 'resid', 'headroom'))
    for r in sorted(rows, key=lambda r: r['all_gold']):
        resid = r['v9'] - r['all_gold']
        off = abs(resid) > FIT_TOLERANCE
        print('  %-13s %-22s %4d %7.2f %8.2f %6.2f %5d %+7.2f %9.3f%s'
              % (r['vertical'], r['shape'], r['n'], r['any_gold'], r['all_gold'], r['v9'],
                 r['depth_median'], resid, r['headroom'],
                 '  <- declared exception' if off else ''))

    xs = [r['all_gold'] for r in rows]
    ys = [r['v9'] for r in rows]
    dev = sorted(abs(a - b) for a, b in zip(xs, ys))
    slope, inter, r2 = fit(xs, ys)
    exact = sum(1 for a, b in zip(xs, ys) if abs(a - b) < 0.005)

    print()
    print('THE IDENTITY:  V9_rate == rate at which BM25 top-%d holds ALL gold' % tmc.K_REF)
    print('  median |V9 - ALLgold|   %.3f' % dev[len(dev) // 2])
    print('  mean   |V9 - ALLgold|   %.3f' % (sum(dev) / len(dev)))
    print('  exact matches           %d of %d shapes' % (exact, len(rows)))
    print('  least squares           slope %+.3f  intercept %+.3f  R^2 %.3f' % (slope, inter, r2))
    print()
    print('  => V1 is at or near 1.0 on most shapes, so  headroom = V1 - V9 ~= 1 - ALLgold.')
    print('     Headroom is a statement about the K_ref BUDGET first and shape design second.')

    # my own broken operand, kept as a standing warning rather than deleted
    axs = [r['any_gold'] for r in rows]
    aslope, ainter, ar2 = fit(axs, ys)
    print()
    print('  the ANY-gold operand, for contrast: slope %+.3f  R^2 %.3f  (anti-predictive)'
          % (aslope, ar2))
    overstated = sum(1 for r in rows if r['any_gold'] - r['all_gold'] >= 0.30)
    print('  ANY-gold overstates retrieval by >=0.30 on %d of %d shapes.' % (overstated, len(rows)))

    print()
    sat = [r for r in rows if r['all_gold'] >= 0.999]
    print('SATURATED SHAPES  (ALLgold = 1.00 -> V9 saturates -> no headroom left to win):')
    for r in sat:
        print('  %-13s %-22s V1 %.2f  V9 %.2f  headroom %+.3f  discriminates=%s'
              % (r['vertical'], r['shape'], r['v1'], r['v9'], r['headroom'], r['discriminates']))
    if not sat:
        print('  none')

    print()
    off = {(r['vertical'], r['shape']) for r in rows if abs(r['v9'] - r['all_gold']) > FIT_TOLERANCE}
    print('EXCEPTIONS  (|residual| > %.2f), each declared:' % FIT_TOLERANCE)
    for key in sorted(off):
        print('  %-13s %-22s %s' % (key[0], key[1], EXCEPTIONS.get(key, 'UNDECLARED')))

    rc = 0
    if check:
        missing = off - set(EXCEPTIONS)
        stale = set(EXCEPTIONS) - off
        if missing:
            print()
            print('FAIL: %d shape(s) now depart from the identity with no declared reason: %s'
                  % (len(missing), sorted(missing)))
            rc = 2
        if stale:
            print()
            print('FAIL: %d declared exception(s) now fit; the declaration is stale: %s'
                  % (len(stale), sorted(stale)))
            rc = 2
        if dev[len(dev) // 2] > 0.05:
            print()
            print('FAIL: median |V9 - ALLgold| rose to %.3f; the identity no longer holds.'
                  % dev[len(dev) // 2])
            rc = 2
        if rc == 0:
            print()
            print('OK: identity holds (median %.3f) and the exception set is exactly as declared.'
                  % dev[len(dev) // 2])
    return rc


if __name__ == '__main__':
    raise SystemExit(main())
