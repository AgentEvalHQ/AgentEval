#!/usr/bin/env python3
"""Adds ADR-027 SS10 commitment 1 -- per-item type labels on gold -- to every shipped vertical.

Prompt 10's Conjunction vertical composes CERTIFIED verticals rather than authoring fresh
questions, and composition has to know what it is joining: a merged question draws gold from more
than one memory type, and the consuming decision rule needs a per-type denominator. Today the type
is recorded once per QUESTION (`typedmemeval.vertical`), which is sufficient only while every gold
item in a question shares it.

WHY THIS PATCHES THE META AND NOT THE CORPUS, and why it patches rather than regenerates:

  - `corpus_sha256` is sha256 over the ENTIRE corpus JSON including the typedmemeval extension, so
    adding a field there would change the sha of all eight verticals and invalidate every probe
    record with it. That is roughly 16,000 live calls to re-earn for a labelling change that
    measures nothing new. ADR-027 SS10 says this is "a metadata addition, not a regeneration", and
    the sidecar is the only place that is literally true.
  - Regenerating is also destructive for a second reason: `finalise()` writes a FRESH sidecar with
    `probes.status: not_run`, so re-running a generator to add a field would silently discard the
    probe results that field was supposed to sit beside.

So the labels are derived from the corpus that already ships, written into the sidecar, and the
corpus bytes are not touched. The script asserts that: it re-hashes the corpus and refuses to write
if the sha moved.

Idempotent. Safe to re-run. Usage: python add_gold_item_types.py [--check]
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import typedmemeval_common as tmc

DATA = Path(__file__).resolve().parent.parent / "src/AgentEval.Memory/Data/typedmemeval"


def gold_item_types(corpus: list[dict], vertical: str) -> dict[str, list[str]]:
    """One label per gold item, in the order the gold session ids appear.

    Every label is the vertical's own name today, because no shipped corpus mixes types. That is
    the point: the field is uniform now and stops being uniform the moment Conjunction composes,
    which is exactly when a per-question label would stop being enough.
    """
    out: dict[str, list[str]] = {}
    for entry in corpus:
        gold_count = len(entry["answer_session_ids"])

        # A corpus that already knows its own per-item types wins. Conjunction draws gold from two
        # verticals' constructs and records which is which at generation time; deriving from the
        # vertical name here would overwrite that with a uniform label and destroy the per-type
        # denominator the whole commitment exists to enable. It did, once, before this branch.
        declared = entry["typedmemeval"].get("gold_item_types")
        if declared:
            if len(declared) != gold_count:
                raise SystemExit(
                    f"{entry['question_id']}: corpus declares {len(declared)} gold item types for "
                    f"{gold_count} gold items")
            out[entry["question_id"]] = list(declared)
        else:
            out[entry["question_id"]] = [vertical] * gold_count
    return out


def main() -> int:
    check_only = "--check" in sys.argv
    problems: list[str] = []
    changed = 0

    for vertical, (_abbrev, _count) in tmc.VERTICALS.items():
        folder = DATA / vertical
        corpus_path = next(folder.glob("*v5.json"))
        meta_path = next(folder.glob("*v5.meta.json"))

        corpus_text = corpus_path.read_text(encoding="utf-8")
        corpus = json.loads(corpus_text)
        meta = json.loads(meta_path.read_text(encoding="utf-8"))

        # The whole justification for patching the sidecar is that corpus bytes do not move. Assert
        # it rather than trust it.
        recomputed = tmc.sha256_normalized(corpus_text)
        if recomputed != meta["corpus_sha256"]:
            problems.append(
                f"{vertical}: corpus sha {recomputed[:16]} does not match the sidecar's "
                f"{meta['corpus_sha256'][:16]} BEFORE any edit -- refusing to touch it")
            continue

        labels = gold_item_types(corpus, vertical)
        if meta.get("gold_item_types") == labels:
            continue

        if check_only:
            problems.append(f"{vertical}: gold_item_types missing or stale")
            continue

        # Inserted before `probes` so the sidecar reads corpus-facts-then-measurements.
        rebuilt = {}
        for key, value in meta.items():
            if key == "probes":
                rebuilt["gold_item_types"] = labels
            rebuilt[key] = value
        if "gold_item_types" not in rebuilt:
            rebuilt["gold_item_types"] = labels

        meta_path.write_text(json.dumps(rebuilt, indent=2) + "\n", encoding="utf-8", newline="\n")

        # And that the corpus is still byte-identical after the write.
        if corpus_path.read_text(encoding="utf-8") != corpus_text:
            problems.append(f"{vertical}: corpus changed during a sidecar-only edit")
        changed += 1
        print(f"  {vertical}: {len(labels)} questions labelled")

    if problems:
        for problem in problems:
            print(f"FAIL {problem}", file=sys.stderr)
        return 1

    print("all sidecars carry gold_item_types" if not changed else f"{changed} sidecar(s) updated")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
