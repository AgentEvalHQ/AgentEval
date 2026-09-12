# -*- coding: utf-8 -*-
"""C-E stage 4 -- read the agreement run and RE-WEIGHT it. Spends nothing.

WHY A RAW RATE FROM THIS SAMPLE IS NOT A RESULT
-----------------------------------------------
The 50 cases were drawn stratified by arm and balanced on the shipped judge's own yes/no, because a
bias shows as a systematic flip in ONE direction and a cell holding no `yes` cases cannot see a
yes->no flip. That balancing DELIBERATELY over-samples the rare class: `v2` is ~0.3% yes in the live
frame and ~57% yes in the sample.

So the sample's own agreement rate answers a question nobody asked. Every number here is computed
PER CELL -- (arm, shipped-judge verdict) -- and then re-weighted by that cell's true share of the
live frame. Both are printed, and the raw one is labelled as what it is.

⚠ THE FRAME IS REBUILT HERE, AND THAT IS A DUPLICATION RISK. `build_judge_sample.py` owns the
definition of a "live" verdict (`:judge` suffix, not an abstention arm, hash present in a CURRENT
corpus, answer key present). This file replicates those rules to recover the per-cell weights the
sample file does not carry. If the two ever drift, the re-weighting is silently wrong -- so the
rebuilt frame size is asserted against `drawn_from` recorded in the sample. A drift fails loudly
instead of producing a confident number.

Usage:  python tools/ce/analyse_judge_agreement.py
"""
import collections
import glob
import hashlib
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
CACHE = os.path.join(ROOT, 'tools', '.typedmemeval_probe_cache.json')
CORPORA = os.path.join(ROOT, 'src', 'AgentEval.Memory', 'Data', 'typedmemeval')
SAMPLE = os.path.join(HERE, 'judge-sample-50.json')
RESULTS = os.path.join(HERE, 'judge-agreement-results.json')

ABSTENTION_ARMS = {'v10', 'v11'}       # their ':judge' keys hold commit/abstain, NOT yes/no


def key_for(entry):
    material = json.dumps(
        [entry["question"], entry["answer"], entry.get("question_date"),
         entry.get("haystack_sessions"), entry.get("haystack_dates")],
        sort_keys=True, ensure_ascii=False)
    return hashlib.sha256(material.encode("utf-8")).hexdigest()[:16]


def live_frame():
    index = {}
    for path in glob.glob(os.path.join(CORPORA, '*', '*-v5.json')):
        data = json.load(open(path, encoding='utf-8'))
        for e in (data if isinstance(data, list) else data.get('questions', [])):
            if isinstance(e, dict) and 'question' in e:
                index[key_for(e)] = True
    cache = json.load(open(CACHE, encoding='utf-8'))
    frame = []
    for k, v in cache.items():
        if not k.endswith(':judge'):
            continue
        arm = k.split(':')[1]
        if arm in ABSTENTION_ARMS or k.split(':')[0] not in index:
            continue
        if k[:-len(':judge')] not in cache:
            continue
        frame.append((arm, 'yes' if str(v).strip().lower().startswith('yes') else 'no'))
    return frame


def main():
    if not os.path.exists(RESULTS):
        print('no results file; run tools/ce/run_judge_agreement.py first')
        return 1
    res = json.load(open(RESULTS, encoding='utf-8'))
    sample = json.load(open(SAMPLE, encoding='utf-8'))
    models = res['models']

    frame = live_frame()
    declared = sample.get('drawn_from')
    print('C-E  provider=%s' % res.get('provider', 'openai'))
    print('claim this run can support: %s' % res.get('claim_supported', '(unrecorded)'))
    print()
    print('POSITIVE CONTROL on the rebuilt frame')
    print('  rebuilt %d live verdicts; the sample was drawn from %s' % (len(frame), declared))
    if declared is not None and len(frame) != declared:
        print('  🔴 FRAME DRIFT: the rules in this file no longer match build_judge_sample.py, or')
        print('     the corpora moved since the sample was drawn. The re-weighting below would be')
        print('     wrong, so it is not printed. Re-draw the sample, or reconcile the two files.')
        return 2
    print('  OK: the frame reproduces, so the per-cell weights below are the population shares.')

    weight = collections.Counter(frame)
    total = sum(weight.values())

    # per cell: (arm, shipped verdict) -> agreements / n, per second judge
    cells = collections.defaultdict(lambda: collections.defaultdict(lambda: [0, 0]))
    unparseable = collections.Counter()
    for row in res['cases']:
        cell = (row['arm'], row['judge1'])
        for m in models:
            v = row.get(m)
            if v == 'unparseable':
                unparseable[m] += 1
                continue
            cells[cell][m][1] += 1
            cells[cell][m][0] += (v == row['judge1'])

    print()
    print('PER CELL  (arm, shipped verdict) -- agreement with the shipped judge')
    hdr = '  %-6s %-5s %9s %8s' % ('arm', 'j1', 'pop share', 'n')
    print(hdr + ''.join('  %14s' % m for m in models))
    for cell in sorted(cells, key=lambda c: -weight.get(c, 0)):
        share = weight.get(cell, 0) / total if total else 0.0
        n = max(cells[cell][m][1] for m in models)
        line = '  %-6s %-5s %8.2f%% %8d' % (cell[0], cell[1], 100 * share, n)
        for m in models:
            a, k = cells[cell][m]
            line += '  %13s' % ('%d/%d' % (a, k) if k else '-')
        print(line)

    print()
    print('AGREEMENT WITH THE SHIPPED JUDGE')
    for m in models:
        raw_a = sum(cells[c][m][0] for c in cells)
        raw_n = sum(cells[c][m][1] for c in cells)
        num = den = 0.0
        covered = 0.0
        for c in cells:
            a, k = cells[c][m]
            if not k:
                continue
            w = weight.get(c, 0) / total if total else 0.0
            num += w * (a / k)
            den += w
            covered += w
        rew = (num / den) if den else float('nan')
        # FIVE decimals deliberately. At three, a re-weighted 0.99952 prints as "1.000" and reads as
        # PERFECT agreement while real disagreements exist -- they simply landed in cells worth a
        # fraction of a percent of the frame. Rounding a bound in the flattering direction is the
        # one thing a bound must not do, so the precision carries the difference instead.
        print('  %-14s raw %d/%d = %.3f   |   RE-WEIGHTED %.5f   (cells covering %.1f%% of the frame)'
              % (m, raw_a, raw_n, raw_a / raw_n if raw_n else float('nan'), rew, 100 * covered))
        disagreements = raw_n - raw_a
        if disagreements and rew > 0.9995:
            print('  %-14s ⚠ NOT perfect agreement: %d disagreement(s), each in a cell worth <0.2%%'
                  % ('', disagreements))
            print('  %-14s   of the frame. Re-weighted %.5f, NOT 1.0.' % ('', rew))
        if unparseable[m]:
            print('  %-14s ⚠ %d unparseable verdict(s), excluded from both figures'
                  % ('', unparseable[m]))

    print()
    print('  ⚠ Quote the RE-WEIGHTED figure. The raw one is inflated or deflated by the deliberate')
    print('    over-sampling of the rare class and is not a population rate.')
    print('  ⚠ Cells present in the frame but NOT in the sample contribute nothing; the coverage')
    print('    percentage above says how much of the frame the number actually speaks for.')

    # do the two second judges agree with EACH OTHER? that is what separates the two readings
    if len(models) == 2:
        both = [r for r in res['cases']
                if r.get(models[0]) in ('yes', 'no') and r.get(models[1]) in ('yes', 'no')]
        same = sum(1 for r in both if r[models[0]] == r[models[1]])
        codis = [r for r in both
                 if r[models[0]] == r[models[1]] and r[models[0]] != r['judge1']]
        print()
        print('THE TWO SECOND JUDGES AGAINST EACH OTHER  (this is what makes the reading decidable)')
        print('  they agree with each other on %d of %d' % (same, len(both)))
        print('  they BOTH differ from the shipped judge on %d' % len(codis))
        if codis:
            d = collections.Counter((r['judge1'], r[models[0]]) for r in codis)
            for (j1, j2), c in sorted(d.items()):
                print('      shipped=%s -> both second judges=%s : %d' % (j1, j2, c))
            print('  -> co-directional disagreement is signal ABOUT THE SHIPPED JUDGE.')
        else:
            print('  -> no case where both second judges agree with each other and differ from the')
            print('     shipped judge. No systematic flip is visible at this sample size.')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
