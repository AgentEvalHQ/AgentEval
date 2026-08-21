#!/usr/bin/env python3
"""
What can a GOOD retriever actually score here? Measures accuracy at `K_ref` for a retriever that
discounts the calibration scaffolding, and separates the part of the shortfall that no ranker can fix.

This exists because we published a number we had not measured. Having found that stripping the
bracketed echo clause lifts BM25 COVERAGE to 0.87-1.00, we told a consuming project to expect a
scaffolding-robust retriever to land near V8 -- 0.84 on Arithmetic. That was an extrapolation from a
coverage figure presented as an expectation about accuracy, and it was wrong:

    vertical      coverage(stripped)   ACCURACY(stripped)   V8      G        questions with G > K_ref
    arithmetic          0.953               0.640          0.840   3-6      14 of 50
    forgetting          0.871               0.914          1.000   0,2      0 of 50

The gap is not ranking. It is `K`. Arithmetic asks 14 questions whose answer needs MORE than
`K_ref` = 5 sessions, so a top-5 retriever cannot physically deliver their inputs however good it is,
and one missing input to a count or a sum is a wrong answer. Forgetting, whose G never exceeds K,
nearly saturates.

**So part of what the guide publishes as `V1 - V9` retrieval headroom is unreachable at `K_ref`.** It
is a property of the corpus's G distribution against the retrieval budget, not of any retriever, and
a consuming project sizing a better ranker against it would be buying something a larger K gives away
for free. That is the distinction this tool measures and stamps.

Costs one answer-model call per question. Model-free parts (coverage, G-over-K counts) run without
credentials via --structural-only.
"""

from __future__ import annotations

import argparse
import json
import re
import sys

import typedmemeval_common as tmc
import run_typedmemeval_probes as probes

ECHO_CLAUSE = re.compile(re.escape(f"({tmc.ECHO_LEAD}") + r"[^)]*\)")


def measure(vertical: str, structural_only: bool) -> dict:
    corpus_id = f"agenteval-typedmemeval-{vertical}-{tmc.CORPUS_REVISION}"
    root = tmc.DATA_ROOT / vertical
    entries = json.loads((root / f"{corpus_id}.json").read_text(encoding="utf-8"))

    covered, correct, scored, over_budget = [], 0, 0, 0
    for entry in entries:
        gold_ids = set(entry["answer_session_ids"])
        if not gold_ids:
            continue
        if len(gold_ids) > tmc.K_REF:
            over_budget += 1

        ranked_text = []
        for session_id, session, date in zip(entry["haystack_session_ids"],
                                             entry["haystack_sessions"],
                                             entry["haystack_dates"]):
            text = probes.render([session], [date])
            ranked_text.append(ECHO_CLAUSE.sub("", text) if session_id not in gold_ids else text)

        ranked = tmc.bm25_rank(entry["question"], ranked_text)[:tmc.K_REF]
        top_ids = {entry["haystack_session_ids"][i] for i in ranked}
        covered.append(len(gold_ids & top_ids) / len(gold_ids))

        if structural_only:
            continue
        # The model reads the ORIGINAL sessions the stripped ranking chose: discounting scaffolding
        # is a retrieval-side step, not a rewrite of the corpus.
        key = probes.question_key(entry)
        answer = probes.complete(
            probes.ask(entry["question"], entry["question_date"],
                       probes.subset(entry, sorted(ranked))),
            cache_key=f"{key}:v9strip")
        scored += 1
        if probes.produced_gold(entry["question"], entry["answer"], answer,
                                f"{key}:v9strip:judge"):
            correct += 1
        # Bank the completions periodically. Without this the whole run's cache is held in memory
        # until exit, and a killed process re-pays for everything -- which is not hypothetical here:
        # probe runs in this repo are killed routinely, and a separate cache defect already cost
        # ~30,000 completions in one go.
        if scored % 25 == 0:
            with probes._cache_lock:                      # noqa: SLF001 - same package, same file
                probes._flush_cache()                     # noqa: SLF001

    record = {
        "k_ref": tmc.K_REF,
        "coverage_scaffolding_stripped": round(sum(covered) / len(covered), 4) if covered else None,
        "questions_needing_more_than_k_ref": over_budget,
        "questions_scored": len(covered),
        "reading": (
            "Accuracy a retriever can reach at K_ref if it discounts the calibration scaffolding. "
            "Where questions_needing_more_than_k_ref is non-zero, part of the V1 - V9 headroom is "
            "unreachable by ANY ranker at this budget -- it is a G-against-K property of the corpus, "
            "and a larger K buys it more cheaply than a better retriever."),
    }
    if not structural_only and scored:
        record["accuracy_scaffolding_stripped"] = round(correct / scored, 4)
    return record


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("verticals", nargs="*", default=[])
    parser.add_argument("--structural-only", action="store_true",
                        help="coverage and G-over-K only; no model calls")
    parser.add_argument("--check", action="store_true", help="report only; write nothing")
    args = parser.parse_args()

    print(f"{'vertical':<15}{'cov(strip)':>12}{'acc(strip)':>12}{'G>K_ref':>9}")
    for vertical in (args.verticals or list(tmc.VERTICALS)):
        record = measure(vertical, args.structural_only)
        accuracy = record.get("accuracy_scaffolding_stripped")
        print(f"{vertical:<15}{record['coverage_scaffolding_stripped']:>12.3f}"
              f"{(f'{accuracy:.3f}' if accuracy is not None else '-'):>12}"
              f"{record['questions_needing_more_than_k_ref']:>9}")
        if args.check:
            continue
        corpus_id = f"agenteval-typedmemeval-{vertical}-{tmc.CORPUS_REVISION}"
        path = tmc.DATA_ROOT / vertical / f"{corpus_id}.meta.json"
        metadata = json.loads(path.read_text(encoding="utf-8"))
        metadata.setdefault("structure", {})["retrieval_ceiling"] = record
        path.write_text(json.dumps(metadata, indent=2, ensure_ascii=False) + "\n",
                        encoding="utf-8", newline="\n")
    sys.exit(0)


if __name__ == "__main__":
    main()
