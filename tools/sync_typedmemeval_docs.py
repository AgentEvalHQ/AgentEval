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

#: Display names that title-casing gets wrong. Everything else falls back to `.title()`, so adding a
#: vertical does not require an entry here -- `bitemporal` raised a KeyError on its first sync, which
#: is a hand-maintained map failing exactly the way hand-maintained things do.
DISPLAY = {
    "workingmemory": "WorkingMemory",
}


def _display(vertical: str) -> str:
    return DISPLAY.get(vertical, vertical.title())

PROBE_HEADER = (
    "| Vertical | V1 oracle | V1 pair-flip | V2 non-inferability | V3 gold-ablated | V6 leave-one-out | V8 full-haystack | V9 BM25 top-K | Retrieval headroom |\n"
    "|---|---|---|---|---|---|---|---|---|"
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


def _headroom_cell(record: dict | None) -> str:
    """V1 - V9: what a better retriever can buy over a lexical baseline.

    This replaced the interference cost in the published table because the interference cost was
    being read as a headroom number and is not one. See the guide's own note.
    """
    if not record or record.get("headroom_over_lexical_retrieval") is None:
        return "—"
    return f"{record['headroom_over_lexical_retrieval']:+.2f}"


def _interference_cell(record: dict | None) -> str:
    """V1 - V8, rendered WITH its sign, because a negative value is a real result here.

    Episodic reads -0.04: two questions fail on gold alone and succeed on the whole haystack, so V1
    is not the strict ceiling ADR-026 calls it. Printing that unsigned, or rounding it to 0.00, would
    hide the one number in this table that contradicts a claim made elsewhere in these docs.
    """
    if not record or record.get("interference_cost") is None:
        return "—"
    return f"{record['interference_cost']:+.2f}"


def check_citation_revisions(revision: str) -> list[str]:
    """Docs whose citation rule names a revision other than `revision`.

    A hardcoded revision in a citation rule is the most expensive kind of stale doc: it tells every
    reader to cite a corpus that was superseded precisely because it was wrong. It happened at
    v3->v4 and was caught by hand; at v4->v5 it was in THREE places -- the guide, docs/cli.md, and
    the CLI command's own doc comment -- and all three still said v4.

    Keyed on the citation TEMPLATE rather than on prose, so supersession notices keep their
    historical revision numbers while live instructions cannot lag. Its own function, and called
    before the tables are built, because a stale citation rule is a real finding and the first
    version of this check sat behind a probe-status check that masked it.
    """
    docs_root = GUIDE.parent.parent.parent
    stale = []
    for doc in sorted(docs_root.rglob("*.md")):
        # ADRs are historical by construction: their numbered sections quote the revision that was
        # current when each decision was taken, and rewriting those would destroy the record. Skipped
        # wholesale rather than pattern-matched, because "which quotes are historical" is exactly the
        # judgement a check should not be making.
        if "_site" in doc.parts or "adr" in doc.parts:
            continue
        for number, line in enumerate(doc.read_text(encoding="utf-8").splitlines(), 1):
            if "TypedMemEval-<Vertical>" not in line and r"TypedMemEval-\<Vertical\>" not in line:
                continue
            # Only lines that actually name a revision. A template row -- "| Subset |
            # `TypedMemEval-<Vertical>` |" -- names none and is not an instruction to cite anything.
            named = set(re.findall(r"\bv\d+\b", line))
            if not named or revision in named:
                continue
            stale.append(f"{doc.relative_to(docs_root)}:{number}: {line.strip()[:100]}")
    return stale


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

        name = _display(vertical)
        probe_rows.append(
            f"| {name} | {_cell(probes.get('v1_oracle_answerability'))} "
            f"| {_cell(probes.get('v1_pair_flip'), total='pairs')} "
            f"| {_cell(probes.get('v2_non_inferability'))} "
            f"| {_cell(probes.get('v3_gold_ablated'))} "
            f"| {_cell(probes.get('v6_leave_one_out'))} "
            f"| {_cell(probes.get('v8_full_haystack'), passed='v8_passed')} "
            f"| {_cell(probes.get('v9_reference_retrieval'), passed='v9_bm25_top_k')} "
            f"| {_headroom_cell(probes.get('v9_reference_retrieval'))} |")

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

    stale = check_citation_revisions(tmc.CORPUS_REVISION)
    if stale:
        joined = "\n  ".join(stale)
        raise SystemExit(
            f"a citation rule does not name the current revision "
            f"({tmc.CORPUS_REVISION}):\n  {joined}")

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
