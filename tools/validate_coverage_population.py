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
from run_typedmemeval_probes import closed_choice_k

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

        # PER-SHAPE, FOR THE FIVE VERTICALS THAT DO NOT PUBLISH IT. Only four verticals opted into
        # per-shape calibration, and the vertical mean is exactly what hid Prospective's
        # `not-yet-true` at a saturated 1.000 for its whole shipped life. Coverage is structural
        # BM25, so recomputing it costs nothing - there is no reason for these numbers to be absent
        # from a report just because they are absent from a sidecar.
        by_shape: dict[str, list[float]] = {}
        by_question: dict[str, list] = {}
        for question in questions:
            shape = question.extension.get("shape", "(unshaped)")
            by_shape.setdefault(shape, []).append(tmc.realised_coverage(question))
            by_question.setdefault(shape, []).append(question)
        # THE SATURATION SCREEN, named by the consuming project: "a perfect score under partial
        # coverage means the missing part was never needed". If a shape's V9 pass RATE sits above
        # its BM25 coverage, the model is answering without the evidence the retriever failed to
        # fetch -- which means that gold was never load-bearing. It is the cheap, always-available
        # form of the V6 leave-one-out question, and it runs on two columns already in the record.
        #
        # conjunction/order-then-value read coverage 0.667 against V9 15/15 -- a gap of +0.333 --
        # for its whole shipped life. One gold session named both the anchor and the answer, so the
        # join was never required and the un-retrieved third could not matter.
        arms = ((meta.get("probes") or {}).get("by_shape") or {})
        shapes = {}
        for shape, values in sorted(by_shape.items()):
            realised = sum(values) / len(values)
            d = arms.get(shape) or {}
            v9_applicable, v9_passed = d.get("v9_applicable", 0), d.get("v9_passed", 0)
            v9_rate = (v9_passed / v9_applicable) if v9_applicable else None

            # THE BASELINE IS NOT COVERAGE ON A CLOSED-CHOICE SHAPE. When gold is missing the
            # model can still pick from the candidates the question names and lands on it at 1/k,
            # so the score to beat is `coverage + (1 - coverage) / k`, not coverage. Comparing
            # against bare coverage reports the CHANCE FLOOR as if it were evidence of saturation:
            # episodic/participant-attribution (k=2) read +0.133 against coverage and -0.033
            # against its actual floor -- a false positive produced by the screen, not the shape.
            ks = {closed_choice_k(q.question) for q in by_question[shape]}
            k = ks.pop() if len(ks) == 1 else None
            floor = realised + (1 - realised) / k if k else realised
            shapes[shape] = {
                "questions": len(values),
                "realised": round(realised, 3),
                "candidates": k,
                "expected_floor": round(floor, 3),
                "v9_rate": round(v9_rate, 3) if v9_rate is not None else None,
                # Positive = scores above what evidence plus guessing can explain. Negative =
                # reasoning is the constraint, which is what a healthy hard shape looks like.
                "scores_above_evidence": (round(v9_rate - floor, 3)
                                          if v9_rate is not None else None),
                "published_by_generator": "per_shape_realised" in (meta.get("coverage") or {}),
            }

        report[vertical] = {
            "questions": len(questions),
            "questions_with_no_gold": len(no_gold),
            "per_shape_realised": shapes,
            "published_mean_realised": published,
            "recomputed_over_all": round(pooled, 4),
            "recomputed_over_gold_bearing": round(measured, 4) if measured is not None else None,
            "dilution": round(measured - pooled, 4) if measured is not None else None,
            "in_band_as_published": published is not None and BAND_LOW <= published <= BAND_HIGH,
            "in_band_as_measured": measured is not None and BAND_LOW <= measured <= BAND_HIGH,
        }
        rows.append((vertical, len(questions), len(no_gold), published, pooled, measured, shapes,
                     "per_shape_realised" in (meta.get("coverage") or {})))

    if as_json:
        print(json.dumps(report, indent=2))
        return 0

    print("COVERAGE POPULATION AUDIT - does the published mean include questions it cannot measure?")
    print("=" * 96)
    print(f"{'vertical':14s} {'q':>4s} {'G=0':>4s} {'published':>10s} {'gold-only':>10s} "
          f"{'dilution':>9s}")
    print("-" * 96)
    diluted = 0
    for vertical, n, zero, published, pooled, measured, _shapes, _pub in rows:
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

    print()
    print("PER-SHAPE COVERAGE, INCLUDING THE FIVE VERTICALS THAT DO NOT PUBLISH IT")
    print("=" * 96)
    print("Only four verticals opted into per-shape calibration, and the vertical mean is exactly")
    print("what hid Prospective's not-yet-true at a saturated 1.000 for its whole shipped life.")
    print("Coverage is structural BM25, so these cost nothing to recompute.")
    print()
    print(f"{'vertical':14s} {'shape':24s} {'q':>3s} {'realised':>9s} {'V9':>7s} {'V9-flr':>7s}  note")
    print("-" * 96)
    for vertical, _n, _z, _pub_mean, _pooled, _measured, shapes, published_shapes in rows:
        for shape, d in shapes.items():
            note = ""
            if d["realised"] >= 1.0:
                note = "saturated"
            elif not (BAND_LOW <= d["realised"] <= BAND_HIGH):
                note = "out of band"
            gap = d.get("scores_above_evidence")
            if gap is not None and gap > 0.15:
                note = ((note + "; ") if note else "") + "SCORES ABOVE ITS EVIDENCE"
            if not published_shapes:
                note = (note + "; not published") if note else "not published"
            v9 = "-" if d.get("v9_rate") is None else f"{d['v9_rate']:.3f}"
            gp = "-" if gap is None else f"{gap:+.3f}"
            print(f"{vertical:14s} {shape:24s} {d['questions']:3d} {d['realised']:9.3f} "
                  f"{v9:>7s} {gp:>7s}  {note}")
    print()
    print("V9-floor is the saturation screen. The floor is coverage for an open question and")
    print("coverage + (1-coverage)/k for a CLOSED-CHOICE one, because a model with gold missing")
    print("still guesses right at 1/k. A shape scoring above its floor is answering without")
    print("evidence the retriever failed to fetch, so that gold was never load-bearing.")
    print("conjunction/order-then-value read +0.333 for its whole shipped life before it was fixed.")
    print("Negative is healthy -- it means reasoning, not retrieval, is the constraint.")
    print()
    print("A saturated shape is not automatically a defect - WorkingMemory's distance ladder is")
    print("declared, and coverage is MEANT to fall with distance, so its short rungs sit high by")
    print("design. Forgetting's never-known sits at 1.000 because it is G=0 and measures nothing.")
    print("Read the generator's declaration before calling any row here a fault.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
