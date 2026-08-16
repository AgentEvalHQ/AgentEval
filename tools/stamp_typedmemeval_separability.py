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
from pathlib import Path
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
        # zip() would silently truncate to the shorter array. A corpus whose sessions and dates
        # disagree in length is corrupt, and a separability measurement over the surviving prefix
        # would be a confident number about a corpus nobody has.
        if len(entry["haystack_sessions"]) != len(entry["haystack_dates"]):
            raise SystemExit(
                f"{entry['question_id']}: {len(entry['haystack_sessions'])} sessions against "
                f"{len(entry['haystack_dates'])} dates -- the corpus is malformed.")
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
    parser.add_argument(
        "--baseline", default=None,
        help="path to a known-blocked baseline; fail only on failures that are new or worse")
    args = parser.parse_args()

    failed = False
    failures: dict[str, dict[str, float]] = {}
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
        failures[vertical] = {
            name: score for name, score in report["features"].items()
            if name in tmc.SEPARABILITY_REFUSED_FEATURES and name not in exempt
            and score >= tmc.SEPARABILITY_MAX_AUC
        }
        for name in report.get("bimodal_features", {}):
            failures[vertical].setdefault(name, report["features"].get(name, 1.0))
        failed |= not report["passed"]

        if args.check:
            continue

        corpus_id = f"agenteval-typedmemeval-{vertical}-{tmc.CORPUS_REVISION}"
        path = tmc.DATA_ROOT / vertical / f"{corpus_id}.meta.json"
        metadata = json.loads(path.read_text(encoding="utf-8"))
        metadata.setdefault("probes", {})["v7_separability"] = report
        path.write_text(json.dumps(metadata, indent=2, ensure_ascii=False) + "\n",
                        encoding="utf-8", newline="\n")

    if not args.baseline:
        raise SystemExit(1 if failed else 0)

    # Ratchet, not an exemption. A corpus revision can be legitimately blocked for weeks (v3 is,
    # on three verticals), and a check that is uniformly red for that whole period reports nothing
    # about the work happening on top of it -- a NEW separability regression introduced while
    # fixing the old one would be invisible. The baseline pins the exact known failures and their
    # measured values: anything new, or anything worse, still fails. It can only shrink, and the
    # revision cannot ship until it is empty.
    baseline_path = Path(args.baseline)
    baseline = json.loads(baseline_path.read_text(encoding="utf-8")) if baseline_path.exists() else {}
    known = {v: dict(f) for v, f in baseline.get("blocked", {}).items()}

    regressions = []
    for vertical, current in failures.items():
        for name, score in sorted(current.items()):
            recorded = known.get(vertical, {}).get(name)
            if recorded is None:
                regressions.append(f"{vertical}: NEW blocking feature '{name}' at {score:.3f}")
            elif score > recorded + 1e-6:
                regressions.append(
                    f"{vertical}: '{name}' worsened {recorded:.3f} -> {score:.3f}")

    outstanding = sum(len(f) for f in failures.values())
    if outstanding:
        print(f"\nKNOWN-BLOCKED: {outstanding} refused feature(s) across "
              f"{sum(1 for f in failures.values() if f)} corpora, pinned in {baseline_path.name}.")
        print("This corpus revision cannot ship until that count reaches zero.")
    if regressions:
        print("\nREGRESSION against the recorded baseline:")
        for line in regressions:
            print(f"  {line}")
        raise SystemExit(1)
    print("\nNo separability regression against the baseline.")
    raise SystemExit(0)


if __name__ == "__main__":
    main()
