# -*- coding: utf-8 -*-
"""Build / check the declared discrimination state of every shape in the family.

WHY THIS EXISTS
---------------
The family publishes "N of M shapes rank two systems". Three of 36 currently do not, and the goal
they are measured against allows either 36/36 *or each exception declared with a drift check*.
Today only ONE of the three is declared: `forgetting/never-known` carries
`discrimination_exempt_reason`, generated automatically because it has no gold at all. The other two
-- `episodic/participant-attribution` and `forgetting/still-valid` -- HAVE gold and measurably fail
to discriminate, which is a different thing, and nothing declares it.

An undeclared exception is indistinguishable from a regression nobody noticed.

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
        "Remains declared until the question form changes.",
    ('forgetting', 'still-valid'):
        "Headroom 0.0667, below the 0.15 floor. This is the CONTROL arm of a pair: its job is "
        "catching over-forgetting -- a system reporting a still-valid fact as superseded -- which "
        "is a property of the PAIR, not of either arm's retrieval headroom. Read `paired_arms` "
        "instead, where pair headroom is 0.4667 against a scaled floor of 0.24 at 3.68 sd. The arm "
        "alone is not supposed to discriminate and is not a defect. "
        "ADDED 2026-09-12: the 0.0667 is ALSO an artefact of the wrong ceiling. This shape's answer "
        "asserts an ABSENCE -- 'nothing has cancelled it' -- and gold cannot hold an absence, so V1 "
        "is not a valid ceiling for it: V8 (15/15) BEATS V1 (13/15), which is impossible when gold "
        "suffices. Measured against the ceiling that applies, V8-V9 = 0.200 CLEARS the 0.15 floor "
        "on its own. So the arm is exempt because it is a control, NOT because it cannot rank two "
        "systems -- it can. See MEASUREMENT_STATUS 88.16 and ABSENCE_SHAPES in "
        "tools/typedmemeval_quality_board.py.",
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
