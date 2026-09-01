#!/usr/bin/env python3
"""Measures WHY gold's turns end up longer than every distractor's, per slot. No model calls.

THE OBSERVATION. `equalise_shape` pads every turn slot toward a common target, and padding only ever
ADDS. Measured on Prospective, gold's first assistant turn is longer than EVERY distractor's in 8-10
of 50 questions and shorter than all of them in 0-1, against 4.1 expected by chance. That is the
separability finding known as S3, and it is latent across the family.

WHY THE OBVIOUS EXPLANATION IS NOT ENOUGH. If gold sits at the peak the target is derived from, the
target is `peak * margin` -- ABOVE gold -- so gold should be padded too and land near the target like
everything else. It does not, and three explanations are consistent with the observation:

  1. filler never REACHES the target, because `_PAD_MAX_ROUNDS` runs out first;
  2. padding EXITS EARLY on a non-chars axis, because the loop stops when all six deficits clear and
     gold's text is dense in `types`/`caps`;
  3. jitter -- but jitter alone predicts about 2.4 of 50, not 8-10, so it cannot be the whole story.

Each implies a DIFFERENT fix, and guessing between them is how three attempts at this already failed.
So this measures rather than argues: for every slot it reports where gold and filler actually land
relative to the target the algorithm computed, and which axis was the last to clear.

MEASURED, 2026-09-01, and it was none of the three:

  pad_calls 8107 (prospective) / 10222 (episodic)
  returned NOTHING: 3740 (46%) / 4787 (47%)
  median chars deficit when asked:  18 / 15

Of the 3740 empty returns, the axis still short was punct ALONE 842 times, sentences alone 528,
types alone 238, caps alone 179; and 517 had chars short with sentences and punct already satisfied,
median 10 chars.

THE PADDING BANK CANNOT MAKE SINGLE-AXIS ADJUSTMENTS. Every phrase moves all six axes at once, so
once five are satisfied and one is short by a little, no candidate improves the weighted score --
overshoot is scored as harshly as shortfall -- and the loop adds nothing. Equalisation therefore
converges to "within one phrase of the target" and stops, and inside that band the ordering is
decided by where each session STARTED. Gold starts highest, so it finishes longest more often than
chance.

`_pad_greedy`'s own docstring predicted the coupling and concluded the mix should be SEARCHED rather
than derived. That is right and insufficient: a search cannot win when the bank holds no move that
helps. The fix is EDIT OPERATIONS rather than append-only sentences -- a comma inserted into existing
text (punct, ~0 chars), a clause split (sentence, ~0 chars), a synonym swap (types, 0 tokens), a word
lengthened (chars, no sentence). Several are SUBSTITUTIONS, which is also the bidirectional
capability that "padding only ever adds" has always lacked.
"""

from __future__ import annotations

import argparse
import importlib
import pathlib
import random  # DevSkim: ignore DS148264 - deterministic corpus generation, not secrets
import statistics
import sys
from collections import Counter

sys.path.insert(0, str(pathlib.Path(__file__).parent))

import typedmemeval_common as tmc  # noqa: E402

PER_SHAPE = {"arithmetic", "conjunction", "episodic", "prospective", "semantic"}


def slot_map(question):
    """(role, ordinal-within-role) -> [sessions], exactly as equalise_shape groups them."""
    slots: dict[tuple[str, int], list] = {}
    for session in question.sessions:
        seen: Counter[str] = Counter()
        for turn in session.turns:
            slots.setdefault((turn.role, seen[turn.role]), []).append(session)
            seen[turn.role] += 1
    return slots


def slot_target(sessions, role, ordinal):
    """The target equalise_shape computes for this slot. Mirrored, not imported, so a change to the
    real function shows up here as a disagreement rather than being silently tracked."""
    vectors = [tmc._profile_vector(tmc._slot_text(s, role, ordinal)) for s in sessions]
    if not vectors:
        return None
    peak = {axis: max(v[axis] for v in vectors) for axis in tmc._PAD_AXES}
    margin = (peak["chars"] + tmc._PAD_SLOT_MARGIN_CHARS) / max(1, peak["chars"])
    return {axis: int(round(peak[axis] * margin)) for axis in tmc._PAD_AXES}


def build(vertical, echo_map):
    module = importlib.import_module(f"gen_typedmemeval_{vertical}")
    seed = 20260815
    questions = module.build(echo_map, random.Random(seed))  # DevSkim: ignore DS148264
    tmc.equalise_echo(questions, 0.0 if isinstance(echo_map, dict) else echo_map,
                      random.Random(seed + 1))  # DevSkim: ignore DS148264
    tmc.equalise_reply(questions, random.Random(seed + 3))  # DevSkim: ignore DS148264
    tmc.equalise_shape(questions, random.Random(seed + 2))  # DevSkim: ignore DS148264
    return questions


def diagnose(vertical: str) -> dict:
    """Observes the padding loop ITSELF, by intercepting `_pad_greedy`.

    A first version of this computed the slot target from the sessions AFTER equalisation and
    reported that every session sits 70 chars below it -- which is not a finding, it is arithmetic:
    the post-padding peak is by definition `target - margin`. Measuring the OUTPUT of a process to
    infer what the process did is the same off-pipeline error this file exists to avoid, so the
    padding call is intercepted instead: what deficit was it handed, and what did it return.
    """
    import json
    meta = json.loads(
        next((tmc.DATA_ROOT / vertical).glob("*.meta.json")).read_text(encoding="utf-8"))
    coverage = meta["coverage"]
    echo_map = coverage.get("echo_by_shape") or coverage["echo"]

    calls = []
    real_pad = tmc._pad_greedy

    def spy(deficit, rng, vocabulary=frozenset()):
        produced = real_pad(deficit, rng, vocabulary)
        calls.append((dict(deficit), len(produced)))
        return produced

    tmc._pad_greedy = spy
    try:
        questions = build(vertical, echo_map)
    finally:
        tmc._pad_greedy = real_pad

    # Which axis was binding when the loop asked for padding, and did the call deliver?
    empty = sum(1 for _, produced in calls if produced == 0)
    chars_deficit = [d["chars"] for d, _ in calls]
    binding = Counter()
    for deficit, _ in calls:
        worst = max(tmc._PAD_AXES, key=lambda a: deficit[a])
        binding[worst] += 1

    # And separately: after equalisation, how often is gold strictly the longest in its slot?
    longest_is_gold = longest_is_filler = overlapping = 0
    for question in questions:
        for (role, ordinal), members in slot_map(question).items():
            golds = [len(tmc._slot_text(s, role, ordinal)) for s in members if s.is_gold]
            fillers = [len(tmc._slot_text(s, role, ordinal)) for s in members if not s.is_gold]
            if not golds or not fillers:
                continue
            if min(golds) > max(fillers):
                longest_is_gold += 1
            elif max(golds) < min(fillers):
                longest_is_filler += 1
            else:
                overlapping += 1

    total = longest_is_gold + longest_is_filler + overlapping
    return {
        "vertical": vertical,
        "pad_calls": len(calls),
        "pad_calls_returning_nothing": empty,
        "median_chars_deficit_requested": round(statistics.median(chars_deficit), 1) if chars_deficit else None,
        "binding_axis": dict(binding.most_common(3)),
        "slots": total,
        "gold_longest": longest_is_gold,
        "gold_shortest": longest_is_filler,
        "gold_longest_share": round(longest_is_gold / total, 4) if total else None,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("verticals", nargs="*",
                        default=sorted(p.name for p in tmc.DATA_ROOT.iterdir() if p.is_dir()))
    args = parser.parse_args()

    for vertical in args.verticals:
        try:
            row = diagnose(vertical)
        except Exception as error:                        # noqa: BLE001 - a diagnostic must not stop
            print(f"{vertical:14} FAILED: {type(error).__name__}: {error}")
            continue
        print(f"{row['vertical']:14} pad_calls={row['pad_calls']:<6} "
              f"returned_nothing={row['pad_calls_returning_nothing']:<6} "
              f"median_chars_deficit={str(row['median_chars_deficit_requested']):<7} "
              f"binding={row['binding_axis']}")
        print(f"{'':14} gold_longest={row['gold_longest']}/{row['slots']} "
              f"({row['gold_longest_share']})  gold_shortest={row['gold_shortest']}")
        sys.stdout.flush()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
