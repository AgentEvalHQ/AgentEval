#!/usr/bin/env python3
"""
Checks each vertical's difficulty bands against the acceptance rule the consuming project proposed:
a band is a MEMORY difficulty only if the reference retriever slopes down across it AND the answer
model does not.

Both halves are required, and the second is the one that earns its keep. A ladder whose easy end is
quietly the answer model's hard end produces a clean-looking retriever gradient built partly out of
the oracle failing -- which reads as memory difficulty in every number downstream and is not. That
confound is real in this family: Arithmetic's `duration` shape lives at the low input counts and is
where the answer model struggles, so its two easiest bands drag the oracle down.

A band that does not slope is RECLASSIFIED, not dropped. Dropping it would leave the family implying
memory difficulty is only ever lexical, which is the opposite of what it exists to measure -- BM25
has no time component, so a dial measured in days cannot move it however real the difficulty is.

Usage:
    python tools/validate_typedmemeval_difficulty.py           # report
    python tools/validate_typedmemeval_difficulty.py --check   # fail if a stamp disagrees
"""

from __future__ import annotations

import argparse
import collections
import json
import statistics
import sys

import typedmemeval_common as tmc

#: A validated dial must lose at least this much reference-retriever coverage from its easiest band
#: to its hardest. Not a p-value: per-band n runs 2-17 here, far too small for one, and a threshold
#: that small samples cannot clear would reclassify every band including the ones that plainly work.
MIN_RETRIEVER_DROP = 0.15
#: ...and the fall has to be a TREND, not a difference between two endpoints. Endpoint-difference
#: alone validated three verticals on noise the moment the corpus was regenerated: Prospective read
#: 1.00 / 0.67 / 0.80 / 0.70 / 0.79 -- no slope at all, just a high first band with n=1 -- and
#: Forgetting's second band was 0.00 from a SINGLE question. A rank correlation asks the question the
#: band labels actually claim: does coverage fall as the dial rises?
MAX_TREND_RHO = -0.7
#: Bands thinner than this are reported but cannot carry a verdict. n=1 is not a measurement, and
#: two of the three false validations above rested on one.
MIN_BAND_N = 3
#: And a verdict needs at least this many usable bands. Three points that happen to fall in order do
#: so one time in six by chance, so rho = -1.00 on three bands is not evidence -- it is what Episodic
#: and Forgetting produced once the thin bands were excluded. Four points in order is one in
#: twenty-four, which is the least that can honestly be called a gradient at this n.
MIN_USABLE_BANDS = 4
#: ...and the answer model must stay within this of flat across the same bands, or the gradient is
#: partly the oracle failing rather than retrieval getting harder.
MAX_ORACLE_SPREAD = 0.15


def bands(vertical: str) -> tuple[dict[int, list[float]], dict[int, list[bool]], str | None, bool]:
    """Per band: reference-retriever coverage, oracle outcomes, the dial name, and the stamp."""
    corpus_id = f"agenteval-typedmemeval-{vertical}-{tmc.CORPUS_REVISION}"
    root = tmc.DATA_ROOT / vertical
    entries = json.loads((root / f"{corpus_id}.json").read_text(encoding="utf-8"))
    metadata = json.loads((root / f"{corpus_id}.meta.json").read_text(encoding="utf-8"))

    probes = metadata.get("probes", {})
    oracle_failed = set()
    if probes.get("status") == "run":
        oracle_failed = set(probes.get("v1_oracle_answerability", {}).get("failed", []))
    ran = probes.get("status") == "run"

    coverage: dict[int, list[float]] = collections.defaultdict(list)
    oracle: dict[int, list[bool]] = collections.defaultdict(list)
    dial = None
    stamped = False
    for entry in entries:
        extension = entry.get("typedmemeval", {})
        band = extension.get("difficulty")
        if band is None:
            continue
        dial = extension.get("difficulty_dial")
        stamped = bool(extension.get("difficulty_validated"))

        texts = ["\n".join(f"{t['role']}: {t['content']}" for t in session)
                 for session in entry["haystack_sessions"]]
        order = tmc.bm25_rank(entry["question"], texts)
        top = {entry["haystack_session_ids"][i] for i in order[:tmc.K_REF]}
        gold = set(entry["answer_session_ids"])
        coverage[band].append(len(gold & top) / max(1, len(gold)))
        if ran:
            oracle[band].append(entry["question_id"] not in oracle_failed)

    return coverage, oracle, dial, stamped


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true",
                        help="exit non-zero if a difficulty_validated stamp disagrees")
    args = parser.parse_args()

    disagreements = []
    for vertical in tmc.VERTICALS:
        coverage, oracle, dial, stamped = bands(vertical)
        if not coverage:
            print(f"{vertical:<14} no difficulty stamps")
            continue

        order = sorted(coverage)
        means = {band: statistics.mean(coverage[band]) for band in order}
        drop = means[order[0]] - means[order[-1]]
        oracle_means = {band: statistics.mean(oracle[band]) for band in order if oracle.get(band)}
        spread = (max(oracle_means.values()) - min(oracle_means.values())) if oracle_means else None

        # Spearman rank correlation of band index against band mean. Ties averaged; with four or
        # five bands this is a blunt instrument, which is the point -- it is being asked to reject
        # noise, not to size an effect.
        def _ranks(values):
            order = sorted(range(len(values)), key=lambda i: values[i])
            ranks = [0.0] * len(values)
            i = 0
            while i < len(order):
                j = i
                while j + 1 < len(order) and values[order[j + 1]] == values[order[i]]:
                    j += 1
                shared = (i + j) / 2 + 1
                for k in range(i, j + 1):
                    ranks[order[k]] = shared
                i = j + 1
            return ranks

        usable = [b for b in order if len(coverage[b]) >= MIN_BAND_N]
        if len(usable) >= MIN_USABLE_BANDS:
            xs = _ranks([float(b) for b in usable])
            ys = _ranks([means[b] for b in usable])
            mx, my = statistics.mean(xs), statistics.mean(ys)
            num = sum((a - mx) * (b - my) for a, b in zip(xs, ys))
            den = (sum((a - mx) ** 2 for a in xs) * sum((b - my) ** 2 for b in ys)) ** 0.5
            rho = num / den if den else 0.0
        else:
            # NOT zero. Printing an uncomputed statistic as 0.00 is how an absence of measurement
            # becomes a measurement in a table, which is the failure this whole file exists to catch.
            rho = None
        thin = [b for b in order if len(coverage[b]) < MIN_BAND_N]

        slopes = (drop >= MIN_RETRIEVER_DROP and rho is not None and rho <= MAX_TREND_RHO
                  and len(usable) >= MIN_USABLE_BANDS)
        flat = spread is not None and spread <= MAX_ORACLE_SPREAD
        # "Not yet probed" is its own answer and must not collapse into "does not slope". They
        # differ in what to do next: one needs a decision, the other needs a probe run.
        if not slopes:
            verdict, judged = (
                "does not slope" if len(usable) >= MIN_USABLE_BANDS
                else f"only {len(usable)} band(s) with n >= {MIN_BAND_N}; "
                     f"{MIN_USABLE_BANDS} needed to call a gradient"), True
        elif spread is None:
            verdict, judged = "slopes; oracle half NOT YET PROBED", False
        elif not flat:
            verdict, judged = "CONFOUNDED - oracle is not flat", True
        else:
            verdict, judged = "memory difficulty", True

        print(f"{vertical:<14} dial={str(dial):<15} stamped={str(stamped):<5} {verdict}")
        print(f"{'':<14} retriever  " + "  ".join(
            f"{b}:{means[b]:.2f}(n={len(coverage[b])})" for b in order)
            + f"   drop {drop:+.2f}  rho " + ("n/a" if rho is None else f"{rho:+.2f}")
            + (f"  [{len(thin)} band(s) under n={MIN_BAND_N}, excluded]" if thin else ""))
        if oracle_means:
            print(f"{'':<14} oracle     " + "  ".join(
                f"{b}:{oracle_means[b]:.2f}" for b in order if b in oracle_means)
                + f"   spread {spread:.2f}")
        else:
            print(f"{'':<14} oracle     not probed - the second half of the rule cannot be checked")

        if not judged:
            continue
        if stamped and not (slopes and flat):
            disagreements.append(f"{vertical}: stamped difficulty_validated=true but {verdict}")
        if not stamped and slopes and flat:
            disagreements.append(
                f"{vertical}: stamped difficulty_validated=false but the bands do validate")

    if disagreements:
        print("\nSTAMPS DISAGREE WITH THE MEASUREMENT:")
        for line in disagreements:
            print(f"  {line}")
    if args.check:
        sys.exit(1 if disagreements else 0)


if __name__ == "__main__":
    main()
