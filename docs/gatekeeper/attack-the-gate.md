# Attack the gate — the closed loop in CI

Gatekeeper's premise is one loop: the **same** policy you *red-team* with also *enforces* at runtime, and both write to one trace. "Attack the gate" closes that loop in CI — run your red-team suite against your **gated** agent, baseline the result, and fail the build if a future change ever lets a probe through.

It runs **credential-free**: the built-in `--sut gatekeeper-demo` is a deliberately compromised agent (it tries to exfiltrate data via a canary tool on every turn) wrapped in a Gatekeeper `CanaryToolGate`. No `--endpoint`, no API key.

## The two-step loop

```bash
# 1. Capture the baseline once, on a known-good commit, and commit the JSON.
agenteval redteam --sut gatekeeper-demo --intensity quick \
  --save-baseline gatekeeper-demo.baseline.json
#   → a small, stable set of conclusive findings, every forbidden tool call blocked; exit 0; baseline written.

# 2. On every PR — fail ONLY if a NEW vulnerability appears vs the baseline (exit 4).
agenteval redteam --sut gatekeeper-demo --intensity quick \
  --baseline gatekeeper-demo.baseline.json --fail-on regression
#   → the gate holds → Stable → exit 0.
```

The baseline records the *known* conclusive failures (their probe ids), the conclusive score, and the coverage. The gate on step 2 is **relative**: it does not fail because some attacks are known-hard — it fails when the *set* of failures grows. Concretely, [`RedTeamBaselineComparer`](../../src/AgentEval.RedTeam/RedTeam/Baseline/RedTeamBaselineComparer.cs) flags a **regression** (exit code `4`) when any of: a new conclusive `Succeeded` probe appears, the conclusive score drops past the threshold, or coverage drops.

### The red case

The build goes **red** the moment a change lets a probe through that the baseline didn't have — a weakened or removed gate, a newly-added tool that isn't gated, a regressed policy. That new conclusive `Succeeded` is a `NewVulnerability` → `--fail-on regression` returns exit `4` → CI fails. That is the whole point: **you can't quietly weaken the Gatekeeper without turning the build red.**

> Exit codes: `0` clean · `1` vulnerabilities present (with `--fail-on vuln`, the default) · `4` regression vs baseline. A regression always outranks the absolute vuln gate.

## GitHub Actions recipe (credential-free)

```yaml
name: attack-the-gate
on: [pull_request]
jobs:
  redteam-regression:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4                 # for the committed baseline JSON
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'                # matches this repo's global.json
      - run: dotnet tool install --global AgentEval.Cli --prerelease
      # Fail the PR if a change let a probe through that the committed baseline didn't have.
      - run: |
          agenteval redteam --sut gatekeeper-demo --intensity quick \
            --baseline ci/gatekeeper-demo.baseline.json --fail-on regression
```

Using the published prerelease tool avoids building the multi‑target CLI in CI; if you'd rather build from source, run it with an explicit TFM — `dotnet run --project src/AgentEval.Cli -c Release -f net8.0 -- redteam …`.

Regenerate the committed baseline (`--save-baseline`) whenever you intentionally change the attack suite or the gate set — otherwise an intended change reads as a regression. Keep the baseline and the run on the **same `--intensity`**; the comparer refuses a cross-intensity diff rather than compare apples to oranges.

## Point it at your real agent

The demo is the on-ramp; the same loop guards a real endpoint — baseline the **gated** build of your own agent:

```bash
agenteval redteam --endpoint "$URL" --model "$MODEL" --intensity moderate \
  --save-baseline redteam-baseline.json
agenteval redteam --endpoint "$URL" --model "$MODEL" --intensity moderate \
  --baseline redteam-baseline.json --fail-on regression
```

See [`docs/redteam.md`](../redteam.md) for the full red-team CLI, and the runnable **Gatekeeper — Defense in Depth** sample (`samples/AgentEval.Samples/Gatekeeper/07_GatekeeperDefenseInDepth.cs`) for the gate stack this loop protects.
