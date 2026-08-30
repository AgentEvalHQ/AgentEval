#!/usr/bin/env python3
"""V2's reject line sits BELOW the chance floor on closed-choice questions. This measures how far.

WHAT V2 IS FOR. The non-inferability arm asks the reference model each question with NO haystack,
`samples_per_question` times, and rejects the question if it lands the gold answer on
`reject_at_hits` or more of them. The intent is to catch a question answerable from world knowledge
or from its own wording, without the memory.

WHY IT CANNOT DO THAT JOB HERE. A CLOSED-CHOICE question enumerates its own alternatives -- "Which
came first, X or Y?", "Was that me or you?", "Is my Lumen trial still running?" -- so a model that
has never seen the haystack still picks from a set of known size k, and lands gold at rate 1/k by
construction. With the shipped configuration (10 samples, reject at 2) the reject line is an
observed rate of 0.20, while the chance floor is 0.50 at k=2 and 0.33 at k=3. The line is BELOW the
floor. A model that does nothing but guess is rejected with probability 0.989 at k=2.

So on these questions V2 cannot separate a clean question from a guessable one. What its verdict
actually records is whether the reference model ABSTAINED. It passes when the model declines and
fails when the model guesses, and neither outcome is a property of the corpus.

THE DIRECTION MATTERS. An abstaining reference model turns the uninformative zone into PASSES, so
the failure is in the flattering direction -- the corpus collects credit for a gate that never
tested it. That is the shape to flag hardest.

This tool needs NO model calls. `k` comes from the question text alone, by literal pattern, so it
cannot be tuned toward a comfortable answer; the hit counts are read from the probe records already
on disk.

Usage:  python validate_v2_chance_floor.py [--json]
"""
from __future__ import annotations

import glob
import json
import os
import re
import sys
from math import comb
from pathlib import Path

BASE = Path(__file__).resolve().parent.parent / "src/AgentEval.Memory/Data/typedmemeval"

_OF_WHICH = re.compile(r"^Of (.+?), which\b")
_CAME_FIRST = re.compile(r"\bWhich came first, (.+?)\?")
_YESNO = re.compile(r"^(Did|Was|Is|Are|Do|Does|Have|Has|Had|Will|Can|Should|Am|Were)\b")
_OR_TAIL = re.compile(r"\bor\b[^?]*\?$")


def closed_choice_k(question: str) -> int | None:
    """Alternatives the question offers, or None when it is open-ended.

    Read off the question's own wording and nothing else - no model, no answer, no probe record.
    A question that does not enumerate its alternatives is open, and V2 applies to it unchanged.
    """
    q = question.strip()
    m = _OF_WHICH.search(q)
    if m:
        return m.group(1).count(",") + 2
    m = _CAME_FIRST.search(q)
    if m:
        return m.group(1).count(",") + 2
    if _YESNO.match(q):
        return 2
    if _OR_TAIL.search(q) and len(q) < 160:
        return q.count(" or ") + 1
    return None


def p_at_least(hits: int, samples: int, p: float) -> float:
    """One-sided binomial tail: P(X >= hits) for a model that only guesses."""
    return sum(comb(samples, i) * p ** i * (1 - p) ** (samples - i)
               for i in range(hits, samples + 1))


def main() -> int:
    as_json = "--json" in sys.argv
    report: dict[str, object] = {}
    fam = {"questions": 0, "closed": 0, "unearned_pass": 0, "false_fail": 0, "above_chance": 0}
    rows = []

    for vertical in sorted(os.listdir(BASE)):
        corpus_paths = glob.glob(str(BASE / vertical / "*v5.json"))
        meta_paths = glob.glob(str(BASE / vertical / "*v5.meta.json"))
        if not corpus_paths or not meta_paths:
            continue
        corpus = json.loads(Path(corpus_paths[0]).read_text(encoding="utf-8"))
        meta = json.loads(Path(meta_paths[0]).read_text(encoding="utf-8"))
        probes = meta.get("probes", {})
        if probes.get("status") != "run":
            continue
        v2 = probes.get("v2_non_inferability", {})
        samples = v2.get("samples_per_question")
        reject_at = v2.get("reject_at_hits")
        per_q = probes.get("per_question") or {}
        if not samples or not reject_at:
            continue

        detail = []
        for entry in corpus:
            k = closed_choice_k(entry["question"])
            if k is None:
                continue
            record = per_q.get(entry["question_id"]) or {}
            if "v2_hits" not in record:
                continue
            hits = record["v2_hits"]
            chance = 1.0 / k
            # Is the observed hit count actually evidence of inferability, or just guessing?
            p_value = p_at_least(hits, samples, chance)
            gate_says_pass = hits < reject_at
            # UNEARNED PASS: the gate passed it, but a pure guesser would have been rejected, so the
            # pass is a statement about the reference model declining, not about the question.
            guesser_rejected = p_at_least(reject_at, samples, chance)
            unearned = gate_says_pass and guesser_rejected >= 0.5
            # FALSE FAIL: the gate rejected it on a hit count at or below what guessing gives.
            false_fail = (not gate_says_pass) and p_value > 0.05
            detail.append({
                "question_id": entry["question_id"], "k": k, "v2_hits": hits,
                "chance": round(chance, 3), "p_value_vs_chance": round(p_value, 4),
                "gate": "pass" if gate_says_pass else "fail",
                "unearned_pass": unearned, "false_fail": false_fail,
                "above_chance": p_value < 0.05,
            })

        # THE FLOOR IS NOT V2'S ALONE. Every arm reports a raw pass count, and on a closed-choice
        # question a system that only guesses already banks 1/k of them. So the published V1/V8/V9
        # figures for these questions start above zero and no reader is told where. Correcting for
        # it is the standard (observed - chance) / (1 - chance): 0.0 is guessing, 1.0 is perfect.
        arms = {}
        closed_ids = [(d["question_id"], d["k"]) for d in detail]
        floor = sum(1.0 / k for _, k in closed_ids)
        for arm in ("v1", "v8", "v9"):
            passed = sum(1 for qid, _ in closed_ids if (per_q.get(qid) or {}).get(arm) is True)
            n = len(closed_ids)
            arms[arm] = {
                "raw": f"{passed}/{n}",
                "raw_rate": round(passed / n, 4) if n else None,
                "chance_floor": round(floor, 2),
                "chance_corrected": round((passed - floor) / (n - floor), 4) if n > floor else None,
            }

        closed = len(detail)
        unearned = sum(1 for d in detail if d["unearned_pass"])
        false_fail = sum(1 for d in detail if d["false_fail"])
        above = sum(1 for d in detail if d["above_chance"])
        fam["questions"] += len(corpus)
        fam["closed"] += closed
        fam["unearned_pass"] += unearned
        fam["false_fail"] += false_fail
        fam["above_chance"] += above
        report[vertical] = {
            "questions": len(corpus), "closed_choice": closed,
            "v2_reject_at_rate": round(reject_at / samples, 3),
            "unearned_passes": unearned, "false_fails": false_fail,
            "genuinely_above_chance": above,
            "arms_on_closed_choice": arms, "detail": detail,
        }
        rows.append((vertical, len(corpus), closed, unearned, false_fail, above, arms))

    if as_json:
        print(json.dumps({"family": fam, "by_vertical": report}, indent=2))
        return 0

    print("V2 CHANCE-FLOOR AUDIT - closed-choice questions V2 cannot judge")
    print("=" * 96)
    print(f"{'vertical':14s} {'q':>4s} {'closed':>7s} {'unearned':>9s} {'false-fail':>11s} {'>chance':>8s}")
    print("-" * 96)
    for v, n, c, u, f, a, _ in rows:
        print(f"{v:14s} {n:4d} {c:7d} {u:9d} {f:11d} {a:8d}")
    print("-" * 96)
    print(f"{'FAMILY':14s} {fam['questions']:4d} {fam['closed']:7d} {fam['unearned_pass']:9d} "
          f"{fam['false_fail']:11d} {fam['above_chance']:8d}")
    print()
    print(f"UNEARNED PASSES: {fam['unearned_pass']} questions carry a V2 pass that records the")
    print("  reference model abstaining, not the corpus being non-inferable. A pure guesser would")
    print("  have been rejected on every one of them. This is the flattering direction.")
    print(f"FALSE FAILS: {fam['false_fail']} questions are reported as V2 defects on a hit count at or")
    print("  below what guessing alone produces.")
    print(f"GENUINELY ABOVE CHANCE: {fam['above_chance']} - the only closed-choice questions on which")
    print("  V2's verdict carries information about the corpus.")
    print()
    print("THE SAME FLOOR SITS UNDER THE ARMS WE PUBLISH")
    print("=" * 96)
    print(f"{'vertical':14s} {'arm':>4s} {'raw':>9s} {'floor':>7s} {'corrected':>10s}")
    print("-" * 96)
    for v, _, closed_n, _, _, _, arms in rows:
        if not closed_n:
            continue  # nothing closed-choice here, so no floor and nothing to correct
        for arm, d in arms.items():
            corrected = "n/a" if d["chance_corrected"] is None else f"{d['chance_corrected']:.2f}"
            print(f"{v:14s} {arm:>4s} {d['raw']:>9s} {d['chance_floor']:7.1f} {corrected:>10s}")
    print()
    print("Corrected is (observed - chance) / (1 - chance) over the closed-choice questions only:")
    print("  0.00 is indistinguishable from guessing, 1.00 is perfect. A raw count published without")
    print("  it reads as if 0 were the floor, and on these questions it is not.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
