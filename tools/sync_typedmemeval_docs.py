#!/usr/bin/env python3
"""
Rewrites the probe and coverage tables in the TypedMemEval guide from the shipped corpus
metadata.

These tables were hand-copied, and hand-copied numbers drift: the guide has carried a stale
V1 count, a stale coverage figure, and a phrase-recurrence range that appeared in three places
with two different values. Deriving them removes the class of error rather than the instances.

Refuses to write anything if a corpus has no probe record, because a table assembled from
"not_run" would read as a measurement.

Usage:
    python tools/sync_typedmemeval_docs.py            # rewrite the tables
    python tools/sync_typedmemeval_docs.py --check    # fail if they are out of date
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

import typedmemeval_common as tmc

GUIDE = Path(__file__).resolve().parent.parent / "docs" / "benchmarks" / "typedmemeval" / "getting-started.md"

DISPLAY = {
    "prospective": "Prospective",
    "episodic": "Episodic",
    "arithmetic": "Arithmetic",
    "workingmemory": "WorkingMemory",
    "forgetting": "Forgetting",
}

PROBE_HEADER = (
    "| Vertical | V1 oracle | V1 pair-flip | V2 non-inferability | V3 gold-ablated | V6 leave-one-out |\n"
    "|---|---|---|---|---|---|"
)
COVERAGE_HEADER = (
    "| Vertical | n | Mean realised coverage | `G` distribution |\n"
    "|---|---|---|---|"
)


def _metadata(vertical: str) -> dict:
    corpus_id = f"agenteval-typedmemeval-{vertical}-{tmc.CORPUS_REVISION}"
    path = tmc.DATA_ROOT / vertical / f"{corpus_id}.meta.json"
    return json.loads(path.read_text(encoding="utf-8"))


def _cell(record: dict, passed: str = "passed", total: str = "applicable") -> str:
    if not record or not record.get(total):
        return "—"
    return f"{record.get(passed, '—')}/{record[total]}"


def build_tables() -> tuple[str, str]:
    probe_rows, coverage_rows = [], []
    for vertical in tmc.VERTICALS:
        metadata = _metadata(vertical)
        probes = metadata.get("probes", {})
        if probes.get("status") != "run":
            raise SystemExit(
                f"{vertical}: probes are '{probes.get('status')}'. A published table assembled "
                f"from an unrun probe would read as a measurement. Run "
                f"tools/run_typedmemeval_probes.py first.")

        name = DISPLAY[vertical]
        probe_rows.append(
            f"| {name} | {_cell(probes.get('v1_oracle_answerability'))} "
            f"| {_cell(probes.get('v1_pair_flip'), total='pairs')} "
            f"| {_cell(probes.get('v2_non_inferability'))} "
            f"| {_cell(probes.get('v3_gold_ablated'))} "
            f"| {_cell(probes.get('v6_leave_one_out'))} |")

        distribution = ", ".join(
            f"{g} (×{n})" for g, n in sorted(metadata["structure"]["g_distribution"].items(),
                                             key=lambda kv: int(kv[0])))
        coverage_rows.append(
            f"| {name} | {metadata['question_count']} "
            f"| {metadata['coverage']['mean_realised']:.3f} | {distribution} |")

    return (PROBE_HEADER + "\n" + "\n".join(probe_rows),
            COVERAGE_HEADER + "\n" + "\n".join(coverage_rows))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="fail if the guide is out of date")
    args = parser.parse_args()

    probe_table, coverage_table = build_tables()
    text = original = GUIDE.read_text(encoding="utf-8")

    for header, table in ((PROBE_HEADER, probe_table), (COVERAGE_HEADER, coverage_table)):
        pattern = re.compile(re.escape(header) + r"(?:\n\|[^\n]*)+")
        if not pattern.search(text):
            raise SystemExit(f"guide does not contain the expected table header:\n{header}")
        text = pattern.sub(lambda _: table, text, count=1)

    if args.check:
        if text != original:
            raise SystemExit(
                "getting-started.md is out of date with the shipped corpus metadata. "
                "Run: python tools/sync_typedmemeval_docs.py")
        print("guide tables match the shipped metadata.")
        return

    GUIDE.write_text(text, encoding="utf-8", newline="\n")
    print("updated" if text != original else "already up to date")


if __name__ == "__main__":
    main()
