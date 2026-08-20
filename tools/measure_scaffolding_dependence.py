#!/usr/bin/env python3
"""
Measures how much of a corpus's retrieval difficulty is manufactured by the calibration ECHO CLAUSE.

The calibration gate drags BM25 coverage into the 0.50-0.90 band by injecting the question's own
vocabulary into distractors, as a bracketed, labelled clause: "(Also on my mind: a, b, c.)". That is
an explicitly lexical intervention against an explicitly lexical retriever, and this tool asks the
obvious question nobody had asked: how much of the difficulty survives if a retriever simply ignores
that clause?

Measured on v5, the answer is almost none of it:

    vertical        as shipped   echo stripped from distractors
    arithmetic          0.637              0.953
    forgetting          0.529              0.871
    episodic            0.687              0.975
    prospective         0.700              0.980

Stripping the clause from GOLD moves nothing (+0.065 on arithmetic); stripping it from DISTRACTORS
reproduces the whole gain. So the entire retrieval difficulty of these corpora, for a lexical
retriever, is one parenthetical keyword list on the distractors.

WHY THIS MATTERS AND WHY IT IS STAMPED RATHER THAN FIXED HERE. Difficulty that a one-line regex
defeats is not difficulty. Any retriever that discounts formulaic scaffolding -- by stripping
parentheticals, or semantically, since an incoherent word list is a poor match for a coherent
question -- sees these corpora at 0.87-1.00, which is saturated. Two consequences follow, and both
are published rather than argued:

  1. The BM25 baseline in V9 is ARTIFICIALLY DEPRESSED, so the V1 - V9 headroom figures are an upper
     bound rather than an estimate. A consuming project sizing retrieval work against +0.62 on
     Arithmetic is sizing against a number that includes this artifact.
  2. `scaffolding_dependence` is the honest disclosure until the generator earns its difficulty from
     naturalistic same-domain competition instead. That is a v6 generation change, not a patch.
"""

from __future__ import annotations

import argparse
import json
import re
import sys

import typedmemeval_common as tmc

ECHO_CLAUSE = re.compile(re.escape(f"({tmc.ECHO_LEAD}") + r"[^)]*\)")


def _coverage(entries: list[dict], strip_distractors: bool) -> float:
    hits = []
    for entry in entries:
        gold = set(entry["answer_session_ids"])
        if not gold:
            continue
        texts = []
        for session_id, session in zip(entry["haystack_session_ids"], entry["haystack_sessions"]):
            text = "\n".join(f"{t['role']}: {t['content']}" for t in session)
            if strip_distractors and session_id not in gold:
                text = ECHO_CLAUSE.sub("", text)
            texts.append(text)
        ranked = tmc.bm25_rank(entry["question"], texts)[:tmc.K_REF]
        top = {entry["haystack_session_ids"][i] for i in ranked}
        hits.append(len(gold & top) / len(gold))
    return sum(hits) / len(hits) if hits else 0.0


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true",
                        help="report only; write nothing")
    args = parser.parse_args()

    print(f"{'vertical':<15}{'shipped':>10}{'scaffolding stripped':>23}{'dependence':>12}")
    for vertical in tmc.VERTICALS:
        corpus_id = f"agenteval-typedmemeval-{vertical}-{tmc.CORPUS_REVISION}"
        root = tmc.DATA_ROOT / vertical
        entries = json.loads((root / f"{corpus_id}.json").read_text(encoding="utf-8"))

        shipped = _coverage(entries, strip_distractors=False)
        stripped = _coverage(entries, strip_distractors=True)
        dependence = stripped - shipped
        print(f"{vertical:<15}{shipped:>10.3f}{stripped:>23.3f}{dependence:>+12.3f}")

        if args.check:
            continue
        path = root / f"{corpus_id}.meta.json"
        metadata = json.loads(path.read_text(encoding="utf-8"))
        metadata.setdefault("structure", {})["scaffolding_dependence"] = {
            "bm25_coverage_as_shipped": round(shipped, 4),
            "bm25_coverage_without_distractor_echo": round(stripped, 4),
            "dependence": round(dependence, 4),
            "reading": (
                "How much of this corpus's retrieval difficulty is carried by the bracketed "
                "calibration echo clause on its distractors. A retriever that discounts formulaic "
                "scaffolding sees the second number, so V9's BM25 baseline is depressed by roughly "
                "this much and the V1 - V9 headroom is an upper bound rather than an estimate."),
        }
        path.write_text(json.dumps(metadata, indent=2, ensure_ascii=False) + "\n",
                        encoding="utf-8", newline="\n")

    if not args.check:
        print("\nstamped into structure.scaffolding_dependence")
    sys.exit(0)


if __name__ == "__main__":
    main()
