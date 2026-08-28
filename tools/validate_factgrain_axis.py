#!/usr/bin/env python3
"""R2: does fact-grain competition predict V9 misses better than required-session-count G?

The consuming project asked for a paired second difficulty axis, on two conditions: it must be
MEASURED from the corpus (R1), and it must SEPARATE where G does not (R2), validated against our
own reference arms before a corpus ships on it.

This script answers R2 for Arithmetic, and the answer is NO -- not because competition is the wrong
idea, but because we cannot currently test it. Three operationalisations are compared:

  C_bm25    non-gold sessions ranked above the weakest gold session, under the same BM25 the V9
            arm uses.
  C_struct  the corpus's own declared near-miss candidates (`candidates` with matches=false),
            authored at generation time and independent of any retriever.
  C_entity  non-gold sessions sharing a capitalised entity with the question. Set membership, no
            ranking and no term weighting.

Only C_bm25 predicts, and it is the one that cannot count: V9 IS BM25 top-K retrieval, so
"non-gold outranks gold" is very nearly a restatement of "retrieval failed". The two
retriever-independent measures carry no signal at all.

The blocker is structural. V1 and V8 pass every question, so V9 is our ONLY reference arm with
variance, and it is lexical -- any lexical competition measure is confounded with it by
construction. Validating this axis needs an arm that does not share BM25's representation.

Usage:  python validate_factgrain_axis.py [vertical]      (default: arithmetic)
"""
from __future__ import annotations

import json
import os
import re
import statistics as st
import sys
from pathlib import Path

import typedmemeval_common as tmc

DATA = Path(__file__).resolve().parent.parent / "src/AgentEval.Memory/Data/typedmemeval"


def _text(session) -> str:
    if isinstance(session, str):
        return session
    out = []
    for turn in session:
        if isinstance(turn, dict):
            out.append(str(turn.get("content") or turn.get("text") or ""))
        else:
            out.append(str(turn))
    return "\n".join(out)


def _entities(text: str) -> set[str]:
    """Capitalised words and bigrams. Deliberately crude: the point is that it is a SET test with
    no ranking and no term weighting, so it cannot inherit BM25's ordering."""
    return set(re.findall(r"\b[A-Z][a-z]+(?:\s+[A-Z][a-z]+)?\b", text)) - {"I"}


def auc(rows: list[dict], key: str) -> float | None:
    """P(predictor is higher on a V9 failure than on a pass), ties counted as half.

    0.5 is no signal; below 0.5 means the predictor points the wrong way. Reported instead of a
    correlation because the outcome is binary and the predictors are small integers with heavy
    ties."""
    fail = [r[key] for r in rows if not r["v9"]]
    ok = [r[key] for r in rows if r["v9"]]
    if not fail or not ok:
        return None
    return sum((f > p) + 0.5 * (f == p) for f in fail for p in ok) / (len(fail) * len(ok))


def collect(vertical: str) -> list[dict]:
    base = DATA / vertical
    corpus = json.loads(next(base.glob("*v5.json")).read_text(encoding="utf-8"))
    meta = json.loads(next(base.glob("*v5.meta.json")).read_text(encoding="utf-8"))
    per_question = meta.get("probes", {}).get("per_question")
    if not per_question:
        raise SystemExit(f"{vertical}: probes have not been run, so there is nothing to validate against.")

    rows = []
    for entry in corpus:
        ids = entry["haystack_session_ids"]
        gold = {ids.index(g) for g in entry["answer_session_ids"] if g in ids}
        if not gold:
            continue
        docs = [_text(s) for s in entry["haystack_sessions"]]

        ranked = tmc.bm25_rank(entry["question"], docs)
        rank_of = {doc: r for r, doc in enumerate(ranked)}
        weakest_gold = max(rank_of[g] for g in gold)
        c_bm25 = sum(1 for d in range(len(docs)) if d not in gold and rank_of[d] < weakest_gold)

        candidates = entry["typedmemeval"].get("candidates")
        c_struct = sum(1 for c in candidates if not c.get("matches")) if candidates else None

        asked = _entities(entry["question"])
        c_entity = sum(1 for i, d in enumerate(docs) if i not in gold and (_entities(d) & asked))

        record = per_question[entry["question_id"]]
        rows.append({
            "qid": entry["question_id"],
            "shape": entry["typedmemeval"]["shape"],
            "G": len(gold),
            "C_bm25": c_bm25,
            "C_struct": c_struct,
            "C_entity": c_entity,
            "v9": bool(record.get("v9")),
        })
    return rows


def main() -> int:
    vertical = sys.argv[1] if len(sys.argv) > 1 else "arithmetic"
    rows = collect(vertical)
    print(f"{vertical}: {len(rows)} questions, V9 passes {sum(r['v9'] for r in rows)}\n")

    print("PREDICTING A V9 MISS  (AUC; 0.5 = no signal, <0.5 = points the wrong way)")
    for key, label in (("G", "G  required session count"),
                       ("C_bm25", "C_bm25    rank-derived"),
                       ("C_entity", "C_entity  set-based, no ranking")):
        print(f"  {label:<34} {auc(rows, key):.3f}")
    structural = [r for r in rows if r["C_struct"] is not None]
    if structural:
        print(f"  {'C_struct  corpus-declared near-miss':<34} {auc(structural, 'C_struct'):.3f}"
              f"   (n={len(structural)}, shapes that declare candidates only)")

    print("\nWITHIN CONSTANT G -- where G has no residual signal by construction")
    for g in sorted({r["G"] for r in rows}):
        subset = [r for r in rows if r["G"] == g]
        parts = []
        for key in ("C_bm25", "C_entity"):
            value = auc(subset, key)
            parts.append(f"{key} {value:.2f}" if value is not None else f"{key}  n/a")
        passes = sum(r["v9"] for r in subset)
        note = "" if 0 < passes < len(subset) else "   <- no variance, uninformative"
        print(f"  G={g}  n={len(subset):<3} {'  '.join(parts)}{note}")

    print("\nREAD: only the rank-derived measure predicts, and it is the one that cannot count --")
    print("V9 IS BM25 top-K, so 'non-gold outranks gold' nearly restates 'retrieval failed'.")
    print("Both retriever-independent measures carry no signal. V1 and V8 pass every question, so")
    print("V9 is our only arm with variance and it is lexical; the axis cannot be validated")
    print("against our current arms without confounding. It needs a non-lexical reference arm.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
