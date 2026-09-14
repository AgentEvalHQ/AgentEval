# -*- coding: utf-8 -*-
"""The dense arm's contract: it keeps what it paid for, and it names what produced it.

Three things this pins, each found or built on 2026-09-14.

1. A PARTIAL RUN MUST NOT EAT THE CACHE. `_save_shard` built its payload from the current run's
   keys alone, so a `--limit 4` run wrote a four-question shard over a full one. Measured across
   the family before the fix: 8,941 of 15,040 vectors were banked nowhere -- `episodic.json` held
   27 of 1,179, `workingmemory.json` 62 of 3,672 -- while arithmetic and bitemporal, the two
   verticals never re-run with `--limit`, were intact. The survivors were the untouched ones,
   which is the signature of the writer and not the reader.

2. A SHARD MUST NAME ITS OWN MODEL. The shard recorded the deployment NAME, which is an alias the
   resource owner picked. Repointing an alias changes the model and leaves the name alone, so the
   name cannot catch the one substitution that matters.

3. THE PUBLISHED IDENTITY MUST REFUSE TO GUESS. `dense_retriever_id` returns '' when the model is
   unknown, and `_stamp` refuses on ''. A sidecar naming no retriever is a gap a reader can act
   on; a sidecar naming the wrong one is not.

Each `--ablate-*` flag reinstates the corresponding defect and requires the check to FAIL. An
ablation that passes means the check is decorative, and the run reports that as the failure.

Exit 0 = contract holds. Exit 1 = it does not.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import typedmemeval_dense_retrieval as dr  # noqa: E402

FULL = ['alpha text', 'beta text', 'gamma text', 'delta text']
PARTIAL = ['alpha text']
LIVE_MODEL = 'test-embedding-model'
OTHER_MODEL = 'some-other-embedding-model'


def _replace_save(name, keys):
    """`_save_shard` as shipped before 2026-09-14: the payload is THIS RUN's keys, nothing else."""
    os.makedirs(dr.CACHE_DIR, exist_ok=True)
    payload = {k: dr._cache[k] for k in keys if k in dr._cache}
    payload[dr._PROVENANCE_KEY] = dr._deployment_name()
    tmp = dr._shard(name) + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as fh:
        json.dump(payload, fh)
    os.replace(tmp, dr._shard(name))


def _bank(texts, dims=4):
    for text in texts:
        dr._cache[dr._key(text)] = dr._pack([0.5] * dims)


def check_partial_is_non_destructive(failures, ablate_replace):
    """A full run then a 1-of-4 partial run. All four vectors must still be on disk."""
    if ablate_replace:
        dr._save_shard = _replace_save
    dr._cache.clear()
    _bank(FULL)
    dr._save_shard('probe', [dr._key(t) for t in FULL])

    dr._cache.clear()
    dr._load_cache(['probe'])
    _bank(PARTIAL)
    dr._save_shard('probe', [dr._key(t) for t in PARTIAL])

    shard = json.loads(open(dr._shard('probe'), encoding='utf-8').read())
    vectors = {k for k in shard if not k.startswith('__')}
    lost = [t for t in FULL if dr._key(t) not in vectors]
    print('1. partial run, %d of %d vectors survive%s'
          % (len(FULL) - len(lost), len(FULL), ('   LOST: ' + ', '.join(lost)) if lost else ''))
    if lost:
        failures.append('a partial run destroyed %d paid vector(s)' % len(lost))

    print('   shard provenance: deployment=%r model=%r dims=%r'
          % (shard.get(dr._PROVENANCE_KEY), shard.get(dr._MODEL_KEY), shard.get(dr._DIMS_KEY)))
    if shard.get(dr._MODEL_KEY) != LIVE_MODEL:
        failures.append('the shard does not record which MODEL produced it (got %r)'
                        % shard.get(dr._MODEL_KEY))
    if shard.get(dr._DIMS_KEY) != 4:
        failures.append('the shard does not record its vector width (got %r)'
                        % shard.get(dr._DIMS_KEY))


def check_model_mismatch_is_refused(failures, ablate_model_check):
    """A shard stamped with a different MODEL under the SAME deployment alias must be ignored.

    This is the substitution the deployment-name check cannot see, because repointing an alias is
    exactly the operation that leaves the name unchanged.
    """
    shard = json.loads(open(dr._shard('probe'), encoding='utf-8').read())
    shard[dr._MODEL_KEY] = OTHER_MODEL          # same alias, different model behind it
    with open(dr._shard('probe'), 'w', encoding='utf-8') as fh:
        json.dump(shard, fh)

    original = dr._load_cache
    if ablate_model_check:
        def _name_only_load(names, dry_run=False):
            """The pre-fix loader: it compares the alias and nothing else."""
            for name in list(names) + ['_migrated']:
                path = dr._shard(name)
                if not os.path.exists(path):
                    continue
                data = json.loads(open(path, encoding='utf-8').read())
                stamped = data.pop(dr._PROVENANCE_KEY, None)
                data.pop(dr._MODEL_KEY, None)
                data.pop(dr._DIMS_KEY, None)
                if stamped is not None and stamped != dr._deployment_name():
                    continue
                dr._cache.update(data)
        dr._load_cache = _name_only_load
    try:
        dr._cache.clear()
        dr._load_cache(['probe'])
        loaded = len(dr._cache)
        print('2. shard says model=%r, live deployment serves %r -> %d vector(s) loaded'
              % (OTHER_MODEL, LIVE_MODEL, loaded))
        if loaded:
            failures.append('vectors from a different model were loaded; a ranking mixing two '
                            'embedding spaces is not a weaker measurement, it is not one')
    finally:
        dr._load_cache = original

    # And the provenance keys themselves must never become cache entries: they are not text
    # hashes, and `_save_shard` would write them back out as if they were vectors.
    shard[dr._MODEL_KEY] = LIVE_MODEL
    with open(dr._shard('probe'), 'w', encoding='utf-8') as fh:
        json.dump(shard, fh)
    dr._cache.clear()
    dr._load_cache(['probe'])
    leaked = sorted(k for k in dr._cache if k.startswith('__'))
    print('3. provenance keys leaked into the vector cache: %s' % (leaked or 'none'))
    if leaked:
        failures.append('provenance keys %s were loaded as if they were vectors' % leaked)


def check_identity_refuses_to_guess(failures):
    """The published id is built from the model, and is EMPTY when the model is unknown."""
    known = dr.dense_retriever_id(LIVE_MODEL, 1536)
    unknown = dr.dense_retriever_id('', 1536)
    print('4. id(known model) = %r' % known)
    print('   id(unresolved)  = %r' % unknown)
    if unknown != '':
        failures.append('the identity fell back to something when the model was unknown (%r); a '
                        'sidecar naming the wrong retriever is worse than one naming none'
                        % unknown)
    for field in (LIVE_MODEL, '1536'):
        if field not in known:
            failures.append('the identity does not carry %r, which changes the ranking' % field)
    if 'cosine' not in known:
        failures.append('the identity does not name the similarity')


def run(ablate_replace=False, ablate_model_check=False) -> int:
    sandbox = tempfile.mkdtemp(prefix='densecache-')
    saved = (dr.CACHE_DIR, dr._save_shard, dr._load_cache, dr._resolved_model)
    # The model lookup is a network call and every check here is about local behaviour. Pinned so
    # the result cannot depend on whether a deployment happens to be reachable.
    dr.CACHE_DIR, dr._resolved_model = sandbox, LIVE_MODEL
    failures: list[str] = []
    try:
        check_partial_is_non_destructive(failures, ablate_replace)
        check_model_mismatch_is_refused(failures, ablate_model_check)
        check_identity_refuses_to_guess(failures)
    finally:
        dr.CACHE_DIR, dr._save_shard, dr._load_cache, dr._resolved_model = saved
        dr._cache.clear()
        shutil.rmtree(sandbox, ignore_errors=True)

    print()
    if failures:
        for f in failures:
            print('FAIL: %s' % f)
        return 1
    print('PASS: partial runs are non-destructive, shards name their model, and the published '
          'identity refuses to guess.')
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--ablate-replace', action='store_true',
                    help='reinstate the replace-not-merge shard writer; this check MUST fail')
    ap.add_argument('--ablate-model-check', action='store_true',
                    help='reinstate the alias-only loader; this check MUST fail')
    args = ap.parse_args()

    ablating = args.ablate_replace or args.ablate_model_check
    if not ablating:
        return run()

    which = [n for n, on in (('replace-not-merge', args.ablate_replace),
                             ('alias-only load', args.ablate_model_check)) if on]
    print('ABLATION: %s reinstated. Expecting FAIL.\n' % ' + '.join(which))
    code = run(args.ablate_replace, args.ablate_model_check)
    if code == 0:
        print('\n\U0001f534 THE ABLATION PASSED. This check does not test what it claims to.')
        return 1
    print('\nablation failed as required -- the check has hold of the real defect.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
