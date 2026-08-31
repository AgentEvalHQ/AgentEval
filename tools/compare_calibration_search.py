#!/usr/bin/env python3
"""Recomputes every vertical's calibration under the CURRENT search and diffs it against what shipped.

WHY THIS EXISTS. `search_echo` used to bisect on a monotone it asserted and never measured, and
2 of 3 shapes tested are not monotone (ADR-028 s10). Bisection on a non-monotone function returns
wherever the bracket started, so every shipped echo was potentially an artefact of the search path
rather than a property of the corpus. The search is fixed; the question this answers is whether the
fix CHANGES ANYTHING, which is a different question and has to be measured separately.

IT COSTS NOTHING TO RUN. Calibration is structural -- BM25 over generated text, no model calls -- so
what the new search picks is knowable before committing to a re-probe. A shape whose echo does not
move needs no new probe run: same echo, same seed, same generator means the same corpus bytes.
That is the whole point. Re-baselining is priced in probe calls; this tells you which shapes are
actually owed one.

Read the output as a WORK LIST, not a verdict: `moved` shapes need regeneration and a re-probe,
`stable` shapes are already correct and must not be regenerated for tidiness.
"""

from __future__ import annotations

import argparse
import importlib
import json
import pathlib
import inspect
import sys

sys.path.insert(0, str(pathlib.Path(__file__).parent))

import typedmemeval_common as tmc  # noqa: E402

DATA = pathlib.Path(__file__).resolve().parents[1] / "src/AgentEval.Memory/Data/typedmemeval"

#: Verticals opting into per-shape calibration, mirroring each generator's `shape_of` argument.
#: Listed rather than introspected because the generators only pass it inside `__main__`, which
#: does not run on import -- reading it back would mean executing a full generation to learn it.
PER_SHAPE = {"arithmetic", "conjunction", "episodic", "prospective", "semantic"}


def shipped(vertical: str) -> dict:
    path = next((DATA / vertical).glob("*.meta.json"))
    return json.loads(path.read_text(encoding="utf-8"))["coverage"]


def recompute(vertical: str) -> tuple[dict[str, float], float, float]:
    """Returns (echo_by_shape, overall echo, mean) under the search as it stands today."""
    module = importlib.import_module(f"gen_typedmemeval_{vertical}")
    # Read from the signature rather than a positional index into __defaults__, so adding a
    # keyword to finalise cannot silently re-point this at the wrong value.
    seed = inspect.signature(tmc.finalise).parameters["seed"].default
    if vertical in PER_SHAPE:
        _, cal = tmc.calibrate_per_shape(
            module.build, seed, lambda q: (q.extension or {}).get("shape"))
        return cal.echo_by_shape, cal.echo, cal.mean
    _, cal = tmc.calibrate(module.build, seed)
    return {}, cal.echo, cal.mean


def compare(vertical: str) -> dict:
    was = shipped(vertical)
    by_shape, echo, mean = recompute(vertical)
    rows = []
    if by_shape:
        old = was.get("echo_by_shape") or {}
        for shape in sorted(set(by_shape) | set(old)):
            a, b = old.get(shape), by_shape.get(shape)
            rows.append({"shape": shape, "shipped": a, "recomputed": b,
                         "moved": a is None or b is None or abs(a - b) > 1e-9})
    else:
        a, b = was.get("echo"), echo
        rows.append({"shape": "(single knob)", "shipped": a, "recomputed": b,
                     "moved": abs(a - b) > 1e-9})
    return {"vertical": vertical, "shipped_mean": was.get("mean_realised"),
            "recomputed_mean": round(mean, 4), "rows": rows,
            "moved": any(r["moved"] for r in rows)}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("verticals", nargs="*", default=sorted(tmc.VERTICALS))
    parser.add_argument("--json", type=pathlib.Path)
    args = parser.parse_args()

    results = []
    for vertical in args.verticals:
        result = compare(vertical)
        results.append(result)
        flag = "MOVED" if result["moved"] else "stable"
        print(f"\n== {vertical}  [{flag}]  mean {result['shipped_mean']} -> {result['recomputed_mean']}")
        for row in result["rows"]:
            mark = "  <-- MOVED" if row["moved"] else ""
            print(f"   {row['shape']:26} shipped {str(row['shipped']):8} -> {str(row['recomputed']):8}{mark}")
        sys.stdout.flush()

    moved = [r["vertical"] for r in results if r["moved"]]
    print(f"\n{'=' * 60}\nverticals whose calibration MOVES: {moved or 'none'}")
    print("shapes owed a re-probe:",
          sum(1 for r in results for row in r["rows"] if row["moved"]))
    if args.json:
        args.json.write_text(json.dumps(results, indent=1), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
