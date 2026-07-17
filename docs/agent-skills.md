# MAF Agent Skills Evaluation

Microsoft Agent Framework's **Agent Skills** feature (GA'd 2026‑07‑07) lets an agent progressively disclose
capabilities through three stable tools — `load_skill`, `read_skill_resource`, `run_skill_script` — instead of
stuffing every capability into the system prompt up front. AgentEval evaluates and governs that surface end to
end: assertions on the disclosure trace, a compliance scanner for `SKILL.md` authoring, a dedicated red‑team
attack for a poisoned skill description, deterministic governance gates for `run_skill_script` code execution,
and a composite Skill Health & Security Index.

None of this requires a special build — it's the same `AgentEval.Core` / `AgentEval.MAF` / `AgentEval.RedTeam`
packages you already use, plus one new package-free namespace, `AgentEval.Skills`.

> **Live-verified sample:** [`samples/AgentEval.AgentSkillsEval`](https://github.com/AgentEvalHQ/AgentEval/tree/main/samples/AgentEval.AgentSkillsEval)
> runs all seven phases below against a real Azure OpenAI agent and a real file-based skill fixture — every
> pass/fail line in its output is keyed on a real trace, never a bare claim.

## What ships

| Phase | Capability | Status |
|---|---|---|
| 1 | Assertions + progressive-disclosure metric | Inline-ready |
| 2 | Compliance scanner (`SKILL.md` authoring + governance rules) | Inline-ready |
| 3 | Skill-injection red-team attack | **Shadow-only** (judge did not clear calibration on this surface — see below) |
| 3 | `run_skill_script` governance gates | Inline-ready (deterministic, no calibration debt) |
| 4a/4b | Skill Health & Security Index + hash-pin drift detection | Inline-ready (both deterministic) |
| 4c | Skill fuzzing, canary-skill honeypot, typosquat detection | **Not built** — deprioritized, see `strategy/TODO.md` |

## 1 — Assertions and the disclosure-efficiency metric

`AgentEval.Assertions.SkillUsageAssertions` extends the same `ToolUsageAssertions`/`ToolCallAssertion` fluent
API you already use for ordinary tools — no new MAF-type coupling; `AgentEval.Core` still never references
`Microsoft.Agents.AI`. Tool and argument names live in one place, `AgentEval.Skills.SkillToolNames`.

```csharp
result.ToolUsage!.Should()
    .HaveLoadedSkill("expense-report")
    .And().HaveReadSkillResource("expense-report", "resources/policy.md").AfterTool(SkillToolNames.LoadSkill)
    .And().HaveDisclosedProgressively()
    .And().NotHaveRunSkillScript(because: "a policy lookup does not require running the compliance script");
```

- `HaveLoadedSkill` / `HaveReadSkillResource` / `HaveRunSkillScript` match by argument **value** (the
  skill/resource/script name), not just tool name, and degrade to a key-agnostic fallback if a future MAF
  version renames a parameter.
- `HaveDisclosedProgressively()` asserts every `read_skill_resource` / `run_skill_script` call is preceded by
  a `load_skill` for the **same** skill.
- `NotHaveRunSkillScript` is a safety-policy assertion (maps to `NeverCallTool`, requires a `because`).
- `SkillContractAssertions.AssertSkillWellFormed` is a zero-cost sanity check you can run before wiring a
  skill into an agent at all.

The companion metric, `AgentEval.Metrics.Agentic.SkillDisclosureEfficiencyMetric`
(`code_skill_disclosure_efficiency`), scores the observable `load_skill → read_skill_resource →
run_skill_script` funnel: disclosure-order validity, load precision (redundant loads / "load storms"), and an
optional load-selection F1 when `EvaluationContext.Properties["expected_skills"]` is supplied. It never
fabricates a selection score without ground truth, and it never fabricates an "advertise" stage count — the
skill-inventory listing injected into the system prompt isn't a tool call and isn't observable from a
`ToolUsageReport`.

## 2 — Compliance scanner

`AgentEval.Skills.SkillComplianceValidator` (pure, MAF-free, lives in `AgentEval.Core`) checks a skill's GA
`SKILL.md` rules (name/description/compatibility) plus governance flags
(`ScriptRequiresGovernanceReview`, `ResourceFromUntrustedSource`, `AllowedToolsExperimental`) against an
AgentEval-owned `SkillManifest` DTO.

`AgentEval.MAF.Skills.MafSkillScanner` is the one adapter that touches a live `AgentSkill`/`AgentSkillsSource`:

```csharp
var report = await MafSkillScanner.ScanFileSkillsAsync(skillPath, scanAgent);
Console.WriteLine(SkillComplianceReportRenderer.RenderConsole(report));
```

`SkillComplianceReportRenderer` renders console, Markdown, or JSON output.

**Honest, documented limitation:** MAF exposes no public API to enumerate a file skill's resources/scripts
(`AgentFileSkill` stores them in private fields — verified against the live 1.13.0 assembly), so the scanner
re-derives the inventory from the `resources/`/`scripts/` directory convention on disk. Non-file sources
(in-memory/class/MCP skills) are honestly reported with **zero** resources/scripts rather than guessed — this
is locked in by a regression test, not silently swallowed.

**CLI:** the same scanner is reachable without writing any code — `agenteval skills scan <path>` (console/
markdown/json output, `--fail-on-noncompliant` for a CI gate). Credential-free and offline (no model call in
the scan itself — a no-op agent satisfies MAF's own `AgentSkillsSourceContext` constructor requirement only).
See [CLI reference](cli.md#agenteval-skills-scan). v1 is compliance-only, not the full Security Index below.

**Baseline ledger + repo-wide discovery (Wave 1):** `--write-baseline` captures a timestamped snapshot
(structural fingerprint + full file-content hash per skill, reusing `ManifestFingerprint`/
`ManifestDriftDetector` — the same primitive `McpToolDescriptionPoisoningGate` and skill manifest drift
detection already use) into an append-only ledger (`AgentEval.Skills.ISkillBaselineStore`/
`JsonFileSkillBaselineStore`, mirroring — not subtyping — the Memory benchmark's `JsonFileBaselineStore`
pattern). `agenteval skills baseline list|diff|history` inspects it. `--repo` scans every known
skill-directory convention under a repo root (`AgentEval.Skills.AgentSkillDirectoryConventions`) and
aggregates the results. See [CLI reference](cli.md#agenteval-skills-baseline) for the full command surface.

**Cross-location drift + trust-on-first-use (Wave 2):** two governance signals built directly on Wave 1's
content hashing, both surfaced as ordinary `SkillComplianceFinding`s (so they render in console/markdown/
json output automatically, no new schema):
- **Cross-location content drift** — always checked on a `--repo` scan (no extra flag; a single-directory
  scan has only one location by definition). Groups every discovered skill by name across all conventions
  found under the repo root; if the same name resolves to two or more DIFFERENT content hashes, that's
  flagged (`Medium` severity — a governance signal to investigate, since the divergence may be deliberate
  per-tool customization rather than drift or poisoning). `internal static SkillsScanCommand.DetectCrossLocationDrift`.
- **Trust-on-first-use reputation matching** — opt-in via `--check-baseline`. Loads the baseline ledger's
  full history (`ISkillBaselineStore.ListAsync`) and checks whether each scanned skill's current content hash
  already appears in ANY prior snapshot for the same name; a match is reported as an informational (`Low`
  severity) "✓ matches a previously-vetted copy" finding, naming the most recent match's capture date.
  Meaningless on a first-ever scan — pair with `--write-baseline` on earlier runs to build the history it
  reads. `internal static SkillsScanCommand.DetectPreviouslyVetted`.

Neither feature is High severity — both are informational/governance signals, not automatic compliance
failures, so `--fail-on-noncompliant` is unaffected by either.

**Filesystem multi-repo scan (Wave 3a):** `agenteval skills scan-workspace <path>` treats `<path>` as a folder
of *already-cloned* repos — each immediate subdirectory is one repo — and runs the existing `--repo` pipeline
against every one of them, combining the results into one report. Zero new discovery code: it's a fan-out loop
over the same pipeline `--repo` already uses, per repo. Filesystem-only — no network, no credentials, no new
trust surface; the operator's own clone step (`git clone` / `gh repo clone`, their own access, their own
tooling) is what decides which repos are visible here, deliberately kept outside this verb's scope. Every
entry is tagged with a `{repoFolder}/{conventionPath}` location, so the *existing*, unchanged
`DetectCrossLocationDrift` (it only ever groups by skill name, never by location) also catches drift **across
repos** for free — the same "skill X is byte-identical across N repos except M outliers" signal Wave 2 gives
you within one repo, now at workspace scale:

```bash
# clone the repos you want to audit yourself first, e.g.:
#   gh repo clone myorg/service-a ~/audit/service-a
#   gh repo clone myorg/service-b ~/audit/service-b
agenteval skills scan-workspace ~/audit --write-baseline --format json -o report.json
```

Same option surface as `scan --repo` (`--format`, `--fail-on-noncompliant`, `--write-baseline`,
`--baseline-root`, `--check-baseline`) — deliberately a separate verb rather than a `--workspace` flag bolted
onto `scan`, to avoid colliding with Mission Control's own, unrelated `--workspace` concept. Defaults to its
own baseline root (`.agenteval/skills-baselines-workspace`, not `scan`'s `.agenteval/skills-baselines`) so a
later plain `scan --write-baseline` can't accidentally get diffed against a wildly larger workspace-scale
snapshot (or vice versa) — pass `--baseline-root` explicitly if you genuinely want one shared ledger.

**Two known limitations, disclosed here rather than discovered the hard way:**
- **`baseline diff`/`history` track only one location per skill name, even when the SAME scan legitimately
  produced several** (Wave 2's own pre-existing shortcut, `GroupBy(Name).First()`, chosen to avoid crashing on
  duplicate names rather than to build a full per-location history — see the code's own remarks). At `--repo`
  scale this rarely bites; at workspace scale, where the same skill name in N repos is the expected case, it
  means `baseline diff`/`history` can silently miss drift in every repo except whichever one sorts first. The
  live scan-time `CrossLocationContentDrift` finding (re-run each time) does NOT have this limitation — it
  checks every location, every scan. For genuine per-repo longitudinal tracking, treat each repo's own
  baseline separately (`scan --repo <one-repo> --write-baseline`) rather than relying on the workspace ledger.
- **A skill name shared by two unrelated repos looks identical to real drift.** `DetectCrossLocationDrift` has
  no concept of "these repos are unrelated" — two different teams both naming a skill `code-review` with
  legitimately different content will produce the same `Medium`-severity finding as an actual rug-pull. The
  severity and "verify this is intentional" wording already account for this ambiguity; expect more such
  findings to triage as workspace size grows.

## 3 — Skill-injection red-team attack + `run_skill_script` governance

`AgentEval.RedTeam.Attacks.SkillInjectionAttack` (OWASP LLM01, in `Attack.All` — the framework now ships **14**
attack types / **264** probes) red-teams two surfaces:

- a malicious skill's **description**, spliced into the system prompt via `{skills}` — a *higher-trust*
  position than a retrieved document (`InjectionSurface.SkillInstruction`);
- `read_skill_resource` output (`InjectionSurface.SkillResource`).

It reuses the shipped `IndirectInjectionRubric` judge rather than inventing a new mega-judge — but a **live
calibration found the reused rubric does not clear the promotion bar on this new surface**: 4 missed attacks
out of a 52-case, both-directions gold set
(`AgentEval.Guardrails.Judges.Rubrics.SkillInjectionGoldSet`), `IsInlineReady == false`. It ships
**shadow-only** for skills — the judge's verdict is advisory, printed for visibility, but the actual
pass/fail is keyed on real tool-call evidence (did the agent call the forbidden tool?), never on an
uncalibrated judge's opinion. This is documented explicitly, not silently promoted; see
[Gatekeeper — gate reference](gatekeeper/gate-reference.md) for the general calibration bar every judge is
held to.

Governance for the *other* half of the surface — actually executing `run_skill_script` code — is deterministic
and has no calibration debt:

- **`SkillScriptExecutionGate`** (`IToolGate`) — an allowlist gate at the function-invocation seam. Matches by
  argument **value** (every string argument, plus pairwise `"/"`-joins), not by a specific key name, so it
  survives a MAF parameter rename.
- **`SkillScriptApprovalGate`** (`IToolApprovalGate`) — a human-in-the-loop trust allowlist, sitting *before*
  the function-invocation seam (MAF's own approval layer).

**Composition-ordering note:** MAF's own approval pause runs *before* the FICC seam. If you leave
`run_skill_script` on its human-approval-by-default posture, the approval gate — not
`SkillScriptExecutionGate` — is what stops an unlisted script, and `gate.tool.*` will record **zero** blocks.
To demonstrate (or test) the deterministic gate specifically, auto-approve at the MAF layer
(`AgentSkillsProviderOptions.DisableRunSkillScriptApproval = true`) so the call reaches the FICC seam — see
Run 6 of the sample for the full, live-verified walkthrough.

## 4a/4b — Skill Health & Security Index + hash-pin drift

`AgentEval.Skills.SkillSecurityIndex` joins three independently-produced signals into one composite 0–100
score:

| Axis | Source |
|---|---|
| Compliance | Phase 2's `SkillComplianceReport` |
| Efficiency | Phase 1's `SkillDisclosureEfficiencyMetric` (optional) |
| Security | Phase 3's real attack outcome + hash-pin drift findings |

A missing axis is **never** fabricated as perfect — the score is the mean of only the axes actually supplied,
and `SkillSecurityIndexResult.AxesMeasured` tells you how many of the three went in.

```csharp
var indexResult = SkillSecurityIndex.Compute(new SkillSecurityIndexInputs(complianceReport, efficiencyResult: null, securityOutcome));
Console.WriteLine($"Skill Security Index: {indexResult.Score:F0}/100 ({indexResult.AxesMeasured}/3 axes measured)");
```

**Hash-pin drift detection** guards against a "rug pull" — a skill's description or resources silently
changing *after* it was reviewed and trusted. `AgentEval.Guardrails.ManifestFingerprint` (SHA-256) +
`ManifestDriftDetector` are generic, MAF-free primitives — the same pair also backs Gatekeeper's
`McpToolDescriptionPoisoningGate` (see [gate reference](gatekeeper/gate-reference.md)). For skills
specifically:

```csharp
var baseline = SkillManifestBaseline.Capture(manifests, notes: "reviewed and approved 2026-07-16");
await baseline.SaveAsync("skills-baseline.json");
// ... later, before every run:
var current = await baseline.LoadAsync("skills-baseline.json");
var findings = SkillManifestPoisoningGate.CheckDrift(latestManifests, current);
// findings.Any(f => f.Kind == ManifestDriftKind.Changed) => the skill changed since it was approved
```

This is trust-time drift detection (JSON-persisted, mirrors the RedTeam baseline/diff CI pattern), not a
runtime gate that scores every turn — it fires when you re-scan, e.g. in CI before deploying an updated skill
pack.

## 5 — Detecting MAF's silent skill-discovery exclusions

MAF's own `AgentFileSkillsSource.GetSkillsAsync()` silently **excludes** a skill folder from discovery
entirely whenever its `SKILL.md` fails certain GA rules (invalid `name:` characters, consecutive hyphens, a
directory-name mismatch, or a missing/too-long `description:`) — the folder just vanishes from the scan with
no error and no finding, reported as if it never existed. Confirmed against the live 1.13.0 assembly: the
exclusion is **recursive** (a malformed `SKILL.md` at any level blanks its entire subtree, not just itself),
and MAF's own container-mode discovery is bounded to exactly 2 directory levels deep.

`agenteval skills scan` now runs a second, independent raw directory walk (`AgentEval.Core.Skills.RawSkillDirectoryScanner`)
mirroring MAF's discovery convention, reconciles it against what `GetSkillsAsync()` actually returned, and for
anything present-on-disk-but-silently-excluded, re-parses the `SKILL.md` directly and reports a
`SkillExcludedFromDiscovery` (High) finding — worded to make clear the skill is **non-functional as authored**,
not merely non-compliant. `SkillComplianceReport.Coverage.SilentlyExcludedCount` surfaces the count prominently
in every renderer. A `compatibility` field over 500 characters, which otherwise makes `GetSkillsAsync()` throw
and would crash the whole scan, is caught and reported as one clean finding instead.

## What's not built yet

Phase 4c (skill fuzzing via the transform/codec pipeline, a canary-skill honeypot, skill-name typosquatting,
load-storm-as-denial-of-wallet) was deprioritized this session in favor of shipping 4a/4b and the exclusion-
detection fix (§5) with full rigor. Wave 3a (filesystem multi-repo scan, `scan-workspace`) shipped — see §2
above. **Wave 3b** (a live, API-driven org-wide scan reaching repos you haven't cloned — `scan-org` — plus live
upstream verification against a skill's declared source URL) is still gated on a security/credential-scope
review not yet held, since it needs a real GitHub/GitLab API client and token handling that `scan-workspace`
deliberately avoids — see `strategy/TODO.md` (local-only) for the up-to-date backlog.

## Related

- [Assertions](assertions.md) — the general fluent-assertion API `SkillUsageAssertions` extends.
- [Red Team Security](redteam.md) — the general attack framework `SkillInjectionAttack` plugs into.
- [Gatekeeper — gate reference](gatekeeper/gate-reference.md) — the general gate/judge calibration bar, and
  the two other gates (`MemoryWritePoisoningGate`, `McpToolDescriptionPoisoningGate`) that reuse the same
  patterns introduced here.
- [Architecture](architecture.md#maf-agent-skills-assertions) — implementation-level detail (file layout, MAF
  API constraints discovered this session).
