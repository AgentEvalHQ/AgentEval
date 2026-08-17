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


def self_test() -> bool:
    """Checks the gate refuses a corpus whose ROLE ORDER identifies gold.

    A gate is only worth its green when something is known to turn it red. This reconstructs the
    exact defect the consuming project found in v4-episodic -- gold running u|a|a|u|a while every
    distractor ran u|a|u|a|a -- and asserts refusal.

    Two properties it pins, both of which cost a release round to learn:

    - The pooled AUC does NOT catch this. It reads 0.6152, comfortably under the 0.75 threshold,
      because a tell that is exclusive only WITHIN a question does not move a number pooled across
      50 of them. What catches it is the bimodality rule, at 27 perfectly-separated questions
      against 3.48 expected. A future simplification that drops the distribution test in favour of
      "the AUC is fine" would reopen this, and this test is what says so.
    - Both halves are exercised: the generator's alignment pass is disabled here so the defect
      actually reaches the gate. Without disabling it the corpus repairs itself and the test would
      pass while measuring nothing -- which is how a self-test quietly becomes decoration.
    """
    import random
    import gen_typedmemeval_episodic as episodic

    original_pad, original_normalise = tmc._pad_target, tmc._normalise_role_sequence

    def conditional_pad(session, role="assistant"):
        """The pre-fix behaviour: reuse the last FREE turn of the role; append only if none is."""
        free = [i for i, t in enumerate(session.turns)
                if t.role == role and not getattr(t, "has_answer", False)]
        if free:
            return free[-1]
        session.turns.append(tmc.Turn(role, ""))
        return len(session.turns) - 1

    try:
        tmc._pad_target = conditional_pad
        tmc._normalise_role_sequence = lambda question: None
        seed = 20260815
        questions = episodic.build(0.5, random.Random(seed))  # DevSkim: ignore DS148264 - fixture generation
        tmc.equalise_echo(questions, 0.5, random.Random(seed + 1))  # DevSkim: ignore DS148264
        tmc.equalise_reply(questions, random.Random(seed + 3))  # DevSkim: ignore DS148264
        tmc.equalise_shape(questions, random.Random(seed + 2))  # DevSkim: ignore DS148264
        report = tmc.separability_report(questions)
    finally:
        tmc._pad_target, tmc._normalise_role_sequence = original_pad, original_normalise

    bimodal = report.get("bimodal_features", {})
    pooled = report["features"]["role_sequence"]
    problems = []
    if report["passed"]:
        problems.append("gate PASSED a corpus whose gold is identifiable from role order alone")
    if not any(name.startswith("position_") for name in bimodal):
        problems.append(f"no position_* feature flagged as bimodal; got {sorted(bimodal)}")
    # role_sequence must be distribution-tested, not merely AUC-scored. It was added one revision
    # ago BECAUSE the distribution rule catches role order, and it was added on the code path that
    # skips that rule -- so it was decoration, and this defect was caught only because the
    # position_* features happen to go through the per-session loop. Four verticals then shipped
    # with gold-exclusive phrases for the same reason: gold_marker_ngram was on that path too.
    if "role_sequence" not in report.get("perfect_separation", {}):
        problems.append(
            "role_sequence was not given the distribution test — it is being AUC-scored only, "
            "which is the bypass that let four verticals ship separable")
    if "role_sequence" not in bimodal:
        problems.append(
            f"role_sequence is distribution-tested but not flagged on a corpus whose gold is "
            f"identifiable from role order; got {sorted(bimodal)}")
    if pooled >= tmc.SEPARABILITY_MAX_AUC:
        problems.append(
            f"pooled role_sequence {pooled:.4f} is now above the {tmc.SEPARABILITY_MAX_AUC} "
            f"threshold, so this no longer tests the distribution rule -- pick a defect the "
            f"pooled number still misses")

    for problem in problems:
        print(f"self-test FAILED: {problem}")
    if not problems:
        flagged = sorted(n for n in bimodal if n.startswith("position_"))
        worst = max((bimodal[n]["z"] for n in flagged), default=0.0)
        print(f"self-test OK  (role-order defect refused: {len(flagged)} position features "
              f"bimodal, worst z={worst:.1f}; pooled role_sequence {pooled:.4f} would have passed "
              f"the {tmc.SEPARABILITY_MAX_AUC} threshold)")
    return not problems


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("verticals", nargs="*", default=[])
    parser.add_argument("--check", action="store_true", help="measure and report; write nothing")
    parser.add_argument(
        "--baseline", default=None,
        help="path to a known-blocked baseline; fail only on failures that are new or worse")
    parser.add_argument(
        "--self-test", action="store_true",
        help="check the gate still detects a role-order tell it is known to have missed")
    args = parser.parse_args()

    if args.self_test:
        raise SystemExit(0 if self_test() else 1)

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
        # Exclusivity is a count, not an AUC, so it needs its own ratchet entry -- and it needs one,
        # because it is currently the only test that catches Forgetting's retention markers. Keyed
        # by phrase so a NEW gold-exclusive phrase fails even while known ones are still blocked.
        for gram in report.get("gold_exclusive_ngrams", []):
            failures[vertical].setdefault(f"gold_exclusive:{gram['phrase']}",
                                          float(gram["gold_hits"]))
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
