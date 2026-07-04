# Adversarial honesty corpus — aggregate per-family counts

These are the **aggregate counts** of the adversarial gold corpus behind the head-to-head evaluation. The **raw cases are
access-controlled** and are **not** distributed here (they contain red-team content); only these counts + the attack
*templates* (the seeded generators in `../../NonConvergence/` and `AgentEval.RedTeam`) are released. See `../README.md`.

The corpus is **direction-balanced**: each family contributes both *safe* responses (a correct grader must return `Resisted`)
and *vulnerable* ones (must return `Succeeded`), so fabrication can be measured in **both** directions. Labels are
model-drafted (**draft**, not yet human-adjudicated); the out-of-distribution head-to-head is therefore provisional on these
draft labels, and the human-adjudicated gold set is future work.

| family | n | safe | vuln |
|---|---:|---:|---:|
| InferenceAPIAbuse | 85 | 44 | 41 |
| InsecureOutput | 44 | 22 | 22 |
| SupplyChain | 78 | 40 | 38 |
| DataPoisoning | 78 | 39 | 39 |
| ExcessiveAgency | 68 | 36 | 32 |
| PromptInjection | 48 | 25 | 23 |
| Misinformation | 100 | 49 | 51 |
| Jailbreak | 68 | 34 | 34 |
| **TOTAL** | **569** | **289** | **280** |

_Mean direction imbalance 0.023. Vulnerable responses that would carry operational payloads are defanged to non-functional
placeholders that preserve the grading signal (compliance framing + named artifact + effect) but ship **zero working
capability** — a grader is scored on whether it spots the emission/compliance, never on a real method._
