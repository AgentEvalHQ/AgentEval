# -*- coding: utf-8 -*-
"""How much of TypedMemEval's published headroom is an artifact of LEXICAL retrieval?

WHY THIS EXISTS
---------------
Every headroom figure this family publishes is V1 - V9, and V9 gives the model the top-K_ref
sessions **a plain BM25 retriever returns**. `realised_coverage` says so in its own docstring:

    "This is the *floor proxy* of ADR SS4: a stronger (embedding) retriever will exceed it."

By how much has never been measured. That is claim-without-instrument on the single most
consequential assumption in the family, and it is the question every consumer actually has, because
almost none of them retrieve with BM25. If a dense retriever closes most of the gap, the family's
discriminating power is substantially a statement about lexical matching rather than about memory.

WHAT IT MEASURES, AND WHAT IT DOES NOT
--------------------------------------
The operand is **ALLgold** -- `gold.issubset(top_k)` -- not ANY-gold and not the SHARE. MEASUREMENT
STATUS SS88.12: V9's pass rate equals the rate at which the top-K holds ALL of a question's gold
(median residual 0.000, R^2 0.868), while ANY-gold runs ANTI-predictive at slope -0.504. Share is
reported beside it because the calibration band is (wrongly) tuned on share, so the two columns
together say how much of the band's behaviour is the wrong operand.

This tool PREDICTS dense headroom as `1 - ALLgold_dense`. It does not measure it. The identity
over-predicts consistently on the cases checked so far, so the honest reading is **direction, with
the magnitude discounted**. The measurement is a V9 re-run against dense top-K, which costs model
calls; this exists to say whether that spend is warranted and where to point it.

CONTROLS
--------
A "dense retrieval reaches X" number is worthless without a floor. Three arms are computed over the
SAME documents V9 uses (`render([session], [date])`, one per session):

    RANDOM   K sessions drawn by a fixed seed -- the floor. A broken embedding call returning
             constant vectors produces a ranking that is arbitrary but STABLE, which looks like a
             result; it cannot beat random by much, and that is the tell.
    BM25     the incumbent, recomputed here rather than read from the sidecar, so both arms come
             from one code path over one document set.
    DENSE    cosine over embeddings of those same documents.

If DENSE does not clearly beat RANDOM the run is a wiring fault, not a finding, and the summary
says so rather than printing a number.

USAGE
-----
    python typedmemeval_dense_retrieval.py --dry-run            # stub embedder, spends nothing
    python typedmemeval_dense_retrieval.py --vertical semantic --limit 3
    python typedmemeval_dense_retrieval.py                      # the full family

Embeddings are cached by sha256 of the text, so a killed run banks its work and a re-run is free.
"""
import argparse
import base64
import collections
import glob
import hashlib
import json
import os
import random
import struct
import sys
import time
import urllib.error
import urllib.request


# A GATE MUST NOT DIE ON ITS OWN WARNING TEXT. See typedmemeval_quality_board.py for the incident.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import typedmemeval_common as tmc  # noqa: E402

CORPORA = os.path.join(os.path.dirname(HERE), 'src', 'AgentEval.Memory', 'Data', 'typedmemeval')
API_VERSION = "2024-02-01"

#: Embedding cache directory, one file per vertical.
#:
#: THE FIRST DESIGN WAS THE MEMORY TRAP THIS PROJECT KEEPS HITTING. One JSON object holding
#: every vector as a list of floats, rewritten in full after each 128-item batch: at 3072
#: dimensions that is ~60 KB of TEXT per vector, so the file was heading for ~900 MB and the
#: rewrites were O(n^2). Measured mid-run: 139 MB after about a sixth of the work.
#:
#: Vectors are float16 in a base64 blob -- 2 bytes per dimension instead of ~20 -- and each
#: vertical gets its own file, flushed when that vertical finishes. A kill now costs one
#: vertical rather than the run, and the whole family lands near 100 MB instead of a gigabyte.
#: float16 is lossless enough for a COSINE RANKING: the gap between adjacent documents is many
#: orders of magnitude above 2^-11, and the arm is scored on set membership of a top-5, not on
#: the scores themselves.
CACHE_DIR = os.path.join(HERE, '.typedmemeval_embeddings')

#: Inputs per embeddings request.
#:
#: 128 -> 32 after a 429 killed a full-family run. The binding limit is TOKENS PER MINUTE, not
#: requests: a session document averages ~190 tokens, so a 128-item batch is ~24k tokens in one
#: request and a handful of those exhausts a minute window immediately. Smaller batches cost more
#: round-trips and let the pacing below actually pace something.
BATCH = 32

#: Seconds between requests. Deliberate throttling rather than sprint-then-fail: a 429 storm
#: spends the retry budget and then loses the run, which is strictly worse than being slow.
REQUEST_SPACING = 1.5

_cache = {}
_stats = collections.Counter()


def _config():
    endpoint = os.environ.get("AZURE_OPENAI_ENDPOINT", "").rstrip("/")
    key = os.environ.get("AZURE_OPENAI_API_KEY", "")
    deployment = os.environ.get("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", "")
    if not (endpoint and key and deployment):
        sys.exit(
            "Embedding credentials are not set. This needs AZURE_OPENAI_ENDPOINT, "
            "AZURE_OPENAI_API_KEY and AZURE_OPENAI_EMBEDDING_DEPLOYMENT.\n"
            "Without them there is no dense arm, and a run that silently fell back to BM25 would "
            "report 'no difference' -- the most misleading answer available.")
    return endpoint, key, deployment


def _pack(vector) -> str:
    """float16 via struct, NOT array. `array` has no half-float typecode -- "e" is a struct
    format code and array.array("e", ...) raises ValueError: bad typecode. Two bytes per
    dimension instead of ~20 as JSON text is the whole reason the cache fits."""
    return base64.b64encode(struct.pack("<%de" % len(vector), *vector)).decode("ascii")


def _unpack(blob: str):
    data = base64.b64decode(blob)
    return struct.unpack("<%de" % (len(data) // 2), data)


def _shard(name: str) -> str:
    return os.path.join(CACHE_DIR, '%s.json' % name)


#: Key under which a shard records WHICH deployment produced its vectors.
#:
#: A shard was keyed by text hash alone, so re-running against a different
#: AZURE_OPENAI_EMBEDDING_DEPLOYMENT -- or copying a shard between machines -- silently skipped
#: re-embedding and ranked with vectors from the other model. The run would look complete and
#: cost nothing, and the dense arm would be a comparison between two retrievers that were never
#: the same one. Found in review of PR #238.
#:
#: The deployment NAME is recorded, never the endpoint or the key.
_PROVENANCE_KEY = '__embedding_deployment__'


def _deployment_name() -> str:
    return os.environ.get("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", "") or "(unset)"


def _load_cache(names) -> None:
    """Load only the shards this run will touch. Loading the family to measure one vertical is
    the same mistake in a smaller coat.

    `_migrated` is always read: it holds the vectors bought under the first cache format, which
    was replaced mid-run. They are paid for and identical -- keyed by sha256 of the same text --
    so re-buying them would be spending money to reproduce bytes already on disk."""
    for name in list(names) + ['_migrated']:
        path = _shard(name)
        if not os.path.exists(path):
            continue
        try:
            shard = json.loads(open(path, encoding='utf-8').read())
        except json.JSONDecodeError:
            continue                  # a torn shard is re-embedded, never half-trusted
        stamped = shard.pop(_PROVENANCE_KEY, None)
        if stamped is not None and stamped != _deployment_name():
            # REFUSE RATHER THAN MIX. Two models' vectors in one ranking is not a weaker
            # measurement, it is not a measurement.
            print('    ignoring shard %s: built by deployment %r, this run uses %r'
                  % (name, stamped, _deployment_name()), flush=True)
            continue
        _cache.update(shard)


def _save_shard(name: str, keys) -> None:
    os.makedirs(CACHE_DIR, exist_ok=True)
    payload = {k: _cache[k] for k in keys if k in _cache}
    payload[_PROVENANCE_KEY] = _deployment_name()
    tmp = _shard(name) + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as fh:
        json.dump(payload, fh)
    os.replace(tmp, _shard(name))


def _stub_vector(text: str) -> list[float]:
    """A deterministic pseudo-embedding for --dry-run.

    DELIBERATELY WEAK, like the probe tool's stub model. It hashes character 3-grams into 64 buckets,
    so it carries a little lexical signal and no semantics -- a dry run that produced a GOOD dense
    result would tell you nothing about the real one, and might talk you out of the real run.
    """
    vec = [0.0] * 64
    for i in range(len(text) - 2):
        # sha256, not sha1. The digest is used as an arbitrary bucket index, so the choice is
        # free -- and a scanner alert that has to be argued away in a comment costs more than
        # picking the algorithm nobody has to argue about.
        h = hashlib.sha256(text[i:i + 3].encode('utf-8')).digest()
        vec[h[0] % 64] += 1.0
    norm = sum(v * v for v in vec) ** 0.5 or 1.0
    return _pack([v / norm for v in vec])


def _retry_delay(error, attempt: int) -> int:
    """Seconds to wait, preferring what the service asked for over what we guessed."""
    hinted = error.headers.get('Retry-After') if error.headers else None
    if hinted and str(hinted).strip().isdigit():
        return min(90, max(5, int(str(hinted).strip())))
    return min(60, 5 * 2 ** attempt)


def embed_all(texts: list[str], dry_run: bool) -> None:
    """Fill the cache for every text not already in it."""
    todo = [t for t in texts if _key(t) not in _cache]
    if not todo:
        return
    if dry_run:
        for t in todo:
            _cache[_key(t)] = _stub_vector(t)
            _stats['stub'] += 1
        return

    endpoint, key, deployment = _config()
    url = f"{endpoint}/openai/deployments/{deployment}/embeddings?api-version={API_VERSION}"
    for start in range(0, len(todo), BATCH):
        batch = todo[start:start + BATCH]
        body = json.dumps({"input": batch}).encode('utf-8')
        request = urllib.request.Request(
            url, data=body, method='POST',
            headers={"Content-Type": "application/json", "api-key": key})
        # RETRY ON THE SERVICE'S OWN TIMESCALE, NOT ON A GUESS. The first version backed off
        # 1/2/4/8 seconds and lost a full-family run to a 429: the limit is a TOKENS-PER-MINUTE
        # window, so every retry inside the first fifteen seconds is spent against a window that
        # has not moved. Retry-After is honoured where the service sends it, and the fallback
        # grows to a minute, which is the granularity the limit is enforced at.
        payload = None
        for attempt in range(8):
            try:
                with urllib.request.urlopen(request, timeout=180) as response:  # DevSkim: ignore DS137138
                    payload = json.loads(response.read().decode('utf-8'))
                break
            except urllib.error.HTTPError as error:
                if error.code not in (429, 500, 502, 503, 504) or attempt == 7:
                    raise
                delay = _retry_delay(error, attempt)
                _stats['throttled'] += 1
                print('    %d, waiting %ds' % (error.code, delay), flush=True)
                time.sleep(delay)
        if payload is None:
            raise SystemExit(
                'embeddings kept refusing after 8 attempts. Every vertical that finished is banked, so re-running resumes rather than restarts.')
        rows = sorted(payload['data'], key=lambda d: d['index'])
        if len(rows) != len(batch):
            raise SystemExit('embeddings returned %d vectors for %d inputs' % (len(rows), len(batch)))
        for text, row in zip(batch, rows):
            _cache[_key(text)] = _pack(row['embedding'])
        _stats['embedded'] += len(batch)
        if (start // BATCH) % 8 == 0 or start + BATCH >= len(todo):
            print('    embedded %d/%d' % (min(start + BATCH, len(todo)), len(todo)), flush=True)
        time.sleep(REQUEST_SPACING)


def _key(text: str) -> str:
    return hashlib.sha256(text.encode('utf-8')).hexdigest()[:32]


def cosine_rank(query: str, documents: list[str]) -> list[int]:
    """Cosine, not dot product. The embeddings service returns unit-norm vectors so the two
    coincide today -- but a ranking that is only correct while an upstream invariant holds is a
    ranking that breaks silently when it stops."""
    q = _unpack(_cache[_key(query)])
    qn = sum(a * a for a in q) ** 0.5 or 1.0
    scored = []
    for i, doc in enumerate(documents):
        d = _unpack(_cache[_key(doc)])
        dn = sum(b * b for b in d) ** 0.5 or 1.0
        scored.append((-sum(a * b for a, b in zip(q, d)) / (qn * dn), i))
    scored.sort()
    return [i for _, i in scored]


def render_one(session, date) -> str:
    """EXACTLY the document V9 ranks. Two spellings of one document is how an arm ends up
    measuring something the other arm never saw."""
    turns = "\n".join(f"{t['role']}: {t['content']}" for t in session)
    return f"### Session 1 ({date})\n{turns}"


def _stamp(by_shape, args, k: int) -> None:
    """Write `retriever_sensitivity` into every measured vertical's sidecar.

    REFUSES A PARTIAL RUN. `--limit` or `--vertical` or a non-K_ref budget would stamp a claim
    about shapes this run never measured, or measure them at a budget the family does not
    publish at -- and a sidecar is the one place a consumer takes a number at face value. The
    corpus bytes do not move, so `corpus_sha256` is unchanged and no consumer control resets;
    only the measurement block grows.
    """
    if args.limit or args.vertical or k != tmc.K_REF or args.dry_run:
        raise SystemExit('--stamp needs a full-family run at K_ref with real embeddings; this run was partial, and a partial stamp is a claim about shapes it never measured')

    per_vertical = collections.defaultdict(dict)
    for (vertical, shape), c in by_shape.items():
        n = c['n']
        bm25, dense = c['bm25_all'] / n, c['dense_all'] / n
        per_vertical[vertical][shape] = {
            'questions': n,
            'allgold_bm25': round(bm25, 4),
            'allgold_dense': round(dense, 4),
            'predicted_headroom_bm25': round(1 - bm25, 4),
            'predicted_headroom_dense': round(1 - dense, 4),
            'discriminates_under_dense': (1 - dense) >= 0.15,
        }

    reading = (
        "Published headroom is V1-V9, and V9 uses a BM25 retriever at K_ref=5. That pairing is a "
        "CONDITION of every headroom figure in this sidecar and was left implicit until "
        "2026-09-13. This block states it. `allgold_dense` is the same measurement with an "
        "embedding retriever over the same documents and the same budget; `1 - ALLgold` predicts "
        "headroom at slope +0.905 / R^2 0.853 / median residual 0.000 against this family's "
        "published figures, over-stating by +0.032. A shape with "
        "`discriminates_under_dense: false` can still rank two systems for a BM25 consumer and "
        "cannot for an embedding one. This is a PREDICTION from retrieval, not a probe run, and "
        "it says nothing about any consumer's chunking, reranking or query rewriting.")

    for vertical, shapes in sorted(per_vertical.items()):
        path = os.path.join(CORPORA, vertical,
                            'agenteval-typedmemeval-%s-v5.meta.json' % vertical)
        if not os.path.exists(path):
            continue
        meta = json.loads(open(path, encoding='utf-8-sig').read())
        meta.setdefault('probes', {})['retriever_sensitivity'] = {
            'reference_retriever': tmc.RETRIEVER_ID,
            'reference_k': tmc.K_REF,
            'dense_retriever': 'azure-openai-embeddings, cosine, same documents and budget',
            'operand': 'ALLgold -- gold.issubset(top_k), the quantity V9 tracks',
            'reading': reading,
            'by_shape': dict(sorted(shapes.items())),
        }
        with open(path, 'w', encoding='utf-8') as fh:
            json.dump(meta, fh, indent=2, ensure_ascii=False)
            fh.write('\n')
        print('  stamped %s (%d shapes)' % (vertical, len(shapes)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--dry-run', action='store_true',
                    help='stub embedder, spends nothing, exercises the whole path')
    ap.add_argument('--vertical', help='one vertical instead of the family')
    ap.add_argument('--limit', type=int, help='first N questions per vertical')
    ap.add_argument('--seed', type=int, default=20260913)
    # --budget rather than --k. argparse accepts unambiguous PREFIXES, and `--k 10` matches
    # `--k-sweep` -- it ran a one-row sweep and printed a K_ref table, which looks like a result.
    ap.add_argument('--stamp', action='store_true',
                    help='write the per-shape sensitivity into each sidecar. Refuses unless the run covered the whole family at K_ref, because a partial stamp is a claim about shapes it never measured.')
    ap.add_argument('--budget', type=int, default=tmc.K_REF,
                    help='retrieval budget for the per-shape table (default K_REF=5)')
    ap.add_argument('--k-sweep', metavar='K,K,...',
                    help='also report ALLgold at these retrieval budgets. K_ref=5 is a single point, and the identity makes headroom a statement about the BUDGET first.')
    args = ap.parse_args()

    rng = random.Random(args.seed)  # DevSkim: ignore DS148264 - a control arm, not a secret

    paths = sorted(glob.glob(os.path.join(CORPORA, '*', '*-v5.json')))
    paths = [p for p in paths if not p.endswith('.meta.json')]
    if args.vertical:
        paths = [p for p in paths if os.path.basename(os.path.dirname(p)) == args.vertical]
    if not paths:
        raise SystemExit('no corpora matched')

    k = args.budget
    sweep = sorted({int(x) for x in args.k_sweep.split(",")}) if args.k_sweep else []
    by_shape = collections.defaultdict(lambda: collections.Counter())
    by_k = collections.defaultdict(lambda: collections.Counter())
    total_questions = 0

    # ONE VERTICAL AT A TIME. Holding the family's vectors at once is what made the first
    # version a memory problem; the arms are computed per question and only the COUNTS survive
    # the loop, so peak residency is one vertical rather than ten.
    for path in paths:
        vertical = os.path.basename(os.path.dirname(path))
        entries = json.load(open(path, encoding='utf-8'))
        if args.limit:
            entries = entries[:args.limit]

        questions = []
        for entry in entries:
            gold_ids = set(entry['answer_session_ids'])
            gold = {i for i, sid in enumerate(entry['haystack_session_ids']) if sid in gold_ids}
            if not gold:
                continue        # no gold -> ALLgold is 0/0; excluded, as everywhere else here
            docs = [render_one(sess, date) for sess, date in
                    zip(entry['haystack_sessions'], entry['haystack_dates'])]
            questions.append({
                'shape': (entry.get('typedmemeval') or {}).get('shape') or '(none)',
                'question': entry['question'],
                'docs': docs,
                'gold': gold,
            })
        if not questions:
            continue

        texts = []
        for q in questions:
            texts.append(q['question'])
            texts.extend(q['docs'])
        texts = list(dict.fromkeys(texts))

        _cache.clear()
        _load_cache([vertical])
        print('%-14s %3d questions, %5d texts%s'
              % (vertical, len(questions), len(texts), ' (STUB)' if args.dry_run else ''),
              flush=True)
        embed_all(texts, args.dry_run)
        if not args.dry_run:
            _save_shard(vertical, [_key(t) for t in texts])

        for q in questions:
            docs, gold = q['docs'], q['gold']
            # RANK ONCE, SLICE MANY. Re-ranking per budget would be the same ordering computed
            # again, and any drift between the two would be an artefact of this loop rather
            # than of the budget.
            ranked = {'bm25': tmc.bm25_rank(q['question'], docs),
                      'dense': cosine_rank(q['question'], docs)}
            shuffled = list(range(len(docs)))
            # DevSkim: ignore DS148264 - the RANDOM CONTROL ARM. A seeded RNG is the point:
            # this arm is the floor the dense arm is measured against, and a floor that
            # changes between runs cannot be compared to anything.
            rng.shuffle(shuffled)  # DevSkim: ignore DS148264
            ranked['random'] = shuffled
            cell = by_shape[(vertical, q['shape'])]
            cell['n'] += 1
            for arm, order in ranked.items():
                top = set(order[:k])
                cell[arm + '_all'] += 1 if gold.issubset(top) else 0
                cell[arm + '_share'] += len(gold & top) / len(gold)
                for budget in sweep:
                    wide = set(order[:budget])
                    by_k[budget]['n' if arm == 'bm25' else 'skip'] += 1
                    by_k[budget][arm + '_all'] += 1 if gold.issubset(wide) else 0
        total_questions += len(questions)

    if not total_questions:
        raise SystemExit('the scan found no questions with gold, so it measured nothing')
    print()
    print('  embedded this run: %d real, %d stub' % (_stats['embedded'], _stats['stub']))
    print()

    print('ALLgold = the rate at which top-%d holds ALL of a question\'s gold.' % k)
    if k != tmc.K_REF:
        print('\u26a0 BUDGET K=%d, NOT the published K_ref=%d. Every headroom figure this'
              % (k, tmc.K_REF))
        print('  family publishes is the K_ref point; this table is a different budget.')
    print('SS88.12: V9\'s pass rate EQUALS this, so 1 - ALLgold predicts headroom.')
    print()
    print('%-14s %-24s %4s   %-17s %-17s %-17s' %
          ('vertical', 'shape', 'n', 'RANDOM all/share', 'BM25 all/share', 'DENSE all/share'))
    print('-' * 104)
    fam = collections.Counter()
    for (vertical, shape), c in sorted(by_shape.items()):
        n = c['n']
        for arm in ('random', 'bm25', 'dense'):
            fam[arm + '_all'] += c[arm + '_all']
            fam[arm + '_share'] += c[arm + '_share']
        fam['n'] += n
        print('%-14s %-24s %4d   %5.3f / %-9.3f %5.3f / %-9.3f %5.3f / %-9.3f' % (
            vertical, shape, n,
            c['random_all'] / n, c['random_share'] / n,
            c['bm25_all'] / n, c['bm25_share'] / n,
            c['dense_all'] / n, c['dense_share'] / n))

    n = fam['n']
    r_all, b_all, d_all = (fam['random_all'] / n, fam['bm25_all'] / n, fam['dense_all'] / n)
    print('-' * 104)
    print('%-14s %-24s %4d   %5.3f / %-9.3f %5.3f / %-9.3f %5.3f / %-9.3f' % (
        'FAMILY', '', n, r_all, fam['random_share'] / n,
        b_all, fam['bm25_share'] / n, d_all, fam['dense_share'] / n))
    print()

    # POSITIVE CONTROL BEFORE ANY FINDING. A dense arm that cannot beat random is not a weak
    # retriever, it is a broken one, and reporting its number as "dense retrieval does not help"
    # would be the most misleading sentence this tool could produce.
    margin = d_all - r_all
    print('CONTROL  dense - random = %+.3f on ALLgold' % margin)
    if args.dry_run:
        print('  (dry run: the stub carries 3-gram lexical signal and no semantics, so this number')
        print('   says the PATH works and nothing about real dense retrieval.)')
    elif margin < 0.05:
        print('  \U0001f534 DENSE DOES NOT BEAT RANDOM. Treat this run as a WIRING FAULT, not a')
        print('     finding: an embedding call returning constant or mismatched vectors produces a')
        print('     stable arbitrary ranking, which is what this looks like. Do not quote the')
        print('     numbers above until this margin is real.')
        return 1
    else:
        print('  dense clearly beats random, so the comparison below is about retrievers.')
    print()

    if sweep:
        print('THE SECOND MONOCULTURE: retrieval BUDGET')
        print('  K_ref is 5 and every published headroom figure is that one point. ALLgold at')
        print('  other budgets, same rankings sliced wider:')
        print()
        print('    %5s  %8s  %8s  %8s   predicted headroom (BM25 / DENSE)'
              % ('K', 'RANDOM', 'BM25', 'DENSE'))
        for budget in sweep:
            row = by_k[budget]
            m = row['n'] or 1
            print('    %5d  %8.3f  %8.3f  %8.3f       %.3f / %.3f'
                  % (budget, row['random_all'] / m, row['bm25_all'] / m, row['dense_all'] / m,
                     1 - row['bm25_all'] / m, 1 - row['dense_all'] / m))
        print()
    if args.stamp:
        _stamp(by_shape, args, k)

    print('WHAT IT MEANS FOR PUBLISHED HEADROOM  (prediction, not measurement)')
    print('  predicted headroom = 1 - ALLgold      BM25 %.3f   ->   DENSE %.3f'
          % (1 - b_all, 1 - d_all))
    # SIGN SPELLED OUT RATHER THAN LEFT TO A %+.3f. A dense arm can land on either side of BM25,
    # and 'closes -0.224 of headroom' is a sentence a reader has to decode instead of read.
    # d_all - b_all, NOT the other way round. I wrote this subtraction backwards and the
    # two-question stage run caught it: dense scored ALLgold 1.000 against BM25's 0.000 and the
    # summary said 'dense retrieves LESS gold'. Same class as the ANY-gold/ALL-gold inversion in
    # SS88.12 -- a reference quantity whose SIGN nobody checked against a case with a known answer.
    closed = d_all - b_all           # >0 when DENSE retrieves MORE gold, i.e. headroom shrinks
    room = 1 - b_all
    if room > 0:
        if closed < 0:
            print('  dense retrieves LESS gold than BM25: headroom would GROW by %.3f (%.0f%% of'
                  ' the %.3f BM25 leaves open).' % (-closed, 100.0 * -closed / room, room))
        else:
            print('  dense retrieves MORE gold than BM25: headroom SHRINKS by %.3f, which is'
                  ' %.0f%% of the %.3f BM25 leaves open.' % (closed, 100.0 * closed / room, room))
    print()
    print('  ⚠ The identity has R^2 0.868 and OVER-predicts on the cases checked so far, so read')
    print('    the DIRECTION and discount the magnitude. The measurement is a V9 re-run against')
    print('    dense top-%d; this run exists to say whether that spend is warranted.' % k)
    return 0


# Guarded so the cosine ranking and the embedding cache can be REUSED rather than reimplemented.
# A second copy of the ranking is a second thing to get wrong, and the two would drift silently.
if __name__ == "__main__":
    sys.exit(main())
