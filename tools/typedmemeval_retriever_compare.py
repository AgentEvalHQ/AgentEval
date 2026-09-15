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
import base64
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


#: Vector width per model, read from the shards as they load. NOT assumed.
_dims_by_model: dict = {}


def dims_of(model: str) -> int:
    """The width this model's banked vectors actually have.

    THE IDENTITY MUST DESCRIBE THE VECTORS. `dense_retriever_id` takes a dimension because the
    width changes the ranking, and an earlier version of this tool passed a hard-coded 1536 -- so
    stamping any other model would have published `d1536` beside vectors of a different width. An
    id that does not identify is the exact defect the id was introduced to end. Found in review of
    PR #250.
    """
    dims = _dims_by_model.get(model)
    if not dims:
        raise SystemExit('no shard of %r recorded __embedding_dims__, so the width of its vectors '
                         'is unknown and its identity cannot be written. Re-embed it.' % model)
    return dims


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
        width = shard.get(dr._DIMS_KEY)
        if not isinstance(width, int) or width <= 0:
            # EVERY shard, not whichever happens to carry the field. Accepting a shard with no
            # width and taking the number from a sibling publishes an identity covering vectors
            # whose width was never established -- the id would be describing the other shard.
            raise SystemExit('%s records no usable __embedding_dims__ (%r). Its vectors have an '
                             'unestablished width, so no identity can honestly cover them. '
                             'Re-embed it.' % (path, width))
        seen = _dims_by_model.setdefault(model, width)
        if seen != width:
            raise SystemExit('%s: shards of %r disagree on vector width (%d vs %d). One of '
                             'them was not produced by that model.'
                             % (path, model, seen, width))
        # THE STATED WIDTH IS CHECKED AGAINST THE BYTES. `_save_shard` derives __embedding_dims__
        # from ONE sample, and `cosine_rank` zips the two vectors -- so a short or corrupted payload
        # is silently TRUNCATED during ranking and still published under the stated width. float16,
        # so two bytes per dimension; no unpacking needed to measure it.
        for key, blob in shard.items():
            if key.startswith('__'):
                continue
            # EXACT BYTES, AND VALIDATED BASE64. `// 2` floors, so a payload of 2*width+1 bytes
            # passed and only failed later inside `_unpack`; and `b64decode` without validate=True
            # silently DISCARDS non-base64 characters, so corruption could shrink a payload to a
            # legal-looking length. Both make a malformed vector reach ranking. Found in review of
            # PR #250.
            try:
                raw_bytes = base64.b64decode(blob, validate=True)
            except Exception as error:
                raise SystemExit('%s: vector %s is not valid base64 (%s). A payload that cannot be '
                                 'decoded cannot be ranked with.' % (path, key[:12], error))
            if len(raw_bytes) != 2 * width:
                raise SystemExit(
                    '%s: vector %s decodes to %d bytes but the shard states %d dimensions, i.e. %d '
                    'bytes. Ranking zips vectors, so a mismatch is silently truncated and still '
                    'published as d%d.'
                    % (path, key[:12], len(raw_bytes), width, 2 * width, width))
        stamped = shard.get(dr._MODEL_KEY)
        # AN EXACT MATCH, NOT "no contradiction". `if stamped and stamped != model` accepts a shard
        # with NO stamp, because the first operand is false -- the identical fail-open that
        # `_load_cache` carried on `if stamped is not None` and that was fixed there earlier in this
        # same pull request. Being under a model-named directory is not evidence of provenance: the
        # directory records where a run chose to write, the stamp records what produced the bytes.
        # Applied-once, caught in review of PR #245.
        if stamped != model:
            raise SystemExit(
                '%s carries model %r, not %r. A comparison must not include vectors whose '
                'producer is unproven -- re-embed that shard or drop the model from --models.'
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


def _family_spread(model_a: str, model_b: str, budget: int) -> float:
    """Family ALLgold difference between two models, over the shapes both can score.

    Used only as the self-check's operand: called with the SAME model twice it must return exactly
    0.0, because both sides then read the same banked vectors through the same ranking.
    """
    totals = {0: 0, 1: 0}
    n = 0
    paths = [p for p in sorted(glob.glob(os.path.join(dr.CORPORA, '*', '*-v5.json')))
             if not p.endswith('.meta.json')]
    for path in paths:
        vertical = os.path.basename(os.path.dirname(path))
        questions = list(questions_for(path))
        if not questions:
            continue
        needed = set()
        for q in questions:
            needed.add(dr._key(q['question']))
            needed.update(dr._key(d) for d in q['docs'])
        caches = [load_model_cache(m, vertical) for m in (model_a, model_b)]
        if any(needed - set(c) for c in caches):
            continue
        for q in questions:
            n += 1
            for i in (0, 1):
                dr._cache = caches[i]
                top = set(dr.cosine_rank(q['question'], q['docs'])[:budget])
                totals[i] += 1 if q['gold'].issubset(top) else 0
    if not n:
        return float('nan')
    return totals[0] / n - totals[1] / n


def classify(disc_a: bool, disc_b: bool, beats_a: bool, beats_b: bool) -> str:
    """The three-class read of a shape across two dense retrievers.

    Named because SEND-37 demoted the single boolean to exactly this, on the evidence that the flag
    flips on 4 of 35 shapes and the dense-beats-BM25 SIGN flips on 6 -- and neither retriever is any
    consumer\'s. Sensitivity is checked FIRST: a shape that disagrees between two embedders is a
    caution whatever else is true of it, and burying that under "non-ranking" would hide the one
    thing the second column was published to show.
    """
    if disc_a != disc_b or beats_a != beats_b:
        return 'retriever-sensitive'
    return 'robust-ranking' if (disc_a and disc_b) else 'non-ranking'


def stamp_second_column(by_shape, models, budget: int) -> int:
    """Write the second retriever into every sidecar, beside the first rather than over it.

    ADDITIVE. `allgold_dense`, `predicted_headroom_dense` and `discriminates_under_dense` keep their
    exact meaning and value -- they are the reference column and a consumer reading them today reads
    the same bytes afterwards. The second retriever arrives as its own nested block plus a derived
    class, so nothing existing changes shape.

    REFUSES ON DISAGREEMENT WITH WHAT IS ALREADY PUBLISHED. The reference column here is recomputed
    from banked vectors, and the sidecar already carries the same quantity from the dense tool's own
    run. Two independent computations of one number, never previously compared -- so they are
    compared, and a mismatch stops the publication rather than overwriting the older one. If they
    disagree, one of the two is wrong and neither should ship.
    """
    first_id = dr.dense_retriever_id(models[0], dims_of(models[0]))
    second_id = dr.dense_retriever_id(models[1], dims_of(models[1]))
    if not (first_id and second_id):
        raise SystemExit('a retriever without an id cannot be published as a column')

    per_vertical = collections.defaultdict(dict)
    for (vertical, shape), c in by_shape.items():
        per_vertical[vertical][shape] = c

    # VALIDATE EVERYTHING, THEN WRITE. Writing each vertical as it passed meant a failure on the
    # ninth left eight sidecars already carrying a second column and the family half-published --
    # and the refusals here exist precisely because something might be wrong, so the path that
    # fires them is the one that must not leave a mess. Staged in memory; files are touched only
    # after every vertical has passed. Found in review of PR #250.
    staged = []
    tally = collections.Counter()
    for vertical, shapes in sorted(per_vertical.items()):
        path = os.path.join(dr.CORPORA, vertical,
                            'agenteval-typedmemeval-%s-v5.meta.json' % vertical)
        if not os.path.exists(path):
            # SKIPPING IT WOULD PUBLISH A PARTIAL FAMILY while the completeness checks upstream all
            # passed -- the vertical had vectors and shapes, it simply has nowhere to write. That is
            # the whole-family refusal being bypassed by a missing file.
            raise SystemExit('%s has no sidecar at %s, so its column cannot be written. Refusing '
                             'to publish a column over part of the family.' % (vertical, path))
        meta = json.loads(open(path, encoding='utf-8-sig').read())
        block = (meta.get('probes') or {}).get('retriever_sensitivity')
        if not block:
            raise SystemExit('%s has no retriever_sensitivity block. Run the dense tool\'s --stamp '
                             'first: this column sits beside that one, it does not replace it.'
                             % vertical)
        published_second = block.get('second_dense_retriever')
        # Rewriting under the SAME id is the quieter half of this hazard: the cache writer can
        # replace vectors beneath an unchanged model name (a re-embed, a model update), and then a
        # re-stamp rewrites published values with nothing in the diff to distinguish it from a
        # no-op. Handled per shape below, where the new values can actually be compared.
        if published_second and published_second != second_id:
            # RELEASE EVIDENCE IS NOT OVERWRITTEN ON A TYPO. A changed alias or a mistyped --models
            # would silently rewrite every second_dense value and every retriever_agreement in a
            # shipped sidecar, and the diff would look like an ordinary re-stamp.
            raise SystemExit(
                '%s already publishes second_dense_retriever %r and this run would write %r. '
                'Replacing a published column is a deliberate act: remove the existing block by '
                'hand if that is what you mean.' % (vertical, published_second, second_id))
        if block.get('dense_retriever') != first_id:
            raise SystemExit('%s publishes dense_retriever %r but this run\'s first model is %r. '
                             'The reference column must be the one already published.'
                             % (vertical, block.get('dense_retriever'), first_id))

        # IS THIS SIDECAR ABOUT THE CORPUS WE JUST MEASURED? Matching rounded aggregates does not
        # answer that -- two different inputs can produce equal per-shape rates and receive a column
        # attached to stale reference data. The corpus identity does answer it.
        corpus_path = os.path.join(dr.CORPORA, vertical,
                                   'agenteval-typedmemeval-%s-v5.json' % vertical)
        on_disk = tmc.corpus_sha256(corpus_path)
        if meta.get('corpus_sha256') != on_disk:
            raise SystemExit(
                '%s: the sidecar describes corpus %s but the corpus on disk is %s. A column written '
                'beside reference data from a different input is not a comparison.'
                % (vertical, str(meta.get('corpus_sha256'))[:12], on_disk[:12]))

        existing = block['by_shape']
        # Shapes DECLARED not-applicable carry no measurement to compare, and must not read as a
        # shape-set disagreement -- they are the dense tool saying on the record that the operand is
        # undefined for them (empty gold, so ALLgold would be vacuously 1.000). See SEND-41 SS2.
        # EVERY SHAPE THE CORPUS HAS, not every shape that happens to be measurable. Filtering the
        # declared rows out made a MISSING declared row invisible: a sidecar with no `never-known`
        # entry has a measurable set equal to the measured shapes and sails through, while one of
        # the family's 36 shapes carries no verdict at all. That is the hole the declared row was
        # added to close, reopened by the check written to tolerate it.
        in_corpus = {(e.get('typedmemeval') or {}).get('shape') or '(none)'
                     for e in json.loads(open(corpus_path, encoding='utf-8').read())}
        if set(existing) != in_corpus:
            raise SystemExit(
                '%s: the corpus has shapes %s and the sidecar carries %s. Every shape needs a '
                'verdict -- measured, or declared not-applicable by the dense tool. Re-run its '
                '--stamp first.' % (vertical, sorted(in_corpus), sorted(existing)))
        measurable = {k for k, v in existing.items() if v.get('retrieval_measured') is not False}
        if measurable != set(shapes):
            raise SystemExit('%s: the sidecar has measurable shapes %s and this comparison has %s. '
                             'A column written over a different shape set is not the same '
                             'measurement.' % (vertical, sorted(measurable), sorted(shapes)))

        for shape, c in shapes.items():
            n = c['n']
            bm25 = c['bm25'] / n
            a, b = c['slot0'] / n, c['slot1'] / n
            # THE CROSS-CHECK. Same quantity, two independent computations, never compared before.
            published = existing[shape]['allgold_dense']
            if round(a, 4) != round(published, 4):
                raise SystemExit(
                    '%s/%s: recomputed reference ALLgold %.4f but the sidecar publishes %.4f. One '
                    'of the two is wrong; refusing to publish a second column beside a first that '
                    'does not reproduce.' % (vertical, shape, a, published))
            if existing[shape].get('questions') != n:
                raise SystemExit(
                    '%s/%s: the sidecar counts %r questions and this run counted %d. Equal rates '
                    'over different populations are not the same measurement.'
                    % (vertical, shape, existing[shape].get('questions'), n))
            if round(bm25, 4) != round(existing[shape]['allgold_bm25'], 4):
                raise SystemExit(
                    '%s/%s: recomputed BM25 ALLgold %.4f but the sidecar publishes %.4f.'
                    % (vertical, shape, bm25, existing[shape]['allgold_bm25']))

            klass = classify(existing[shape]['discriminates_under_dense'],
                             (1 - b) >= 0.15, a > bm25, b > bm25)
            prior_col = existing[shape].get('second_dense')
            if published_second and prior_col is not None:
                if (round(prior_col.get('allgold', -1), 4) != round(b, 4)
                        or existing[shape].get('retriever_agreement') != klass):
                    raise SystemExit(
                        '%s/%s already publishes a second column that this run does not reproduce '
                        '(allgold %.4f -> %.4f, class %r -> %r). The vectors under %r changed. '
                        'Rewriting shipped evidence is a deliberate act: remove the existing column '
                        'by hand if that is what you mean.'
                        % (vertical, shape, prior_col.get('allgold', float('nan')), b,
                           existing[shape].get('retriever_agreement'), klass, second_id))
            tally[klass] += 1
            existing[shape]['second_dense'] = {
                'allgold': round(b, 4),
                'predicted_headroom': round(1 - b, 4),
                'discriminates': (1 - b) >= 0.15,
            }
            existing[shape]['retriever_agreement'] = klass

        block['second_dense_retriever'] = second_id
        block['second_dense_note'] = (
            'A SECOND published dense column, not an appendix. `dense_retriever` above stays the '
            'reference every headroom figure here was computed against; this one is the same '
            'measurement over the same documents at the same budget under a different embedding '
            'model. Co-published because the flag flips: a consumer deriving this column themselves '
            'would hold a second copy of one number that can drift from ours invisibly. '
            '`retriever_agreement` reads the pair per shape -- robust-ranking (discriminates under '
            'both), retriever-sensitive (the two disagree on discrimination OR on whether dense '
            'beats BM25), non-ranking (neither). NEITHER retriever is yours; the pair is published '
            'so the conditionality is visible rather than described. '
            'Reproduce: tools/typedmemeval_retriever_compare.py --models A,B (zero API calls).')
        staged.append((path, vertical, meta, len(shapes)))

    for path, vertical, meta, n_shapes in staged:
        with open(path, 'w', encoding='utf-8') as fh:
            json.dump(meta, fh, indent=2, ensure_ascii=False)
            fh.write('\n')
        print('  stamped %-14s %d shapes' % (vertical, n_shapes))
    stamped = len(staged)
    classes = sum(n for _, _, _, n in staged)

    print()
    print('  second column: %s' % second_id)
    print('  verticals %d, shapes %d' % (stamped, classes))
    for k in ('robust-ranking', 'retriever-sensitive', 'non-ranking'):
        print('    %-20s %d' % (k, tally[k]))
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--models', required=True,
                    help='two or more DEPLOYMENT aliases, comma separated')
    ap.add_argument('--budget', type=int, default=tmc.K_REF)
    ap.add_argument('--vertical')
    ap.add_argument('--stamp', action='store_true',
                    help='co-publish the SECOND model as a column in every sidecar. Refuses a '
                         'partial run, refuses more than two models, and runs the plumbing '
                         'self-check for each one first.')
    args = ap.parse_args()

    aliases = [a for a in args.models.split(',') if a]
    if len(aliases) < 2:
        raise SystemExit('a comparison needs at least two deployments')
    resolved = resolve_aliases(aliases)
    models = list(dict.fromkeys(resolved[a] for a in aliases))

    if args.stamp:
        # A STAMP IS A PUBLICATION, so the preconditions are the publication's, not the tool's.
        if args.vertical or args.budget != tmc.K_REF:
            raise SystemExit('--stamp needs the whole family at K_ref=%d. A partial comparison '
                             'published as a column is a claim about shapes it never compared.'
                             % tmc.K_REF)
        if len(models) != 2:
            raise SystemExit('--stamp publishes exactly ONE second column, so it needs exactly '
                             'two distinct models; got %d.' % len(models))
        # THE SELF-CHECK RUNS BEFORE THE PUBLICATION, not as a thing the operator is trusted to
        # have done. The coordinator asked for it by name in SEND-37 and they were right to: a
        # compare tool is a probe, and a probe that cannot come out the other way publishes noise.
        for m in models:
            spread = _family_spread(m, m, args.budget)
            print('  self-check %-28s spread %.6f' % (m, spread))
            if spread != 0.0:
                raise SystemExit(
                    'self-check FAILED for %s: one model against itself must agree with itself '
                    'exactly, and it came out at %.6f. Nothing is stamped -- every cross-model '
                    'number this run could produce is noise of unknown origin.' % (m, spread))
        print()

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
    no_gold_verticals = []
    for path in paths:
        vertical = os.path.basename(os.path.dirname(path))
        questions = list(questions_for(path))
        if not questions:
            # NOT A QUIET `continue`. `questions_for` yields nothing when EVERY question in the
            # vertical has empty gold, and that vertical then never reached `skipped` -- so the
            # whole-family refusal could not see it, and --stamp would publish the rest and call
            # it the family. Recorded so the caller can refuse. Found in review of PR #250.
            no_gold_verticals.append(vertical)
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
    # THE ZERO-SPREAD GUARD RUNS BEFORE THE PUBLICATION, not after it. The stamp branch used to
    # return above this check, so two DISTINCT models landing on an identical family ALLgold -- which
    # the ordinary path calls a wiring fault and exits 1 on -- would have been published instead.
    # A control the reporting path enforces and the publishing path skips is not a control.
    if args.stamp and spread == 0:
        raise SystemExit(
            'refusing to stamp: two DIFFERENT models produced an identical family ALLgold. The '
            'ordinary comparison treats that as a wiring fault until proven otherwise, and a '
            'publication cannot be held to a weaker bar than a printout. Run --models <alias>,'
            '<the same alias> -- that self-check is the only place a zero belongs.')

    if args.stamp:
        # THE SKIP LIST IS A REFUSAL, not a note. `--stamp` advertises that it refuses a partial
        # family; it enforced that for `--vertical` and `--budget` and then published whatever
        # survived the per-vertical completeness check. A model with an incomplete cache would have
        # stamped some verticals and returned success -- the docstring refusing what the code
        # permitted. Found in review of PR #250.
        if skipped:
            raise SystemExit(
                'refusing to stamp: %d vertical(s) were skipped for missing vectors (%s). A column '
                'published over part of the family is a claim about shapes it never compared, and '
                'this is exactly the partial run --stamp says it refuses. Embed the missing model '
                'first.' % (len(skipped), ', '.join(v for v, _ in skipped)))
        if no_gold_verticals:
            raise SystemExit(
                'refusing to stamp: %s contributed no gold-bearing question, so the comparison '
                'never covered it at all. The dense tool declares such shapes as not-applicable; a '
                'second column must not be published over a family with a vertical missing from it '
                'entirely.' % ', '.join(no_gold_verticals))
        print()
        return stamp_second_column(by_shape, models, args.budget)
    if spread == 0:
        print('  \u26a0 A spread of EXACTLY zero across two DIFFERENT models is a wiring fault until')
        print('    proven otherwise. Run --models <one alias>,<the same alias> : that self-check is')
        print('    the only place a zero is the right answer.')
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
