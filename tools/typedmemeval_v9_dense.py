# -*- coding: utf-8 -*-
"""MEASURE what §88.38 predicted: does V9 change when the retriever does?

WHY THIS EXISTS
---------------
§88.38 predicted that 8 of 35 shapes stop discriminating under a dense retriever, from
`1 - ALLgold`. That predictor is good -- slope +0.905, R^2 0.853, median residual 0.000 against the
family's published headroom -- and it is still a PREDICTION. The remedy it points at (an E1-b sweep
re-forming every shape that names its own target) is a multi-vertical corpus arc costing several
re-probes, so the diagnosis is worth measuring before the remedy is funded.

This runs the V9 arm again, changed in exactly one place: the top-K comes from cosine over
embeddings instead of BM25. Same documents, same budget, same prompt, same judge, same
`require_distinctive` / `already_known` / `answer_must_name` handling -- reused from
`run_typedmemeval_probes` rather than reimplemented, because a second copy of the grading path is a
second thing to get wrong and the two would drift silently.

SCOPE, AND WHY IT IS NOT THE FAMILY
-----------------------------------
Eight AT-RISK shapes (predicted to fall below the 0.15 floor) and four CONTROLS (predicted to hold
or to get harder). The controls are the point: if the at-risk shapes rise and the controls also
rise, the arm is measuring the change of retriever on everything and the prediction is unconfirmed.
A one-directional result on at-risk shapes alone could not tell those apart.

    python typedmemeval_v9_dense.py --dry-run     # stub model, spends nothing
    python typedmemeval_v9_dense.py --limit 1     # one question per shape
    python typedmemeval_v9_dense.py
"""
import argparse
import collections
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import typedmemeval_common as tmc                      # noqa: E402
import run_typedmemeval_probes as probes               # noqa: E402
import typedmemeval_dense_retrieval as dense           # noqa: E402

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

CORPORA = os.path.join(os.path.dirname(HERE), 'src', 'AgentEval.Memory', 'Data', 'typedmemeval')

#: Predicted to fall below the 0.15 floor under dense retrieval (§88.38).
AT_RISK = [
    ('episodic', 'assistant-stated'),
    ('forgetting', 'invalidated'),
    ('prospective', 'due-later-reminder'),
    ('prospective', 'expiring-validity'),
    ('prospective', 'not-yet-true'),
    ('prospective', 'seed-carry-over'),
    ('semantic', 'source-attribution'),
    ('workingmemory', 'distance-40'),
]

#: Predicted to HOLD or to get harder. Without these the run cannot tell "the prediction was right"
#: from "changing the retriever moved everything".
CONTROLS = [
    ('conjunction', 'order-then-value'),      # dense ALLgold 0.200 -> 0.000, predicted HARDER
    ('procedural', 'step-order'),             # 0.000 both, predicted unchanged
    ('semantic', 'co-reference'),             # 0.400 -> 0.467, predicted ~unchanged
    ('temporal', 'occurrence-order'),         # 0.050 -> 0.200, predicted mild
]


def load(vertical):
    path = os.path.join(CORPORA, vertical, 'agenteval-typedmemeval-%s-v5.json' % vertical)
    return json.load(open(path, encoding='utf-8-sig'))


def published_v9(vertical, shape):
    path = os.path.join(CORPORA, vertical, 'agenteval-typedmemeval-%s-v5.meta.json' % vertical)
    block = ((json.loads(open(path, encoding='utf-8-sig').read()).get('probes') or {})
             .get('by_shape') or {}).get(shape) or {}
    passed, applicable = block.get('v9_passed'), block.get('v9_applicable')
    return (passed, applicable, block.get('headroom_perfect_selector'),
            block.get('v1_passed'), block.get('v1_applicable'))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--dry-run', action='store_true')
    ap.add_argument('--limit', type=int, help='questions per shape')
    args = ap.parse_args()

    if args.dry_run:
        # The module-private name, set the way the probe tool sets it on itself. Assigning a
        # NEW public attribute would leave _DRY_RUN False and quietly spend money on a run
        # whose whole purpose is to spend none.
        probes.__dict__["_DRY_RUN"] = True
        assert probes._DRY_RUN is True

    targets = [(v, s, 'at-risk') for v, s in AT_RISK] + [(v, s, 'control') for v, s in CONTROLS]
    by_vertical = collections.defaultdict(list)
    for vertical, shape, kind in targets:
        by_vertical[vertical].append((shape, kind))

    results = {}
    for vertical in sorted(by_vertical):
        entries = load(vertical)
        wanted = dict(by_vertical[vertical])
        dense._cache.clear()
        dense._load_cache([vertical])

        for entry in entries:
            shape = (entry.get('typedmemeval') or {}).get('shape')
            if shape not in wanted:
                continue
            golds = probes.gold_indices(entry)
            if not golds:
                continue
            cell = results.setdefault((vertical, shape), {'kind': wanted[shape], 'n': 0,
                                                          'passed': 0, 'silent': 0})
            if args.limit and cell['n'] >= args.limit:
                continue

            question, gold = entry['question'], entry['answer']
            date = entry['question_date']
            known = '%s %s' % (question, date)
            needs_value = probes.negative_gold_requires_value(gold, known)
            must_name = ((entry.get('typedmemeval') or {}).get('answer_must_name') or '')

            docs = [dense.render_one(sess, d) for sess, d in
                    zip(entry['haystack_sessions'], entry['haystack_dates'])]
            ranked = sorted(dense.cosine_rank(question, docs)[:tmc.K_REF])

            key = probes.question_key(entry)
            answer = probes.complete(probes.ask(question, date, probes.subset(entry, ranked)),
                                     cache_key='%s:v9dense' % key)
            cell['n'] += 1
            if not answer:
                cell['silent'] += 1
                continue
            if probes.produced_gold(question, gold, answer, '%s:v9dense:judge' % key,
                                    require_distinctive=needs_value, already_known=known,
                                    must_name=must_name):
                cell['passed'] += 1
        print('  %s done' % vertical, flush=True)

    print()
    print('V9 MEASURED UNDER A DENSE RETRIEVER  (same documents, K=%d, same judge)' % tmc.K_REF)
    print()
    print('%-9s %-14s %-24s %14s %14s %12s' %
          ('kind', 'vertical', 'shape', 'V9 published', 'V9 dense', 'predicted'))
    print('-' * 96)
    moved = collections.Counter()
    for (vertical, shape), cell in sorted(results.items(), key=lambda kv: (kv[1]['kind'], kv[0])):
        p9, a9, head, p1, a1 = published_v9(vertical, shape)
        n = cell['n'] - cell['silent']
        if not n or not a9:
            print('%-9s %-14s %-24s   UNMEASURED (n=%d, silent=%d)' %
                  (cell['kind'], vertical, shape, cell['n'], cell['silent']))
            continue
        old_rate, new_rate = p9 / a9, cell['passed'] / n
        v1_rate = (p1 / a1) if a1 else None
        new_head = (v1_rate - new_rate) if v1_rate is not None else None
        print('%-9s %-14s %-24s   %2d/%-2d %.3f   %2d/%-2d %.3f   head %s -> %s' %
              (cell['kind'], vertical, shape, p9, a9, old_rate, cell['passed'], n, new_rate,
               ('%.3f' % head) if head is not None else '  -  ',
               ('%.3f' % new_head) if new_head is not None else '  -  '))
        if new_head is not None:
            moved[cell['kind'] + ('_below' if new_head < 0.15 else '_above')] += 1

    print()
    print('CONFIRMATION TEST')
    print('  at-risk shapes now below the 0.15 floor : %d of %d'
          % (moved['at-risk_below'], moved['at-risk_below'] + moved['at-risk_above']))
    print('  controls still above it                 : %d of %d'
          % (moved['control_above'], moved['control_below'] + moved['control_above']))
    print()
    print('  The prediction is CONFIRMED only if both columns are high. At-risk falling while')
    print('  controls also fall would mean the dense retriever helped everything, which is a')
    print('  different claim and does not support an E1-b sweep.')
    print()
    print('  calls: %s' % dict(probes._stats))
    return 0


if __name__ == '__main__':
    sys.exit(main())
