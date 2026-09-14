# -*- coding: utf-8 -*-
"""THE THIRD MONOCULTURE: the embedding model itself.

`typedmemeval_dense_retrieval.py` already argues that K_ref=5 is one point and that headroom is a
statement about the retrieval BUDGET first. The same argument applies one level up. Its "dense" arm
is not dense retrieval; it is `text-embedding-ada-002`, a 2022 model, and until 2026-09-14 the
sidecar did not say so -- it said "azure-openai-embeddings", which every embedding model Azure has
ever served satisfies.

So a published line like "dense retrieval closes 21% of the headroom BM25 leaves open" is a
statement about ONE retriever. This tool turns that point into a comparison, and it does so for
ZERO calls: both models' vectors are already banked, each under its own model directory, and this
only re-ranks them.

WHY A SEPARATE FILE. The measurement tool is under test and was just shipped; adding a second
global vector cache to it would put two dictionaries keyed by the same text hashes inside the code
path that produces the published numbers. Everything here is IMPORTED from that module -- the
document rendering, the key derivation, the unpacking, the BM25 arm -- so there is no second copy of
the ranking to drift.

REFUSES A PARTIAL COMPARISON. If either model is missing a single text a vertical needs, that
vertical is skipped and named. Two retrievers scored over different question sets is not a weaker
comparison; it is not a comparison.

    python tools/typedmemeval_retriever_compare.py --models text-embedding-ada-002,text-embedding-3-small
"""
from __future__ import annotations

import argparse
import collections
import glob
import json
import os
import sys
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import typedmemeval_common as tmc          # noqa: E402
import typedmemeval_dense_retrieval as dr  # noqa: E402

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')


def resolve_aliases(aliases):
    """deployment alias -> underlying model, for every alias at once.

    One listing call rather than one per alias, and it does NOT go through the dense module's
    process-wide `_resolved_model` cache, which is pinned to whatever this process's environment
    says. Comparing two deployments means resolving both, and neither is "the" current one.
    """
    endpoint = os.environ.get('AZURE_OPENAI_ENDPOINT', '').rstrip('/')
    key = os.environ.get('AZURE_OPENAI_API_KEY', '')
    if not (endpoint and key):
        raise SystemExit('AZURE_OPENAI_ENDPOINT / AZURE_OPENAI_API_KEY are not set, so a '
                         'deployment alias cannot be resolved to a model. Refusing to guess.')
    request = urllib.request.Request(
        endpoint + '/openai/deployments?api-version=2023-03-15-preview',
        headers={'api-key': key})
    with urllib.request.urlopen(request, timeout=60) as response:  # DevSkim: ignore DS137138
        listing = json.loads(response.read().decode('utf-8'))
    mapping = {d.get('id'): d.get('model') for d in listing.get('data', [])}
    out = {}
    for alias in aliases:
        model = mapping.get(alias)
        if not model:
            raise SystemExit('%r is not a deployment on this resource, so it has no model and no '
                             'banked vectors. Refusing to compare against a name.' % alias)
        out[alias] = model
    return out


def load_model_cache(model: str, vertical: str) -> dict:
    """Every vector this model has for this vertical, from ITS OWN directory.

    The per-model directory is the whole reason this comparison is safe to make: before 2026-09-14
    the cache was flat, and a second model either had its shards refused or -- through the
    merge-on-save path -- silently blended into the first model's file.
    """
    cache: dict = {}
    root = os.path.join(dr.CACHE_DIR, model)
    for name in (vertical, '_migrated'):
        path = os.path.join(root, '%s.json' % name)
        if not os.path.exists(path):
            continue
        shard = json.loads(open(path, encoding='utf-8').read())
        stamped = shard.get(dr._MODEL_KEY)
        if stamped and stamped != model:
            raise SystemExit('%s claims model %r; refusing to read it as %r.'
                             % (path, stamped, model))
        cache.update({k: v for k, v in shard.items() if not k.startswith('__')})
    return cache


def questions_for(path: str):
    for entry in json.loads(open(path, encoding='utf-8').read()):
        gold_ids = set(entry['answer_session_ids'])
        gold = {i for i, sid in enumerate(entry['haystack_session_ids']) if sid in gold_ids}
        if not gold:
            continue                    # no gold -> ALLgold is 0/0, excluded as everywhere else
        yield {
            'shape': (entry.get('typedmemeval') or {}).get('shape') or '(none)',
            'question': entry['question'],
            'docs': [dr.render_one(s, d) for s, d
                     in zip(entry['haystack_sessions'], entry['haystack_dates'])],
            'gold': gold,
        }


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--models', required=True,
                    help='two or more DEPLOYMENT aliases, comma separated')
    ap.add_argument('--budget', type=int, default=tmc.K_REF)
    ap.add_argument('--vertical')
    args = ap.parse_args()

    aliases = [a for a in args.models.split(',') if a]
    if len(aliases) < 2:
        raise SystemExit('a comparison needs at least two deployments')
    resolved = resolve_aliases(aliases)
    models = list(dict.fromkeys(resolved[a] for a in aliases))

    # POSITIVE CONTROL FOR THE PLUMBING. Naming one model twice must produce a spread of EXACTLY
    # zero. If it does not, the two caches are not being read the way this tool thinks they are and
    # every cross-model number it prints is noise of unknown origin. It is the cheapest possible
    # check and it costs no calls, because both sides read the same banked vectors.
    self_check = len(models) == 1
    if self_check:
        print('SELF-CHECK: one model named twice. The spread must come out at exactly 0.000;')
        print('anything else means the two sides are not reading what this tool believes.')
        models = models * 2
    print('comparing retrievers at K=%d' % args.budget)
    for alias in aliases:
        print('  %-28s -> %s' % (alias, resolved[alias]))
    print()

    paths = [p for p in sorted(glob.glob(os.path.join(dr.CORPORA, '*', '*-v5.json')))
             if not p.endswith('.meta.json')]
    if args.vertical:
        paths = [p for p in paths if os.path.basename(os.path.dirname(p)) == args.vertical]

    by_shape = collections.defaultdict(lambda: collections.Counter())
    skipped = []
    for path in paths:
        vertical = os.path.basename(os.path.dirname(path))
        questions = list(questions_for(path))
        if not questions:
            continue
        needed = set()
        for q in questions:
            needed.add(dr._key(q['question']))
            needed.update(dr._key(d) for d in q['docs'])
        # Loaded per SLOT, not per model name, so a self-check really does build two caches
        # and rank through each rather than aliasing one dictionary to both sides.
        caches = [load_model_cache(m, vertical) for m in models]
        short = {'%s#%d' % (m, i): len(needed - set(c))
                 for i, (m, c) in enumerate(zip(models, caches))}
        if any(short.values()):
            skipped.append((vertical, short))
            continue
        for q in questions:
            cell = by_shape[(vertical, q['shape'])]
            cell['n'] += 1
            top_bm25 = set(tmc.bm25_rank(q['question'], q['docs'])[:args.budget])
            cell['bm25'] += 1 if q['gold'].issubset(top_bm25) else 0
            for i, m in enumerate(models):
                dr._cache = caches[i]          # cosine_rank reads the module-level cache
                top = set(dr.cosine_rank(q['question'], q['docs'])[:args.budget])
                cell['slot%d' % i] += 1 if q['gold'].issubset(top) else 0

    if skipped:
        print('SKIPPED -- a model is missing vectors these verticals need. Scoring two retrievers')
        print('over different question sets would not be a weaker comparison, it would not be one:')
        for vertical, short in skipped:
            print('  %-14s %s' % (vertical, ', '.join('%s short %d' % kv for kv in sorted(short.items()))))
        print()
    if not by_shape:
        raise SystemExit('nothing was comparable; embed the missing model first')

    header = '%-14s %-24s %4s %8s' % ('vertical', 'shape', 'n', 'BM25')
    for m in models:
        header += ' %22s' % m[-22:]
    slots = ['slot%d' % i for i in range(len(models))]
    print('ALLgold = the rate at which top-%d holds ALL of a question\'s gold.' % args.budget)
    print(header)
    print('-' * len(header))
    fam = collections.Counter()
    for (vertical, shape), c in sorted(by_shape.items()):
        n = c['n']
        fam['n'] += n
        fam['bm25'] += c['bm25']
        row = '%-14s %-24s %4d %8.3f' % (vertical, shape, n, c['bm25'] / n)
        for slot in slots:
            fam[slot] += c[slot]
            row += ' %22.3f' % (c[slot] / n)
        print(row)
    n = fam['n']
    print('-' * len(header))
    row = '%-14s %-24s %4d %8.3f' % ('FAMILY', '', n, fam['bm25'] / n)
    for slot in slots:
        row += ' %22.3f' % (fam[slot] / n)
    print(row)
    print()

    spread = (max(fam[s] for s in slots) - min(fam[s] for s in slots)) / n
    if self_check:
        print('SELF-CHECK spread: %.6f' % spread)
        if spread:
            print('  \U0001f534 FAIL. One model against itself must agree with itself exactly. The two')
            print('     sides are not reading what this tool believes, so no cross-model number it')
            print('     prints can be trusted.')
            return 1
        print('  PASS: two independently loaded caches of one model rank identically, so the')
        print('  cross-model spread below measures the MODEL and not the plumbing.')
        return 0

    print('WHAT CHANGES WHEN THE RETRIEVER MODEL CHANGES')
    print('  BM25 leaves %.3f of headroom open at K=%d.' % (1 - fam['bm25'] / n, args.budget))
    for slot, m in zip(slots, models):
        closed = fam[slot] / n - fam['bm25'] / n
        room = 1 - fam['bm25'] / n
        print('  %-28s ALLgold %.3f  ->  headroom %.3f  (%s %.1f%% of what BM25 leaves)'
              % (m, fam[slot] / n, 1 - fam[slot] / n,
                 'closes' if closed >= 0 else 'OPENS', abs(100.0 * closed / room) if room else 0.0))
    print()
    print('  SPREAD BETWEEN EMBEDDING MODELS: %.3f ALLgold.' % spread)
    print('  That spread is the part of any "dense retrieval closes X" sentence that belongs to the')
    print('  MODEL rather than to dense retrieval. Quote the retriever id with the number, or the')
    print('  number is a claim about a model the reader was never told about.')
    if spread == 0:
        print('  \u26a0 A spread of EXACTLY zero across two DIFFERENT models is a wiring fault until')
        print('    proven otherwise. Run --models <one alias>,<the same alias> : that self-check is')
        print('    the only place a zero is the right answer.')
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
