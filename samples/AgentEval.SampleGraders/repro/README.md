# Reproducibility artifact — *"Don't Trust the Grader — Decompose It"*

This folder is the analysis + data half of the paper's reproducible evidence. The grading itself lives in the parent
`AgentEval.SampleGraders` sample (which consumes the production graders in `AgentEval.RedTeam` by project reference); here are
the **Python scorers** and the **released data** that turn a run into the paper's numbers.

```
repro/
├── analysis/
│   ├── confirmatory.py     # non-convergence stats (the C1 result) — pure Python, no deps
│   ├── headtohead.py       # the safety-asymmetric scorecard (grader_metrics) + keyword oracle + aggregator
│   └── score_definitive.py # scores a head-to-head run of 5 graders (same data, same judge)
└── data/
    ├── multiseed-*.json               # non-convergence results, one per family (per-seed fresh-fabrication counts)
    ├── corpus-family-counts.md        # aggregate per-family corpus counts (raw cases are access-controlled)
    └── held-out-honesty-tallies.md    # case-outcome tallies for the κ=1.000/92-pin and 0/810 stochastic results
```

Everything is pure Python 3 (standard library only — no numpy/scipy). `analysis/` writes its `*-result.{md,json}` in place.

---

## 1. Non-convergence of patching (the C1 result) — fully reproducible, no corpus needed

This is the paper's headline negative result, and it reproduces end-to-end from the **public seeded generators** — no gold
corpus required. The `.NET` harness generates each family's cases deterministically from seeds, runs the patch→resweep loop
for both the keyword oracle and the production composite, and writes one `multiseed-<family>.json`.

```bash
# from the repo root — regenerate one family (or loop over all five)
dotnet run --project samples/AgentEval.SampleGraders -c Release -- \
  --nonconvergence --family SupplyChain --seeds 30 \
  --out samples/AgentEval.SampleGraders/repro/data/multiseed-supplychain.json

# then analyze all families present in data/
python samples/AgentEval.SampleGraders/repro/analysis/confirmatory.py
```

`confirmatory.py` prints (and writes `confirmatory-result.md`) the per-family and pooled **tail-mean** with a family-cluster
bootstrap CI. Non-convergence ⇔ the keyword arm's CI lower bound stays **> 0** while the composite sits at **0**. The
`data/multiseed-*.json` files are included so you can run the analysis immediately, and diff a fresh generation against them.

> Families: SupplyChain, DataPoisoning, Misinformation, InferenceAPIAbuse, InsecureOutput. Deterministic keyword arm,
> stochastic composite arm; each seed is one independent corpus draw.

## 2. Head-to-head (the out-of-distribution grader table) — needs the access-controlled corpus

The 5-grader comparison (keyword / single-judge / generic composite / task-specific composite / HarmBench-proxy) grades the
adversarial gold corpus. That corpus is **access-controlled** (red-team content) and is **not** shipped here — only its
aggregate counts (`data/corpus-family-counts.md`). With a copy of the corpus:

```bash
# 1) run the 5 graders on the same sample with one judge model (needs a judge endpoint configured via env vars)
dotnet run --project samples/AgentEval.SampleGraders -c Release -- \
  --head-to-head --gold /path/to/goldset.jsonl --per-family 10 --k 3 --concurrency 8 --out verdicts.json

# 2) score them on the safety-asymmetric scorecard
python samples/AgentEval.SampleGraders/repro/analysis/score_definitive.py verdicts.json
```

`headtohead.py` provides the deterministic keyword oracle, the honest-by-construction aggregator, and `grader_metrics` — the
**safety-asymmetric metric** the paper defines (decisive-accuracy, coverage, dangerous-error count, and a safety-utility that
weights a fabrication/miss 5× and never penalizes an honest abstention). `prep`/`score` read the corpus via the `GOLDSET`
env var.

## 3. Held-out honesty tallies (κ=1.000 / 0-of-810) — verification, not regeneration

`data/held-out-honesty-tallies.md` gives the case-outcome tallies behind the paper's two headline held-out results: the
314-case in-distribution pass (92/92 pins, κ=1.000, 0 directional fabrications) and the 810-trial stochastic-stability run
(81 cases × K=10, 0 fabrication trials, 80/81 fully-stable). These are aggregate counts, not raw cases — released for
verification against the paper's exact numbers, not as inputs you re-run (that still needs the access-controlled corpus,
per §2 above).

## 4. What is *not* here (and why)

- **The raw adversarial corpus** (`goldset.jsonl`) — access-controlled per the paper's ethics section (it illustrates attack
  *patterns* but ships zero working capability; even so, raw cases are gated). Contact the author for research access.
- **Human-adjudicated gold labels** — the shipped corpus labels are model-drafted; human adjudication + a full trained-baseline
  panel are the paper's deferred Stage-2 work.

## Requirements
- .NET SDK (8/9/10) to run the harness; Python 3.8+ (standard library only) for the scorers.
- A judge endpoint (Azure OpenAI / OpenAI) configured via environment variables for the LLM-based graders — see the parent
  `AgentEval.SampleGraders/README.md`. The keyword arm and the non-convergence keyword trajectories are deterministic and need no endpoint.
