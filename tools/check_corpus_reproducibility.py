#!/usr/bin/env python3
"""Regenerates each corpus from its committed generator and checks the bytes still match.

THE CLAIM THIS GATES. Every sidecar records `generator.tool`, `generator.version` and
`generator.seed`, which together assert that the committed corpus is reproducible from the
committed generator. Nothing tested that assertion, and it is exactly the kind of claim that stops
being true without anyone noticing: the generators are not run in CI, so a change to shared
generation code silently desynchronises every corpus from the generator that is supposed to produce
it.

IT HAS ALREADY HAPPENED ONCE. #210 replaced the calibration search (bisection, which assumed a
monotone that 2 of 3 shapes violate) with a sweep plus local refinement. The fix is correct and the
shipped corpora were not regenerated with it, so `arithmetic` and `conjunction` would rebuild with
different echoes than the ones their sidecars record. That is not a corpus being WRONG -- echo is a
difficulty knob, not a correctness property -- but the reproducibility claim was false from the
moment that search changed, and no gate said so.

WHY IT IS SCOPED TO tools/ CHANGES. A full family regeneration takes tens of minutes, which is too
slow for every push and unnecessary on pushes that cannot affect it. Reproducibility can only break
when a generator or the shared calibration code changes, so that is when this runs. Restricting it
by PATH rather than by schedule means the run happens on the change that could break it, while that
change is still in review.

NON-DESTRUCTIVE. The generators write straight into the data directory, so this snapshots every
corpus first and restores it afterwards whatever the outcome -- a failing check must leave the tree
exactly as it found it, or the gate becomes a reason not to run the gate.
"""

from __future__ import annotations

import argparse
import hashlib
import pathlib
import shutil
import subprocess  # DevSkim: ignore DS107369 - runs this repo's own generators with a fixed argv
import sys
import tempfile

TOOLS = pathlib.Path(__file__).resolve().parent
DATA = TOOLS.parent / "src" / "AgentEval.Memory" / "Data" / "typedmemeval"


def digest(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def corpus_files(vertical: str) -> list[pathlib.Path]:
    return sorted((DATA / vertical).glob("*.json"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("verticals", nargs="*",
                        default=sorted(p.name for p in DATA.iterdir() if p.is_dir()))
    parser.add_argument("--allow-dirty", action="store_true",
                        help="run even though the corpus directory has uncommitted changes, which "
                             "this tool will overwrite when it restores its snapshot")
    args = parser.parse_args()

    # REFUSE TO RUN OVER UNCOMMITTED WORK. This snapshots the data directory and restores it at the
    # end, which silently DISCARDS anything written to those files while it runs -- it ate a
    # regenerated corpus that way, and the loss was invisible because the restore looks exactly like
    # a clean pass. A snapshot-and-restore tool and a concurrent edit cannot both be right about
    # what the files should contain, so this stops rather than guessing. CI always starts clean, so
    # the guard costs nothing there.
    if not args.allow_dirty:
        status = subprocess.run(  # DevSkim: ignore DS107369 - fixed argv
            ["git", "status", "--porcelain", "--", str(DATA)],
            capture_output=True, text=True, cwd=str(DATA.parents[3]))
        dirty = [line for line in status.stdout.splitlines() if line.strip()]
        if dirty:
            print('refusing to run: the corpus directory has uncommitted changes, and this '
                  'tool restores a snapshot over them when it finishes. Commit or stash them '
                  'first, or pass --allow-dirty if losing them is fine.')
            for line in dirty[:10]:
                print(f'  {line}')
            return 2

    failures: list[str] = []
    with tempfile.TemporaryDirectory() as tmp:
        backup = pathlib.Path(tmp)
        for vertical in args.verticals:
            for path in corpus_files(vertical):
                shutil.copy2(path, backup / f"{vertical}__{path.name}")

        try:
            for vertical in args.verticals:
                before = {p.name: digest(p) for p in corpus_files(vertical)
                          if not p.name.endswith(".meta.json")}
                generator = TOOLS / f"gen_typedmemeval_{vertical}.py"
                if not generator.exists():
                    failures.append(f"{vertical}: no generator at {generator.name}")
                    continue
                run = subprocess.run(  # DevSkim: ignore DS107369 - fixed argv, repo-local script
                    [sys.executable, str(generator)], cwd=str(TOOLS),
                    capture_output=True, text=True)
                if run.returncode != 0:
                    failures.append(
                        f"{vertical}: generator exited {run.returncode}\n"
                        f"    {(run.stderr or run.stdout).strip().splitlines()[-1][:300]}")
                    continue
                after = {p.name: digest(p) for p in corpus_files(vertical)
                         if not p.name.endswith(".meta.json")}
                for name, was in before.items():
                    now = after.get(name)
                    if now != was:
                        # Reported as it happens, not banked for the summary. A full family run
                        # takes tens of minutes; a gate that prints only its successes looks
                        # indistinguishable from a hang for the whole time it is finding problems,
                        # which is the interval when somebody most wants to know.
                        detail = (f"{vertical}: {name} does NOT reproduce from its generator "
                                  f"({was[:12]} committed, {(now or 'missing')[:12]} rebuilt). "
                                  f"Either regenerate and re-probe the corpus, or revert the "
                                  f"generator change.")
                        failures.append(detail)
                        print(f"  DIFFERS  {detail}")
                    else:
                        print(f"  {vertical}: reproduces ({was[:12]})")
                sys.stdout.flush()
        finally:
            # Restore unconditionally. A gate that leaves the tree dirty on failure is a gate people
            # learn to skip.
            for saved in backup.iterdir():
                vertical, name = saved.name.split("__", 1)
                shutil.copy2(saved, DATA / vertical / name)

    if failures:
        print("\n".join(["", "CORPUS REPRODUCIBILITY FAILURES:"] + [f"  {f}" for f in failures]))
        return 1
    print(f"\nall {len(args.verticals)} corpora reproduce from their committed generators")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
