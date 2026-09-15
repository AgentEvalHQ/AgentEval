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
import sys

# Derived, not hard-coded. This was an absolute path to one machine's checkout, so the first
# cache read below failed everywhere else -- including in the repository it ships in. The line
# immediately under it already used __file__, which is how the defect survived review: the correct
# idiom was sitting next to the broken one.
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CACHE = os.path.join(ROOT, 'tools', '.typedmemeval_probe_cache.json')
CORPORA = os.path.join(ROOT, 'src', 'AgentEval.Memory', 'Data', 'typedmemeval')
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'judge-sample-50.json')

SEED = 20260912          # fixed and declared: the sample must be redrawable
PER_ARM_TARGET = 8       # 7 arms x 8 = 56, trimmed to 50 by frame size
# ONE definition of the frame, imported rather than restated. These two files had separate copies
# of the rule and the analyser's drift check exists precisely because they can disagree -- which
# they then did: a deny-list of abstention arms admitted the experimental `v9dense` arm into both,
# and the builder drew 8 cases from it. A shared constant cannot drift from itself.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from analyse_judge_agreement import SHIPPED_ARMS, frame_fingerprint  # noqa: E402


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
    if arm not in SHIPPED_ARMS:
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

# DevSkim: ignore DS148264 - a STRATIFIED SAMPLING seed, not a security function. The sample is
# the unit of a judge-agreement study, so it has to be the same 50 items every time it is drawn
# or two runs of the study are not comparable. A cryptographic RNG would destroy exactly the
# property this line exists for.
rng = random.Random(SEED)  # DevSkim: ignore DS148264
sample = []
for arm in sorted(by_arm):
    yes = [r for r in by_arm[arm] if r['judge1'] == 'yes']
    no = [r for r in by_arm[arm] if r['judge1'] == 'no']
    half = PER_ARM_TARGET // 2
    take_y = rng.sample(yes, min(half, len(yes)))  # DevSkim: ignore DS148264
    take_n = rng.sample(no, min(PER_ARM_TARGET - len(take_y), len(no)))
    # if one side was short, top the other side up so the arm still reaches its target
    if len(take_y) + len(take_n) < PER_ARM_TARGET:
        pool = [r for r in yes + no if r not in take_y + take_n]
        take_n += rng.sample(pool, min(PER_ARM_TARGET - len(take_y) - len(take_n), len(pool)))
    sample.extend(take_y + take_n)

rng.shuffle(sample)  # DevSkim: ignore DS148264
# TRUNCATION USED TO BE ABLE TO EMPTY A CELL. The arms are balanced first and then all selected
# rows were shuffled and cut to 50, so an arbitrary trim could remove an entire arm -- or every
# `yes` or every `no` within one -- while the file still called itself stratified and the
# direction-flip check it exists for became unobservable. Found in review of PR #238.
#
# Asserted rather than re-engineered: the allocation above is what should decide the composition,
# so the check names the cell it lost instead of silently rebalancing behind the caller.
_before = {}
for row in sample:
    _before.setdefault(row.get('arm'), set()).add(row.get('expected'))
sample = sample[:50]
_after = {}
for row in sample:
    _after.setdefault(row.get('arm'), set()).add(row.get('expected'))
if _before != _after:
    raise SystemExit(
        'trimming to 50 dropped a stratum: arms/verdicts went %r -> %r. The sample is labelled '
        'stratified and the direction-flip check depends on every cell surviving.'
        % ({k: sorted(v) for k, v in sorted(_before.items())},
           {k: sorted(v) for k, v in sorted(_after.items())}))

print('SAMPLE DRAWN  n = %d   seed = %d' % (len(sample), SEED))
comp = collections.Counter((r['arm'], r['judge1']) for r in sample)
for arm in sorted({a for a, _ in comp}):
    print('  %-8s yes=%d no=%d' % (arm, comp[(arm, 'yes')], comp[(arm, 'no')]))
print('  verticals covered: %d' % len({r['vertical'] for r in sample}))

json.dump({'seed': SEED, 'drawn_from': len(live),
           # THE POPULATION'S IDENTITY, not just its size. `drawn_from` is a COUNT, and a count
           # passes for any two populations of the same length while the per-cell weights depend on
           # composition. Built from the same rows this sample was drawn from, using the analyser's
           # own function so the two cannot drift. See SS88.52.
           'drawn_from_fingerprint': frame_fingerprint(
               [(r['cache_key'], r['judge1']) for r in live]),
           'cases': sample},
          open(OUT, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
print('\n  written: %s' % OUT)
