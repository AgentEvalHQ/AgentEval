#!/usr/bin/env python3
"""
Measures V7 (adversarial separability) over the shipped TypedMemEval corpora and stamps the record
into each corpus's .meta.json.

Separate from the generators because it must be runnable against corpora that already exist: V7 was
added after v0.22.0-beta shipped, and re-running a generator to obtain the record would reset the
V1-V6 probe records that cost a reference model several thousand calls to produce. This reads the
corpus, computes the record, and merges it in — leaving the corpus bytes, its hash, and every other
record untouched.

Model-free and deterministic, so unlike tools/run_typedmemeval_probes.py it needs no credentials and
can run in CI. The C# side re-asserts the same property over the shipped corpora
(TypedMemEvalCorpusTests.NoCheapFeatureSeparatesGoldFromDistractors), because a record stamped by a
tool nobody re-runs is a claim, not a check.

Usage:
    python tools/stamp_typedmemeval_separability.py            # all verticals
    python tools/stamp_typedmemeval_separability.py --check    # measure and report, write nothing
"""

from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone

import typedmemeval_common as tmc


def load(vertical: str) -> tuple[list[tmc.Question], str]:
    """Rebuilds Question objects from a shipped corpus.

    Only the fields separability reads are reconstructed — turns, gold labelling, order. The
    extension block and answers are irrelevant to a question about whether sessions are separable
    by shape, and rebuilding them would only invite drift.
    """
    corpus_id = f"agenteval-typedmemeval-{vertical}-{tmc.CORPUS_REVISION}"
    text = (tmc.DATA_ROOT / vertical / f"{corpus_id}.json").read_text(encoding="utf-8")
    questions = []
    for entry in json.loads(text):
        ids = entry["haystack_session_ids"]
        gold = {ids.index(a) for a in entry["answer_session_ids"] if a in ids}
        sessions = [
            tmc.Session(
                [tmc.Turn(t["role"], t["content"], bool(t.get("has_answer"))) for t in turns],
                datetime.strptime(date, tmc.DATE_FORMAT),
                is_gold=index in gold,
            )
            for index, (turns, date) in enumerate(zip(entry["haystack_sessions"], entry["haystack_dates"]))
        ]
        questions.append(tmc.Question(
            entry["question_id"], entry["question_type"], entry["question"], entry["answer"],
            datetime.strptime(entry["question_date"], tmc.DATE_FORMAT), sessions))
    return questions, tmc.sha256_normalized(text)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("verticals", nargs="*", default=[])
    parser.add_argument("--check", action="store_true", help="measure and report; write nothing")
    args = parser.parse_args()

    failed = False
    for vertical in (args.verticals or list(tmc.VERTICALS)):
        questions, corpus_sha = load(vertical)
        # WorkingMemory pins its fact to session 0 by design (ADR §5.4), so position separates gold
        # perfectly and is supposed to. Declared here rather than tolerated silently.
        exempt = (frozenset({"position_in_haystack"})
                  if vertical == "workingmemory" else frozenset())
        report = tmc.separability_report(questions, exempt)
        report["status"] = "run"
        report["measured_at"] = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        # Bound to the corpus text it describes, exactly as the V1-V6 records are: a record that
        # outlived the corpus it was measured on is a claim about questions that no longer exist.
        report["probed_corpus_sha256"] = corpus_sha

        worst = (f"{report['worst_refused_feature']} AUC {report['worst_refused_auc']:.3f}"
                 f" | filler-phrase recurrence "
                 f"{report['boilerplate_ngram_auc']:.3f}")
        print(f"{vertical:14s} {'PASS' if report['passed'] else 'FAIL'}  worst: {worst}"
              f"  [threshold {tmc.SEPARABILITY_MAX_AUC}]")
        print(f"{'':16s}" + "  ".join(f"{k}={v:.3f}" for k, v in sorted(report["features"].items())))
        failed |= not report["passed"]

        if args.check:
            continue

        corpus_id = f"agenteval-typedmemeval-{vertical}-{tmc.CORPUS_REVISION}"
        path = tmc.DATA_ROOT / vertical / f"{corpus_id}.meta.json"
        metadata = json.loads(path.read_text(encoding="utf-8"))
        metadata.setdefault("probes", {})["v7_separability"] = report
        path.write_text(json.dumps(metadata, indent=2, ensure_ascii=False) + "\n",
                        encoding="utf-8", newline="\n")

    raise SystemExit(1 if failed else 0)


if __name__ == "__main__":
    main()
