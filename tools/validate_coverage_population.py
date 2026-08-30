#!/usr/bin/env python3
"""Published coverage counts questions it cannot measure. On Forgetting that is 30% of them.

THE DEFECT. `realised_coverage` is gold recall@K, and it opens with:

    gold = set(question.gold_indices)
    if not gold:
        return 1.0

which is the right answer to "what share of gold did we find" when there is no gold - vacuously all
of it. It is the wrong thing to average. Forgetting's 15 never-known probes are G=0 BY DESIGN (their
gold IS an absence), so each contributes a constant 1.0 to the vertical mean while measuring nothing
at all.

    Forgetting published mean_realised : 0.670
    over questions that HAVE gold      : 0.529
    band                               : [0.50, 0.90]

DIRECTION: flattering, and materially so. 0.529 sits 0.029 above the band floor; 0.670 sits
comfortably mid-band. The echo calibration search that placed Forgetting "safely" in the band was
optimising a statistic that was 30% constant, so the vertical is far closer to the floor than
anything we have published says.

BLAST RADIUS: Forgetting alone. Every other vertical has zero G=0 questions and its delta is exactly
0.000, verified rather than assumed - that is what the table below is for.

This is the diluted-denominator shape from the gate self-examination rule, wearing a statistic
rather than a gate: a population that absorbs items which cannot fail. It is the same error as
pooling judge grades into probe-answer denominators, and it fails in the same direction.

WHAT THIS TOOL DOES NOT DO. It does not change the aggregation. Fixing the statistic changes the
calibration target, which changes the echo, which regenerates the corpus, moves the sha and resets
every Forgetting control - a declared corpus revision, not a side effect of a reporting fix. This
measures and publishes the gap so the number can be read correctly in the meantime.

Usage:  python validate_coverage_population.py [--json]
"""
from __future__ import annotations

import glob
import json
import os
import sys
from pathlib import Path

import typedmemeval_common as tmc
from measure_padding_value import load

BASE = Path(__file__).resolve().parent.parent / "src/AgentEval.Memory/Data/typedmemeval"
BAND_LOW, BAND_HIGH = 0.50, 0.90


def main() -> int:
    as_json = "--json" in sys.argv
    report, rows = {}, []

    for vertical in sorted(os.listdir(BASE)):
        corpus_paths = glob.glob(str(BASE / vertical / "*v5.json"))
        meta_paths = glob.glob(str(BASE / vertical / "*v5.meta.json"))
        if not corpus_paths or not meta_paths:
            continue
        meta = json.loads(Path(meta_paths[0]).read_text(encoding="utf-8"))
        questions = load(Path(corpus_paths[0]))

        no_gold = [q for q in questions if q.g == 0]
        with_gold = [q for q in questions if q.g > 0]
        pooled = sum(tmc.realised_coverage(q) for q in questions) / len(questions)
        measured = (sum(tmc.realised_coverage(q) for q in with_gold) / len(with_gold)
                    if with_gold else None)
        published = meta.get("coverage", {}).get("mean_realised")

        report[vertical] = {
            "questions": len(questions),
            "questions_with_no_gold": len(no_gold),
            "published_mean_realised": published,
            "recomputed_over_all": round(pooled, 4),
            "recomputed_over_gold_bearing": round(measured, 4) if measured is not None else None,
            "dilution": round(measured - pooled, 4) if measured is not None else None,
            "in_band_as_published": published is not None and BAND_LOW <= published <= BAND_HIGH,
            "in_band_as_measured": measured is not None and BAND_LOW <= measured <= BAND_HIGH,
        }
        rows.append((vertical, len(questions), len(no_gold), published, pooled, measured))

    if as_json:
        print(json.dumps(report, indent=2))
        return 0

    print("COVERAGE POPULATION AUDIT - does the published mean include questions it cannot measure?")
    print("=" * 96)
    print(f"{'vertical':14s} {'q':>4s} {'G=0':>4s} {'published':>10s} {'gold-only':>10s} "
          f"{'dilution':>9s}")
    print("-" * 96)
    diluted = 0
    for vertical, n, zero, published, pooled, measured in rows:
        gap = (measured - pooled) if measured is not None else 0.0
        flag = ""
        if zero:
            diluted += 1
            flag = "   <-- DILUTED"
        shown = f"{published}" if published is not None else "-"
        print(f"{vertical:14s} {n:4d} {zero:4d} {shown:>10s} "
              f"{measured if measured is not None else float('nan'):10.3f} {gap:+9.3f}{flag}")
    print()
    print(f"Band is [{BAND_LOW}, {BAND_HIGH}]. A G=0 question contributes a constant 1.0 - "
          f"realised_coverage")
    print("returns 1.0 when there is no gold - so it pulls the mean toward the top of the band and")
    print("the calibration search optimises a partly constant statistic.")
    print()
    print(f"VERTICALS AFFECTED: {diluted}. The rest are verified 0.000, not assumed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
