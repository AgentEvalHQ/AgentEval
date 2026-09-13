# -*- coding: utf-8 -*-
"""Build / check the declared discrimination state of every shape in the family.

WHY THIS EXISTS
---------------
The family publishes "N of M shapes rank two systems", and the goal it is measured against allows
either 36/36 *or each exception declared with a drift check*.

An undeclared exception is indistinguishable from a regression nobody noticed.

CURRENT STATE (2026-09-13): 33 of 36 rank two systems. The three exceptions are
`forgetting/never-known` (no gold at all, scored on abstention) and
`prospective/expiring-validity` + `prospective/not-yet-true`, which P2 grew from 6 questions to 14
and which then read 0.1429 -- one question below the floor -- where their n=6 figures had said
0.3333 and 0.5000. Those were noise; these are the shapes' behaviour.

⚠ BOTH DIRECTIONS OF THIS CHECK HAVE NOW FIRED IN ANGER. `episodic/participant-attribution` and
`forgetting/still-valid` were once declared here and BOTH now discriminate (0.6667 and 0.2000), so
their declarations were removed -- good news the check surfaced rather than let the headline
understate. The prospective pair moved the other way in the same run.

THE DRIFT CHECK, AND WHY IT RUNS BOTH WAYS
------------------------------------------
A one-way check is the failure this family keeps finding. So:

  declared discriminating  -> must still discriminate   (a regression)
  declared NOT             -> must still not            (a STALE declaration)

The second direction matters as much as the first. If `participant-attribution` is rebuilt and
starts ranking systems, leaving it declared-exempt would understate the family: the headline count
stays 33 of 36 while the truth is 34, and nobody is told. A ratchet that only tightens is a ratchet
that lies in one direction.

This is the same shape as `typedmemeval-separability-baseline.json`: a declared state, checked, that
can be changed only deliberately. Deleting an entry to make the check pass is the one thing it must
never be used for.
"""
import argparse
import glob
import json
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CORPORA = os.path.join(ROOT, 'src', 'AgentEval.Memory', 'Data', 'typedmemeval')
BASELINE = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        'typedmemeval-discrimination-baseline.json')

# Written reasons for shapes that do NOT discriminate. A declaration without a reason is a number
# someone will delete when it becomes inconvenient.
REASONS = {
    ('episodic', 'participant-attribution'):
        "STRUCTURAL, and re-forming it once did not lift it. The question quotes the statement it "
        "asks about (\"Earlier, about the corner pharmacy, someone said that it shuts for an hour "
        "...\"), so a lexical retriever is handed the session containing that exact text. Measured: "
        "V1 0.933, V9 1.000, headroom -0.067 -- the retriever BEATS a perfect gold-only selector, "
        "which is the signature of a question form that leaks its own answer. E1 re-formed the "
        "shape and the cap survived; the triage plan's verdict is 'new forms, not new knobs'. "
        "⚠ HISTORICAL as of 2026-09-12: the question form DID change (`E1-b`, identify the claim "
        "by its consequence) and this shape now runs headroom 0.6667 and discriminates, so it is no "
        "longer a declared exception. The text is kept because the reason a shape once failed is "
        "worth more to a future reader than a deleted key, and it is applied only if the shape ever "
        "stops discriminating again.",
    ('prospective', 'expiring-validity'):
        "Headroom 0.1429 at n=14, which is 0.007 -- ONE QUESTION -- below the 0.15 floor. It is "
        "declared rather than fixed because the number is NEW INFORMATION, not a regression: at "
        "n=6 this shape reported 0.3333, and P2 (2026-09-13) grew it to 14 questions precisely "
        "because F2 had measured these pair-shapes failing on SAMPLE SIZE rather than on any "
        "floor. The old value was sampling noise; this one is the shape's actual behaviour. V9 "
        "runs 12/14 with a 95% interval of [0.60, 0.96], so 'at the floor' is the honest reading "
        "and 'below it' overstates the precision. TRIGGER: a design that lowers ALLgold on a "
        "depth-1 prospective shape, or another growth to n>=25 that narrows the interval.",
    ('prospective', 'not-yet-true'):
        "Headroom 0.1429 at n=14, same story and same arc as `expiring-validity`: it reported "
        "0.5000 at n=6 and 0.1429 at n=14. Two shapes moving from different noisy values to the "
        "SAME value under the same growth is itself evidence the n=6 figures carried no signal. "
        "V9 11/14, 95% interval [0.52, 0.92]. ALSO an ABSENCE shape (MEASUREMENT_STATUS 88.16): "
        "its answer asserts a triggering event has not occurred, so V1 is not a valid ceiling for "
        "it and V8-V9 = 0.2143 is the statistic that applies -- which clears the floor. Declared "
        "on the uncorrected number because `discriminates` is keyed on it. TRIGGER: as above.",
    ('forgetting', 'never-known'):
        "Exempt by construction: every question has zero gold sessions, so V1/V8/V9 are undefined "
        "and V1-V9 cannot be formed. Scored on abstention (V10/V11) instead. The probe tool emits "
        "this reason itself as `discrimination_exempt_reason`.",
}


def observed():
    """Every shape's current discrimination state, read from the shipped sidecars."""
    out = {}
    for meta in sorted(glob.glob(os.path.join(CORPORA, '*', '*-v5.meta.json'))):
        vertical = os.path.basename(os.path.dirname(meta))
        probes = (json.load(open(meta, encoding='utf-8')).get('probes') or {})
        for shape, block in sorted((probes.get('by_shape') or {}).items()):
            if 'discriminates' not in block and 'discrimination_basis' not in block:
                continue
            out[f'{vertical}/{shape}'] = {
                'discriminates': block.get('discriminates'),
                'headroom': block.get('headroom_perfect_selector'),
                'basis': block.get('discrimination_basis', 'headroom'),
            }
    return out


def build():
    obs = observed()
    entries = {}
    for key, state in obs.items():
        vertical, shape = key.split('/', 1)
        entry = {'discriminates': state['discriminates'], 'basis': state['basis']}
        if state['discriminates'] is not True:
            reason = REASONS.get((vertical, shape))
            if not reason:
                raise SystemExit(
                    f"{key} does not discriminate and has no written reason. Add one to REASONS "
                    f"rather than letting an undeclared exception into the baseline.")
            entry['reason'] = reason
        entries[key] = entry

    doc = {
        '_comment': [
            "Declared discrimination state per shape. Checked BOTH ways by --check:",
            "  declared True  -> must still discriminate (catches a regression)",
            "  declared False -> must still not          (catches a STALE declaration)",
            "A shape that starts discriminating is good news the family must not keep publishing as",
            "an exception. Deleting an entry to make the check pass is the one thing this file must",
            "never be used for -- change the corpus, re-probe, then rebuild this deliberately.",
        ],
        'shapes': entries,
    }
    json.dump(doc, open(BASELINE, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
    n_false = sum(1 for e in entries.values() if e['discriminates'] is not True)
    print('wrote %s' % BASELINE)
    print('  %d shapes, %d declared non-discriminating (each with a written reason)'
          % (len(entries), n_false))


def check():
    if not os.path.exists(BASELINE):
        raise SystemExit('no baseline; run without --check to build one')
    declared = json.load(open(BASELINE, encoding='utf-8'))['shapes']
    obs = observed()

    # POSITIVE CONTROL FIRST. "no drift" is also true of a scan that found no shapes.
    if len(obs) < 30:
        raise SystemExit(
            'the scan found only %d shapes, so it measured nothing' % len(obs))

    problems = []
    for key, want in sorted(declared.items()):
        got = obs.get(key)
        if got is None:
            problems.append('%s: declared but NOT FOUND in any sidecar '
                            '(renamed or removed without updating this file)' % key)
            continue
        if got['discriminates'] != want['discriminates']:
            direction = ('STALE DECLARATION -- it discriminates now, and the family is still '
                         'publishing it as an exception'
                         if want['discriminates'] is not True
                         else 'REGRESSION -- it no longer discriminates')
            problems.append('%s: declared %s, observed %s (headroom %s). %s'
                            % (key, want['discriminates'], got['discriminates'],
                               got['headroom'], direction))
    for key in sorted(set(obs) - set(declared)):
        problems.append('%s: present in a sidecar and NOT declared here '
                        '(a new shape must be declared deliberately)' % key)

    total = len(obs)
    ranking = sum(1 for s in obs.values() if s['discriminates'] is True)
    print('%d shapes scanned; %d rank two systems, %d declared exceptions'
          % (total, ranking, total - ranking))
    if problems:
        print('\nDRIFT:')
        for p in problems:
            print('  - %s' % p)
        raise SystemExit(1)
    print('no drift: every shape matches its declaration, both directions checked')


ap = argparse.ArgumentParser()
ap.add_argument('--check', action='store_true')
args = ap.parse_args()
check() if args.check else build()
