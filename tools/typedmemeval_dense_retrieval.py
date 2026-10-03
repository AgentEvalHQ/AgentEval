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

PROVIDER
--------
The embedding provider follows `AI_INFERENCE_PROVIDER`, by the rules `inference_provider.py` shares with
the CLI -- with one deliberate difference: an UNSET selector means azure, never auto-detection. The
retriever's identity is published, and auto-detection would let whichever keys sit in the shell pick it.

    unset / azure   AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY + AZURE_OPENAI_EMBEDDING_DEPLOYMENT,
                    api-version 2024-02-01, exactly as before. Vectors bank under
                    .typedmemeval_embeddings/<model behind the deployment>/, so every shard banked
                    before providers existed here stays where its readers look.
    bitdeer         BITDEER_API_KEY (BITDEER_ENDPOINT optional). Model: TYPEDMEMEVAL_EMBEDDING_MODEL,
                    default BAAI/bge-m3.
    openai, foundry, openai-compatible
                    that provider's variables (inference_provider.py); TYPEDMEMEVAL_EMBEDDING_MODEL is
                    REQUIRED -- no default, because a guessed embedding model is a guessed retriever.

Every provider but azure banks under .typedmemeval_embeddings/<provider_model slug>/ (bitdeer +
BAAI/bge-m3 -> bitdeer_BAAI_bge-m3/), and each shard is stamped with provider and model. A shard's keys
are text hashes, identical under every model, so without both guards one model's vectors would be
served as another's -- the probe cache's lesson (no model in its key), not repeated here.

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
import http.client
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
import inference_provider as ip    # noqa: E402

CORPORA = os.path.join(os.path.dirname(HERE), 'src', 'AgentEval.Memory', 'Data', 'typedmemeval')
API_VERSION = "2024-02-01"         # the azure embeddings path's api-version, unchanged

#: The embedding model for every provider except azure, which names a DEPLOYMENT in
#: AZURE_OPENAI_EMBEDDING_DEPLOYMENT as it always has (and ignores this variable).
EMBEDDING_MODEL_VARIABLE = 'TYPEDMEMEVAL_EMBEDDING_MODEL'

#: A default only where the project chose one (TME-2, 2026-10-03). Anywhere else the model must be named.
DEFAULT_EMBEDDING_MODEL = {'bitdeer': 'BAAI/bge-m3'}

#: Retried statuses: rate limits, server faults, and 520-524 -- Cloudflare's "the origin did not answer
#: properly", which a provider behind it (Bitdeer's edge) returns for a transient fault. Same set as the
#: probe runner, where one of them ended a full baseline pass partway through.
_TRANSIENT = (408, 429, 500, 502, 503, 504, 520, 521, 522, 523, 524)

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
    """The azure path's credentials, exactly as before providers existed here."""
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


#: The embedding provider's tag, resolved once per process. None = not yet resolved. Pinnable: the
#: contract check pins it (as it pins `_resolved_model`) so its result cannot depend on the shell.
_provider_tag = None


def _embedding_provider() -> str:
    """`azure` unless AI_INFERENCE_PROVIDER names another provider. Never auto-detected (module docstring)."""
    global _provider_tag
    if _provider_tag is None:
        _provider_tag = ip.selected_tag() or 'azure'
    return _provider_tag


def _requested_model() -> str:
    """The model a non-azure provider is asked for, or '' when none is named and there is no default."""
    return (os.environ.get(EMBEDDING_MODEL_VARIABLE, '').strip()
            or DEFAULT_EMBEDDING_MODEL.get(_embedding_provider(), ''))


def cache_root_for(tag: str, model: str) -> str:
    """Where one provider's model banks its vectors.

    Azure keeps the layout it has always had, CACHE_DIR/<model>, so the ada-002 and 3-small shards stay
    valid where they are. Every other provider gets CACHE_DIR/<slug of provider:model>: bge-m3 on bitdeer
    and bge-m3 on a local server are different deployments of one weight file, quantised and served
    differently, and they bank apart.
    """
    if tag == 'azure':
        return os.path.join(CACHE_DIR, model)
    return os.path.join(CACHE_DIR, ip.slug('%s:%s' % (tag, model)))


def embedding_identity() -> str:
    """`provider:model` of the dense arm -- the name a vector, a cached answer or a record must carry.
    '' when the model is unknown (an azure deployment whose model could not be resolved)."""
    model = _resolve_deployment_model()
    return '%s:%s' % (_embedding_provider(), model) if model else ''


def _embedding_request():
    """(url, headers, extra body fields) for one embeddings call, or exit naming what is missing.

    Reads the environment only; sends nothing. Nothing it returns is ever recorded -- the key and the
    endpoint stay in this process.
    """
    tag = _embedding_provider()
    if tag == 'azure':
        endpoint, key, deployment = _config()
        return (f"{endpoint}/openai/deployments/{deployment}/embeddings?api-version={API_VERSION}",
                {"Content-Type": "application/json", "api-key": key}, {})
    model = _resolve_deployment_model()
    if not model:
        sys.exit('%s=%s has no default embedding model, so %s must name one. Refusing to guess: a '
                 'guessed embedding model is a guessed retriever.'
                 % (ip.SELECTOR_VARIABLE, tag, EMBEDDING_MODEL_VARIABLE))
    try:
        provider = ip.resolve()
    except ip.ProviderNotConfigured as error:
        sys.exit('Embedding credentials are not set: %s\nWithout them there is no dense arm, and a run '
                 'that silently fell back to BM25 would report "no difference".' % error)
    if provider.tag != tag:
        sys.exit('the embedding provider is %r but the environment resolves %r' % (tag, provider.tag))
    # `headers()` carries an explicit User-Agent: Bitdeer's Cloudflare edge refuses urllib's default
    # with 403 / error 1010 before the request reaches the API.
    return provider.embeddings_url(), provider.headers(), {'model': model}


def describe_target(dry_run: bool) -> str:
    """One line naming the retriever this run would use. Never an endpoint or a key.

    A dry run never resolves an azure deployment to its model: that is a network call, and a dry run
    sends nothing.
    """
    tag = _embedding_provider()
    if tag == 'azure':
        ignored = (' (%s is ignored for azure: the deployment names the model)' % EMBEDDING_MODEL_VARIABLE
                   if os.environ.get(EMBEDDING_MODEL_VARIABLE, '').strip() else '')
        if dry_run:
            return ('azure deployment %r, api-version %s; the model behind it is resolved from the '
                    'deployments listing on a real run, not in a dry run%s'
                    % (_deployment_name(), API_VERSION, ignored))
        return ('%s (deployment %r, api-version %s), banked under %s%s'
                % (embedding_identity() or 'azure:(model unresolved)', _deployment_name(), API_VERSION,
                   os.path.relpath(_cache_root(), HERE), ignored))
    model = _resolve_deployment_model()
    if not model:
        return '%s: NO MODEL -- set %s (this provider has no default)' % (tag, EMBEDDING_MODEL_VARIABLE)
    source = (EMBEDDING_MODEL_VARIABLE if os.environ.get(EMBEDDING_MODEL_VARIABLE, '').strip()
              else "the %s default" % tag)
    return ('%s (model from %s), banked under %s'
            % (embedding_identity(), source, os.path.relpath(cache_root_for(tag, model), HERE)))


def _pack(vector) -> str:
    """float16 via struct, NOT array. `array` has no half-float typecode -- "e" is a struct
    format code and array.array("e", ...) raises ValueError: bad typecode. Two bytes per
    dimension instead of ~20 as JSON text is the whole reason the cache fits."""
    return base64.b64encode(struct.pack("<%de" % len(vector), *vector)).decode("ascii")


def _unpack(blob: str):
    data = base64.b64decode(blob)
    return struct.unpack("<%de" % (len(data) // 2), data)


def _cache_root() -> str:
    """The directory this run's vectors belong in: one per MODEL.

    A flat cache can hold one retriever. The moment a second embedding model runs -- which is the
    point of having a retriever id at all -- a flat cache either refuses the run or mixes the two,
    and mixing is the failure nobody can see afterwards.

    Falls back to the flat directory when the model cannot be resolved, so an unreachable
    deployments listing degrades to the previous behaviour rather than writing into a directory
    named ''. AZURE ONLY: the flat directory holds pre-split azure shards, and a non-azure provider
    with no model has nothing to bank, so it stops instead.
    """
    model = _resolve_deployment_model()
    tag = _embedding_provider()
    if tag != 'azure' and not model:
        raise SystemExit('%s names no embedding model for %s, so there is no directory its vectors '
                         'belong in.' % (EMBEDDING_MODEL_VARIABLE, tag))
    return cache_root_for(tag, model) if model else CACHE_DIR


def _shard(name: str) -> str:
    return os.path.join(_cache_root(), '%s.json' % name)


def _flat_shard(name: str) -> str:
    """Where shards lived before the split. READ ONLY, and only when the stamp matches."""
    return os.path.join(CACHE_DIR, '%s.json' % name)


#: Key under which a shard records WHICH deployment produced its vectors.
#:
#: A shard was keyed by text hash alone, so re-running against a different
#: AZURE_OPENAI_EMBEDDING_DEPLOYMENT -- or copying a shard between machines -- silently skipped
#: re-embedding and ranked with vectors from the other model. The run would look complete and
#: cost nothing, and the dense arm would be a comparison between two retrievers that were never
#: the same one. Found in review of PR #238.
#:
#: The deployment NAME is recorded, never the endpoint or the key. Off azure there is no alias, and
#: this key holds the identity itself, `provider:model` (`bitdeer:BAAI/bge-m3`) -- which no azure
#: deployment name can spell, so the two can never match each other.
_PROVENANCE_KEY = '__embedding_deployment__'

#: The provider that produced a shard's vectors. Absent on every shard banked before providers existed
#: here, all of which came from azure, so absent reads as `azure` -- and nothing else does.
_PROVIDER_KEY = '__embedding_provider__'


#: Keys under which a shard records WHAT its vectors are, as opposed to where they came from.
#:
#: `_PROVENANCE_KEY` above holds the deployment NAME, which is a LOCAL identity: an alias the
#: resource owner picked. It is the right thing to refuse a mismatch on and the wrong thing to
#: publish -- `embeddings` on one resource and `embeddings` on another are different retrievers
#: wearing one name, and repointing an alias changes the model without changing the name. These
#: two record the model and the vector width, which is what a reader needs to reproduce a ranking.
_MODEL_KEY = '__embedding_model__'
_DIMS_KEY = '__embedding_dims__'

#: Resolved once per process. None = not yet looked up, '' = looked up and could not be resolved.
_resolved_model = None

#: Whether the on-disk vectors have been checked against the live deployment this run.
_provenance_checked = False

#: Whether ANY vector came off disk. A run that bought every vector it used needs no provenance
#: check -- the live deployment produced them, by construction. Without this the stamp refused a
#: wholly fresh run, which is the one case where the provenance is least in doubt.
_loaded_from_disk = False


def _deployment_name() -> str:
    """The stamp `_PROVENANCE_KEY` carries: the azure deployment alias, or `provider:model` elsewhere."""
    tag = _embedding_provider()
    if tag != 'azure':
        return '%s:%s' % (tag, _resolve_deployment_model() or '(unset)')
    return os.environ.get("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", "") or "(unset)"


def _resolve_deployment_model() -> str:
    """The MODEL behind AZURE_OPENAI_EMBEDDING_DEPLOYMENT, or '' if it cannot be resolved.

    Returns '' rather than falling back to the deployment name. A retriever identity built from
    an alias identifies nothing, and one that GUESSES is worse than one that is absent: a reader
    can act on a missing field and cannot act on a wrong one.

    Off azure there is no alias to resolve: the request names the model, so the model is what the
    caller asked for (TYPEDMEMEVAL_EMBEDDING_MODEL or the provider's default) -- the same standing
    the probe runner gives its `reference_model`. No network call either way off azure.
    """
    global _resolved_model
    if _resolved_model is not None:
        return _resolved_model
    if _embedding_provider() != 'azure':
        _resolved_model = _requested_model()
        return _resolved_model
    _resolved_model = ''
    endpoint = os.environ.get("AZURE_OPENAI_ENDPOINT", "").rstrip("/")
    key = os.environ.get("AZURE_OPENAI_API_KEY", "")
    if not (endpoint and key):
        return _resolved_model
    # 2023-03-15-preview is not a stale copy-paste: it is the ONLY api-version this data plane
    # answers a deployments listing on. 2023-05-15, 2024-06-01 and 2024-10-21 all return 404.
    try:
        request = urllib.request.Request(
            endpoint + "/openai/deployments?api-version=2023-03-15-preview",
            headers={"api-key": key})
        with urllib.request.urlopen(request, timeout=60) as response:  # DevSkim: ignore DS137138
            listing = json.loads(response.read().decode("utf-8"))
    except Exception:
        return _resolved_model
    for item in listing.get("data", []):
        if item.get("id") == _deployment_name():
            _resolved_model = item.get("model") or ''
            break
    return _resolved_model


def dense_retriever_id(model: str, dims: int, provider: str = 'azure') -> str:
    """A versioned identity for the dense arm, in the shape `tmc.RETRIEVER_ID` uses for BM25.

    WHY THIS EXISTS. `RETRIEVER_ID` is `bm25-okapi-k1.5-b0.75`: the algorithm plus the two knobs
    that change its answers, so a published coverage figure names the thing that produced it. The
    dense arm shipped instead with the prose string "azure-openai-embeddings, cosine, same
    documents and budget" -- a vendor and a similarity function, pinning NOTHING. Every embedding
    model Azure has ever served satisfies that sentence, and they do not rank the same documents.
    It was measured but not VERSIONED, which is the open half of the C-B row.

    Each field is here because it changes the ranking, not because it was available:
      * provider -- who served the model. `azure` for every id published before 2026-10, whose
                  spelling is therefore unchanged (`azure-emb-...`); `bitdeer-emb-BAAI/bge-m3-...`
                  after TME-2. One weight file served by two providers is two retrievers.
      * model  -- the whole retriever. This family's was `text-embedding-ada-002`, which a reader
                  seeing only the word "azure-openai-embeddings" would have had no way to know.
      * dims   -- the width of the space the cosine is taken in.
      * cosine -- the similarity, taken over L2-normalised vectors (`cosine_rank` normalises at
                  rank time rather than trusting the service's unit-norm invariant).
      * f16    -- vectors are stored as float16. A lossy step between the model and the ranking
                  can reorder near-ties, so it belongs in the identity of the ranking.

    Returns '' when the model is unknown, which is the caller's signal to refuse to publish.
    """
    if not model:
        return ''
    return '%s-emb-%s-d%d-cosine-f16' % (provider, model, dims)


#: The dense column published until 2026-10. The stamp's 2026-09-14 model-comparison figures were
#: measured against it and are written only beside it.
_ADA_REFERENCE_ID = dense_retriever_id('text-embedding-ada-002', 1536)


def _stamp_refusal(shard: dict):
    """Why a shard's vectors must NOT be used by this run, or None when its stamp names this run's
    provider, deployment and model. The ONE rule both the loader and the saver apply: a shard the
    loader refuses must not be laundered back in by the merge-on-save under this run's stamp."""
    stamped = shard.get(_PROVENANCE_KEY)
    stamped_model = shard.get(_MODEL_KEY)
    if stamped is None or stamped_model is None:
        # UNSTAMPED IS REFUSED, not accepted. The guard used to read `if stamped is not None`,
        # which accepts a shard carrying no provenance at all under ANY deployment -- and the
        # one such file, `_migrated.json`, was read for every vertical. It was back-stamped on
        # 2026-09-14 after re-embedding one of its own texts, so every shard on disk now
        # carries provenance and requiring it costs nothing.
        #
        # It also closes the hole for callers that never reach `_verify_cache_matches_live`:
        # `typedmemeval_v9_dense.py` loads this cache directly, and a structural refusal
        # protects it whether or not it runs the live probe.
        return ('no model provenance. Re-embed it, or delete it -- a shard that cannot say which '
                'model produced it cannot be ranked against one.')
    stamped_provider = shard.get(_PROVIDER_KEY) or 'azure'
    if stamped_provider != _embedding_provider():
        # A DIFFERENT PROVIDER IS A DIFFERENT RETRIEVER, whatever the names say. Checked first so the
        # refusal names the real difference rather than a deployment string that merely differs.
        return ('built on provider %r, this run embeds on %r'
                % (stamped_provider, _embedding_provider()))
    if stamped != _deployment_name():
        # REFUSE RATHER THAN MIX. Two models' vectors in one ranking is not a weaker
        # measurement, it is not a measurement.
        return 'built by deployment %r, this run uses %r' % (stamped, _deployment_name())
    live_model = _resolve_deployment_model()
    if stamped_model and live_model and stamped_model != live_model:
        # The alias matched and the MODEL did not, which is the case the deployment-name
        # check above cannot see: someone repointed the alias. Same refusal, different
        # operand -- and this is the operand that actually gets published.
        return 'built by model %r, this deployment now serves %r' % (stamped_model, live_model)
    return None


def _load_cache(names, dry_run: bool = False) -> None:
    """Load only the shards this run will touch. Loading the family to measure one vertical is
    the same mistake in a smaller coat.

    `_migrated` is always read: it holds the vectors bought under the first cache format, which
    was replaced mid-run. They are paid for and identical -- keyed by sha256 of the same text --
    so re-buying them would be spending money to reproduce bytes already on disk."""
    if dry_run:
        # A DRY RUN MUST NOT READ THE REAL SHARDS. It loaded them, found every text already
        # banked, embedded nothing, and printed `DENSE 1.000` under the line "the stub carries
        # 3-gram lexical signal and no semantics, so this number says the PATH works and nothing
        # about real dense retrieval". That number WAS real dense retrieval. The disclaimer and
        # the measurement described different runs, and the counter agreed with the disclaimer
        # (`0 real, 0 stub`) because nothing was embedded either way.
        #
        # Understating a result is still a claim that does not match its artifact. The dry run's
        # whole job is to exercise the path on vectors it produced itself, so it gets none.
        return
    global _loaded_from_disk
    for name in list(names) + ['_migrated']:
        path = _shard(name)
        if not os.path.exists(path):
            # PRE-SPLIT SHARDS ARE READ, NEVER WRITTEN BACK. They are accepted only when their
            # stamp matches; the one unstamped file, `_migrated.json`, was back-stamped on
            # 2026-09-14 after re-embedding one of its own texts and measuring the agreement.
            # Azure only: every pre-split shard is an azure one.
            flat = _flat_shard(name)
            if _embedding_provider() != 'azure' or not os.path.exists(flat):
                continue
            path = flat
        try:
            shard = json.loads(open(path, encoding='utf-8').read())
        except json.JSONDecodeError:
            continue                  # a torn shard is re-embedded, never half-trusted
        refusal = _stamp_refusal(shard)
        # POPPED BEFORE THE UPDATE, ALL FOUR. A provenance key left in the shard becomes a
        # cache entry keyed by a string that is not a text hash, and `_save_shard` would write it
        # back as if it were a vector.
        for key in (_PROVENANCE_KEY, _MODEL_KEY, _DIMS_KEY, _PROVIDER_KEY):
            shard.pop(key, None)
        if refusal:
            print('    ignoring shard %s: %s' % (name, refusal), flush=True)
            continue
        _cache.update(shard)
        _loaded_from_disk = _loaded_from_disk or bool(shard)


def _save_shard(name: str, keys) -> None:
    """Write this run's vectors into the vertical's shard, KEEPING what the shard already held.

    IT USED TO REPLACE. `payload` was built from this run's keys alone, so a `--limit 4` run wrote
    a four-question shard over a full one and the rest of the vectors were gone. Measured on
    2026-09-14 before this fix: 8,941 of the family's 15,040 vectors were banked nowhere --
    `episodic.json` held 27 of 1,179, `workingmemory.json` 62 of 3,672 -- while `arithmetic` and
    `bitemporal`, the two verticals never re-run with `--limit`, were intact at 1,259 and 1,274.
    The shards that survived are the ones nobody touched, which is the signature of the writer
    rather than the reader.

    It is only about $0.25 of ada-002 to re-buy, and that is the reason to fix it rather than a
    reason not to: the tool that discards a cache discards whatever cache it is pointed at, and
    the next one will not be a cheap one. A partial run now costs the shard nothing.
    """
    os.makedirs(_cache_root(), exist_ok=True)
    existing = _shard(name)
    # READ, MERGE AND REPLACE UNDER ONE LOCK. `os.replace` makes each write atomic and does nothing
    # about two writers: both would read the same prior shard, add their own vectors, and the
    # second would drop the first's. That is this very defect one level up.
    with tmc.exclusive_file_lock(existing):
        payload = {}
        # THE MIGRATION PATH IS THE LOSS PATH AGAIN. On the first save after the per-model split,
        # `existing` does not exist yet -- but `_load_cache` may have just accepted the PRE-SPLIT
        # flat shard. Writing only this run's keys would leave the flat file shadowed: later loads
        # prefer the model-scoped shard, find a partial one, and the rest is lost exactly as the
        # `--limit` defect lost 8,941 vectors. The flat file is folded in on that first write, and
        # only when its own stamp matches -- a mismatched one was already refused at load and must
        # not be resurrected here. Found in review of PR #245.
        flat = _flat_shard(name)
        if (_embedding_provider() == 'azure'
                and not os.path.exists(existing) and os.path.exists(flat)):
            try:
                legacy = json.loads(open(flat, encoding='utf-8').read())
            except json.JSONDecodeError:
                legacy = {}
            if (legacy.get(_PROVENANCE_KEY) == _deployment_name()
                    and legacy.get(_MODEL_KEY) == (_resolve_deployment_model() or None)):
                payload.update({k: v for k, v in legacy.items() if not k.startswith('__')})
        if os.path.exists(existing):
            try:
                prior = json.loads(open(existing, encoding='utf-8').read())
            except json.JSONDecodeError:
                prior = {}          # a torn shard is replaced, never half-merged
            refusal = _stamp_refusal(prior) if prior else None
            if refusal:
                # A SHARD THE LOADER REFUSED MUST NOT BE MERGED BACK UNDER THIS RUN'S STAMP. The merge
                # used to take whatever sat in the directory and re-stamp it -- so a shard copied into
                # the wrong model's directory was refused at load and then laundered at save: its
                # vectors not re-embedded this run (a --limit run, say) would be published as this
                # model's. Set aside rather than deleted: they were paid for, by some model.
                aside = '%s.refused-%d' % (existing, os.getpid())
                os.replace(existing, aside)
                print('    not merging shard %s (%s); moved aside to %s'
                      % (name, refusal, os.path.basename(aside)), flush=True)
            else:
                # `__`-prefixed provenance is re-derived below, never carried forward: a stale model
                # stamp surviving a merge would be a claim about vectors it no longer describes.
                payload.update({k: v for k, v in prior.items() if not k.startswith('__')})
        payload.update({k: _cache[k] for k in keys if k in _cache})
        sample = next(iter(payload.values()), None)
        payload[_PROVENANCE_KEY] = _deployment_name()
        payload[_MODEL_KEY] = _resolve_deployment_model() or None
        payload[_DIMS_KEY] = len(_unpack(sample)) if sample else None
        payload[_PROVIDER_KEY] = _embedding_provider()
        # PER-PROCESS TEMP NAME. A single `.tmp` is a second way two writers destroy each other,
        # independent of the merge: both write the same scratch file and the first replace consumes
        # it, so the second gets FileNotFoundError and loses its whole run. The lock above makes
        # this unreachable for THIS writer, and the name is still made unique -- a shared scratch
        # path between processes is wrong whether or not something else currently prevents the
        # overlap. Surfaced by the lock ablation in review of PR #245.
        tmp = '%s.%d.tmp' % (existing, os.getpid())
        with open(tmp, 'w', encoding='utf-8') as fh:
            json.dump(payload, fh)
        os.replace(tmp, existing)


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
    headers = getattr(error, 'headers', None)       # a dropped connection carries none
    hinted = headers.get('Retry-After') if headers else None
    if hinted and str(hinted).strip().isdigit():
        return min(90, max(5, int(str(hinted).strip())))
    return min(60, 5 * 2 ** attempt)


#: Model names a non-azure provider REPORTED serving, where they differ from the one requested.
#: `_stamp` refuses on any: the identity names the requested model, so it must be the one that answered.
_served_mismatch: set = set()


def _same_model(requested: str, served: str) -> bool:
    """Whether a provider's reported model is the requested one. Lenient on spelling only -- case, and an
    organisation prefix some providers drop (`BAAI/bge-m3` vs `bge-m3`) -- never on the model."""
    def norm(name):
        return name.strip().lower().rsplit('/', 1)[-1]
    return norm(requested) == norm(served)


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

    url, headers, fields = _embedding_request()
    for start in range(0, len(todo), BATCH):
        batch = todo[start:start + BATCH]
        # `fields` is empty on azure (the URL names the deployment), so its body is byte-for-byte
        # what it always was; every other provider names the model here.
        body = json.dumps(dict(fields, input=batch)).encode('utf-8')
        request = urllib.request.Request(url, data=body, method='POST', headers=headers)
        # RETRY ON THE SERVICE'S OWN TIMESCALE, NOT ON A GUESS. The first version backed off
        # 1/2/4/8 seconds and lost a full-family run to a 429: the limit is a TOKENS-PER-MINUTE
        # window, so every retry inside the first fifteen seconds is spent against a window that
        # has not moved. Retry-After is honoured where the service sends it, and the fallback
        # grows to a minute, which is the granularity the limit is enforced at.
        #
        # A DROPPED CONNECTION IS TRANSIENT TOO. Only HTTP statuses were retried, so a connection
        # reset or a read timeout -- routine on a long run through a CDN edge -- ended the run with
        # a traceback. Retried on the same schedule as the probe runner's; a non-transient status
        # (400, 401, 403) still stops at once, naming itself.
        payload = None
        for attempt in range(8):
            try:
                with urllib.request.urlopen(request, timeout=180) as response:  # DevSkim: ignore DS137138
                    payload = json.loads(response.read().decode('utf-8'))
                break
            except urllib.error.HTTPError as error:
                if error.code not in _TRANSIENT:
                    detail = error.read().decode('utf-8', 'replace')[:300]
                    raise SystemExit('embeddings request rejected (%d): %s' % (error.code, detail))
                reason, delay = str(error.code), _retry_delay(error, attempt)
            except (urllib.error.URLError, TimeoutError, ConnectionError,
                    http.client.HTTPException) as error:
                reason, delay = type(error).__name__, _retry_delay(error, attempt)
            if attempt == 7:
                break
            _stats['throttled'] += 1
            print('    %s, waiting %ds' % (reason, delay), flush=True)
            time.sleep(delay)
        if payload is None:
            raise SystemExit(
                'embeddings kept failing after 8 attempts. Every vertical that finished is banked, so re-running resumes rather than restarts.')
        served = payload.get('model')
        if (served and _embedding_provider() != 'azure'
                and not _same_model(_resolve_deployment_model(), served)
                and served not in _served_mismatch):
            _served_mismatch.add(served)
            print('    ⚠ asked for %r, the provider reports serving %r. --stamp will refuse.'
                  % (_resolve_deployment_model(), served), flush=True)
        rows = sorted(payload['data'], key=lambda d: d['index'])
        if len(rows) != len(batch):
            raise SystemExit('embeddings returned %d vectors for %d inputs' % (len(rows), len(batch)))
        for text, row in zip(batch, rows):
            _cache[_key(text)] = _pack(row['embedding'])
        _stats['embedded'] += len(batch)
        if (start // BATCH) % 8 == 0 or start + BATCH >= len(todo):
            print('    embedded %d/%d' % (min(start + BATCH, len(todo)), len(todo)), flush=True)
        time.sleep(REQUEST_SPACING)


def _verify_cache_matches_live(texts) -> None:
    """Check that the vectors ALREADY ON DISK came from the deployment this run is about to name.

    THE STAMP IS OTHERWISE ENVIRONMENT-DERIVED. `_resolve_deployment_model()` asks what the alias
    points at TODAY; the shards hold vectors embedded on some earlier day. The deployment-name
    check cannot separate those, because the name is exactly what stays the same when an alias is
    repointed -- so a re-stamp with no re-embedding would publish today's model name over
    yesterday's vectors, and every number in the sidecar would belong to a retriever that is not
    the one named. That is the artifact supplying its own provenance.

    So: re-embed one text that is already banked and compare. One call, on the shortest cached
    text in the run. Runs once per process; skipped when nothing was cached, because then this run
    IS the source and there is nothing for it to disagree with.
    """
    global _provenance_checked
    if _provenance_checked:
        return
    cached = [t for t in texts if _key(t) in _cache]
    if not cached:
        return
    probe = min(cached, key=len)
    stored = _unpack(_cache.pop(_key(probe)))
    embed_all([probe], False)                 # reuses the real path, retries and all
    fresh = _unpack(_cache[_key(probe)])
    _provenance_checked = True

    if len(stored) != len(fresh):
        raise SystemExit(
            'the banked vectors are %d-dimensional and this deployment returns %d. They are '
            'different retrievers; a ranking mixing them is not a weaker measurement, it is not '
            'a measurement. Delete %s and re-embed.' % (len(stored), len(fresh), _cache_root()))
    num = sum(a * b for a, b in zip(stored, fresh))
    den = ((sum(a * a for a in stored) ** 0.5) * (sum(b * b for b in fresh) ** 0.5)) or 1.0
    agreement = num / den
    # NOT 1.0, AND NOT BECAUSE THE BAR IS BEING LOWERED TO PASS. The stored side has been through
    # a float16 round-trip (~1e-3 per component) and the service is not bit-deterministic across
    # calls, so the same model against itself lands just under 1. Two DIFFERENT models embed into
    # unrelated spaces and land near 0 -- there is no band between these where the test becomes a
    # judgement call.
    print('    provenance: banked vs live cosine %.6f on one re-embedded text' % agreement,
          flush=True)
    if agreement < 0.995:
        raise SystemExit(
            'banked vectors disagree with the live deployment (cosine %.4f). The shards were not '
            'built by the model this run would name. Delete %s and re-embed rather than publish '
            'a retriever id that describes neither.' % (agreement, _cache_root()))


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


def _carry_second_column(fresh: dict, prior: dict, prior_dense_id, dense_id):
    """Re-attach a co-published second column to freshly computed rows, where it still applies.

    The second column and its `retriever_agreement` are derived from BOTH retrievers, so they stay
    true only while this run's reference figures match the ones they were derived against. A shape
    whose reference moved gets the second column dropped, loudly -- a verdict re-attached to numbers
    it no longer describes is worse than an absent one.
    """
    # THE VERDICT IS ABOUT A PAIR, so the pair's first half has to match too. Rates and the
    # denominator pin the MEASUREMENT; they do not pin WHICH RETRIEVER produced it. A later stamp
    # under a different dense model whose figures happen to coincide would have re-attached the old
    # class and then written the new `dense_retriever` beside it -- a published class describing a
    # retriever pair that never existed. Found in review of PR #250.
    if prior_dense_id != dense_id:
        if any('second_dense' in (prior.get(s) or {}) for s in fresh):
            print('    dropping the second column across this vertical: it was paired with %r and '
                  'this run publishes %r' % (prior_dense_id, dense_id), flush=True)
        return dict(fresh), 0, len(fresh)

    out, carried, dropped = {}, 0, 0
    for shape, row in fresh.items():
        old = prior.get(shape) or {}
        keep = {k: old[k] for k in ('second_dense', 'retriever_agreement') if k in old}
        if keep:
            # THE DENOMINATOR IS PART OF THE IDENTITY. Rounded rates can survive a population
            # change -- a shape that gained or lost questions can land on the same figure -- and the
            # paired verdict was computed for the OLD population. Checked in review of PR #250.
            same = (old.get('questions') == row.get('questions')
                    and all(round(old.get(f, object()), 4) == round(row[f], 4)
                            for f in ('allgold_bm25', 'allgold_dense')))
            if same:
                row = dict(row, **keep)
                carried += 1
            else:
                print('    dropping the second column on %s: its reference figures moved, so the '
                      'paired verdict no longer describes them' % shape, flush=True)
                dropped += 1
        out[shape] = row
    return out, carried, dropped


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

    sample = next(iter(_cache.values()), None)
    dense_dims = len(_unpack(sample)) if sample else 0
    provider, model = _embedding_provider(), _resolve_deployment_model()
    dense_id = dense_retriever_id(model, dense_dims, provider)
    if not dense_id:
        raise SystemExit(
            'refusing to stamp: the embedding deployment did not resolve to a model, so the dense '
            'arm has no identity to record. The numbers would sit in a sidecar beside the name of '
            'a retriever nobody can reproduce, which is the defect this field exists to fix.')
    if _served_mismatch:
        raise SystemExit(
            'refusing to stamp: this run asked %s for %r and the provider reported serving %s. The '
            'id would name a model that did not answer. Confirm what the endpoint serves first.'
            % (provider, model, ', '.join(repr(s) for s in sorted(_served_mismatch))))
    if _loaded_from_disk and not _provenance_checked:
        raise SystemExit(
            'refusing to stamp: vectors were read from disk but none was re-embedded against the '
            'live deployment, so they were never shown to come from %s. Delete one shard and '
            're-run, or accept that the identity would be asserted rather than measured.'
            % dense_id)

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
        "2026-09-13. This block states it. `allgold_dense` is the same measurement with the "
        "retriever " + dense_id + " over the same documents and the same budget; `1 - ALLgold` predicts "
        "headroom at slope +0.905 / R^2 0.853 / median residual 0.000 against this family's "
        "published figures, over-stating by +0.032. A shape with "
        "`discriminates_under_dense: false` can still rank two systems for a BM25 consumer and "
        "cannot for an embedding one. This is a PREDICTION from retrieval, not a probe run, and "
        "it says nothing about any consumer's chunking, reranking or query rewriting. ")
    if dense_id == _ADA_REFERENCE_ID:
        # THE 2026-09-14 COMPARISON'S FIGURES DESCRIBE THE ADA-002 BLOCK AND NO OTHER. "this
        # block's 0.540" is ada-002's family ALLgold; written beside any other retriever's column it
        # would be a number about a model the block does not contain.
        reading += (
            "HOW MUCH OF THIS BELONGS TO THE MODEL RATHER THAN TO 'DENSE': re-run on 2026-09-14 "
            "against a second embedding model, text-embedding-3-small, over the same documents "
            "and the same budget. Family ALLgold 0.575 against this block's 0.540, so 'dense "
            "closes 21% of the headroom BM25 leaves open' becomes 27%. Per shape it is larger and "
            "not uniform: 25 of 35 shapes move, 6 flip the SIGN of whether dense beats BM25 (in "
            "both directions), and 4 flip `discriminates_under_dense` itself -- "
            "forgetting/still-valid, temporal/interval-position and workingmemory/distance-25 go "
            "true->false, workingmemory/distance-40 goes false->true. So read every figure here "
            "as a property of the NAMED retriever, not of dense retrieval. Reproduce with "
            "tools/typedmemeval_retriever_compare.py.")
    else:
        reading += (
            "HOW MUCH OF THIS BELONGS TO THE MODEL RATHER THAN TO 'DENSE': an earlier comparison "
            "of two embedding models found the per-shape verdict moves with the model (shapes "
            "flip `discriminates_under_dense` and the sign of dense-vs-BM25), but it was made "
            "against a different reference retriever and none of its figures describe this block. "
            "Read every figure here as a property of the NAMED retriever, not of dense retrieval. "
            "Compare against another model with tools/typedmemeval_retriever_compare.py.")
    if provider == 'azure':
        dense_note = (
            'Resolved from the deployment alias to the underlying model, and the banked '
            'vectors were re-checked against the live deployment before this stamp was '
            'written. Fields: model, dimensions, similarity, stored precision -- each one '
            'changes the ranking. Supersedes the prose string "azure-openai-embeddings, '
            'cosine, same documents and budget", which pinned no model at all.')
    else:
        dense_note = (
            'The model id requested from %s -- an OpenAI-compatible endpoint names the model in '
            'each request, so there is no deployment alias to resolve; the model the provider '
            'reported serving, where it reported one, was checked against it, and any banked '
            'vectors were re-checked against the live provider before this stamp was written. '
            'Fields: provider, model, dimensions, similarity, stored precision -- each one '
            'changes the ranking.' % provider)

    # SHAPES THIS MEASUREMENT CANNOT COVER ARE DECLARED, NOT OMITTED. A question with no gold is
    # skipped everywhere in this tool, correctly -- but a shape where EVERY question has no gold then
    # vanishes from `by_shape` entirely, and an absent row reads as "nothing to say" when the truth
    # is "the operand is undefined here".
    #
    # WHY IT IS UNDEFINED, which is the part worth publishing: `gold.issubset(top_k)` is vacuously
    # TRUE for an empty gold set. So ALLgold would come out at 1.000 under every retriever at every
    # budget -- the most flattering value available, and the least true. Excluding these shapes is
    # right; letting the exclusion be inferred was not. Raised by the consuming project in SEND-41
    # after their ranking-only column had to drop the shape on a guess.
    not_applicable = collections.defaultdict(dict)
    for path in sorted(glob.glob(os.path.join(CORPORA, '*', '*-v5.json'))):
        if path.endswith('.meta.json'):
            continue
        vertical = os.path.basename(os.path.dirname(path))
        counts = collections.Counter()
        golds = collections.Counter()
        for entry in json.load(open(path, encoding='utf-8')):
            shape = (entry.get('typedmemeval') or {}).get('shape') or '(none)'
            counts[shape] += 1
            gold_ids = set(entry['answer_session_ids'])
            if any(sid in gold_ids for sid in entry['haystack_session_ids']):
                golds[shape] += 1
        for shape, n in counts.items():
            if golds[shape] == 0 and shape not in per_vertical.get(vertical, {}):
                not_applicable[vertical][shape] = n

    for vertical, shapes in sorted(per_vertical.items()):
        path = os.path.join(CORPORA, vertical,
                            'agenteval-typedmemeval-%s-v5.meta.json' % vertical)
        if not os.path.exists(path):
            continue
        meta = json.loads(open(path, encoding='utf-8-sig').read())

        # A CO-PUBLISHED SECOND COLUMN MUST SURVIVE THIS TOOL. `retriever_sensitivity` is assigned
        # fresh below, so a plain re-stamp silently dropped `second_dense_retriever`, every
        # `second_dense` value and every `retriever_agreement` written by the compare tool -- shipped
        # fields, deleted by the tool that owns the block, with nothing saying so.
        #
        # Carried forward ONLY where the reference figures this run computes are identical to the
        # ones the classification was derived from. If a reference number moved, the paired verdict
        # is stale and is dropped rather than re-attached to a column it no longer describes.
        prior = ((meta.get('probes') or {}).get('retriever_sensitivity') or {})
        prior_shapes = prior.get('by_shape') or {}

        # AND THE CORPUS THE PAIR WAS COMPUTED OVER. Rates, denominator and the reference retriever
        # pin a great deal and still not the INPUT: a corpus edit that preserves the rounded
        # aggregates would carry the old second-model values onto new reference data, beside a
        # sidecar hash that no longer describes either. Fourth place this same mistake has been
        # found in one pull request, which is why it is spelled out rather than fixed quietly.
        corpus_file = os.path.join(CORPORA, vertical,
                                   'agenteval-typedmemeval-%s-v5.json' % vertical)
        corpus_now = tmc.corpus_sha256(corpus_file)
        if meta.get('corpus_sha256') != corpus_now:
            print('    the sidecar describes corpus %s and the corpus on disk is %s; any '
                  'co-published column is dropped rather than carried onto different input'
                  % (str(meta.get('corpus_sha256'))[:12], corpus_now[:12]), flush=True)
            prior_shapes = {}
        measured_rows, carried, dropped = _carry_second_column(
            shapes, prior_shapes, prior.get('dense_retriever'), dense_id)
        meta.setdefault('probes', {})['retriever_sensitivity'] = {
            'reference_retriever': tmc.RETRIEVER_ID,
            'reference_k': tmc.K_REF,
            'dense_retriever': dense_id,
            # Mirrors the probe records' reference_provider / reference_model. Never an endpoint.
            'dense_provider': provider,
            'dense_model': model,
            'dense_retriever_note': dense_note,
            'operand': 'ALLgold -- gold.issubset(top_k), the quantity V9 tracks',
            'reading': reading,
            'by_shape': dict(sorted(
                list(measured_rows.items())
                + [(shape, {
                    'questions': n,
                    'retrieval_measured': False,
                    'retriever_agreement': 'not-applicable',
                    'not_measured_because': (
                        'Every question in this shape has an EMPTY gold set -- the correct answer '
                        'is an abstention, so there is nothing to retrieve. The operand is not '
                        'unmeasured here, it is undefined: `gold.issubset(top_k)` is vacuously '
                        'true for an empty gold set, so ALLgold would read 1.000 under every '
                        'retriever at every budget. That would be the most flattering number in '
                        'this block and the least true one. No allgold or headroom fields are '
                        'published for this shape, deliberately -- a row that cannot be averaged '
                        'by accident.'),
                }) for shape, n in not_applicable.get(vertical, {}).items()]
            )),
        }
        # The block-level second-column metadata travels with the per-shape rows, never alone: a
        # `second_dense_retriever` left behind after the rows were dropped would name a column that
        # is no longer there.
        if carried:
            for key in ('second_dense_retriever', 'second_dense_note'):
                if key in prior:
                    meta['probes']['retriever_sensitivity'][key] = prior[key]
        with open(path, 'w', encoding='utf-8') as fh:
            json.dump(meta, fh, indent=2, ensure_ascii=False)
            fh.write('\n')
        print('  stamped %s (%d shapes%s)'
              % (vertical, len(shapes),
                 ', second column carried on %d' % carried if carried else ''))


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

    # NAME THE RETRIEVER BEFORE ANY NUMBER. The table's DENSE column is one model, and a summary that
    # did not say which one let a reader assume the published one.
    print('dense retriever: %s' % describe_target(args.dry_run))
    if args.dry_run:
        try:
            _embedding_request()          # reads the environment only; sends nothing
            configuration = 'complete'
        except SystemExit as error:
            configuration = ('INCOMPLETE, a real run would stop here: %s'
                             % str(error).splitlines()[0].rstrip('.'))
        print('  configuration: %s. DRY RUN: nothing is sent, every vector is the 3-gram stub, and '
              'no shard is read or written.' % configuration)
        dense_label = 'STUB (3-gram hash, no semantics)'
    else:
        dense_label = embedding_identity() or 'azure deployment %r, model unresolved' % _deployment_name()
    print()

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
        _load_cache([vertical], args.dry_run)
        print('%-14s %3d questions, %5d texts%s'
              % (vertical, len(questions), len(texts), ' (STUB)' if args.dry_run else ''),
              flush=True)
        if not args.dry_run:
            _verify_cache_matches_live(texts)
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
    print('DENSE = %s' % dense_label)
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
    print('  predicted headroom = 1 - ALLgold      BM25 %.3f   ->   DENSE %.3f   [%s]'
          % (1 - b_all, 1 - d_all, dense_label))
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
