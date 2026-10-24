# AgentEval.AgentSkillsEval

A live sample for evaluating **MAF Agent Skills** (GA'd 2026-07-07) with AgentEval — Phase 1 of the
Agent Skills evaluation work described in [`docs/agent-skills.md`](../../docs/agent-skills.md).

Runs a **real** `ChatClientAgent` against the configured chat provider, wrapped with a **real**
`Microsoft.Agents.AI.AgentSkillsProvider` over a real file-based skill fixture
(`skills/expense-report/`), and asserts on the **real** tool-call trace — no scripted fakes, no
fabricated success. Demonstrates:

- The five skill assertion extension methods in `AgentEval.Assertions.SkillUsageAssertions`:
  `HaveLoadedSkill`, `HaveReadSkillResource`, `HaveRunSkillScript`, `NotHaveRunSkillScript`,
  `HaveDisclosedProgressively`.
- The progressive-disclosure efficiency metric,
  `AgentEval.Metrics.Agentic.SkillDisclosureEfficiencyMetric`.

## Scope

The seven runs cover assertions and disclosure efficiency, compliance scanning, skill-description
injection with an advisory shadow judge, script execution gates, and the health/security index with
manifest drift detection. See [`docs/agent-skills.md`](../../docs/agent-skills.md) for their scope and limits.

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

Requires a configured chat provider — this sample runs a **real** agent and will not fake a run.
It uses the main samples' `AI_INFERENCE_PROVIDER` selector. An explicit missing or invalid selection
does not fall back to another provider. With no selector, auto-detection tries Azure, Bitdeer,
OpenAI, Foundry, then OpenAI-compatible.

| Selector | Variables |
| --- | --- |
| `azure` | `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`, `AZURE_OPENAI_DEPLOYMENT` |
| `bitdeer` | `BITDEER_API_KEY`; optional `BITDEER_ENDPOINT`, `BITDEER_MODEL` |
| `openai` | `OPENAI_API_KEY`; optional `OPENAI_BASE_URL`, `OPENAI_MODEL` |
| `foundry` | `FOUNDRY_ENDPOINT`, `FOUNDRY_API_KEY`, `FOUNDRY_MODEL` (Azure OpenAI protocol) |
| `openai-compatible` | `OPENAI_COMPATIBLE_ENDPOINT`, `OPENAI_COMPATIBLE_MODEL`; optional `OPENAI_COMPATIBLE_API_KEY` |

For example, in PowerShell:

```powershell
$env:AI_INFERENCE_PROVIDER = 'bitdeer'
$env:BITDEER_API_KEY = '<your-key>'
dotnet run --project samples/AgentEval.AgentSkillsEval
```

Azure configurations continue to work (Windows Command Prompt):

```bat
set AI_INFERENCE_PROVIDER=azure
set AZURE_OPENAI_ENDPOINT=https://<your>.openai.azure.com/
set AZURE_OPENAI_API_KEY=<your-key>
set AZURE_OPENAI_DEPLOYMENT=gpt-4o
dotnet run --project samples/AgentEval.AgentSkillsEval
```

Use models that support tool calls. The banner identifies the selected provider/model; model capability
and live behavior still depend on that provider. Missing configuration exits **11** (not measured),
and a missing required skill fixture exits **3** (runtime error).

The first three runs show different real assertion/output combinations:

1. **Read-only policy lookup** — expects `load_skill` → `read_skill_resource`, no script.
2. **Script-computed overage** — expects `load_skill` → `run_skill_script` (a real `run_skill_script` call).
3. **Off-topic task** — the skill genuinely isn't needed; shows the metric's honest vacuous 100/100
   pass (no skill tools called ⇒ nothing to measure) **and** a deliberately-asserted `HaveLoadedSkill`
   check that is expected to FAIL — printed in full, never swallowed, never a false pass.

Runs 4–7 add the compliance scan, injection probe, script gate, and security index/drift check.
The agent runs print their captured tool-call traces; the static checks report the fixture evidence
they inspect. The injection judge is advisory, as the console explains.

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
