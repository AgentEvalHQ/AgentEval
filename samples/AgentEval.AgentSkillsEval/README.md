# AgentEval.AgentSkillsEval

A live sample for evaluating **MAF Agent Skills** (GA'd 2026-07-07) with AgentEval — Phase 1 of
[`strategy/FutureFeatures/Skills/AgentEval-AgentSkills-Evals-Design-and-Plan.md`](../../strategy/FutureFeatures/Skills/AgentEval-AgentSkills-Evals-Design-and-Plan.md)
(local-only strategy doc; see `CHANGELOG.md` for the public-facing summary).

Runs a **real** `ChatClientAgent` against Azure OpenAI, wrapped with a **real**
`Microsoft.Agents.AI.AgentSkillsProvider` over a real file-based skill fixture
(`skills/expense-report/`), and asserts on the **real** tool-call trace — no scripted fakes, no
fabricated success. Demonstrates:

- The five skill assertion extension methods in `AgentEval.Assertions.SkillUsageAssertions`:
  `HaveLoadedSkill`, `HaveReadSkillResource`, `HaveRunSkillScript`, `NotHaveRunSkillScript`,
  `HaveDisclosedProgressively`.
- The progressive-disclosure efficiency metric,
  `AgentEval.Metrics.Agentic.SkillDisclosureEfficiencyMetric`.

## Scope

**Phase 1 only** — assertions + metric + this sample. The design doc's Phase 2 (skill-compliance
scanner) and Phase 3 (skill-description-injection red-team + `run_skill_script` governance gates)
are not implemented yet; see the design doc for that roadmap.

## The skill fixture

`skills/expense-report/` — a GA-valid file skill (`SKILL.md` frontmatter: name matches the parent
directory, description ≤1024 chars), with one resource (`resources/policy.md`, the corporate meal
expense policy) and one script (`scripts/summarize.csx`, computes the exact dollar overage against
the $150 dinner cap).

> **Why the script runs in-process, not via a subprocess interpreter.** MAF's Agent Skills GA ships
> **no default subprocess script runner** — verified against the live `Microsoft.Agents.AI 1.13.0`
> assembly this session (there is no `SubprocessScriptRunner` type). A caller must supply an
> `AgentFileSkillScriptRunner` delegate for `run_skill_script` to do anything at all. `Program.cs`
> registers an in-process delegate (`InProcessScriptRunner`) implementing exactly the logic documented
> in `summarize.csx`'s header comment, so the sample has no external interpreter dependency and runs
> the same way on any machine or CI runner.

## Run it

Requires Azure OpenAI credentials — this sample runs a **real** agent and will not fake a run:

```bash
set AZURE_OPENAI_ENDPOINT=https://<your>.openai.azure.com/
set AZURE_OPENAI_API_KEY=<your-key>
set AZURE_OPENAI_DEPLOYMENT=gpt-4o
dotnet run --project samples/AgentEval.AgentSkillsEval
```

It prints three runs, each a different real assertion/output combination:

1. **Read-only policy lookup** — expects `load_skill` → `read_skill_resource`, no script.
2. **Script-computed overage** — expects `load_skill` → `run_skill_script` (a real `run_skill_script` call).
3. **Off-topic task** — the skill genuinely isn't needed; shows the metric's honest vacuous 100/100
   pass (no skill tools called ⇒ nothing to measure) **and** a deliberately-asserted `HaveLoadedSkill`
   check that is expected to FAIL — printed in full, never swallowed, never a false pass.

Every run prints the real captured tool-call trace before any assertion runs, so the trace is always
the ground truth for what's reported — never an assumption.

## Verified against the live MAF 1.13.0 assembly (this session)

The design doc flagged four API details as unverified at design time. All four were confirmed by
reflecting over the real `Microsoft.Agents.AI.dll` (1.13.0) — including building the actual
`AIFunction`s `AgentSkillsProvider` emits for a real file skill and inspecting their JSON schemas:

| Item | Design doc's guess | Verified reality |
|---|---|---|
| (a) tool argument names | `skillName` / `resourceName` / `scriptName` | **Confirmed exactly** — `load_skill{skillName}`, `read_skill_resource{skillName,resourceName}`, `run_skill_script{skillName,scriptName,arguments}` |
| (b) `DisableCaching` shape | builder `.DisableCaching()` + `CachingAgentSkillsSourceOptions.RefreshInterval` | **Confirmed** — both exist on the live assembly |
| (c) provider-level `GetSkillsAsync` convenience | none; source-level only | **Confirmed** — `AgentSkillsProvider` has no such method; only `AgentSkillsSource.GetSkillsAsync(context, ct)` |
| (d) `read_skill_resource` argument shape | path vs. logical name (undetermined) | **Confirmed logical name** — `AgentSkill.GetResourceAsync` does an exact-string lookup against the resource list discovered at skill-load time; `..` traversal, an absolute path, and a bare filename all return "not found" rather than escaping to arbitrary content. A future P3 `SkillResourcePathGate` would guard no live threat and should be dropped per the design doc's own fallback. |

See `SkillToolNames.cs`'s XML doc remarks for the full verification writeup.
