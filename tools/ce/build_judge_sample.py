# -*- coding: utf-8 -*-
"""C-E stage 2 — draw the stratified sample. Spends nothing; writes the exact judge inputs.

Design note, because the sampling is the part that can silently ruin this:

A UNIFORM sample of 50 from the live frame is dominated by arms where the judge almost never
disagrees with itself. v2 is 34% of the frame and 99.4% "no"; a uniform draw would mostly measure
agreement on easy rejections and report a flattering bound. So: stratify by ARM, and within each
arm balance the judge's own yes/no, because a family bias shows up as a systematic flip in ONE
direction and a sample with no "yes" cases cannot see a yes->no flip.

The bound this supports is per-arm, and the per-arm numbers are what get published, not the mean.
"""
import collections
import glob
import hashlib
import json
import os
import random

ROOT = r'C:\git\joslat\AgentEval'
CACHE = os.path.join(ROOT, 'tools', '.typedmemeval_probe_cache.json')
CORPORA = os.path.join(ROOT, 'src', 'AgentEval.Memory', 'Data', 'typedmemeval')
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'judge-sample-50.json')

SEED = 20260912          # fixed and declared: the sample must be redrawable
PER_ARM_TARGET = 8       # 7 arms x 8 = 56, trimmed to 50 by frame size
ABSTENTION_ARMS = {'v10', 'v11'}


def key_for(entry):
    material = json.dumps(
        [entry["question"], entry["answer"], entry.get("question_date"),
         entry.get("haystack_sessions"), entry.get("haystack_dates")],
        sort_keys=True, ensure_ascii=False)
    return hashlib.sha256(material.encode("utf-8")).hexdigest()[:16]


index = {}
for path in glob.glob(os.path.join(CORPORA, '*', '*-v5.json')):
    vertical = os.path.basename(os.path.dirname(path))
    data = json.load(open(path, encoding='utf-8'))
    for e in (data if isinstance(data, list) else data.get('questions', [])):
        if isinstance(e, dict) and 'question' in e:
            index[key_for(e)] = (vertical, e['question'], e['answer'])

cache = json.load(open(CACHE, encoding='utf-8'))

live = []
for k, verdict in cache.items():
    if not k.endswith(':judge'):
        continue
    arm = k.split(':')[1]
    if arm in ABSTENTION_ARMS:
        continue
    h = k.split(':')[0]
    if h not in index:
        continue                       # stale: belongs to a superseded corpus
    answer_key = k[:-len(':judge')]
    if answer_key not in cache:
        continue
    vertical, question, gold = index[h]
    v = str(verdict).strip().lower()
    live.append({
        'cache_key': k, 'arm': arm, 'vertical': vertical,
        'question': question, 'gold': str(gold), 'response': str(cache[answer_key]),
        'judge1_raw': str(verdict).strip(),
        'judge1': 'yes' if v.startswith('yes') else 'no',
    })

print('LIVE FRAME (binds to the CURRENT corpora)  n = %d' % len(live))
by_arm = collections.defaultdict(list)
for r in live:
    by_arm[r['arm']].append(r)
for arm in sorted(by_arm, key=lambda a: -len(by_arm[a])):
    rs = by_arm[arm]
    y = sum(1 for r in rs if r['judge1'] == 'yes')
    print('  %-8s n=%-5d yes=%-5d no=%-5d' % (arm, len(rs), y, len(rs) - y))
print()

rng = random.Random(SEED)
sample = []
for arm in sorted(by_arm):
    yes = [r for r in by_arm[arm] if r['judge1'] == 'yes']
    no = [r for r in by_arm[arm] if r['judge1'] == 'no']
    half = PER_ARM_TARGET // 2
    take_y = rng.sample(yes, min(half, len(yes)))
    take_n = rng.sample(no, min(PER_ARM_TARGET - len(take_y), len(no)))
    # if one side was short, top the other side up so the arm still reaches its target
    if len(take_y) + len(take_n) < PER_ARM_TARGET:
        pool = [r for r in yes + no if r not in take_y + take_n]
        take_n += rng.sample(pool, min(PER_ARM_TARGET - len(take_y) - len(take_n), len(pool)))
    sample.extend(take_y + take_n)

rng.shuffle(sample)
sample = sample[:50]

print('SAMPLE DRAWN  n = %d   seed = %d' % (len(sample), SEED))
comp = collections.Counter((r['arm'], r['judge1']) for r in sample)
for arm in sorted({a for a, _ in comp}):
    print('  %-8s yes=%d no=%d' % (arm, comp[(arm, 'yes')], comp[(arm, 'no')]))
print('  verticals covered: %d' % len({r['vertical'] for r in sample}))

json.dump({'seed': SEED, 'drawn_from': len(live), 'cases': sample},
          open(OUT, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
print('\n  written: %s' % OUT)
