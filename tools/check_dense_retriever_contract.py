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
import subprocess
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
    """`_save_shard` as shipped before 2026-09-14: the payload is THIS RUN's keys, nothing else.

    ABLATES ONE THING. It writes to `dr._shard(name)`, wherever that resolves today, and creates
    that directory -- because the behaviour under test is REPLACE-versus-MERGE, not which folder.
    An earlier version created `CACHE_DIR` while `_shard` had moved to `CACHE_DIR/<model>/`, so the
    ablation died with FileNotFoundError before reaching the defect and still printed "ablation
    failed as required". Caught in review of PR #245. The lesson is narrower than the bug: an
    ablation has to be re-run whenever the code under it moves, and I re-ran it before that move
    and not after.
    """
    os.makedirs(os.path.dirname(dr._shard(name)), exist_ok=True)
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


def check_unstamped_is_refused(failures):
    """A shard carrying NO provenance at all must be refused, not accepted.

    The guard read `if stamped is not None`, which accepts an unstamped shard under ANY
    deployment. The one such file, `_migrated.json`, was read for every vertical and held 5,422
    vectors. It matters most for callers that never run the live probe --
    `typedmemeval_v9_dense.py` loads this cache directly -- because for them the structural
    refusal is the only protection there is. Found in review of PR #245.
    """
    shard = json.loads(open(dr._shard('probe'), encoding='utf-8').read())
    for key in (dr._PROVENANCE_KEY, dr._MODEL_KEY, dr._DIMS_KEY):
        shard.pop(key, None)
    with open(dr._shard('probe'), 'w', encoding='utf-8') as fh:
        json.dump(shard, fh)
    dr._cache.clear()
    dr._load_cache(['probe'])
    print('5. shard with NO provenance -> %d vector(s) loaded' % len(dr._cache))
    if dr._cache:
        failures.append('an unstamped shard was loaded; a shard that cannot say which model '
                        'produced it cannot be ranked against one')


def _write_as_child(tag: str) -> int:
    """The other half of the concurrency check: one writer, run in its own interpreter."""
    import contextlib
    import time
    import typedmemeval_common as tmc

    # THE CRITICAL SECTION IS MICROSECONDS WIDE. `_save_shard` reads the prior shard inside itself,
    # right before the replace, so no sleep placed around the call can make two writers collide
    # reliably. Both arms therefore delay INSIDE the lock boundary, and differ only in whether the
    # boundary is real. Same delay on both sides means the comparison isolates serialisation and
    # nothing else.
    # THE LOSS WINDOW IS THE GAP BETWEEN THE MERGE'S READ AND ITS REPLACE, and `_save_shard` does
    # both inside one call -- a few hundred microseconds apart. No sleep placed AROUND that call can
    # make two writers collide; two earlier attempts at this check failed for exactly that reason
    # and their ablations kept passing.
    #
    # So the delay goes where the window is: `os.replace` is patched, in this short-lived child
    # only, to wait before performing the rename. Both arms get the same delay, so the only
    # difference between them is whether a lock is held across read-delay-replace.
    real_replace = os.replace

    def slow_replace(src, dst):
        time.sleep(2.0)
        return real_replace(src, dst)

    os.replace = slow_replace
    if os.environ.get('AGENTEVAL_CONTRACT_NOLOCK'):
        # ABLATION: read-merge-replace is atomic per WRITE again and unserialised as a TRANSACTION.
        tmc.exclusive_file_lock = lambda path, timeout=None: contextlib.nullcontext()
    dr._load_cache(['shared'])
    read_at = time.time()
    keys = []  # noqa: E501
    for index in range(40):
        text = '%s-text-%d' % (tag, index)
        dr._cache[dr._key(text)] = dr._pack([0.5, 0.5, 0.5, 0.5])
        keys.append(dr._key(text))
    # Enough that both children are certainly inside `_save_shard` before either completes it;
    # the 2s delay that actually matters is inside the lock wrapper above.
    time.sleep(0.5)
    dr._save_shard('shared', keys)
    with open(os.path.join(dr.CACHE_DIR, 'window-%s.json' % tag), 'w', encoding='utf-8') as fh:
        json.dump({'read_at': read_at, 'wrote_at': time.time()}, fh)
    return 0


def check_concurrent_writers_do_not_lose_vectors(failures, ablate_lock=False):
    """Two PROCESSES writing one shard at once must both survive.

    `os.replace` is atomic per WRITE and says nothing about two writers: both read the same
    prior contents, each merges its own additions, each replaces, and the first writer's
    work is gone. What is dropped here is paid API calls -- the same loss the merge-on-save
    fix ended, one level up. A thread lock cannot see it: the writers are separate processes.

    Really spawns two interpreters. A threading test would pass with the defect present.
    """
    env = dict(os.environ)
    env['AZURE_OPENAI_EMBEDDING_DEPLOYMENT'] = 'contract-check'
    env['AGENTEVAL_CONTRACT_SANDBOX'] = dr.CACHE_DIR
    env['AGENTEVAL_CONTRACT_MODEL'] = LIVE_MODEL
    if ablate_lock:
        env['AGENTEVAL_CONTRACT_NOLOCK'] = '1'
    procs = []
    for tag in ('alpha', 'beta'):
        procs.append(subprocess.Popen(  # DevSkim: ignore DS107369 - fixed argv, this same file
            [sys.executable, os.path.abspath(__file__), '--writer', tag],
            env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE))
    for proc in procs:
        _, err = proc.communicate(timeout=180)
        if proc.returncode:
            failures.append('a concurrent writer crashed: %s'
                            % err.decode('utf-8', 'replace')[-300:])
            return
    # DID THE RACE ACTUALLY HAPPEN? Each child's read->write interval must overlap the other's,
    # or the two writers were sequential and this check proved nothing. Reported as INCONCLUSIVE
    # rather than counted as a pass: an untriggered check that says PASS is worse than no check.
    windows = {}
    for tag in ('alpha', 'beta'):
        wpath = os.path.join(dr.CACHE_DIR, 'window-%s.json' % tag)
        if os.path.exists(wpath):
            windows[tag] = json.loads(open(wpath, encoding='utf-8').read())
    overlap = 0.0
    if len(windows) == 2:
        a, b = windows['alpha'], windows['beta']
        overlap = min(a['wrote_at'], b['wrote_at']) - max(a['read_at'], b['read_at'])

    path = os.path.join(dr.CACHE_DIR, LIVE_MODEL, 'shared.json')
    if not os.path.exists(path):
        failures.append('neither concurrent writer produced a shard')
        return
    shard = json.loads(open(path, encoding='utf-8').read())
    kept = len([k for k in shard if not k.startswith('__')])
    print('6. two processes wrote 40 vectors each -> %d of 80 survive '
          '(read/write windows overlapped by %.2fs)' % (kept, overlap))
    if overlap <= 0:
        failures.append('INCONCLUSIVE: the two writers did not overlap (%.2fs), so nothing was '
                        'raced and this check demonstrated nothing. Widen the window.' % overlap)
        return
    if kept != 80:
        failures.append('%d of 80 vectors were lost when two processes wrote one shard; '
                        'read-merge-replace is not serialised across processes' % (80 - kept))


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


def run(ablate_replace=False, ablate_model_check=False, ablate_lock=False) -> int:
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
        check_unstamped_is_refused(failures)
        check_concurrent_writers_do_not_lose_vectors(failures, ablate_lock)
    finally:
        dr.CACHE_DIR, dr._save_shard, dr._load_cache, dr._resolved_model = saved
        dr._cache.clear()
        shutil.rmtree(sandbox, ignore_errors=True)

    print()
    if failures:
        for f in failures:
            print('FAIL: %s' % f)
        return 1
    print('PASS: partial runs are non-destructive, concurrent writers keep each other\'s '
          'vectors, shards name their model, unstamped shards are refused, and the published '
          'identity refuses to guess.')
    return 0


def main() -> int:
    # The child half of the concurrency check re-enters this file rather than carrying an embedded
    # script string: one copy of the writer, and it exercises the real `_save_shard`.
    if '--writer' in sys.argv:
        dr.CACHE_DIR = os.environ['AGENTEVAL_CONTRACT_SANDBOX']
        dr._resolved_model = os.environ['AGENTEVAL_CONTRACT_MODEL']
        return _write_as_child(sys.argv[sys.argv.index('--writer') + 1])

    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--ablate-replace', action='store_true',
                    help='reinstate the replace-not-merge shard writer; this check MUST fail')
    ap.add_argument('--ablate-model-check', action='store_true',
                    help='reinstate the alias-only loader; this check MUST fail')
    ap.add_argument('--ablate-lock', action='store_true',
                    help='replace the interprocess lock with a no-op; this check MUST fail')
    args = ap.parse_args()

    ablating = args.ablate_replace or args.ablate_model_check or args.ablate_lock
    if not ablating:
        return run()

    which = [n for n, on in (('replace-not-merge', args.ablate_replace),
                             ('alias-only load', args.ablate_model_check),
                             ('no interprocess lock', args.ablate_lock)) if on]
    print('ABLATION: %s reinstated. Expecting FAIL.\n' % ' + '.join(which))
    code = run(args.ablate_replace, args.ablate_model_check, args.ablate_lock)
    if code == 0:
        print('\n\U0001f534 THE ABLATION PASSED. This check does not test what it claims to.')
        return 1
    print('\nablation failed as required -- the check has hold of the real defect.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
