#!/usr/bin/env python3
"""Is the equalisation padding EARNING its keep? Structural, no model calls.

WHY THIS EXISTS. `measure_signal_density.py` established that 85.7% of the family by character is
padding. The obvious next question - and the one that must be answered before anyone proposes
trimming it - is what that volume buys. Padding is not decoration: it equalises length, punctuation,
role sequence and vocabulary so V7's single-feature AUC cannot separate a gold session from a
distractor on shape alone. The question is whether the job needs the volume.

It does. On arithmetic, measured:

    as shipped (85.5% padding)   45 features scored,  0 over the 0.75 bar, top feature 0.661
    padding-free (Cell B)        45 features scored, 12 over the bar,      top feature 0.898

Removing padding makes gold trivially findable - uppercase_density 0.598 -> 0.898,
assistant_length_chars 0.523 -> 0.858. So the answer to "can we just use less filler" is no, not
without replacing it with something that does the same work.

WHERE THE PRESSURE ACTUALLY IS, which is the useful part. Every large mover is an UPPERCASE or an
ASSISTANT-LENGTH feature. Gold user turns carry proper nouns (job names, place names) and gold
assistant turns are short acknowledgements ("Noted."), while filler is long, chatty and lowercase.
Padding is therefore paying, in bulk text, for a mismatch that lives in two specific axes. Whether
those could be equalised directly and cheaply instead is a real design question this tool does not
answer - it would need building and measuring, and no claim is made here that it would work.

USE IT AS A GATE ON CHANGE, not as a routine report: any proposal that alters padding volume or
composition should show its numbers here first, against the shipped corpus, before the corpus moves.

Usage:  python measure_padding_value.py [vertical]      (default: arithmetic)
        Requires the padding-free cell to have been built for that vertical:
        python make_padding_free_variant.py <vertical>
"""
from __future__ import annotations

import glob
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

import typedmemeval_common as tmc

ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / "src/AgentEval.Memory/Data/typedmemeval"
CELLS = ROOT / "artifacts/padding-free"


def load(path: Path) -> list[tmc.Question]:
    """Rebuild generator Questions from a shipped corpus so the real report can run on it.

    Timestamps are not reconstructed: no separability feature reads them, and inventing dates that
    disagreed with the corpus would be worse than omitting them.
    """
    corpus = json.loads(path.read_text(encoding="utf-8"))
    questions = []
    for entry in corpus:
        gold = set(entry["answer_session_ids"])
        sessions = [
            tmc.Session(
                turns=[tmc.Turn(role=t.get("role", "user"), content=t["content"]) for t in session],
                timestamp=datetime.now(timezone.utc),
                is_gold=sid in gold)
            for sid, session in zip(entry["haystack_session_ids"], entry["haystack_sessions"])
        ]
        questions.append(tmc.Question(
            question_id=entry["question_id"], question_type=entry.get("question_type", "unknown"),
            question=entry["question"], answer=entry["answer"],
            question_date=datetime.now(timezone.utc), sessions=sessions,
            extension=dict(entry.get("typedmemeval", {}))))
    return questions


def scored(questions: list[tmc.Question]) -> dict[str, float]:
    report = tmc.separability_report(questions)
    features = report.get("features", report)
    return {k: v for k, v in features.items() if isinstance(v, (int, float))}


def show(label: str, features: dict[str, float]) -> None:
    over = [k for k, v in features.items() if v > tmc.SEPARABILITY_MAX_AUC]
    ranked = sorted(features.items(), key=lambda kv: -kv[1])
    print(f"--- {label}")
    print(f"    features scored       : {len(features)}")
    print(f"    over the {tmc.SEPARABILITY_MAX_AUC} bar        : {len(over)}")
    for name, auc in ranked[:6]:
        flag = "   <-- OVER" if auc > tmc.SEPARABILITY_MAX_AUC else ""
        print(f"      {auc:.3f}  {name}{flag}")


def main() -> int:
    vertical = sys.argv[1] if len(sys.argv) > 1 else "arithmetic"
    shipped = next(iter(glob.glob(str(DATA / vertical / "*v5.json"))), None)
    cell = next(iter(glob.glob(str(CELLS / f"*{vertical}*padding-free.json"))), None)
    if not shipped:
        print(f"no shipped corpus for {vertical}", file=sys.stderr)
        return 1
    if not cell:
        print(f"no padding-free cell for {vertical} -- build it first:\n"
              f"  python make_padding_free_variant.py {vertical}", file=sys.stderr)
        return 1

    padded, free = scored(load(Path(shipped))), scored(load(Path(cell)))
    show(f"{vertical} as shipped", padded)
    print()
    show(f"{vertical} padding-free", free)
    print()
    print("BIGGEST AUC MOVES when padding is removed:")
    for name in sorted(set(padded) & set(free), key=lambda k: -abs(free[k] - padded[k]))[:8]:
        print(f"   {free[name] - padded[name]:+.3f}   {name}   "
              f"({padded[name]:.3f} -> {free[name]:.3f})")
    print()
    over_padded = sum(1 for v in padded.values() if v > tmc.SEPARABILITY_MAX_AUC)
    over_free = sum(1 for v in free.values() if v > tmc.SEPARABILITY_MAX_AUC)
    print(f"VERDICT: padding takes {vertical} from {over_free} features over the bar to "
          f"{over_padded}. It is load-bearing, not decoration.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
