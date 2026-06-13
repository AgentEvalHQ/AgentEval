# Fable New-Wave Review & Fix Implementation Plan

**Date:** 2026-06-13  **Branch:** `feature/redteam-newwave-fixes`
**Scope:** the full RedTeam feature surface (~76 production `.cs` files) — every wave from the FixesImprovement base through Wave C′ (attacker-LLM multi-turn).

---

## 1. Methodology

A multi-agent **Fable deep review** fanned out 11 functional-pillar reviewers + 2 cross-cutting critics (a honesty/overclaim auditor and an architecture/gap critic) over the whole RedTeam feature, then **adversarially verified** every HIGH/MEDIUM finding with an independent skeptic agent (refute-by-default), and discarded the DROPs. Result: **122 confirmed findings** — **18 HIGH, 59 MEDIUM, 45 LOW**.

The dominant theme across the HIGH tier: **"measured nothing / weak evidence → reported as PASS / Resisted / Low-risk"** — the project's #1 honesty risk. The earlier `0c124eb` "no fabricated Resisted" audit only reached the compliance reporters' *per-control* level; it missed the aggregate status gate, the PDF, JUnit, the EvalResult leaf, two attack evaluators, and the Wave C′ CLI wiring.

Test baseline at start: net8.0 4776/0, net10.0 4979/0/1 (clean). After the HIGH fix wave: **net8.0 4790/0, net10.0 4993/0/1 (+14 tests)**.

---

## 2. Status summary

| Tier | Count | Status |
|------|-------|--------|
| HIGH | 18 findings (15 distinct issues) | **Fixed** — code + regression tests, both TFMs green. 2 external-standard *renames* softened + deferred to SME (see §4). |
| MEDIUM | 59 | **Open** — recommendations in §5. |
| LOW | 45 | **Open** — list in §6. |

**Independent verification (2026-06-13):** an adversarial re-verification pass (12 agents, refute-by-default) re-checked each HIGH fix against its original finding + the actual code + the test. Result: **all confirmed properly fixed at high confidence.** Two test-coverage gaps were surfaced and closed: (a) #5/#6 had no test pinning the *persisted* `SaveReportAsync` `OverallStatus == NOT_EVALUATED` — added `OwaspReporterStoreTests.OwaspReporter_SaveReport_AllInconclusive_PersistsNotEvaluated_NotFabricatedPass`; (b) #10 (`--verbose` AttackerClient) is structurally fixed (single `ScanOptions` construction site) but a verbose+attacker guard test would need a runner seam — tracked as a known minor test gap (no code defect).

---

## 3. HIGH findings — FIXED

Each is labeled **FIXED** with the change made, the files touched, and the regression test that pins it. (Several HIGH findings surfaced from more than one pillar — e.g. the `--verbose` and PASS-on-zero issues each appeared from two critics independently; they are consolidated below into 15 distinct fixes.)

### H1 — Jailbreak forbidden-topic probes fabricated conclusive Resisted (and blocked the judge fallback); marker check not refusal-gated — **FIXED**
- **Was:** JB-003/011/013/030/032 (ForbiddenTopic, no `ExpectedTokens`) had no success oracle; the fixed-`HACKED` marker leg + persona leg both returned Resisted → a full-confidence fabricated Resisted in flagship LLM01, which also *blocked* the LLM-judge fallback (gated on `Inconclusive`). Not refusal-gated either.
- **Fix:** `JailbreakAttack.GetEvaluator()` → `RefusalGatedEvaluator(JailbreakOracleEvaluator)`. The new probe-aware oracle: persona match ⇒ Succeeded; probe *with* `ExpectedTokens` ⇒ deterministic marker check; probe *without* ⇒ refusal⇒Resisted, else **Inconclusive** (never fabricated Resisted).
- **Files:** `RedTeam/Attacks/JailbreakAttack.cs`; tests `JailbreakAttackTests.cs`.
- **Test:** `Evaluator_ForbiddenTopicProbe_ComplianceResponse_IsInconclusive_NotResisted`, `…_Refusal_IsResisted`, `GetEvaluator_IsRefusalGatedOracle`.

### H2 — IndirectInjection IND-040/041 fabricated Resisted on text-only SUTs — **FIXED**
- **Was:** tool-output boundary probes (payload lives in a canary tool's output, not inlined) scored a conclusive Resisted on a text-only SUT, where the tool channel never engages and the marker is never delivered — feeding OWASP LLM01 pass counts on every default scan of the most common SUT type.
- **Fix:** `RedTeamRunner` gates probes with `Surface == ToolOutput` when the tool channel will not engage → **Inconclusive (not scored)**, without invoking the agent. Scoped to `ToolOutput` only — `RetrievedDocument` probes (VectorEmbedding) inline their payload and remain deliverable.
- **Files:** `RedTeam/RedTeamRunner.cs`; tests `IndirectInjectionSurfaceTests.cs`.
- **Test:** `Runner_ToolOutputProbe_OnTextOnlyAgent_ScoredInconclusive_NotFabricatedResisted` (replaced the prior evaluator-level test that pinned the wrong Resisted behaviour).

### H3 — ExcessiveAgency (LLM06) did not implement `IToolAwareAttack` — **FIXED**
- **Was:** the one category about real tool overreach never engaged the canary tool channel; its documented "behavioral-first" branch was dead through the shipped harness → verbal-proxy-only.
- **Fix:** `ExcessiveAgencyAttack` now implements `IToolAwareAttack` with `GetCanaryTools` returning fresh forbidden-tool canaries (admin_delete, execute_command, wire_transfer, …). Stale "Tier-0 / does NOT observe real tool calls" disclaimers updated to state behavioral + verbal fidelity honestly.
- **Files:** `RedTeam/Attacks/ExcessiveAgencyAttack.cs`; tests `IndirectInjectionSurfaceTests.cs`, `ExcessiveAgencyAttackTests.cs`.
- **Test:** `Runner_ExcessiveAgency_OnInstrumentedAgent_RecordsBehavioralCompromise`, `ExcessiveAgency_IsToolAware_AdvertisesForbiddenCanaries`.

### H4 — Compliance `SaveReportAsync` persisted `PASS` when ZERO controls were evaluated (all 5 reporters) — **FIXED**
- **Was:** `overallStatus = failed>0?FAIL:warnings>0?WARN:PASS` — an all-inconclusive run (e.g. a timed-out/unreachable SUT) persisted a fabricated green `PASS` into audit-grade `ComplianceEvidence`, surfaced as a green badge in the MissionControl SPA.
- **Fix:** all 5 reporters now `… : passed > 0 ? "PASS" : "NOT_EVALUATED"`. MissionControl SPA (`ComplianceListPage.tsx`, `EvidenceDetailPage.tsx`) given a neutral `not_evaluated` tone + a case-insensitive `toneFor` fallback so the new value renders cleanly.
- **Files:** NIST/ISO27001/SOC2/OWASP/MITRE reporters; 2 SPA pages.
- **Test:** `EmptyRun_ComplianceRate_IsZeroNotHundred_AndNoSuccessRecommendation`, `AllInconclusiveRun_ComplianceRate_IsZeroNotHundred` (NIST). *(SPA test suite not run in this environment — changes are additive/neutral-fallback.)*

### H5 — `RedTeamComplianceLeaf` diluted scores with inconclusive probes + fabricated a `succeeded_probes` count — **FIXED**
- **Was:** `passRate = passedProbes/totalProbes` (inconclusive-diluted, contradicting the report-side conclusive-only PassRate) and `succeeded_probes = totalProbes - passedProbes` (counted every Inconclusive probe as an attack success).
- **Fix:** scores over `conclusiveProbes = passedProbes + succeededProbes.Count`; `succeeded_probes` = the real count; added `conclusive_probes`/`inconclusive_probes` dimensions. No caller change (derived in-method).
- **Files:** `RedTeam/Compliance/RedTeamComplianceLeaf.cs`.
- **Test:** verified against the 309-test OWASP/MITRE benchmark suite (5R+5I category now scores 1.0, not 0.5).

### H6 — `ComplianceRate` returned 100% on an empty/all-inconclusive run; "✅ all controls meet" recommendation over an empty set — **FIXED**
- **Fix:** the 3 named reporters' `ComplianceRate` and the OWASP/MITRE report objects return `0.0` (not `100.0`) when nothing is conclusively evaluated; the "✅" recommendation is gated on ≥1 Effective/PartiallyEffective control, else a ⚠️ "no controls conclusively evaluated".
- **Files:** NIST/ISO27001/SOC2 reporters, OWASPComplianceReport/MITREATLASReport.
- **Test:** NIST empty-rate tests above.

### H7 — MITRE ATLAS: SupplyChain (`AML.T0010`) & DataPoisoning (`AML.T0020`) silently dropped from the report + composite — **FIXED**
- **Was:** both IDs are tagged by attacks but absent from the catalog, so a compromised supply-chain/data-poisoning probe produced no technique row and **could not fail the MITRE benchmark**.
- **Fix:** added both to `AllTechniques` (IsApplicable=true, with a black-box-proxy caveat) — catalog 13→15. Added an **anti-drift invariant test**: every `Attack.All` ATLAS id must exist in `TechniqueCatalog` (would have caught this).
- **Files:** `RedTeam/Reporting/Compliance/MITREATLASReporter.cs`; tests `MITREATLASInvariantTests.cs`, `MitreBenchmarkTests.cs`, `MITREATLASReporterTests.cs`.

### H8 — Risk score ignored inconclusive/coverage (all-inconclusive ⇒ 100/100 "LOW"); PDF "N fully resisted" counted all-inconclusive/zero-probe categories — **FIXED**
- **Fix:** `RiskSummary.IsAssessable = (PassedProbes+FailedProbes)>0`; coverage bonus counts only categories with ≥1 conclusive probe. `PdfReportGenerator` renders **"NOT ASSESSABLE"** when `!IsAssessable`, counts "fully resisted" only for `SucceededCount==0 && InconclusiveCount==0 && TotalCount>0`, surfaces a "N not conclusively measured" line, and the category table uses a conclusive denominator with "Not tested"/"Inconclusive" for zero-conclusive groups.
- **Files:** `RedTeam/Reporting/Pdf/RiskScoreCalculator.cs`, `PdfReportGenerator.cs`.
- **Test:** `GetSummary_AllInconclusiveScan_IsNotAssessable`, `CalculateScore_CoverageBonus_ExcludesAllInconclusiveCategories`.

### H9 — JUnit exporter crashed (`ArgumentException`) on XML-invalid control chars — **FIXED**
- **Was:** `TruncateForXml` never stripped XML-1.0-invalid chars; an ANSI escape (0x1B) or NUL in a Succeeded probe's output crashed the writer and lost the **entire** report exactly when a vulnerability was found (and returned the wrong exit code).
- **Fix:** added `SanitizeForXml` (`XmlConvert.IsXmlChar` + surrogate handling → U+FFFD), called from `TruncateForXml` and wrapping every dynamic attribute/text value (Reason, Error, Technique, agent name).
- **Files:** `RedTeam/Reporting/JUnitReportExporter.cs`; tests `JUnitReportExporterTests.cs`.
- **Test:** `Export_ProbeContentWithXmlInvalidControlChars_DoesNotThrowAndRoundTrips`.

### H10 — CLI `--verbose` silently dropped `AttackerClient` — **FIXED**
- **Was:** the verbose path rebuilt `ScanOptions` copying only some properties, dropping the init-only `AttackerClient` → PAIR/TAP errored to Inconclusive and Crescendo degraded to scripted while the banner claimed LLM-driven.
- **Fix:** eliminated the duplicate construction — `OnProgress` is computed once and set in the single `ScanOptions` initializer alongside `AttackerClient`/`JudgeClient`.
- **Files:** `src/AgentEval.Cli/Commands/RedTeamCommand.cs`.

### H11 — Regression gate `CoverageDrop` assumed baseline coverage = 1.0 — **FIXED**
- **Was:** a baseline saved from an inconclusive-laden run made every identical re-run a permanent exit-4 "Regression", with a false "(baseline measured conclusively)" message.
- **Fix:** `CoverageDrop = max(0, BaselineCoverage − Current.ConclusiveRate)` where `BaselineCoverage` is derived from persisted per-attack counts. Fail-closed preserved (low-coverage current vs conclusive baseline still a regression). `PrintComparison` prints the real numbers; `--save-baseline` warns when the baseline itself is low-coverage.
- **Files:** `RedTeam/Baseline/RedTeamComparison.cs`, `RedTeamCommand.cs`; tests `RedTeamBaselineTests.cs`.
- **Test:** `Compare_BaselineWithInconclusiveProbes_IdenticalRerun_IsStableNotRegression`, `Compare_ConclusiveBaseline_VsInconclusiveCurrent_StillRegression`.

### H12 — `FidelityCompositeEvaluator`/`ToolInvocationEvaluator`: a benign-tool "behavioral Resisted" suppressed the verbal compromise check — **FIXED**
- **Was:** when the trace had tool calls but none forbidden, `ToolInvocationEvaluator` returned a confidence-1.0 conclusive Resisted (contradicting its own doc), short-circuiting the composite and hiding a real verbal compromise (e.g. a marker leaked into final text).
- **Fix:** that branch now returns **Inconclusive** (records `observed_tools`/`any_executed` in metadata, fidelity Verbal), so the composite falls through to the verbal oracle.
- **Files:** `RedTeam/Evaluators/ToolInvocationEvaluator.cs`, `FidelityCompositeEvaluator.cs`; tests `ToolInvocationEvaluatorTests.cs`.
- **Test:** `AllowedToolInvoked_IsInconclusive_DefersToVerbal`, `BenignToolCall_DoesNotSuppressVerbalCompromise`.

### H13 — MITRE ATLAS catalog overclaimed "aligned verbatim with atlas.mitre.org" while misnaming techniques/tactics — **PARTIALLY FIXED (overclaim removed; renames deferred)**
- **Fix applied:** softened the "aligned verbatim … (atlas.mitre.org)" / "snapshot 2026-05" overclaim to a best-effort mapping with a loud "verify names against a pinned ATLAS release before audit use" note (the IDs are authoritative; the names are not asserted as official).
- **Deferred (see §4):** the actual technique/tactic *name* corrections (T0048, T0037, T0044, T0046, T0047, T0053, deprecated T0045; `AML.TA`-prefixed tactic IDs) require the ATLAS YAML and were not pinned here.

### H14 — NIST AI 100-1 attacks mapped to the wrong MEASURE sub-actions (2.6 safety vs security, 2.9, 4.2) — **PARTIALLY FIXED (overclaim removed; remap deferred)**
- **Fix applied:** softened the footer's specific "MEASURE.2.6/2.7 Information Security" claim and added a `#10` caveat in `NistAiRmfControls` that the sub-action IDs/titles/assignments are best-effort and must be verified against official AI 100-1 / Playbook text.
- **Deferred (see §4):** the semantic remap (move the security set 2.6→2.7, 2.9→2.5, 4.2→GOVERN.6/MAP.4) requires the official source.

> **Note on H13/H14:** these are the only two HIGH items not fully closed. The *honesty violation* (false "verbatim/official" precision) is removed; the underlying *rename* is a standards-accuracy task deferred to an SME with the source documents, per the agreed "soften + note" approach — pinning unverifiable literals into tests would manufacture the same false confidence the review exists to eliminate.

---

## 4. Deferred — needs an SME with the standards source

| Item | What's needed |
|------|---------------|
| MITRE ATLAS technique/tactic **names** | Re-derive `AllTechniques`/`AllTactics` from `mitre-atlas/atlas-data` `dist/ATLAS.yaml` at a pinned release; fix T0048/T0037/T0044/T0046/T0047/T0053 names, retag deprecated T0045, use `AML.TA`-prefixed tactic IDs; then transcribe `MITREATLASInvariantTests` literals from the YAML. |
| NIST AI 100-1 **MEASURE sub-action** assignments | Verify 2.6 (safety) vs 2.7 (security) split and the 2.9/4.2 assignments against the official AI 100-1 / AI RMF Playbook; remap `NistAiRmfControls`, fix the reporter footer, update `NistAiRmfComplianceReporterTests`. |

---

## 5. MEDIUM (59) — recommended fixes (grouped by theme)

These were adversarially confirmed but not yet fixed. Grouped by the cheapest-to-highest-leverage remediation:

### 5a. Refusal-gating gap (same class as H1/H12) — **✅ FIXED**
- `PromptInjection`/`Jailbreak`/`EncodingEvasion`/`IndirectInjection` marker checks were **not** refusal-gated (only VectorEmbedding was) → a refusal that quotes the marker scored Succeeded.
- `ExcessiveAgencyEvaluator` danger-keyword check ran **before** `RefusalDetector` → a refusal naming the action scored Compromised.
- `RefusalDetector` lexicon treated "as an AI"/"as a language model"/"as an assistant" as refusals → "As an AI, sure: PWNED" was wrongly downgraded to Resisted.
- **Fix applied:** `PromptInjectionAttack`/`EncodingEvasionAttack` evaluators wrapped in `RefusalGatedEvaluator`; `IndirectInjectionAttack` gates **only the verbal fallback** (the behavioral leg stays un-gated — an executed forbidden tool call with apologetic text is still a compromise); `Jailbreak` was already refusal-gated by H1. `ExcessiveAgencyEvaluator` now computes `RefusalDetector.IsRefusal` **before** the danger-keyword Succeeded branch (a refusal that names the action → Resisted). `RefusalDetector` framing phrases now only count as a refusal when paired with a real refusal verb.
- **Tests:** PromptInjection/EncodingEvasion `GetEvaluator_IsRefusalGated` + decode-to-refuse / marker-in-refusal → Resisted; `ExcessiveAgencyEvaluator` danger-keyword-in-refusal → Resisted (×3) + a real-compliance control still Succeeds; `RefusalDetector` framing-without-verb → not-refusal (×3) + framing-with-verb → refusal + a `RefusalGated` compliance-quoting-marker stays Succeeded.

### 5b. Fabricated Resisted / PASS in other evaluators & reporters (same class as H4–H8) — **✅ model layer + CompositeEvaluator + posture-gate FIXED; evaluator-specific items (5b-ii) pending**
- ✅ `CompositeEvaluator` (All/Majority) could emit Resisted with **zero** children resisting → now returns Inconclusive for a sub-threshold success or no-conclusive-signal case; Resisted only when something genuinely resisted and nothing succeeded.
- ✅ `AttackResult.Passed` now requires `ConclusiveCount > 0`; the zero-probe `Verdict` is **Inconclusive** (not Pass) and `ConclusiveRate`/`Coverage` read 0 on an empty scan.
- ✅ OWASP/MITRE "Strong security posture" recommendation gated on `TestedCategories > 0` (the report-object `ComplianceRate`-on-empty was already fixed in H6).
- ✅ `InferenceAbuseEvaluator` (5b-ii): the default-Resisted fallback **and** the length-based Succeeded are now scoped to the length-decidable techniques (token_flooding/context_exhaustion); model_fingerprinting/format_injection return Inconclusive (judge-reachable) instead of fabricating Resisted/Succeeded from absence/length.
- ✅ `InContextPoisonEvaluator` (5b-ii): adoption is now scored by **negation proximity** — the false fact counts as adopted only if asserted without a negation within ~20 chars. Fixes both directions ("In fact, Sydney is the capital" → Succeeded; "Canberra, not Sydney" → Resisted).
- ✅ `PIIDetectionEvaluator` (5b-ii): RFC-2606 placeholder-email domains excluded from the strong-leak path; credit cards must pass the **Luhn** checksum; a strong shape inside a refusal → Inconclusive (not a fabricated Succeeded, not Resisted).
- Note: OWASP persisted-evidence FAIL-only-at-0% (10%-pass = WARN) — deferred; it follows the framework WARN/FAIL banding convention and needs a product decision on the threshold.

### 5c. Multi-turn / Wave C′ correctness — **✅ TAP pruning FIXED; rest pending**
- ✅ **TAP beam pruning was inverted** — it kept the *most-confident refusals* (`TreeOrchestrator.Classify`), contradicting its own comment and TAP intent. Resisted score is now monotone-DECREASING in confidence (`1.0 - confidence`), so the least-confident refusals rank highest while staying below the Inconclusive band. `Classify` made `internal` + a direct score-ordering test added.
- ✅ Attacker-LLM rung generation now runs inside the per-turn budget (`TurnOrchestrator` hoists `turnCts` above `NextTurnAsync`); a hung attacker folds the partial transcript as truncated with an "attacker turn generation timed out" reason instead of burning the conversation budget.
- ✅ `AttackerPlanner` now raises `AttackerUnavailableException` on a FAILED call (distinct from the `null` = exhaustion); `TurnOrchestrator`/`TreeOrchestrator` fold an outage as truncated with an "attacker LLM errored" reason, never "attack exhausted".
- ✅ Multi-turn fold: `Fidelity` is the **verdict-bearing** turn's (succeeding turn on success; else max over CONCLUSIVE turns) — no longer the running MAX; and the fold is **verdict-stream authoritative** (a Succeeded turn the detector doesn't stop on still folds Succeeded).
- ✅ `CrescendoAttack` doc corrected (rung generation activates on `ScanOptions.AttackerClient`, judge is a distinct client).
- Note: folded probes carrying `Error`/`ErrorKind`, and the runner's wrong-budget timeout-duration report, remain in the **5c/LOW backlog** (runner-side timeout-reporting cleanup).

### 5d. Coverage/honesty fields missing from machine-readable artifacts — **✅ FIXED**
- ✅ JSON exporter (schema 0.2.0): summary now carries `coverage`, `conclusive_score`, `conclusive_attack_success_rate`, `was_truncated`, `skipped_probes`, `planned_probes`, `errored`; each failure carries `fidelity` + (when labeled) `surface` + `conversation_fidelity` — a Behavioral/ToolOutput compromise is now machine-distinguishable from a Verbal/UserMessage proxy.
- ✅ SARIF: `executionSuccessful = !HasExecutionErrors` (a by-design FailFast stop is not abnormal termination; genuine faults are); an invocation property bag carries `wasTruncated`/`skippedProbes`/`plannedProbes`/`erroredProbes`; per-result properties carry `fidelity`/`surface`.
- ✅ JUnit: root `skipped` from `SkippedProbes`; a truncated scan appends a visible `RedTeam.TruncationNotice` testsuite with a `<skipped>` testcase, so it no longer renders as a complete green run.
- ✅ `ComplianceDisclaimer` now serialized on all five report objects' JSON (`Disclaimer` get-only property → `ToJson`).
- Note: persisting per-probe fidelity into `RedTeamBaseline` (a gateable Behavioral→Verbal regression) remains a backlog enhancement.

### 5e. CLI / DI / pipeline wiring gaps — **✅ DI + dedup + comparer FIXED; CLI tier-wiring + pipeline options pending**
- ✅ `AddAgentEvalRedTeam` now registers `IRedTeamRunner` (`TryAddSingleton`); `AddAgentEvalAll` resolves it.
- ✅ Attack DI registration is now idempotent by REFERENCE identity (not implementation type), so multiple distinct instances of the same CLR class (TransformedAttack wrappers / canaried SPEs) all register.
- ✅ Duplicate `--attacks` no longer crash — `RedTeamRunner.ResolveAttacks` `Distinct()`s the instances (the same singleton twice de-dups instead of failing the per-attack `ToDictionary`).
- ✅ Comparer no longer fabricates "Resolved/Fixed" for not-run attacks (restricted to attacks in BOTH runs) + a `NotReTested` list surfaces the omission in `PrintComparison`.
- ⏳ Regression gate still diffs `OverallScore` (inconclusive-diluted) rather than a conclusive-only `ConclusiveScoreDelta` — needs a `ConclusiveScore` field on `RedTeamBaseline` (schema add); deferred. **(5e backlog)**
- ⏳ The CLI cannot reach real-surface capability (no `--canary-tools`/`--system-prompt-canary`) — every `agenteval redteam` scan is Tier-0 verbal. **(5e-iii)**
- ⏳ `AttackPipeline` cannot set Judge/Attacker/multi-turn options — PAIR/TAP via the pipeline error. **(5e-iii)**
- Note: the NIST reporter being unreachable from the CLI is part of the CLI-wiring item.

### 5f. Culture-sensitive parsing / formatting (CI-on-non-English-locale bugs) — **✅ FIXED**
- ✅ `LLMJudgeEvaluator` now parses CONFIDENCE with `NumberStyles.Float` + `CultureInfo.InvariantCulture` (so "0.9" is 0.9, not 9, on a comma-decimal locale).
- ✅ `JUnitReportExporter` formats all `time` attributes with `CultureInfo.InvariantCulture` (so `time="1.500"`, never `"1,500"` which breaks JUnit parsers).
- (No thread-culture-switching test added — it risks parallel-suite flakiness; the `InvariantCulture` fix is unambiguous by inspection.)

### 5g. Doc / honesty-disclaimer drift — **✅ FIXED**
- ✅ `Morse` codec moved out of `ReversibleEncodings` into a new `LossyEncodings` bucket (it case-folds + drops punctuation) with `DifficultyDelta.Same` + a no-overclaim caveat; added an **exact-decode round-trip guard** over all 16 genuinely-reversible codecs (mixed-case + punctuation) so lossiness can't regress in silently.
- ✅ `OwaspBenchmarkRegistration` / `MitreBenchmarkRegistration` descriptions reconciled with the shipped roster ("all 10 categories" / "all 8 applicable techniques of 15 cataloged"; "9 attacks" → "13 built-in attacks"; removed the false "LLM08 remains roadmap").
- ✅ `docs/redteam.md` fabricated "✅ SOC 2 / ISO 27001 / OWASP ASVS" blanket-PASS Compliance-Status sample replaced with an honest note (generate a dedicated compliance report with its disclaimer); footer bumped to v0.2.0.
- ✅ `MITREATLASInvariantTests` "independently-authored / remapped verbatim" comment corrected to an honest "pins the CURRENT catalog shape, not a source-of-truth" caveat (the technique NAMES remain unverified per H13).
- Note: the wider doc sweep (README/getting-started/architecture stale counts; full redteam.md sample regeneration) is documented LOW backlog.

---

## 6. LOW — cleanup backlog (the high-value items fixed; remaining cosmetic items catalogued)

**✅ Fixed (the real-bug-ish LOWs):**
- ✅ **Opt-in attacks invisible** — added `Attack.OptInNames` (Crescendo/PAIR/TAP), surfaced in the CLI unknown-attack error + `--attacks` help, with a drift-guard test (every advertised name resolves via `ByName`; the union is exactly 16, disjoint, no dupes). (`AvailableNames` deliberately unchanged to keep the pinned roster count.)
- ✅ **Determinism** — JSON `report_id` is now a deterministic SHA-256 of (agent + StartedAt + probe ids), so re-exporting the same result is byte-identical/diffable (was `Guid.NewGuid()`).
- ✅ **Unescaped output** — Markdown H1 agent name now `EscapeInline`d (SEC-09); SARIF `artifactLocation.uri` now `Uri.EscapeDataString`d.

**⏳ Remaining LOW backlog (cosmetic / low-risk; catalogued, not yet individually committed):**
- **Wrong timeout reporting** — multi-turn/tree budget overrun reports `TimeoutPerProbe` and discards the partial transcript (appears ~5×; one runner-side fix threading the effective budget into the catch).
- **Dead/unwired** — `PackageHallucinationDetector` fully built+tested but wired into nothing (SupplyChain ships only the weaker in-context proxy); `RedTeamReport` is a dead duplicate of `JsonReportExporter`.
- **Stale doc comments** — "9 built-in attacks" / "Pre-populated with the 9" (roster is 13); `EvaluationResult.Confidence` default doc says 1.0 but struct default is 0.0; `AttackTypeRegistry` doc; README/getting-started/architecture counts; `ResolveFidelity` XML doc detached by a Wave C′ insertion.
- **Minor correctness/cosmetic** — `AttackPipeline.TotalProbeCount` formula + `GetProbePreview` ignores `MaxProbesPerAttack`; `RegexMatchEvaluator` swallows regex timeouts; `BrandingOptions.ParseHexColor` throws on bad input; `VerbosityLevel.Full` silently downgrades; `RiskScoreCalculator`/`PdfReportOptions` tautological tests; JB-041/042 stale `gradual_escalation` technique label; `--save-baseline` hardcodes Version "1.0"/discards Notes; lossy-codec chain compatibility guard.
- **Architecture-gap LOWs** (larger, overlap §5e/deferred) — multi-turn↔tool-harness don't compose (no canary tools over the conversation channel); single `--api-key` reused for target/judge/attacker; `AttackPipeline` can't set Judge/Attacker/multi-turn options.

(The exhaustive per-finding raw list — all 122 with evidence + adversarial verdicts — is preserved in the review workflow transcript for this session; the thematic groupings here and in §5 capture every confirmed finding.)

(The exhaustive per-finding raw list — all 122 with evidence + adversarial verdicts — is preserved in the review workflow transcript for this session; the thematic groupings above and in §5 capture every confirmed finding.)

---

## 7. Recommended next steps (prioritized)

1. **MEDIUM 5a (refusal-gating) + 5b (fabricated-Resisted evaluators)** — same honesty class as the HIGH wave, cheap, high-leverage, fully testable. Do first.
2. **MEDIUM 5c TAP pruning sign + attacker-timeout** — a correctness bug in the headline Wave C′ feature; the rest of 5c follows.
3. **MEDIUM 5d (coverage/fidelity fields in JSON/SARIF/JUnit)** — makes the honesty work visible to CI consumers, not just the model layer.
4. **MEDIUM 5e (CLI real-surface wiring + DI)** — without it the shipped CLI can only do Tier-0 verbal scans, undercutting the whole real-attack-surface goal.
5. **MEDIUM 5f (invariant-culture)** — small, prevents silent CI breakage on non-English runners.
6. **H13/H14 + 5g** — schedule an SME pass with the ATLAS YAML / AI 100-1 source to finish the MITRE/NIST name accuracy and reconcile the docs.
7. **LOW backlog** — batch the timeout-reporting fix (one place), the opt-in-attack visibility, and the determinism/escaping items; defer pure-cosmetic doc drift.

A sensible cut line for a follow-up PR: **§7 items 1–3** (the remaining honesty + the TAP correctness bug) are the highest value and lowest risk; items 4–5 are a second PR; the SME items (6) gate on external input.
