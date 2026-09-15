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
import argparse
import collections
import glob
import hashlib
import json
import os
import sys

# A GATE MUST NOT DIE ON ITS OWN WARNING TEXT. Windows hands a bare `python x.py` a cp1252 stdout
# that cannot encode the markers these findings are written with, and every line carrying one sits
# on a branch that fires only when something is WRONG -- so the tool runs green for as long as it
# has nothing to say and dies mid-report the first time it does. Fixed in the quality board and the
# shape profile on 2026-09-13 and NOT carried here, which is the applied-once shape this project
# keeps finding: a right treatment with too small a reach.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
CACHE = os.path.join(ROOT, 'tools', '.typedmemeval_probe_cache.json')
CORPORA = os.path.join(ROOT, 'src', 'AgentEval.Memory', 'Data', 'typedmemeval')
SAMPLE = os.path.join(HERE, 'judge-sample-50.json')
RESULTS = os.path.join(HERE, 'judge-agreement-results.json')

# ABSTENTION_ARMS was removed on 2026-09-14. It listed v10/v11 as the arms to EXCLUDE, and became
# dead the moment the frame switched to SHIPPED_ARMS below -- but a constant that still reads like
# a live rule is worse than no constant, because the next reader assumes something enforces it.
# v10/v11 are still excluded; they are simply not in the allow-list, which is the whole point.

#: The arms a shipped corpus is actually accepted on. An ALLOW-list, not a deny-list, and the
#: difference is not stylistic: the frame used to be "any `:judge` key whose arm is not an
#: abstention arm", which admits every arm anyone ever adds to the shared probe cache. On
#: 2026-09-14 an experimental `v9dense` arm (the retriever-sensitivity work) put 185 verdicts into
#: the frame silently, changing the population the C-E re-weighting is computed against. A deny-list
#: cannot refuse what it has never heard of.
SHIPPED_ARMS = {'v1', 'v2', 'v3', 'v6', 'v8', 'v9'}

#: Arms whose absence from the frame is the DESIGN, not a drift.
#:
#: v10/v11 are the abstention arms (`v10_full_haystack`, `v11_reference_retrieval`). They are
#: scored on whether the model declined, not on whether a judge called two answers equivalent, so
#: there is no judge verdict of theirs that could belong in a judge-agreement population.
#:
#: They are listed rather than silently dropped because the banner below used to fire on them every
#: run, telling the reader to "tell this file about a new shipped arm, or keep the experiment out of
#: the shared probe cache" -- two wrong actions for a permanent and correct exclusion. A warning
#: that is always on is a warning nobody reads, which is precisely the state that let v9dense in.
EXPECTED_ABSENT = {'v10', 'v11'}


def key_for(entry):
    material = json.dumps(
        [entry["question"], entry["answer"], entry.get("question_date"),
         entry.get("haystack_sessions"), entry.get("haystack_dates")],
        sort_keys=True, ensure_ascii=False)
    return hashlib.sha256(material.encode("utf-8")).hexdigest()[:16]


def frame_fingerprint(rows) -> str:
    """A hash over the frame's (key, verdict) pairs -- what the re-weighting actually depends on.

    Not the size. Two populations of 5,254 verdicts are the same size and can be entirely different
    verdicts, and the per-cell shares would move with them while a count check said nothing. Verdicts
    are included as well as keys because a re-judged cache changes the weights without changing the
    membership.
    """
    payload = '\n'.join('%s\t%s' % (k, v) for k, v in sorted(rows))
    return hashlib.sha256(payload.encode('utf-8')).hexdigest()  # DevSkim: ignore DS126858


def live_frame():
    excluded = {}
    identified = []
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
        if k.split(':')[0] not in index or k[:-len(':judge')] not in cache:
            continue
        if arm not in SHIPPED_ARMS:
            # Counted and named rather than dropped in silence: an arm appearing here is either a
            # new shipped arm this file has not been told about, or an experiment leaking into the
            # frame. Both need a human to look; neither should change the population quietly.
            excluded[arm] = excluded.get(arm, 0) + 1
            continue
        verdict = 'yes' if str(v).strip().lower().startswith('yes') else 'no'
        frame.append((arm, verdict))
        # The KEY is kept alongside, so the population can be identified and not merely counted.
        identified.append((k, verdict))
    return frame, excluded, identified


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--accept-unverified-population', action='store_true',
                    help='print the re-weighted figures for a sample carrying no frame '
                         'fingerprint. The claim narrows to "re-weighted against a population '
                         'whose identity was not verified" -- say that wherever the number goes.')
    args = ap.parse_args()
    if not os.path.exists(RESULTS):
        print('no results file; run tools/ce/run_judge_agreement.py first')
        return 1
    res = json.load(open(RESULTS, encoding='utf-8'))
    sample = json.load(open(SAMPLE, encoding='utf-8'))
    models = res['models']

    frame, excluded, identified = live_frame()
    declared = sample.get('drawn_from')
    print('C-E  provider=%s' % res.get('provider', 'openai'))
    print('claim this run can support: %s' % res.get('claim_supported', '(unrecorded)'))
    print()
    print('POSITIVE CONTROL on the rebuilt frame')
    print('  rebuilt %d live verdicts; the sample was drawn from %s' % (len(frame), declared))
    expected = {a: n for a, n in excluded.items() if a in EXPECTED_ABSENT}
    unexpected = {a: n for a, n in excluded.items() if a not in EXPECTED_ABSENT}
    if expected:
        print('  abstention arms, excluded by design: %s'
              % ', '.join('%s=%d' % kv for kv in sorted(expected.items())))
    if unexpected:
        print('  \U0001f534 UNRECOGNISED arms in the shared probe cache: %s'
              % ', '.join('%s=%d' % kv for kv in sorted(unexpected.items())))
        print('    They are kept OUT of the frame, which is the safe direction, but something is')
        print('    writing judge verdicts here that nothing in this file knows about. Either tell')
        print('    this file about a new shipped arm, or keep the experiment out of the shared')
        print('    cache. Silence here is how a population changes unnoticed.')
    if declared is not None and len(frame) != declared:
        print('  🔴 FRAME DRIFT: the rules in this file no longer match build_judge_sample.py, or')
        print('     the corpora moved since the sample was drawn. The re-weighting below would be')
        print('     wrong, so it is not printed. Re-draw the sample, or reconcile the two files.')
        return 2

    # A COUNT IS NOT A REPRODUCTION. The check above passes for any two populations of the same
    # SIZE, and the per-cell weights depend on composition. Two stronger operands, in order of what
    # they can prove:
    #
    #   MEMBERSHIP -- every sampled case must still be in the frame. Works on a sample drawn before
    #   any of this existed, because the sample records its own cache keys.
    #
    #   FINGERPRINT -- a hash over the frame's (key, verdict) pairs, recorded by the sampler. Pins
    #   the whole population, not just the sampled part of it.
    # THE LAST UNPINNED LINK. The chain is frame -> sample -> RESULTS, and the checks below bind
    # the first two. Nothing bound the third: the results file is a separate artifact from a
    # separate run, so a sample redrawn while these results are stale passes membership and the
    # fingerprint while the figures come from rows belonging to another draw. Review of PR #251.
    # MULTISETS, NOT SETS. A set discards multiplicity, so a results file with one row DUPLICATED
    # and another OMITTED compares equal to the sample while the loop below counts the duplicate
    # twice and publishes figures over the wrong denominator. The binding has to reject a malformed
    # artifact, not only a foreign one. Review of PR #251.
    res_rows = collections.Counter((c['cache_key'], c['judge1']) for c in res['cases'])
    sample_rows = collections.Counter((c['cache_key'], c['judge1']) for c in sample['cases'])
    if res_rows != sample_rows:
        extra = res_rows - sample_rows
        short = sample_rows - res_rows
        fmt = lambda m: ', '.join('%s x%d' % (k, n) for (k, _), n in sorted(m.items())[:3]) or 'none'
        print('  🔴 THE RESULTS FILE IS NOT THIS SAMPLE: %d row(s) too many, %d missing.'
              % (sum(extra.values()), sum(short.values())))
        print('     too many: %s' % fmt(extra))
        print('     missing : %s' % fmt(short))
        print('     The agreement figures would be computed over rows this sample does not have,')
        print('     or count one of its rows twice. Re-run the judge agreement against this sample.')
        return 2

    live_verdict = dict(identified)
    live_keys = set(live_verdict)
    missing = sorted(c['cache_key'] for c in sample['cases'] if c['cache_key'] not in live_keys)
    if missing:
        print('  🔴 %d of %d SAMPLED CASES ARE NOT IN THE REBUILT FRAME, e.g. %s.'
              % (len(missing), len(sample['cases']), ', '.join(missing[:3])))
        print('     The sample was drawn from a population this run cannot reproduce, so the')
        print('     per-cell weights are not this frame\'s shares. Re-draw the sample.')
        return 2

    # MEMBERSHIP CANNOT SEE A VERDICT FLIP. Each case is grouped under the `judge1` it carried AT
    # DRAW TIME and weighted by the cell it is in NOW, so a verdict that changed since the draw
    # leaves the case straddling a seam with its key still present. Review of PR #251.
    flipped = sorted(c['cache_key'] for c in sample['cases']
                     if live_verdict.get(c['cache_key']) != c['judge1'])
    if flipped:
        print('  🔴 %d SAMPLED VERDICT(S) CHANGED SINCE THE DRAW, e.g. %s.'
              % (len(flipped), ', '.join(flipped[:3])))
        print('     Those cases are grouped under the verdict they had when sampled and weighted')
        print('     by the cell they are in now, so the re-weighting would cross a seam.')
        print('     Re-draw the sample.')
        return 2

    actual = frame_fingerprint(identified)
    stamped = sample.get('drawn_from_fingerprint')
    if stamped and stamped != actual:
        print('  🔴 FRAME FINGERPRINT MISMATCH: sample %s, rebuilt %s.'
              % (stamped[:12], actual[:12]))
        print('     Same SIZE, different population or different verdicts. The re-weighting below')
        print('     would be computed against shares this frame does not have.')
        return 2

    print('  OK: all %d sampled cases are present in the rebuilt frame.' % len(sample['cases']))
    if stamped:
        print('  OK: frame fingerprint %s matches, so the population is the one sampled.'
              % actual[:12])
    else:
        # A WARNING DOES NOT STOP A PUBLICATION. Membership and the sampled verdicts are verified
        # above; what remains unverifiable without a fingerprint is whether UNSAMPLED verdicts
        # moved -- and those set the per-cell shares the re-weighting multiplies by. Printing the
        # figures under a caution reads exactly like a verified re-weighting, which is the thing
        # to avoid. The block is withheld unless the caller asks for it by name. Review of #251.
        print('  ⚠ This sample predates the frame fingerprint (%s), so the whole-population'
              % actual[:12])
        print('    identity is UNVERIFIED. Membership and sampled verdicts are checked above and')
        print('    both hold; what cannot be checked is whether UNSAMPLED verdicts moved, and')
        print('    those set the per-cell shares the re-weighting multiplies by.')
        if not args.accept_unverified_population:
            print()
            print('  RE-WEIGHTED FIGURES WITHHELD. Re-draw the sample to record a fingerprint, or')
            print('  pass --accept-unverified-population to print them with the claim narrowed to')
            print('  "re-weighted against a population whose identity was not verified".')
            return 3

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
    if not sample.get('drawn_from_fingerprint'):
        print('  🔴 POPULATION IDENTITY UNVERIFIED: these figures are re-weighted against a')
        print('     frame whose unsampled verdicts were never pinned. Carry that sentence with the')
        print('     number, or re-draw the sample.')
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
