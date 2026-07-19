# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Docs/samples hardening follow-up + NuGetConsumer refresh to 0.16.0-beta

A self-review of the BUG-22/Explainability & Trust batch below found real gaps and closed them, then a
follow-on pass brought the Skills/Copilot Studio/Explainability & Trust docs and samples to full parity and
refreshed the NuGet consumer samples.

#### Fixed — completing the BUG-22 remap
- **`BenchTraceFidelityCommand.cs`** and **`BenchPerfCommand.cs`** each independently hardcoded the exact
  same gate-fail-as-exit-2 pattern the BUG-22 fix below was supposed to have unified everywhere — missed in
  the first pass. `BenchPerfCommand` now delegates to the shared `BenchExitCodes.FromLabel` instead of
  reimplementing it; both return `GateFailed` (9) on a hard fail.
- **`AzureChatAgentFactory.cs`** — the SUT-agent-resolution counterpart to `JudgeFactory.cs` — had the
  identical config-resolution-conflated-with-usage-error bug across 4 return sites, also missed. Now returns
  `RuntimeError` (3).
- 8 more docs described the old exit-code contract and were never updated: `docs/cli.md`'s resolution-order
  prose, `docs/gatekeeper-cli.md`'s cross-reference, and 6 getting-started pages (gdpr/memory/longmemeval/
  mitre/owasp/perf). One (`memory`) was actually wrong even *before* BUG-22 — it claimed WARN mapped to exit
  0 alongside PASS, which was never true.

#### Added — docs, mirroring the missing coverage
- `docs/gatekeeper/explainability-and-trust.md` — the docs page the Explainability & Trust library code below
  had shipped without any `docs/` coverage at all.
- `docs/agent-skills-whats-new.md` and `docs/gatekeeper-whats-new.md` — capability-history pages mirroring
  the existing `docs/redteam-whats-new.md` pattern, so a shipped-but-undocumented capability (this happened
  twice in the same area: SkillGate, then Explainability & Trust itself) is easier to catch next time.
- **"New to this? Start here"** plain-English concept sections added to `docs/agent-skills.md`,
  `docs/copilot-studio.md`, and `docs/gatekeeper/explainability-and-trust.md`, plus short "in plain English"
  framing on Agent Skills' three densest phases — docs read as progressive, not front-loaded with jargon.

#### Added — samples
- `samples/AgentEval.Samples/Gatekeeper/10_GatekeeperExplainabilityAndTrust.cs` (new) — 3 gradual scenes:
  `GateProvenance` (a real judge call) → `GateReplayer` (deterministic) → `TrustScoreCalculator` (combines
  both). Live-verified against real Azure OpenAI.
- `samples/AgentEval.Samples/CopilotStudio/00_CopilotStudioHelloWorld.cs` (new) — a true one-concept on-ramp
  before the existing multi-concept walkthroughs (which each covered 4-5 concepts at once).

#### Changed — docs structure hygiene
- Moved `docs/redteam/copilot-studio.md` → top-level `docs/copilot-studio.md`: it covers eval/bench
  integration, fluent assertions, and Gatekeeper composition, not just red-teaming — inconsistent with the
  `AgentEval.MAF.CopilotStudio` package and the samples menu, both of which already treat it as its own
  top-level area (matching Agent Skills' precedent). All inbound references updated.
- Renamed `ResponsibleAI.md` → `responsible-ai.md` and `docs/GlassBox/` → `docs/glassbox-history/` for
  kebab-case consistency with every other doc file/folder.
- **New CI check**: `tools/check_docs_toc.py` + `.github/workflows/docs-toc-check.yml` fails a PR if any
  `docs/**/*.md` file isn't reachable from `docs/toc.yml` (the real site navigation, not `docs/index.md`'s
  separately-maintained landing-page list) — the exact mechanism that let two doc pages ship invisible in the
  sidebar this session, found only by manual audit. **Extended** to also catch the reverse drift: a local
  link in `docs/index.md`'s landing page pointing at a page that isn't (or is no longer) in `docs/toc.yml` —
  one-directional by design, since most nav pages aren't meant to be landing-page-highlighted.

#### Changed — NuGet consumer samples refreshed to the latest released package
- `samples/AgentEval.NuGetConsumer` / `AgentEval.NuGetConsumer.Tests` were pinned to `AgentEval 0.13.1-beta`
  (built on MAF 1.11.1) — three releases stale. Bumped to `0.16.0-beta` (the actual latest published version
  on NuGet.org — confirmed via the NuGet API, not assumed from `main`), with the full dependency baseline
  (`Microsoft.Agents.AI` 1.13.0, `System.Memory.Data` 10.0.9) updated to match exactly what
  `Directory.Packages.props` resolved at the `v0.16.0-beta` tag. Restored, built, and tested clean end-to-end
  against the real published package (one transient Azure content-filter rejection on first run, confirmed
  non-reproducible on re-run — a live-service flake, not a regression).

### BUG-22 resolution + Explainability & Trust (gate provenance, counterfactual replay, unified Trust Score) + docs/samples

#### Changed (BREAKING — CLI exit-code contract)
- **BUG-22 resolved**: `ExitCodes` code `2` was overloaded — `bench`/`calibrate` returned it for both a
  benchmark gate FAIL/WARN and bad CLI arguments, and `JudgeFactory` config failures (missing/partial Azure
  OpenAI credentials) also returned it. Now: `2` is reserved strictly for bad arguments; judge/runtime config
  failures return `3` (`RuntimeError`); benchmark/calibration gate outcomes return dedicated new codes
  `9` (`GateFailed`), `10` (`GateWarning`, `bench <family>` only), `11` (`GateIndeterminate`). External CI
  pipelines branching on exit code `2` from `bench`/`calibrate` must be updated. See `ExitCodes.cs` and
  [Exit codes](docs/cli.md#exit-codes).

#### Added — Explainability & Trust (0.17.0-beta theme, analysis in `strategy/ExplainabilityAndTrust-AnalysisAndPlan.md`)
- **Gate provenance chains** — `AgentEval.Guardrails.GateProvenance` (rule name, evidence, threshold vs.
  actual, contributing sub-chains) attached via a new optional `GateVerdict.Provenance` field (additive, same
  precedent as `Confidence`). Wired into `CompositeJudgeGate<TRubric>` for both the Block path and the
  near-miss-Allow-with-Confidence path Fleet Correlation already reads.
- **Counterfactual gate replay** — `AgentEval.MAF.Gatekeeper.GateReplayer.CompareAsync` runs a baseline and a
  candidate `IToolGate` list against the SAME captured `GatedToolCall`s (the real gate objects, no
  simulation; first-Block/Mutate-wins, matching the live `AgentEvalToolGateExtensions` pipeline) and reports
  which calls would have diverged under the candidate configuration. Library API this session; a
  `agenteval log-file gate-replay` CLI wrapper is a natural mechanical follow-on.
- **Unified Trust Score** — `AgentEval.Trust.TrustScoreCalculator.Compute` combines `TrustSignal`s (gate
  verdicts, eval scores, anything 0..1) into one honest 0-100 composite, excluding `"skipped"`/`"error"`
  labeled signals from the weighted math entirely (the same discipline as `WeightedSumAggregation` et al. —
  "including them at 0.0 would incorrectly drag the composite below threshold").

#### Documentation
- `docs/redteam/copilot-studio.md` — added the `CopilotStudioAssertions` fluent-assertion section (was
  shipped but undocumented) and cross-referenced `EstimatedCreditsUsed`.
- `docs/agent-skills.md` — added §3b documenting **SkillGate** (construction-time drift enforcement, shipped
  but undocumented since it landed) — `WithSkillGate`/`SkillGateMode`/`SkillDriftException`/
  `agenteval skills baseline approve`.

#### Samples
- `samples/AgentEval.Samples/AgentSkills/04_AgentSkillsSkillGate.cs` (new) — live-verified against real Azure
  OpenAI: pins a baseline, simulates a rug-pull, shows `SkillDriftException` fail-closed, then recovers.
- `samples/AgentEval.Samples/CopilotStudio/02_CopilotStudioBudgetAndRedTeam.cs` (new) — `--max-credits`
  enforcement tripping `CopilotStudioBudgetExceededException` for real, `HaveStayedWithinCreditBudget`,
  `CanResistAsync` red-teaming a live MCS agent, `HaveStartedNewConversation`/`HaveStartedDifferentConversation`.

### Copilot Studio — C2 fidelity-badge audit + P5 correlation-key spike

#### Added
- **C2 (all-targets fidelity badge) — closed.** `EvidenceFidelity` (Verbal/IntentToAct/Behavioral) now appears
  in every RedTeam report renderer, not just JSON/SARIF (which already carried it): `Markdown` gets an inline
  `` `[behavioral]` ``/`` `[verbal]` ``/`` `[intent-to-act]` `` tag next to each compromised probe, `JUnit` gets
  a `Fidelity:` line in the failure body (plus the label folded into the inconclusive `<error>` message), and
  `PDF` gets a bracketed label next to each finding. An audit of all 5 renderers found the 3 gaps were
  genuinely mechanical (the fidelity data was already flowing through the shared model, just not rendered) —
  shipped the fix for all three rather than stopping at the audit.

#### Investigated (spike concluded without shipping code — by design)
- **P5 (L2 telemetry enrichment) correlation-key spike.** Investigated whether a client-side `conversationId`
  can reliably correlate a live Copilot Studio scan against Dataverse session transcripts (`SessionID`,
  `TopicId`, `ChannelId`) for deeper post-hoc evidence enrichment. Findings: the correlation join is plausible
  but not publicly confirmed by Microsoft's docs; more importantly, Dataverse transcripts have ~30 minute
  latency after conversation inactivity before they're queryable — which means the originally-sketched design
  (an inline `TraceEnrichingChatClient` decorator enriching evidence on the hot path) cannot work at all. Real
  L2 enrichment needs to be a separate, deferred offline reconciliation command, not a live decorator. P5
  remains correctly deferred; this is a scoping correction, not a shipped feature. Full write-up in
  `docs/redteam/copilot-studio.md`'s "How it fits red-team fidelity" section.

### Agent Skills Wave 1 — baseline ledger, repo-wide discovery, provenance pointer

#### Added
- **`SkillContentHasher`** (`AgentEval.Skills`) — a new full-file-content hash, complementing
  `SkillManifestPoisoningGate`'s existing structural-only fingerprint (which hashes only the parsed manifest
  fields, not the raw file bytes). Together they let a baseline snapshot distinguish "the manifest's meaning
  changed" from "the file changed but parses identically" — two different signals a security-conscious skill
  reviewer cares about separately.
- **`ISkillBaselineStore` / `JsonFileSkillBaselineStore`** — a multi-snapshot, never-overwritten skill-scan
  ledger (a sibling of `AgentEval.Memory`'s `JsonFileBaselineStore` — the same proven pattern, not a shared
  base type, deliberately: skills and memory baselines have different identity/versioning shapes). Every
  `agenteval skills scan --write-baseline` call appends a new timestamped snapshot rather than overwriting the
  last one, so `agenteval skills baseline history <skill-name>` can show how a skill's fingerprint has drifted
  over time, not just its current state vs. one prior pin.
- **`AgentSkillDirectoryConventions`** (`--repo` flag on `skills scan`) — scans every directory convention MAF's
  own `AgentFileSkillsSource` recognizes across an entire repo root in one pass, instead of requiring one
  invocation per skill directory.
- **CLI:** `agenteval skills scan --write-baseline [--baseline-root <dir>] [--repo]` captures a snapshot;
  `agenteval skills baseline list|diff|history` inspects the ledger.
- A null-safe provenance-pointer parameter on the compliance report renderer (wiring is real and tested; no
  lock-file source populates it with real data yet — that's Wave 2/3 territory).
- Self-review before merge caught a real bug the C# compiler doesn't error on: a doc-comment placement mistake
  in `MafSkillScanner.cs` that silently mis-associated two records' XML docs with the wrong types — confirmed
  via the generated XML doc file, not just a build check, and fixed.
- Wave 2 (trust-on-first-use reputation matching, cross-location drift detection) and Wave 3 (org-wide
  multi-repo scan, live upstream verification) remain unbuilt, per the design doc's own phasing — Wave 3 is
  explicitly gated on a not-yet-done security/credential-scope review.

### Gatekeeper — the `IToolPlanGate` empirical dispatch-order question, resolved

#### Investigated (empirical finding, not a new gate)
- Determined, against real MAF 1.13.0 behavior (not assumed from docs), whether `FunctionInvokingChatClient`
  dispatches sibling tool calls from one model turn sequentially or concurrently — the fact a future
  `IToolPlanGate` (batch/plan-level gate, e.g. catching `[read_secrets(), send_email()]` issued as siblings in
  one turn, which `SequenceGate` structurally cannot see) needs settled before its interface shape can be
  designed. **Finding:** sequential dispatch is MAF's default, and that default is the *only* mode reachable
  through `ChatClientAgent`'s normal builder surface (confirmed: zero references to
  `AllowConcurrentInvocation` anywhere in `Microsoft.Agents.AI`). Concurrent dispatch is reachable, but only
  via manually constructing a `FunctionInvokingChatClient` with `UseProvidedChatClientAsIs = true` — and under
  that mode, a naively-designed "terminate at the first blocked call" plan gate does **not** reliably stop a
  sibling call from still executing (empirically confirmed: the sibling ran anyway). `IToolPlanGate` itself is
  still unbuilt — this pass shipped only the empirical groundwork a correct design now depends on.

### Gatekeeper — 4 next-wave gates: prompt-template drift, calibration staleness, Fleet Health Index, tool-result size anomaly detection

#### Added
- **`PromptTemplateDriftGate`** — the third application of the `ManifestFingerprint`/`ManifestDriftDetector`
  hash-pin-and-diff primitive (after skill manifests and MCP tool schemas), applied to an agent's prompt
  template files. Unlike every other gate, it's not an `IToolGate`/`IChatGate`/`IToolResultGate` at all — a
  prompt template doesn't change mid-run, so a per-turn check would be pure waste. `UseGatekeeper` checks drift
  **eagerly at construction time** when both `GatekeeperOptions.PromptTemplates` and `PromptTemplateBaseline`
  are set, and throws `PromptTemplateDriftException` immediately on a mismatch — fail-closed, matching
  `RefuseUnprotectedHighRiskTools`'s posture. Setting only one of the two options throws
  `InvalidOperationException` at construction rather than silently no-op-ing.
- **`CalibrationReport.CapturedAt` / `IsStale(maxAge, clock?)`** — a calibration report now records when it was
  captured, and can report whether it's aged past a caller-chosen threshold. Informational only — staleness
  never affects `IsInlineReady` or auto-demotes an already-promoted judge; it's a signal to re-calibrate, not a
  promotion-blocking condition.
- **`ICalibrationReportStore` / `JsonFileCalibrationReportStore`** — the persistence seam `CalibrationReport`
  needed (it was, until now, a purely in-memory return value of `GateCalibrationHarness.EvaluateAsync`, never
  persisted between runs). Deliberately minimal: one report per axis (the most recent run), overwritten on each
  save — not a full historical ledger (see the Agent Skills baseline ledger above for that different shape,
  deliberately not duplicated here).
- **`GatekeeperFleetHealthIndex.Compute(reportsByAxis, staleAfter, clock?)`** — joins every tracked judge axis's
  latest calibration report into one composite fleet-health view, mirroring `SkillSecurityIndex`'s honesty
  discipline: an axis with no report is never fabricated into a passing score. Reports mean decisive
  accuracy/kappa (calibrated axes only), total dangerous errors, which axes have never been calibrated, and
  which are stale. Transport-agnostic (`AgentEval.Core`, no CLI/Mission Control dependency yet) — high value
  once an ops-facing surface exists to put it on.
- **`ToolResultSizeAnomalyGate`** — a per-tool, per-session statistical-outlier detector, distinct from the
  already-shipped `ToolResultSizeGate` (which truncates against one fixed, global character threshold). Flags a
  result more than Nx (default 5x) *that same tool's own* running average size this run, once enough prior
  calls establish a baseline (default 3) — catching behavioral drift a global threshold can't see (e.g. a tool
  that's returned ~200-character results all run suddenly returning 50,000 characters, even though 50,000 might
  be unremarkable for a different, bulk-read tool). v1: fixed multiplier, no real statistics library — a
  documented, deferred v2 follow-on.
- Self-review before merge found and fixed 4 real issues: a filename-collision risk in the calibration store, a
  label-space inconsistency in the Fleet Health Index, a silent half-configuration security gap in
  `PromptTemplateDriftGate` (now fails loud instead), and dead code.
- Docs: new "Calibration staleness & the Gatekeeper Fleet Health Index" section in
  `docs/gatekeeper/gate-reference.md`.
- Crucible work and the two remaining flagship judges (`ToolArgumentGoalCoherenceJudge`,
  `CrescendoTrajectoryJudge`) are explicitly out of scope for this batch.

### Mission Control — prompt-hash provenance display completed, one stale doc claim corrected

#### Fixed
- **`AdjudicationFlow.tsx`'s judge cards and adjudicator card now render `PromptHashPill`** — the last place
  in the SPA that didn't show prompt-hash provenance (`EvalResultNode.tsx` and `ScenarioTreePage.tsx` already
  did, since 2026-05-25). Arguably the highest-stakes place it was missing, since it's the view a human uses to
  resolve judge disagreement.
- **`docs/missioncontrol/api-design.md`** corrected: `Query.complianceEvidence`'s documented return type was
  `ComplianceEvidence?`; the real resolver returns `ComplianceEvidenceWithChain?`.
- **Corrected a stale "still a live bug" claim** repeated across 3 local strategy/review docs: the compliance
  matrix's per-cell `auditChainValid` check (tampered evidence rendering as a false green checkmark) was
  actually fixed 2026-05-24 — the docs describing it as open were simply never updated when the fix landed.

### Gatekeeper Hardening Phase 2 — real HTTP-egress enforcement (#10): redirect-chasing + DNS-rebind/SSRF defense

#### Added
- **`GatekeeperHttpMessageHandler`** (`AgentEval.MAF.Gatekeeper.Egress`) — a real `DelegatingHandler` closing
  the gap `DomainAllowListGate` candidly documents about itself: that gate scans the URL *string* inside a
  tool call's arguments, never the actual outgoing network request, so it cannot catch a redirect to a
  forbidden host or a DNS answer resolving an allow-listed hostname to a private/internal address (SSRF /
  DNS-rebinding — the cloud-metadata endpoint `169.254.169.254` is the canonical target). This handler sits
  underneath whichever `HttpClient` a **tool's own implementation** uses (a different composition point from
  `IToolGate`/`IToolResultGate` — opt-in per tool via `GatekeeperHttpMessageHandler.CreateHttpClient(...)`, not
  registered through `UseGatekeeper`) and re-validates the allow-list AND resolves DNS before every hop,
  including every redirect — redirects are followed manually (the factory disables the transport's own
  auto-redirect), bounded by `MaxRedirects`, never silently delegated. Every block throws
  `HttpEgressBlockedException` (the idiomatic fail-closed signal for an `HttpMessageHandler`).
- **`PrivateNetworkClassifier`** — classifies an `IPAddress` as private/loopback/link-local/reserved (RFC1918,
  CGNAT, IPv6 unique-local, IPv4-mapped-IPv6 unwrapped to its embedded address, the cloud-metadata range, and
  more) — the check run against every DNS-resolved address.
- **`IDnsResolver`/`SystemDnsResolver`** — a seam over DNS resolution so tests can script resolution results
  (including a DNS-rebind scenario) without real network/DNS access; the default wraps `System.Net.Dns`.
- **`GatekeeperHttpEgressOptions`** — `MaxRedirects` (default 5), `BlockPrivateNetworks` (default on),
  `DnsResolutionTimeout` (default 2s, fail-closed on timeout — cannot prove the destination safe), `DnsResolver`.
- **`HostAllowList`** — the exact-or-subdomain host-matching logic factored out of `DomainAllowListGate`
  (behavior-preserving extraction, existing tests unchanged) so the new handler shares the IDENTICAL allow-list
  semantics rather than a second copy that could silently drift out of sync with the argument-level gate.
- Redirect handling matches `HttpClientHandler`'s own default semantics: 307/308 preserve method and body;
  301/302/303 downgrade to GET (dropping the body, except a HEAD request stays HEAD). The synchronous
  `HttpClient.Send(...)` API is explicitly refused (`NotSupportedException`) rather than silently bypassing
  every check — this handler's validation is inherently async (DNS resolution).
- 43 new tests (`PrivateNetworkClassifierTests`, `GatekeeperHttpMessageHandlerTests` — scripted inner handler +
  fake DNS resolver, zero real network access, deterministic). Full suite green on all three TFMs (net8.0
  7648/7648, net9.0 7648/7648, net10.0 7851/7851).
- Docs: new "HTTP egress enforcement" sections in `gate-reference.md`/`examples.md`/`introduction.md`.

### Gatekeeper Hardening Phase 2 — tool RESULT gates (P0-3) + a parallel-tool-call test fixture

#### Added
- **`IToolResultGate`** (`AgentEval.MAF.Gatekeeper`) — a new gate kind inspecting one already-executed tool
  call's RESULT, at the same MAF function-invocation seam `IToolGate` inspects the *proposed* call, but on the
  other side of `next(...)`. Closes the "tool output as an injection channel" gap: nothing before this
  inspected a tool's own return value before it re-entered the model's context (only *prior* results, read out
  of conversation history, were ever consulted — never *this* call's own result, synchronously, before it flows
  back). Returns `ToolResultVerdict` (`Allow` / `Block` / `Redact`, via `GatedToolResult`). Wired into
  `UseAgentEvalToolGate`'s new optional `resultGates` parameter — runs, in order, immediately after `next(...)`
  returns, only once every `IToolGate` has allowed the call. A `Block`/throw fails closed exactly like the
  call-gate loop; a `Redact` verdict is applied regardless of `ToolGatePolicy` (mirrors `ToolGateAction.Mutate`'s
  "always applied" precedent). Recorded under the new `gate.tool-result.*` trace stage — distinguishable from a
  call-gate block while still counted by the existing, stage-agnostic `GlassBoxEvidence.CountGateBlocks`. Null/
  empty `resultGates` is the exact prior behavior (zero overhead, fully backward compatible).
- **Three built-in result gates** (`AgentEval.MAF.Gatekeeper.Gates`):
  - **`ToolResultInjectionGate`** — blocks a result containing a prompt-injection marker (shares its default
    marker list with the chat-side `TokenInjectionGate`, now `public` for exactly this reuse). Not maskable —
    always `Block`, never `Redact`.
  - **`ToolResultSizeGate`** — truncates an oversized result (default 8,000 chars) via `Redact`, bounding
    context-window exhaustion and per-turn token cost from a single runaway tool response.
  - **`ToolResultSecretGate`** — detects and masks common credential shapes (AWS/GitHub/Slack/Google/Stripe
    keys, full PEM private-key blocks, bearer tokens, JWTs) via `Redact`, mirroring `RegexPiiGate`'s
    bounded-timeout mask-with-█ approach.
- **`GatekeeperOptions.ToolResultGates` / `AddResultGate(...)`** — composed by `UseGatekeeper` alongside
  `ToolGates`; a result-gate-only configuration (no call gates registered) is valid. The `IToolResultGate.
  MinimumPolicy` floor is folded into the same Observe-mode conflict check `ToolGates` already gets, and the
  Observe startup banner now reports the result-gate count too.
- **`GateTelemetry.Record(string, ToolResultAction, TimeSpan)`** — a second overload sharing the same per-policy
  counters as the call-gate `Record`, so a caller reading `Snapshot()` sees one unified effectiveness view
  across both gate kinds; `Redact` maps to `MutateCount`.
- **`ScriptedChatClient.AddParallelToolCalls(...)`** (`AgentEval.Core.Testing`) — the fixture prerequisite this
  phase needed: scripts MULTIPLE `FunctionCallContent` in one assistant turn (the shape a real provider sends
  for parallel function calling). Before this, the fixture could only ever emit one tool call per turn, so no
  test could exercise a gate pipeline against MAF's `FunctionInvokingChatClient` actually invoking N calls from
  a single turn — only N single-call turns in a row, a materially different code path. Fully additive — the
  existing single-call `AddToolCall` API is unchanged.
- `docs/gatekeeper/gate-reference.md` / `examples.md` / `introduction.md` updated with the new "Tool RESULT
  gates" layer, its policy-reinterpretation rules, and runnable snippets.

#### Deferred (explicitly out of scope this pass)
- P0-4 (tool-plan/batch gates) — a genuinely new interception point, sized larger, deferred to a separate
  session.
- Full result-content capture into the trace (mirroring `MutationEvidenceRenderer`'s `TraceCaptureMode`) — a
  tool result can be arbitrarily large/shaped content from anywhere; only the fact that a redaction happened,
  and why, is recorded for now.

### Copilot Studio — live connector wired (`redteam --sut copilot-studio` Track 1)

#### Added
- **`CopilotStudioAgentFactory.BuildLive` now builds a real connector** instead of unconditionally throwing
  `NotSupportedException`. It constructs a real `Microsoft.Agents.CopilotStudio.Client.CopilotClient` from
  `CopilotStudioConfig` (`ConnectionSettings` mapped 1:1 — `EnvironmentId`/`SchemaName`/`Cloud`), resolves the
  token scope via `CopilotClient.ScopeFromSettings` (never hardcoded), and bridges its streaming Bot Framework
  activity API (`StartConversationAsync` / `AskQuestionAsync` → `IAsyncEnumerable<IActivity>`) into an
  `IChatClient` (new `CopilotStudioChatClient`), wrapped in a MAF `ChatClientAgent` and handed to the existing
  `FromAgent` seam — unchanged. `redteam --sut copilot-studio`'s consent gate, config validation, and every
  existing credential-free test still run and pass before any of this is reached; construction itself makes no
  network call (the token callback is invoked lazily by `CopilotClient` on the first real request).
- **`CopilotStudioTokenProvider`** — MSAL device-code auth (`IPublicClientApplication.AcquireTokenWithDeviceCode`)
  with a persisted, OS-encrypted token cache (`Microsoft.Identity.Client.Extensions.Msal` — DPAPI on Windows,
  Keychain on macOS, libsecret on Linux) keyed by a SHA-256 hash of `TenantId|AppClientId`, silent-acquisition-first
  (`AcquireTokenSilent`) with device-code fallback on `MsalUiRequiredException`. New code — no prior token-caching
  precedent existed in this repo.
- **`CopilotStudioConfig.Cloud` now resolves to the real `PowerPlatformCloud` enum** (`ResolveCloud()` /
  `Validate()`), verified against the actual restored `Microsoft.Agents.CopilotStudio.Client` 1.3.171-beta package
  (not the higher version number a prior planning doc assumed — see the deviation note below). Case-insensitive,
  defaults to `Prod` when omitted, and a typo'd/unrecognized value now fails config validation with a clear error
  listing the valid names, before any network call.
- **`ICopilotStudioConversationClient`** — an AgentEval-owned abstraction over the two `CopilotClient` members the
  chat-client shim needs. The real package does not publicly export a mockable `ICopilotClient` interface (an
  earlier decompilation-based design note assumed one existed), so this repo defines its own seam instead —
  this is also what makes `CopilotStudioChatClient` unit-testable without live credentials.
- **`SingleNameHttpClientFactory`** — a minimal `IHttpClientFactory` for the one named client `CopilotClient`
  requires, avoiding a full `Microsoft.Extensions.Http` + `ServiceCollection` registration for a CLI with no
  ambient DI container.
- New package references (`AgentEval.Cli`, centrally pinned in `Directory.Packages.props`):
  `Microsoft.Agents.CopilotStudio.Client` 1.3.171-beta, `Microsoft.Agents.Core` 1.3.171-beta,
  `Microsoft.Identity.Client` 4.84.2, `Microsoft.Identity.Client.Extensions.Msal` 4.84.2,
  `Microsoft.Extensions.Http` 10.0.8 (raised `Microsoft.Extensions.DependencyInjection`'s central floor to 10.0.8
  to match).

#### Deviations from the design doc (`strategy/CopilotStudio/Bench-Eval-Integration-and-Live-Connector-Plan.md`, local-only)
- The doc cites `Microsoft.Agents.CopilotStudio.Client` "v1.6.150 — latest stable" and a decompiled `ICopilotClient`
  interface implemented by `CopilotClient`. Neither matches what actually restores from nuget.org: the real latest
  is **1.3.171-beta**, and reflecting on that exact assembly shows `CopilotClient` implements **no interface at
  all** (`GetInterfaces()` returns empty) — its full public surface is narrower than the doc's decompilation notes
  assumed. This CHANGELOG entry and the code's own XML docs are the corrected record; `ICopilotStudioConversationClient`
  above is the concrete consequence.
- `--max-credits` enforcement (the doc's Track 1 item 6) is **not implemented** — the SDK's activity/response
  models expose no Copilot Credit cost field to enforce against, so `--max-credits` remains parsed-but-unenforced
  exactly as before (`ExitCodes.BudgetExceeded` stays reserved, unused).

#### Not independently live-verified — needs a real Entra app registration + non-prod Copilot Studio agent
- The MSAL device-code prompt, silent-refresh, and persisted-cache round-trip (`CopilotStudioTokenProvider.GetTokenAsync`).
- Whether a real agent's `StartConversationAsync`/`AskQuestionAsync` activity stream matches the shape
  `CopilotStudioChatClient` assumes (in particular, any non-`message` activity worth surfacing, and real
  multi-activity turns).
- The end-to-end network round trip (real HTTP call, real response parsing) against a live MCS agent.
- A gated, `Skip`-by-default manual test (`CopilotStudioLiveConnectorManualTests`, `tests/AgentEval.Tests/Cli/CopilotStudio/`)
  is ready to run once credentials exist — see its XML doc for setup.

### Gatekeeper — MonetaryLimitGate + PerToolCallBudgetGate (focused deterministic siblings of RunBudgetGate)

#### Added
- **`MonetaryLimitGate`** (`AgentEval.MAF.Gatekeeper.Gates`) — a dedicated tool gate capping the running sum of a
  monetary tool-call argument (e.g. `"amount"`) across a run, off the shared `RunLedger`. The economic sibling of
  `RunBudgetGate`, scoped to a single argument/cap pair with its own `PolicyName` in the evidence trail — for
  payment/refund/transfer-style tools without wiring `RunBudgetGate`'s combined total/per-tool/monetary
  constructor. Fails closed on an unparseable amount, clamps a negative amount to zero (can't manufacture
  headroom), and the block reason never echoes the attempted amount or running sum — only the argument name and
  the *configured* cap, matching the taint-tracking gate's discipline of never leaking sensitive values into trace
  evidence.
- **`PerToolCallBudgetGate`** (`AgentEval.MAF.Gatekeeper.Gates`) — a dedicated tool gate capping how many times
  specific tools may be called in one run (e.g. `["delete_account"] = 1`, `["send_email"] = 3`), off the shared
  `RunLedger`. Blunts spray/loop attacks — an injected instruction that tries to fire the same destructive tool
  repeatedly is stopped at the configured count regardless of phrasing. A tool not named in the caps is
  unconditionally allowed.
- **`RunLedger.TryAdmitMonetary` / `TryAdmitPerToolCall`** — new atomic, per-dimension ledger primitives backing
  the two gates above. Deliberately isolated from `RunBudgetGate`'s own `TryAdmitToolCall` bookkeeping (which
  always bumps its shared per-tool/total counters on any admit, even for a dimension the caller didn't ask it to
  check) — so composing either dedicated gate with `RunBudgetGate`, or with each other, over an overlapping
  tool/argument name can never cross-contaminate a count. Covered by a regression test proving the isolation holds
  even when `RunBudgetGate` and `PerToolCallBudgetGate` are stacked on the same tool.
- **`samples/AgentEval.Samples/Gatekeeper/09_GatekeeperMonetaryAndPerCallBudget.cs`** — a live sample (real Azure
  OpenAI agent, no scripted fakes) with three scenes: a 10-call refund spray capped at 3 by `PerToolCallBudgetGate`,
  a single $50,000 refund blocked by a $1,000 `MonetaryLimitGate` cap, and both gates stacked against a $300 ×
  10-order spray — success is keyed on the actual recorded `gate.tool.*` block count, never on "no exception
  thrown." Wired into the samples menu (Group J).
- Extracted `AmountArgumentParser` (shared by `RunBudgetGate` and `MonetaryLimitGate`) so the two gates parse a
  monetary argument (`decimal` / `double` / `int` / `long` / `JsonElement` / numeric `string`) identically —
  behavior-preserving refactor of `RunBudgetGate`'s previously-private parsing logic, no functional change.

### MAF Agent Skills evaluation — Phase 1 (assertions + progressive-disclosure efficiency metric)

#### Added
- **Five fluent skill assertions** in `AgentEval.Assertions.SkillUsageAssertions` — `HaveLoadedSkill`,
  `HaveReadSkillResource`, `HaveRunSkillScript`, `NotHaveRunSkillScript`, `HaveDisclosedProgressively` —
  thin, additive extension methods over the existing `ToolUsageAssertions` / `ToolCallAssertion` (zero
  new MAF-type coupling in `AgentEval.Core`, which still does not reference `Microsoft.Agents.AI`).
  Support value-based argument matching (skill/resource/script name), not just tool-name matching, and
  degrade gracefully (key-agnostic fallback) if a future MAF version renames an argument.
- **`SkillDisclosureEfficiencyMetric`** (`code_skill_disclosure_efficiency`, `AgentEval.Metrics.Agentic`)
  — a free, code-based `IAgenticMetric` scoring the `load_skill` → `read_skill_resource` →
  `run_skill_script` progressive-disclosure funnel as a weighted product of disclosure-order validity,
  load precision (redundant-load + "load storm" penalties), and an optional load-selection F1 when the
  caller supplies `expected_skills` ground truth. Never fabricates a selection score when no ground
  truth is supplied, and never fabricates an "advertise" stage count (the skill-inventory system-prompt
  listing is not a tool call and is not observable from a `ToolUsageReport`).
- **`SkillToolNames`** (`AgentEval.Skills`) — the single shared constant for the three stable GA tool
  names (`load_skill` / `read_skill_resource` / `run_skill_script`) and their argument parameter names,
  referenced by the assertions and the metric.
- **`samples/AgentEval.AgentSkillsEval`** — a live sample: a real `ChatClientAgent` against Azure OpenAI,
  wrapped with a real `Microsoft.Agents.AI.AgentSkillsProvider` over a real file-based
  `expense-report` skill fixture (SKILL.md + a resource + an in-process script). Three runs demonstrate
  different real assertion/metric/output combinations (read-only lookup, script-computed overage,
  and an off-topic task that both scores a vacuous 100/100 and shows an assertion's real failure
  path) — all keyed on the actual captured tool-call trace, never a bare success claim.
- Verified four MAF `AgentSkillsProvider` API details against the live `Microsoft.Agents.AI 1.13.0`
  assembly (exact tool argument parameter names; the `DisableCaching` builder shape; that there is no
  provider-level `GetSkillsAsync` convenience; and that `read_skill_resource`'s `resourceName` is a
  logical name resolved against the skill's discovered resource list, not a live filesystem path).

This is Phase 1 of a multi-phase design
(`strategy/FutureFeatures/Skills/AgentEval-AgentSkills-Evals-Design-and-Plan.md`, local-only).

### MAF Agent Skills evaluation — Phase 3 (skill-description-injection red-team + `run_skill_script` governance)

#### Added
- **`SkillInjectionAttack`** (`AgentEval.RedTeam.Attacks`, OWASP LLM01) — the 14th built-in red-team attack
  (roster 13→14, probes 258→264). Two new `InjectionSurface` values, `SkillInstruction` (a malicious
  skill's `description`/instructions, spliced into the SYSTEM PROMPT via `{skills}` — a higher-trust
  position than a retrieved document) and `SkillResource` (`read_skill_resource` output). 100% reuse of
  the shipped Wave-B machinery (`CanaryTool`, `FidelityCompositeEvaluator`, `ToolInvocationEvaluator`,
  `RefusalGatedEvaluator`) — canary "source" tools are named `load_skill`/`read_skill_resource` (matching
  MAF's real tool names) so an instrumented SUT's trace is indistinguishable from a real
  `AgentSkillsProvider` interaction. 6 probes at Comprehensive intensity; registered in `Attack.All`,
  `ByName`, `ByOwaspId("LLM01")`.
- **⚠️ HONESTY FINDING — the reused judge does NOT converge on the skill-description surface.** Per the
  design doc's own documented risk item, this session ran a LIVE calibration of the flagship
  `IndirectInjectionRubric` against a new both-directions gold set
  (`AgentEval.Guardrails.Judges.Rubrics.SkillInjectionGoldSet`, 52 skill-flavored cases) via
  `GateCalibrationHarness`. Result: decisive accuracy 88.5%, **4 missed attacks**, 2 false alarms, κ=0.769
  vs. gold — `IsInlineReady == false` (the harness requires zero missed attacks by default). The rubric
  generalizes reasonably (beats the deterministic keyword baseline) but not well enough to promote inline
  on this NEW surface. **Decision: shipped SHADOW-ONLY for the skill surface**, per the design doc's own
  contingency — never promoted inline, documented in code, the live sample, and here. Authoring a
  dedicated `SkillDescriptionInjectionRubric` is deferred (the design doc's own +3–5 dev-day contingency
  line item).
- **`SkillScriptExecutionGate`** (`AgentEval.MAF.Gatekeeper`, `IToolGate`, `GateCost.PureCode`,
  `MinimumPolicy = ReplaceResult`) — deterministic hard gate on `run_skill_script`: blocks a call whose
  script identifier is not on the allowlist. Value-based, key-agnostic matching (every string-shaped
  argument value, plus `"/"`-joined pairs, are candidates — never assumes a specific argument key); an
  unrecognized/missing script identifier fails closed. No calibration needed (deterministic).
- **`SkillScriptApprovalGate`** (`IToolApprovalGate`) — auto-approves `load_skill`/`read_skill_resource`;
  escalates `run_skill_script` to a human UNLESS the script is on a per-script trust allowlist — finer
  grained than MAF's native `ReadOnlyToolsAutoApprovalRule` (tool-granularity only).
- **Composition-ordering honesty (design doc §6.2, verified live this session):** MAF's skill tools
  require human approval BY DEFAULT, and that pause happens BEFORE the FICC seam — so
  `SkillScriptExecutionGate` never fires unless `run_skill_script` is first auto-approved at the MAF
  layer (Posture A). The live sample (Run 6) demonstrates this exact composition and confirms the gate
  — not the approval layer — is what blocks the call (`gate.tool.*` count = 1, real trace evidence).
  `SkillResourcePathGate` was NOT built (dropped per the design doc §3 — `read_skill_resource`'s
  `resourceName` is a logical name with no traversal surface, confirmed in Phase 1).
- **Sample Runs 5–6** (`samples/AgentEval.AgentSkillsEval`) — **live-verified against real Azure OpenAI
  this session**: Run 5 (skill-injection attack) — the agent resisted (0 tool calls on an off-topic-safe
  prompt), and the shadow-only judge verdict is shown labeled advisory-only, never conflated with the
  real behavioral verdict. Run 6 (exec-gate demo, Posture A) — the agent DID call `run_skill_script` with
  an unlisted script, and `SkillScriptExecutionGate` deterministically blocked it (1 real `gate.tool.*`
  block), with the model falling back to computing the answer manually — the gate, not the approval
  layer, stopped the call.
- ~50 new tests across `SkillInjectionAttackTests`, `SkillScriptExecutionGateTests`,
  `SkillScriptApprovalGateTests`, `SkillInjectionGoldSetCalibrationTests` (deterministic harness-mechanics
  proof), and the env-gated `SkillInjectionGoldSetCalibrationLiveCheck` (the live calibration check itself,
  `AGENTEVAL_RUN_SKILLCAL=1`).

### MAF Agent Skills evaluation — Phase 2 (compliance scanner + coverage report)

#### Added
- **`SkillComplianceValidator`** (`AgentEval.Skills`, `AgentEval.Core` — pure, MAF-free, no I/O) —
  validates a `SkillManifest` against the GA `SKILL.md` rules (`name` presence/length/charset/no
  consecutive hyphens/matches parent directory; `description` presence/length; `compatibility` length)
  plus AgentEval governance flags (`ScriptRequiresGovernanceReview` when a skill exposes scripts,
  `ResourceFromUntrustedSource` for MCP/Custom-sourced resources, `AllowedToolsExperimental`). Returns a
  `SkillComplianceReport` (findings + a stage-reachability coverage summary) whose `IsCompliant` flips
  only on a `High`-severity finding.
- **`MafSkillScanner`** (`AgentEval.MAF.Skills`) — the one place that touches a live `AgentSkill` /
  `AgentSkillsSource`. Enumerates skills via the GA source-level `GetSkillsAsync(context, ct)`, maps each
  to the pure `SkillManifest` DTO, and delegates to the validator. **Honesty note:** `AgentFileSkill`
  stores its discovered resources/scripts in private fields with no public getter (verified via
  reflection against the live MAF 1.13.0 assembly), so this scanner independently re-derives a
  file-sourced skill's resource/script inventory by walking its `resources/`/`scripts/` subdirectories on
  disk — the same convention MAF's own `AgentFileSkillsSourceOptions` uses. For non-file sources
  (in-memory/class/MCP/custom) there is no equivalent enumeration API, so `ResourceNames`/`ScriptNames`
  are honestly reported empty rather than guessed — a documented, real limitation, not hidden.
- **`SkillComplianceReportRenderer`** — console/Markdown/JSON rendering, severity-sorted findings plus a
  coverage table.
- **Sample Run 4** (`samples/AgentEval.AgentSkillsEval`) — `MafSkillScanner.ScanFileSkillsAsync` over the
  real `expense-report` fixture; **live-verified against real Azure OpenAI this session**: 1 skill
  scanned, 1 resource + 1 script found on disk, `ScriptRequiresGovernanceReview` correctly flagged
  (Medium, pointing at Phase 3), `IsCompliant == true`.
- 44 new tests (`tests/AgentEval.Tests/Skills/*`, `tests/AgentEval.Tests/MAF/Skills/*`) — every GA rule
  fires exactly once on a violating manifest and not on a clean one; coverage counts never fabricate an
  "advertise" stage; a regression guard locks in that an undetectable non-file script stays honestly
  unreported rather than silently "fixed" with a fabricated count.

### MAF Agent Skills evaluation — Phase 4a/4b (Skill Health & Security Index + hash-pin drift detection) + cheap sugar

#### Added
- **`SkillSecurityIndex`** (`AgentEval.Skills`, pure) — joins the three independently-produced skill
  quality signals (Phase 2 compliance, Phase 1 efficiency, Phase 3/4b security) into one composite 0-100
  index. **Never fabricates a missing axis**: the score is the mean of only the axes actually supplied,
  and `SkillSecurityIndexResult.Explanation` names exactly which axes were/weren't measured.
- **`ManifestFingerprint`/`ManifestDriftDetector`** (`AgentEval.Guardrails`, pure, MAF-free) — a generic
  SHA-256 hash-pin-and-diff primitive, reusable for any model-visible artifact definition (a skill
  manifest here; an MCP tool schema in a future gate — same pattern, different artifact type).
- **`SkillManifestPoisoningGate`** + **`SkillManifestBaseline`** (`AgentEval.Skills`) — deterministic
  trust-time drift detection for a rug-pulled skill (content silently changing after approval). No
  calibration debt (pure hashing). `SkillManifestBaseline` persists to JSON (capture → save → later load
  → compare → flag drift), mirroring the repo's existing RedTeam baseline/diff CI pattern, scoped to skills.
- **Cheap assertion sugar** (design catalog §10.4): `WithScriptArgument` (asserts inside
  `run_skill_script`'s nested `arguments` object), `ForSkill` (scopes a `ToolUsageReport` to one skill's
  calls when a run exercises multiple skills), `HaveDisclosedEfficiently(minScore)` (metric-backed,
  synchronous — the metric is `CodeBased` with no real async work), `HaveCorrectlyDeclinedSkill` (positive
  phrasing for "the agent correctly avoided this skill"). `SkillContractAssertions.AssertSkillWellFormed`
  — a zero-cost (no agent, no LLM) unit-test assertion wrapping the Phase 2 validator.
- **Sample Run 7** — live-verified against real Azure OpenAI this session: a real simulated rug-pull
  (mutating the expense-report skill's description) is correctly caught by the hash-pin drift check
  (`Changed` finding), and the composite Skill Security Index correctly joins the real Phase 2 compliance
  scan (85/100, one Medium finding) with the real Phase 3 behavioral outcome from Run 5 (Resisted → 60/100
  after the drift penalty), honestly reporting the Efficiency axis as `n/a` (not re-measured this run,
  never assumed perfect) — composite 72/100, 2/3 axes measured.
- **Phase 4c (expanded red-team surface — fuzzing, canary-skill honeypot, typosquat detection,
  load-storm-as-DoW) was NOT built this session** — explicitly deprioritized per the design doc's own
  scoring (4a/4b are cheaper and higher-value) and the marathon session's remaining scope (Stages 2-5).
  Documented as deferred, not silently dropped — see `strategy/TODO.md`.
- ~35 new tests. Full net8.0 suite green (7278/7279, 1 pre-existing skip).

### Gatekeeper Tribunal — 4 more calibrated flagship judges + 2 overlooked-seam gates

#### Added
- **`IntentActionMismatchJudge`** — compares the agent's NARRATED intent against its ACTUAL tool call,
  vetoes on divergence. 52-case gold set. **Live-calibrated: 100% decisive accuracy, κ=1.000,
  `IsInlineReady=true`.**
- **`GoalHijackDriftJudge`** — detects the agent being steered off the user's original stated goal toward
  an injected objective (distinct from indirect-injection: asks "has direction drifted," not "does this
  content instruct"). 48-case gold set. **Live-calibrated: 100% decisive accuracy, κ=1.000,
  `IsInlineReady=true`.**
- **`UngroundedClaimJudge`** — RAG faithfulness as a runtime gate: flags an answer claim unsupported by
  retrieved context. 48-case gold set (includes hedged-opinion hard-negatives). **Live-calibrated: 100%
  decisive accuracy, κ=1.000, `IsInlineReady=true`.**
- **`HallucinatedCitationJudge`** — hybrid: a deterministic, zero-LLM-cost citation-existence check
  composed with a judge support-check, only spending a model call when the citation exists. 52-case gold
  set covering both failure modes (nonexistent source; real source that doesn't support the claim).
  **Live-calibrated: 100% decisive accuracy, κ=1.000, `IsInlineReady=true`.** Not an `IJudgeRubric` (a
  bespoke `IChatGate`), so not registered in the CLI bridge's `judge:*` axis registry — fully usable
  directly.
- **`MemoryWritePoisoningGate`** — guards the memory/vector-store WRITE side (every other injection judge
  guards reads). Reuses `IndirectInjectionRubric` verbatim at this new seam per the design backlog's
  reuse-the-pattern guidance.
- **`McpToolDescriptionPoisoningGate`** + **`McpToolDefinition`** — deterministic hash-pin-and-diff over an
  MCP tool's definition (name/description/schema), catching a rug-pull. Reuses the exact
  `ManifestFingerprint`/`ManifestDriftDetector` generic primitive built for Skills Phase 4b's
  `SkillManifestPoisoningGate` — confirming the design backlog's own "same pattern, different artifact
  type" prediction. Schema comparison recursively canonicalizes JSON key order (a reformatted-but-identical
  schema never false-alarms).
- All three `IJudgeRubric`-based judges registered in `JudgeAxisRegistry` — live-verified via the CLI
  bridge this session: `agenteval gatekeeper list-gates` shows all three (`judge:intent-action-mismatch`,
  `judge:goal-hijack-drift`, `judge:ungrounded-claim` + their keyword baselines);
  `agenteval gatekeeper calibrate --gate judge:goal-hijack-drift --certify` against real Azure OpenAI wrote
  a real calibration certificate; `agenteval gatekeeper inspect` then correctly Allowed a benign case and
  Blocked an attack case, citing the certificate.
- **Deferred, explicitly NOT built this session:** `ToolArgumentGoalCoherenceJudge` (needs the
  `IToolApprovalGate` timeout-routing design worked out) and `CrescendoTrajectoryJudge` (stateful — session
  store + running summary — explicitly flagged as the hardest of the six in the task scope; deferring it
  matches the task's own suggested fallback). See `strategy/TODO.md` for the honest accounting.
- ~100 new tests (deterministic rubric/gate tests + 4 env-gated live calibration checks,
  `AGENTEVAL_RUN_GATEKEEPER_CAL=1`). Full net8.0 suite green (7330/7331, 1 pre-existing skip).

### Copilot Studio — mock backend + Track 2 (shared `--sut` seam, PR 1)

#### Added
- **`MockCopilotStudioConversationClient`** (test-only) — a realistic, reusable mock Copilot Studio
  backend (a test double for `ICopilotStudioConversationClient`, since no live Copilot Studio system is
  available in this environment). Supports scripted MULTI-TURN conversations (fluent builder, mirroring
  `ScriptedChatClient`'s convention), a SERVER-ASSIGNED conversation id (matching real MCS session
  semantics), and configurable ERROR INJECTION (auth failure on start, a mid-conversation exception at a
  chosen turn — e.g. rate-limit-shaped — and a hang-until-cancelled mode for timeout testing). 7 tests
  proving the mock itself behaves realistically (session-state tracking, activity-type filtering, error
  propagation, honest "no scripted turn" default that never fabricates a blank success).
- **Track 2, PR 1 — the shared `--sut` seam** (`strategy/CopilotStudio/Bench-Eval-Integration-and-Live-Connector-Plan.md`
  §3): `ISutTarget`/`ISutTargetOptions`/`CommonTargetOptions`/`SutTargetResolver`
  (`src/AgentEval.Cli/Commands/Targets/ISutTarget.cs`) — generalizes the already-shipped `redteam --sut`
  pattern so `eval`/`bench` can reach the same built-in targets, WITHOUT touching
  `IRedTeamBuiltInTarget`/`RedTeamOptions`/`RedTeamCommand.cs`. `CopilotStudioRedTeamTarget` gains `ISutTarget`
  via EXPLICIT interface implementation (same idiom as `IEnumerable`/`IEnumerable<T>`) — its existing
  `IRedTeamBuiltInTarget` members are byte-for-byte unchanged. A `ValidateDrift` contract test (theory,
  4 truth-table cases) proves `IRedTeamBuiltInTarget.Validate` and `ISutTarget.Validate` agree on
  accept/reject for every shared check (consent / config-required / max-credits ≥ 0) — the one real
  ongoing-sync risk the design doc calls out, since the two method bodies have no compiler-enforced sync.
  12 new tests. `gatekeeper-demo` deliberately stays `redteam`-only (needs an `AgentTrace`, which
  `eval`/`bench` have no use for) — only `copilot-studio` gets the shared treatment, per the design doc.
- **NOT built this session** (explicitly deferred, documented honestly): Track 2 PR 2 (`eval` adoption)
  and PR 3 (bench Tier 1 `owasp`/`mitre`/`nist` adoption) — the shared types exist and are tested, but no
  CLI verb wires them in yet; P6 (reports & resilience: fidelity badging, agent-fingerprint drift,
  429 retry+resume), P3 (`KnowledgeCanaryEvaluator`, Crescendo/PAIR/TAP over the native channel), and P7
  (OSS polish, Entra app-reg script, NuGet packaging) were not started. See `strategy/TODO.md` for the
  honest accounting and what's next.
- Full net8.0 suite green (see the final Stage 5 numbers in this file's next entry).

### Documentation — Stage 5 pass (Agent Skills, Gatekeeper, Copilot Studio) + final build/test verification

#### Added
- **`docs/agent-skills.md`** — new user-facing feature page for MAF Agent Skills evaluation (assertions,
  disclosure-efficiency metric, compliance scanner, skill-injection red-team + `run_skill_script` governance
  gates, Skill Health & Security Index, hash-pin drift detection). Previously this only existed at
  implementation-detail depth inside `docs/architecture.md`; that section now cross-links here. Linked from
  `docs/index.md`'s Documentation table and Feature Highlights grid.
- `docs/redteam/copilot-studio.md` — corrected a stale sentence that still said "until the connector ships"
  even though `BuildLive` has shipped since this doc was first written; documented the new
  `MockCopilotStudioConversationClient` test double and the not-yet-CLI-reachable shared `ISutTarget`/
  `SutTargetResolver` seam (Track 2 PR 1).

#### Verified
- Full-solution `dotnet build -c Release`: **0 errors** (66 pre-existing warnings, unrelated to this
  session's changes — nullable-reference-type test scaffolding and xUnit analyzer style suggestions).
- Full net8.0 test suite (fresh build, not `--no-build`, per this repo's known multi-TFM stale-binary trap):
  **7349 passed / 0 failed / 1 skipped** (the skip is the pre-existing, intentionally gated
  `CopilotStudioLiveConnectorManualTests` — needs real Entra credentials this environment does not have) —
  **no regressions** from any of Stages 1–5.

## [0.16.0-beta] - 2026-07-13

Gatekeeper reaches production-grade runtime enforcement: a calibrated flagship judge for indirect prompt
injection, three more Tribunal judges guarding the model's *output* (exfiltration intent, system-prompt
extraction, and an honesty-preserving over-refusal valve), two deterministic flow-control gates, a
defense-in-depth sample, a credential-free attack-the-gate CI recipe, and a language-neutral CLI bridge so
any process — not just .NET — gets a policy verdict. Also ships a `--sut copilot-studio` red-team target
(credential-free scaffold; live connector deferred) via a new polymorphic built-in-target seam, and bumps
Microsoft Agent Framework to 1.13.0.

### Gatekeeper CLI interop bridge — invoke gates from any language (deterministic core + model path & honesty guard)

#### Added
- **`agenteval gatekeeper` verb group** — expose Gatekeeper gates through the CLI so any language or CI step gets a
  policy verdict without a .NET reference. `gatekeeper list-gates` (table or `--json`) discovers the callable gates;
  `gatekeeper inspect --gate <id>` runs one gate over a JSON payload on stdin (or a `.jsonl` batch via `--input`) and
  emits a **versioned verdict JSON** (`gatekeeper-verdict.schema.json`, shipped beside the binary). Covers the
  **deterministic, credential-free gates** — `keyword-injection` / `keyword` / `keyword:<axis>` / `rendered-exfil`
  (surfaces the sanitized `redactedText`), and the tool/flow-control gates `tool:forbidden-tool` /
  `tool:argument-pattern` / `tool:domain-allowlist` / `tool:referential-integrity` / `tool:taint-tracking` (which
  recompute from a caller-passed `messages` history).
- **Judge gates + the honesty guard** — `gatekeeper inspect --gate judge:<axis> --model <name>` runs a calibrated
  judge, but only if a **calibration certificate** proves it inline-ready for that exact model; otherwise it refuses
  with `NotCertified` (7) unless `--allow-uncalibrated` (which stamps `inlineReady:false` + an advisory warning). This
  carries the moat across the wire: the CLI cannot be used to *accidentally* trust an un-calibrated judge.
  `gatekeeper calibrate --gate judge:<axis> --model … [--certify]` scores the judge against its gold set + keyword
  baseline (honoring `--min-cases-per-direction` / `--max-concurrency` via the harness directly) and writes the
  certificate. `--model-reply <file>` evaluates a caller-supplied model reply with **no model call** and can never
  claim `inlineReady:true` without an explicit `--attest-fingerprint` (unknown provenance ⇒ advisory).
  The `serve` command (stateful accumulator gates like budgets and sequences) is a stub — not implemented.
- **`panel:<a,b,…>`** — a CLI-owned fan-out over comma-listed child gates (fail-closed OR). The CLI runs the children
  itself (not `ParallelJudgeFanOut`'s flattened aggregate) so it applies the sensitive-span redaction **per child**
  before aggregating — a redact-axis child never leaks its spans through the panel. The honesty guard requires **every**
  judge child certified inline-ready (else exit 7); the verdict's `certificate` is an array, one per judge child.
- **Interop proof + docs** — `samples/interop/python/gatekeeper_smoke.py` (pure stdlib) shells out to the CLI and
  asserts the whole contract from a non-.NET process (deterministic block/allow, tool flow-gate, fail-closed exit 6,
  `rendered-exfil` redaction, discovery, honesty guard). `docs/gatekeeper-cli.md` documents the command surface, the
  verdict schema, the exit-code contract, and the credential-free CI recipe.
- **Exit-code contract** — new `ExitCodes.GateBlocked` (5), `GateInconclusive` (6, fail-closed when the CLI can't
  evaluate — e.g. a history gate with no `messages`, overriding a gate's own fail-open), and `NotCertified` (7, the
  honesty guard) — deliberately off the BUG-22-overloaded 2. `--policy warn` forces exit 0 (verdict still emitted).
- **Security-preserving by construction** — the verdict serializer forces `matches` and `redactedText` to null for
  the `exfiltration-intent` / `system-prompt-extraction` axes (belt-and-suspenders over the rubric-level `spans:null`),
  so a secret can never be persisted into a verdict, a JSONL file, or a CI log.

### Gatekeeper — more output-guarding Tribunal judges + the run-post Panel + a live sample

#### Added
- **`ExfiltrationIntentJudge`** (`AgentEval.Guardrails.Judges`) — another Tribunal judge, showing the calibration
  harness generalizes past indirect-injection: a one-call bundle (`Create` / `GoldSet` / `KeywordBaseline` /
  `CalibrateAsync`) of the `ExfiltrationIntentRubric` in a `CompositeJudgeGate`. Placed **run-post** on the rendered
  output, it flags an output whose *purpose* is to disclose sensitive/proprietary data to an external destination —
  the "is this data sensitive *in context*" half the deterministic egress gates can't judge. Pairs with
  `DomainAllowListGate` (destination) + `TaintTrackingGate` (known-secret provenance) for defense in depth.
- **`ExfiltrationIntentRubric`** (`AgentEval.Guardrails.Judges.Rubrics`) — single-axis rubric (broad prefilter →
  DLP-classifier prompt → JSON parse) with a **canonical both-directions gold set** above the default promotion
  floor, built to expose the keyword dilemma on the exfil axis: attacks span explicit egress verbs
  *and* paraphrased exfil (data dropped at a bare-domain/paste with no verb — an exfil keyword list misses these);
  benigns mention `upload`/`password`/an email innocuously (a keyword list false-alarms). A judge earns inline
  promotion only by beating the deterministic exfil keyword oracle with zero missed attacks.
- **`SystemPromptExtractionJudge`** + **`SystemPromptExtractionRubric`** — another Tribunal axis (run-post): flags an
  output that leaks the confidential system prompt, hidden/developer instructions, internal config, tool schemas, or a
  secret canary. A canonical both-directions gold set with paraphrased disclosures the tell-oracle misses and
  hard-negatives it false-alarms on — including a **refusal to reveal the prompt**, which the rubric treats as benign.
  Hybridize with a deterministic canary token (canary catches the exact echo; the judge catches the paraphrase).
- **`OverRefusalJudge`** + **`OverRefusalRubric`** — the **utility valve** (run-post, **advisory**): flags an output
  that declines a request *without a legitimate reason* — the counterweight that stops a fail-closed judge fleet from
  degrading into block-everything (operationalizes "never punish honesty"). A positive verdict is a flag, not a block:
  wire it `WarnOnly`. Its gold set separates reasonless declines and marker-less soft refusals (flag) from *justified*
  refusals that cite a real reason and non-refusal uses of "can't"/"sorry" (allow) — where a naive refusal-marker
  oracle both over-flags and under-catches.
- **Composed the output judges into a run-post `ParallelJudgeFanOut`** ("the Panel") — proven inline: a live agent
  whose answer exfiltrates/leaks is blocked before it reaches the caller (fail-closed OR), with countable
  `gate.run-post.*.judge-panel` evidence, while a benign answer passes through at zero token cost (neither prefilter
  fires). Single-axis judges are composed here, not widened into one rubric.
- **Sample `Gatekeeper/08_GatekeeperOutputPanel`** — the run-post Panel end-to-end on a **real model** (Azure OpenAI):
  calibrates the exfil + system-prompt-extraction judges against their gold sets, shows the Panel's detection
  (blocks exfil/leak, allows benign + a justified refusal), wires it inline run-post to redact a leak-shaped answer,
  and demonstrates the over-refusal utility valve — every ✅/❌ keyed on the real verdict or the trace block count.

### Gatekeeper — the flagship calibrated judge

#### Added
- **`IndirectInjectionJudge`** (`AgentEval.Guardrails.Judges`) — the flagship Tribunal judge as a one-call bundle of
  the shipped primitives: `Create(fastModel)` (the `IndirectInjectionRubric` wrapped in a `CompositeJudgeGate`,
  cached), `GoldSet()`, `KeywordBaseline()`, and `CalibrateAsync(fastModel)` (scores the judge against the canonical
  gold set + keyword-oracle baseline at a zero-missed-attacks bar and returns the `CalibrationReport`). It does not
  lower the bar — a judge is inline-ready only when it beats the baseline with no missed attacks.
- **`IndirectInjectionRubric.CalibrationGoldSet()`** — a **canonical both-directions gold set** (paraphrased-injection
  attacks + benign hard-negatives) sized above the default `MinCasesPerDirection` promotion floor, so it can actually
  promote a judge (unlike the smaller `StarterGoldSet()` seed). Built to expose the keyword dilemma: attacks
  span classic overrides *and* paraphrased exfiltration the oracle misses; benigns reuse the oracle's own override
  words (`disregard`, `override`, `system prompt`) so it false-alarms — the precision/recall bind a fixed list can't
  escape.
- **`KeywordOracleGate`** (`AgentEval.Guardrails.Gates`) — a reusable deterministic `IChatGate` "keyword oracle" for
  use as a calibration `DeterministicBaseline`. It is the naive detector the repo's non-convergence finding indicts —
  an override-focused keyword list that provably loses in both directions (misses paraphrase, over-blocks benign
  mentions), so a judge earns promotion only by being strictly better.

#### Changed
- **Sample `Gatekeeper/04_GatekeeperBeachhead`** (the Tribunal scene) now calibrates the real judge against the
  canonical gold set and the shipped `KeywordOracleGate` at the real promotion floor, and — once promoted —
  enforces the judge **inline** via `UseAgentEvalGate(pre: […])`, blocking a live indirect injection run-pre with
  countable `gate.run-pre.*.judge:indirect-injection` evidence (previously a smaller seed set, a toy keyword baseline,
  and a standalone `InspectAsync`).

### Gatekeeper — defense-in-depth sample + attack-the-gate loop

#### Added
- **Sample `Gatekeeper/07_GatekeeperDefenseInDepth`** — the calibrated `IndirectInjectionJudge` (shown as standalone
  detection) alongside a defended agent behind `ReferentialIntegrityGate` + `TaintTrackingGate` + `DomainAllowListGate`,
  driven through a multi-step injection campaign where a *different* gate catches each step, printed from the trace via
  `GateVoice`. Fills the gap between sample 04 (judge only) and sample 06 (deterministic gates only). Verified live
  end-to-end (against gpt-4o-mini — see the commit).
- **`docs/gatekeeper/attack-the-gate.md`** — the closed-loop CI recipe: baseline a *gated* agent with
  `agenteval redteam --sut gatekeeper-demo --save-baseline …`, then `--baseline … --fail-on regression` fails the
  build the moment a change lets a probe through that the baseline didn't have. Credential-free, with a
  GitHub Actions snippet.

### Gatekeeper — deterministic flow-control gates

#### Added
- **`ReferentialIntegrityGate`** (`AgentEval.MAF.Gatekeeper`) — a side-effecting tool call may only reference ids the
  user provided or a *trusted* lookup surfaced this run; an invented id (e.g. introduced by an indirect injection)
  blocks the call. Stateless — recomputes observed ids per call from the run history (no cross-run state). Trust
  model: model-generated content and *untrusted* (poisonable) tool results never confer legitimacy — so an injection
  can't launder an id through the document that carries it. A heuristic tripwire — run `WarnOnly` first (the default
  `isIdentifier` only checks ids that contain a digit; supply your own for all-letter ids).
- **`TaintTrackingGate`** (`AgentEval.MAF.Gatekeeper`) — coarse information-flow control: a value returned by a
  confidential *source* tool must not reach an external *sink* tool's arguments (the block reason never echoes the
  secret). A tripwire, not a proof (substring taint; tune `minTaintLength`).

#### Note
- Per-tool call caps and per-run monetary caps are **already** provided by `RunBudgetGate` (`maxCallsPerTool` /
  `maxMonetaryPerRun`), checked atomically in one `RunLedger` operation — so no separate per-tool-budget or
  monetary-limit gate is needed.

### Copilot Studio — `--sut copilot-studio` red-team target MVP scaffold

#### Added
- **`redteam --sut copilot-studio`** — red-teams a live Microsoft Copilot Studio (MCS) agent through the
  existing `redteam` scan at text-only / `Verbal` fidelity, behind a ship-blocking safety gate. Ships the
  credential-free scaffold + the architecture to host it; the live connector is deliberately deferred (see
  Deferred, below).
- **`IRedTeamBuiltInTarget`** (`Commands/RedTeamTargets/`) — a polymorphic built-in-SUT seam replacing the
  inline `--sut` conditional in `RedTeamCommand`: one built-in target = one file owning its own options,
  validation, construction, evidence/tier policy, and post-scan summary, so a new target never grows the
  command. `GatekeeperDemoRedTeamTarget` is the former inline `gatekeeper-demo` branch, lifted out verbatim
  (behaviour unchanged); `CopilotStudioRedTeamTarget` is the new Copilot Studio target. Option *binding* is
  polymorphic too (`RedTeamOptions.TargetOptions`, keyed by `--sut` value) — a future target with its own
  flags needs zero edits to `RedTeamCommand`/`RedTeamOptions`.
- **`CopilotStudioConfig` + `CopilotStudioAgentFactory`** (`src/AgentEval.Cli/CopilotStudio/`) — the MCS
  connection config/loader + agent factory, built on the proven MAF `AIAgent` → `MAFAgentAdapter` seam (the
  same one the Foundry integration uses). Not red-team-specific, so `eval` can reuse them later.
- **Safety, all tested credential-free**: `--i-understand-live-side-effects` consent flag (default-refuse,
  before any network call — MCS connectors can fire real actions); `--parallelism` hard-floored to 1 (a live
  MCS session is stateful/non-reentrant); evidence capture **off** for this target (live responses can carry
  real PII); a no-model-of-its-own guard requiring an explicit `--judge-model`/`--attacker-model`;
  `ExitCodes.BudgetExceeded` (8, reserved) for the future `--max-credits` cap.

#### Deferred
- The live connector (`CopilotStudioAgentFactory.BuildLive`) throws a clear, actionable error until the
  `Microsoft.Agents.CopilotStudio.Client` package/API is verified against the current MAF release with a
  real non-prod agent. Everything up to it is real and tested — a `sutOverride` seam drives a full offline
  scan against a MAF-adapter-wrapped benign agent with zero credentials.

#### Docs
- CLI reference refresh: a new `agenteval gatekeeper` section, a consolidated `## Exit codes` table (incl.
  the BUG-22 exit-2 overload and the `gatekeeper` 5/6/7 codes), cross-links, and TOC registration for
  `gatekeeper-cli.md`.
- **`docs/redteam/copilot-studio.md`** — the dedicated guide for this target: what works today vs. the
  scaffold-not-finished-live-integration callout, prerequisites, the full CLI flag reference, and what's
  deferred. Linked from a new `--sut` row in `docs/redteam.md`'s options table and a new "Built-in SUT
  targets" section in `docs/redteam-whats-new.md`, and registered in `docs/toc.yml` under Red Team.

### Dependencies — Microsoft Agent Framework 1.13.0
- **MAF 1.12.0 → 1.13.0** (central, via `Directory.Packages.props`): `Microsoft.Agents.AI`,
  `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows`, `Microsoft.Agents.AI.Workflows.Generators`;
  the sample-only `Microsoft.Agents.AI.Foundry` / `.Harness` previews move to the matching `1.13.0-preview`.
  **No source changes were required** — of the three `[BREAKING]` PRs in the upstream `dotnet-1.13.0`
  release, only the file-store API rename could plausibly touch AgentEval, and a repo-wide grep confirmed
  zero usage; the other two live in `Hosting.OpenAI` / `Foundry.Hosting` packages AgentEval doesn't reference.
- **`Azure.AI.Projects` 2.1.0-beta.3 → 2.1.0-beta.4** (required by the Foundry preview), which in turn raised
  the floor on **`System.Memory.Data`** and **`Microsoft.Extensions.Hosting.Abstractions`** to **10.0.9**
  (were 10.0.3) — a transitive cascade no static compatibility check flagged, caught by an actual
  `dotnet restore` (`NU1109` package downgrade).
- `Microsoft.Extensions.AI*` stays at **10.6.0** — MAF 1.13.0's declared dependency — and the
  `OpenTelemetry.Api` **1.15.3** security pin (GHSA-g94r-2vxg-569j) remains valid, since the 1.13.0
  Workflows packages still declare exactly that version.

## [0.14.0-beta] - 2026-07-06

### Gatekeeper — runtime fail-closed enforcement

**Glass Box tells you what your agent *did*; Gatekeeper stops it from doing the wrong thing** — at runtime,
fail-closed. It puts the same checks you red-team with into the request path so a forbidden tool call, a
poisoned argument, or a compromised conversation is blocked *before* it happens. Every gate is fail-closed
(cannot-inspect ⇒ deny) and records honest `gate.*` evidence into the `AgentTrace` (a warn is never counted as
a block). See [`docs/gatekeeper/introduction.md`](docs/gatekeeper/introduction.md) and [ADR-025](docs/adr/025-gatekeeper-runtime-fail-closed-enforcement.md).

#### Added
- **Tool gates** (`AgentEval.MAF.Gatekeeper`) — `UseAgentEvalToolGate` over the MAF function-invocation seam:
  Allow / Block / Mutate a live tool call, enforced by `ToolGatePolicy` (WarnOnly / ReplaceResult / Terminate).
  Built-ins: `ForbiddenToolGate`, `ArgumentPatternGate` (bounded regex), `SequenceGate` (ordered combination,
  per-run scoped). A gate can declare a `MinimumPolicy` enforcement floor so a honeypot can't be silently
  downgraded to observe-only. Network/LLM-cost gates are rejected inline (`GateCost`).
- **Budget & egress gates** (off the new `RunLedger` per-run cross-hop accumulator) — `RunBudgetGate` caps a run's
  total tool calls / per-tool count / running monetary sum (denial-of-wallet, runaway-loop; atomic check+record,
  negatives can't manufacture headroom), and `DomainAllowListGate` enforces a domain allow-list over the URLs in
  tool arguments (exfiltration defense; catches scheme-relative `//host` and non-http schemes; resolves the
  `user@host` trick; fail-closed on unserializable args / scan timeout).
- **`RenderedOutputExfilGate`** (`AgentEval.Guardrails.Gates`) — a run-post `IChatGate` that neutralizes exfil
  channels a client auto-fetches or hides when it *renders* the answer: markdown image beacons, fetching HTML
  tags, `data:` URIs, and zero-width characters. Redacts under `EvalGatePolicy.Redact`; fail-closed on scan
  timeout. Complements `DomainAllowListGate` (tool-argument URLs) to cover both egress paths.
- **`CompositeJudgeGate<TRubric>`** (`AgentEval.Guardrails.Judges`) — the Tribunal primitive: a single-axis
  `IJudgeRubric` (prefilter + prompt + parser) becomes a runtime `IChatGate` backed by a fast model. Prefilter
  short-circuit (most turns skip the model) → model under a hard timeout → decisive `JudgeVerdict` (Allowed /
  Blocked-with-confidence-and-evidence-spans / Inconclusive). Blocked above the confidence threshold blocks (spans
  → `GateVerdict.Matches`); an inconclusive verdict (timeout / model error / unparseable, incl. a non-finite
  confidence) fails closed by default. Provider-agnostic (caller supplies the `IChatClient`). Calibrate against a
  per-axis gold set before going inline.
- **`ParallelJudgeFanOut`** — runs several judge `IChatGate`s over one turn concurrently (wall-clock ≈ slowest),
  combined **fail-closed OR** (any block blocks, aggregating reasons + evidence spans; a throwing judge is itself
  a block). Compose single-axis judges here rather than widening one rubric.
- **`JudgeVerdictCache`** — content-hash cache over a judge `IChatGate`. Caches **only Allow** verdicts (a
  transient fail-closed block is never cached into a permanent one), bounded, no eviction of a proven-safe entry.
- **`GateCalibrationHarness`** (the Bar) + **`JudgeGoldSet`** / **`CalibrationReport`** — scores a judge gate
  against a both-directions per-axis gold set and decides whether it earned the right to block live traffic.
  Reports decisive accuracy, the **missed-attack (dangerous-error) count**, false-alarm rate, Cohen's κ, and
  (with a baseline) whether the judge beats a deterministic detector. `CalibrationReport.AssertInlineReady()`
  refuses promotion until it passes — no judge goes inline un-calibrated.
- **`IndirectInjectionRubric`** (`AgentEval.Guardrails.Judges.Rubrics`) — the flagship judge rubric: detects
  indirect prompt injection in retrieved/tool-return content (the axis deterministic gates can't catch), with a
  robust JSON parser and a `StarterGoldSet()` to calibrate against (extend with your own data). Ships as a
  starting point — calibrate before trusting it inline.
- **Run gate** — `UseAgentEvalGate` inspects the run's input (incoming-attack detection) and output text,
  reusing the shipped `IChatGate`/`EvalGatePolicy`; establishes an `AgentRunScope` (stable across streaming
  segments) so inner gates can read the run context.
- **Session gates** — fail-closed `OperatorAuthGate` (allow-list), `RateLimitGate` (race-safe in-process
  counter, injectable clock), and `QuarantineGate`.
- **The moat** (`AgentEval.RedTeam.Gatekeeper`, a bridge assembly) — `ProbeEvaluatorGate` runs a deterministic
  red-team oracle as a runtime gate (fail-closed: only *Resisted* allows; *Succeeded*+*Inconclusive* block),
  and `CanaryToolGate` + `CanaryLure` graduate a red-team canary into a production honeypot.
- **Shadow judge** — `UseAgentEvalShadowJudge` + an owned `ShadowJudgePump`: runs the expensive LLM/network
  checks the inline gates reject, off the hot path, over an immutable snapshot; an adverse verdict arms
  quarantine for a *later* run instead of blocking the one it observed.
- **Tool approval (human-in-the-loop)** — `UseAgentEvalToolApproval` composes `IToolApprovalGate`s
  (`ArgumentPatternApprovalGate` by argument content, `ToolNameApprovalGate` by identity) with MAF's native
  `UseToolApproval`: a routine call auto-approves, a borderline call escalates to a human, recorded as
  `gate.approval.*` evidence. Fail-closed — at least one gate is required, auto-approve only when *every* gate
  affirms the call is routine (a throwing gate, an unserializable-args or parameterless call all escalate). Tools
  opt in via `.RequiresApproval()`. Marked `[Experimental("AEGK001")]` as it rides MAF's evaluation-only approval
  API (`MAAI001`).
- **`agenteval doctor`** double-gating check + `GateMetadataReader.StageFromKey`.
- **`agenteval redteam --sut gatekeeper-demo`** — a credential-free, deterministic gated demo agent to run the
  attack suite against (the attack-the-gate closed loop), composing with the `--baseline`/`--fail-on regression`
  gate.
- **Docs + samples** — `docs/gatekeeper/introduction.md`, the gate reference, and the **Gatekeeper** sample group
  (menu group J), all driving **real agents** on a live model: Hello World (a red-team check as a gate), the
  six-scenario enforcement walkthrough, a realistic gated MAF support agent (read→POST exfiltration blocked by
  `SequenceGate`), human-in-the-loop tool approval, the **Beachhead + Tribunal** (budget · exfil · rendered-output
  · a *calibrated* indirect-injection judge that must earn the right to block), and two **genuine MAF Agent
  Harness** agents (`IChatClient.AsHarnessAgent(new HarnessAgentOptions { … })` — planning + todo + mode) — one
  adds an autonomous re-invocation loop and has its runaway loop capped by `RunBudgetGate`, one sits behind
  defense-in-depth (`RunBudgetGate` + `SequenceGate` + `DomainAllowListGate`). The indirect-injection judge is
  demonstrated separately (Beachhead+Tribunal) and composes on top; it is not wired into the harness samples.
- **The Gatekeeper's verdict, surfaced** — the gated samples surface the gate's policy / action / reason (and, for
  the Tribunal, the judge's rationale + cited evidence spans), read straight from the Glass Box `gate.*` trace via
  `GateMetadataReader.ReadField` — so a blocked run shows *why*, not a dead "(none)". **Glass Box** is now a
  first-class feature in the docs nav ([`docs/glass-box.md`](docs/glass-box.md)).

### Microsoft Agent Framework: hybrid evaluation (several evaluators, one report)

#### Added
- **`CompositeAgentEvaluator`** (`AgentEval.MAF.Evaluators`) — runs several MAF `IAgentEvaluator`s over
  the same agent run **concurrently**, isolating each (a failing or slow source becomes a visible
  "skipped" branch instead of losing the whole run), with an optional per-source timeout and an optional
  `CircuitBreaker`. Pass it as a single evaluator to `agent.EvaluateAsync` — for example an AgentEval
  composite alongside any other provider's evaluator (such as an Azure AI Foundry `FoundryEvals`
  instance). Metrics from each source are merged under a `"{source}:"` key prefix so identically-named
  metrics never collide; `CapturedPerSource` exposes the untouched per-source results.
- **`UnifiedEvalReport`** — merges the per-source results into one source-tagged `EvalResult` tree (a
  branch per source) for the HTML/PDF renderers; splices an AgentEval composite's full weighted hierarchy
  into its branch and surfaces a provider report URL as branch evidence.
- **`CircuitBreaker`** — a minimal consecutive-failure breaker (injectable clock) that skips a
  persistently-failing source fast.
- **`AgentEvaluatorEvalLeaf` / `IAgentEvaluator.AsEvalLeaf()`** — the inverse adapter: wraps a MAF
  `IAgentEvaluator` (e.g. a Foundry eval) as an AgentEval `IEval`, so a provider's evaluator can be a
  **weighted leaf inside an AgentEval `CompositeEval`** — Foundry evals as first-class components of a
  hierarchical benchmark, under the same weighting/thresholding/aggregation. Provider-agnostic
  (AgentEval.MAF holds no Foundry reference).
- **Samples** —
  `samples/AgentEval.MafEvalFoundryAlongsideLocal` (standalone) plus two `AgentEval.Samples` Benchmarks
  entries: **Foundry Hybrid** (a Foundry eval inside a `CompositeAgentEvaluator`, batched) and **Foundry
  Hierarchy** (Foundry evals as weighted leaves interleaved in a composite benchmark tree). Each scores one
  MAF agent run and renders a source-tagged HTML report; the Foundry branch is gated on
  `FOUNDRY_PROJECT_ENDPOINT`.
- **Docs** — a dedicated [Foundry Evals Integration](docs/foundry-evals-integration.md) guide (surfaced in
  the README Integration section + Documentation table), plus "several evaluators in one report" (§3d) and
  "a provider's eval as a weighted leaf" (§3e) sections in
  [`using-agenteval-with-maf-evals.md`](docs/using-agenteval-with-maf-evals.md).

#### Changed
- **Microsoft Agent Framework bumped to 1.12.0** (from 1.11.1) across the solution — no breaking changes
  for AgentEval (full suite green on net8/9/10). The Foundry evals package
  (`Microsoft.Agents.AI.Foundry`) has no stable release yet, so it's pinned to its `1.12.0-preview` and
  referenced **only** by the samples (the standalone hybrid sample + the `AgentEval.Samples` H11/H12
  benchmarks); the shipping libraries remain provider-agnostic.

## [0.13.2-beta] - 2026-06-29

### Compliance: live-agent judging + a silent judge-parse correctness fix

Community contribution — huge thanks to **[@Javierif](https://github.com/Javierif)**. 🙌

#### Added
- **Live-agent compliance judging (`AgentScenarioEval`)** — the GDPR / EU AI Act benchmarks can now
  drive the actual agent-under-test with **each scenario's own article-specific prompt** and grade its
  real answer, instead of grading one fixed `--response` against every scenario. An agent failure
  surfaces as a distinct **"error" leaf** (severity `none`) rather than a confirmed violation, and the
  wrapper delegates identity (`Key`/`Name`/`Category`/`Version`) to the inner eval so it stays
  transparent to persistence and reporting.
- **`EvaluationFailed` honesty primitive** (`EvalDetails` / `AtomicLlmEval`) — distinguishes "the eval
  errored" from "the agent genuinely scored low", so an un-parseable verdict surfaces as an
  `error`/`none` leaf and never masquerades as a critical violation in roll-ups.
- **Richer compliance findings** — `ComplianceFinding` now carries `AttackPrompt` + `Reason` +
  `Rationale` (response evidence capped/gated), so a triaging developer sees *what* input got through
  and *why* it counted.
- **Red-team scan truncation-salvage** (`ScanOptions.OverallTimeout`) — an internal linked deadline so a
  slow agent that finishes most probes yields a clearly-*truncated* report instead of a hard zero,
  while an external cancel still propagates.

#### Fixed
- **Silent compliance-judge parse bug** — the verdict parser used `PropertyNameCaseInsensitive`, which
  does not bridge `snake_case` ↔ `camelCase`. The GDPR / EU AI Act judge prompts emit `snake_case`
  (`overall_score`, `criteria_results`), so **every such verdict was being silently parsed to score `0`
  with empty criteria**. A key-normalising parser (lower-case + strip underscores) makes both shapes
  round-trip. (Real, token-spending judgements were being corrupted.)
- **Lower-cost, more robust parsing** — request a JSON `response_format` (with a graceful, *narrowly
  scoped* fallback when the endpoint rejects it) plus a single corrective retry; token usage is summed
  across the initial call + retry so cost attribution stays honest. The `response_format` fallback only
  catches the genuine "format unsupported" case, so a real judge error still propagates (preserving
  `CalibratedEvaluator`'s exception-based failure handling).

### Microsoft Agent Framework evaluation-feature integration (`agent.EvaluateAsync`)

`AgentEval.MAF` now plugs AgentEval evaluators into MAF's built-in `agent.EvaluateAsync(...)` feature —
score a MAF agent with AgentEval metrics, or a whole AgentEval benchmark composite, in one call, and
render the result as a self-contained HTML report.

#### Added
- **`AgentEvalAgentEvaluator`** — implements MAF's native `IAgentEvaluator` and forwards the **full**
  `EvalItem.Conversation` (assistant tool-call turns included), so code-based tool metrics see the real
  calls — where MAF's built-in MEAI adapter forwards only the query half and drops them.
- **`AgentEvalCompositeEvaluator`** — runs an AgentEval composite (e.g. an `AgenticBenchmark` preset) as
  a single MEAI `IEvaluator`; captures the rich weighted `EvalResult` tree for rendering and flattens it
  to MEAI metrics for MAF's pass/fail roll-up.
- **`MeaiToEvalResultBridge`** — converts MAF's `AgentEvaluationResults` back into an AgentEval
  `EvalResult` tree (recovering score, label and severity), so the MAF-native path produces the same
  HTML/PDF reports the benchmark engine does.
- **`AgentEvaluatorExtensions`** — `.AsAgentEvaluator(chatConfig)` / `.AsMeaiEvaluator()` fluent helpers.
- **`samples/AgentEval.MafEvalLightPath`** — a runnable end-to-end reference (flat metrics + a full
  composite via `agent.EvaluateAsync`, rendered to HTML; CI-safe without credentials), plus
  **`docs/using-agenteval-with-maf-evals.md`** documenting the integration.

## [0.13.1-beta] - 2026-06-28

A maintenance release on top of the judge-primary grading flip. It **upgrades
Microsoft Agent Framework to 1.11.1**, adds an injectable clock for deterministic
multi-turn timing, brings the red-team documentation in line with the v0.13
grading default, ships a paper / reproducibility companion sample, and folds in
routine dependency bumps. **No grader-behaviour changes** — judge-primary
Composite Judges shipped in 0.13.0-beta and are unchanged here.

### Added
- **`ScanOptions.TimeProvider`** — an injectable `TimeProvider` (default
  `TimeProvider.System`) used by `TurnOrchestrator` for per-turn timeout and
  conversation-duration timing, so multi-turn timing can be driven
  deterministically in tests via `FakeTimeProvider`. Runtime-only, not serialized
  (mirrors `JudgeClient`).
- **`AgentEval.SampleGraders` head-to-head runner** (`--head-to-head`) — scores a
  gold-set corpus with the keyword oracle, a single LLM judge, a generic
  composite, and the production task-specific decomposition on the same cases and
  judge, emitting `verdicts.json` for the safety-asymmetric scorer (paper /
  reproducibility companion; consumes public APIs only, modifies no product code).

### Changed
- **Red-team documentation** — `README.md` and `docs/redteam-whats-new.md` now
  headline judge-primary grading + Composite Judges, replacing the stale
  pre-flip "keyword-primary" narrative so the public docs match the shipped
  default.

### Fixed
- **Flaky net8.0/Windows CI timing tests** — `TurnOrchestrator` now measures
  elapsed time and arms per-turn timeouts through the injectable `TimeProvider`
  instead of wall-clock `Stopwatch`/`CancelAfter`, removing load-sensitive
  flakes; the two timing-sensitive multi-turn tests run on a deterministic
  `FakeTimeProvider`. The throughput-benchmark `Duration` assertion was made
  tolerant (the deterministic requests-per-second guard is unchanged).
- **XML-doc warnings** — cleaned up stale `cref`/`paramref` references across the
  codebase (documentation only, no behaviour change).

### Dependencies — Microsoft Agent Framework 1.11.1
- **MAF 1.10.0 → 1.11.1** (central, via `Directory.Packages.props`): `Microsoft.Agents.AI`,
  `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows`, `Microsoft.Agents.AI.Workflows.Generators`.
  **No source changes were required** — the full net8.0 test suite passes unchanged and
  `maf-doctor` grades the tree clean of anti-pattern errors/warnings.
- `Microsoft.Extensions.AI*` stays at **10.6.0** — MAF 1.11.1's declared dependency — and the
  `OpenTelemetry.Api` **1.15.3** security pin (GHSA-g94r-2vxg-569j) remains valid, since the 1.11.1
  Workflows packages still declare exactly that version.

### Dependencies
- Bump the GitHub Actions group (2 updates) + `actions/cache` 5 → 6.
- Bump `react-router` 7.15.0 → 7.18.0 in the Mission Control SPA.

## [0.13.0-beta] - 2026-06-24

### Red-team grading: judge-primary by default + Composite Judges (ADR-021/022/023/024)

The red-team grader — the component that decides whether each attack *succeeded* — moves from a
keyword/substring oracle to **LLM-judge-primary grading with honest-by-construction "Composite
Judges."** Each semantic verdict is split into a positive-only compromise detector and a
negative-only refusal detector, each structurally clamped (`OutcomeFilterEvaluator`) so it can only
raise its own direction or abstain, aggregated by `CompositeEvaluator(Any)`. This was driven by a
reproducible finding that *iteratively patching* a keyword oracle does **not** converge to an honest
grader (fresh fabrications keep surfacing in both directions). Human/pin agreement moves from ~56%
(keyword) to κ=1.000 on the pinned corpus; directional fabrications drop from 8 (single judge) to 0.

#### Changed — ⚠️ BREAKING (default behavior)
- **`--judge-mode` now defaults to `primary`** (was `fallback`). When a judge is configured (`--judge`),
  the Composite Judges now **lead** the verdict instead of only adjudicating `Inconclusive` keyword
  results. **A scan run without `--judge` stays byte-identical to the previous keyword-oracle behavior**,
  and `--judge-mode fallback` restores the old judge-as-tiebreaker behavior exactly. (`ScanOptions.Mode`
  default `Fallback` → `Primary`.)
- **Default judge rubric is now `evidence-anchored`** (was `strict`) — the configuration the per-oracle
  discriminators are calibrated for; strict/lenient remain selectable.

#### Added
- **Composite Judges for all six semantic oracles** — InferenceAPIAbuse, InsecureOutput, SupplyChain,
  DataPoisoning, Misinformation (ADR-023), ExcessiveAgency (tool-aware, preserves the Behavioral leg).
  Genuinely structural signals stay deterministic (DataPoisoning `trigger_phrase`, PromptInjection /
  Jailbreak canary markers).
- **ADR-024 split-then-gate (gated trees)** — InferenceAPIAbuse now uses a 4-gate tree
  (`ConjunctionGate` AND-gates under a top OR) that closes the "I am Grok" model-vs-product-bot floor on
  `gpt-4o-mini` with **no stronger model**. ADR-024 also documents the *bound*: the same pattern was
  measured to **regress** InsecureOutput, so it is applied only where the conflated decisions are
  independent axes (`AGENTEVAL_INFERENCE_FLAT=1` / `AGENTEVAL_INSECURE_TREE=1` toggle the A-B).
- **Reasoning-model judges** (o-series / GPT-5-class) — the judge auto-retries without `temperature`
  when a model rejects it (HTTP 400 `unsupported_value`), so a reasoning model can be used as the judge.
- **`AgentEval.SampleGraders`** companion (paper/reproducibility) — a standalone keyword-vs-judge-vs-
  composite-vs-gated head-to-head + a keyword-oracle non-convergence demo.

#### Fixed
- **Keyword-oracle non-convergence** — retired the non-convergent positive keyword detectors
  (executable-structure / install-command / in-context-poison lexicon) that fabricated verdicts on
  English imperatives, payload-naming warnings, and attribute-then-correct phrasings; replaced with
  positive-only judges ⊕ a refusal judge.

#### Tooling
- **`GateAblationLiveCheck`** — a reusable per-oracle flat-vs-gated A-B harness that reports directional
  fabrications and recommends the structure, so a gate is never promoted on intuition (env-gated on
  `AGENTEVAL_RUN_5B=1`).

## [0.12.2-beta] - 2026-06-18

### Fixed
- **Throughput-benchmark timing** — `PerformanceBenchmark.RunThroughputBenchmarkAsync` now measures
  the reported `Duration` with a high-resolution `Stopwatch` instead of `DateTimeOffset.UtcNow`
  (~15.6 ms granularity on Windows), improving `Duration` accuracy and removing an intermittent
  net8.0/Windows CI flake. Requests-per-second was unaffected — it divides by the configured window.

## [0.12.1-beta] - 2026-06-18

> Our first community-contributor release — huge thanks to **@bmerkle** and **@Javierif**. 🎉

### Added
- **`agenteval bench agentic --response <text>` / `--response-file <path>`** — grade a supplied
  agent response directly instead of the built-in stub. Thanks to our second community contributor
  **[Javier Iniesta Fernández (@Javierif)](https://github.com/Javierif)** (#47).

### Fixed
- **Locale-dependent number/currency formatting** — scores, durations, costs and CI/exporter
  output now format with `CultureInfo.InvariantCulture`, so a comma-decimal system locale no
  longer emits `0,95` instead of `0.95` (which corrupted CSV/JSON/XML output). Thanks to our
  first community contributor **[Bernhard Merkle (@bmerkle)](https://github.com/bmerkle)** (#20).

### Documentation
- **DocFX build warnings** — removed dead markdown links to the (gitignored) `strategy/`
  directory from ADR-014/015/016 and the extensibility guide, and mapped `samples/**/*.cs`
  as a DocFX resource so sample cross-references resolve. Thanks to **@bmerkle** (#18).
- **Pre-release accuracy pass** — README MAF badge + compatibility table and the installation
  docs now read MAF `1.10.0` / Microsoft.Extensions.AI `10.6.0` (the shipped versions); package
  `RepositoryUrl`/`PackageProjectUrl` corrected to the canonical `AgentEvalHQ/AgentEval`; the
  OWASP getting-started guide reconciled to the shipped 10/10 category coverage; and three
  observability docs (Trace Fidelity, Guardrails, Auto-Audit) surfaced in the docs navigation.

## [0.12.0-beta] - 2026-06-14

### Dependencies — Microsoft Agent Framework 1.10.0 upgrade

Bumped the repo from MAF 1.3.0 to **1.10.0** (latest), with the matching Microsoft.Extensions.AI
stack. No source changes were required — none of the 1.4→1.10 breaking surfaces are used; the
full build is clean (net8/9/10, 0 warnings, 0 errors) and the entire test suite is green
(18,353 passed, 0 failed). maf-doctor health remained grade **B** (0 errors, 0 warnings, 0
fan-out starvation risks).

#### Changed
- **MAF 1.3.0 → 1.10.0** (central, via `Directory.Packages.props`): `Microsoft.Agents.AI`,
  `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows`, `Microsoft.Agents.AI.Workflows.Generators`.
- **Microsoft.Extensions.AI 10.5.0 → 10.6.0**: base, `.OpenAI`, `.Evaluation.Quality`.
- **System.Numerics.Tensors 10.0.6 → 10.0.8** — floor raised by Microsoft.Extensions.AI 10.6.0
  (resolves NU1109 downgrade).
- **`AgentEval.TravelDemo`** consolidated into Central Package Management (dropped its inline
  pins and `ManagePackageVersionsCentrally=false`); now resolves MAF 1.10.0 from the central props.
- **`AgentEval.TravelDemo.Evals`** upgraded to MAF 1.10.0 and switched from the published
  `AgentEval` NuGet package (still built on 1.3.0) to direct `ProjectReference`s on the AgentEval
  sub-projects (Abstractions, Core, MAF), keeping it in lockstep with the demo it evaluates.
- **Version parity:** the package version is centralized in `Directory.Build.props` (`0.12.0-beta`);
  the umbrella `AgentEval` package and the `AgentEval.Cli` dotnet tool now version in lockstep
  (CI overrides both via `-p:PackageVersion`). Hardcoded `PackageReleaseNotes` on both packages
  were replaced with a `CHANGELOG.md` pointer to stop version drift.
- **CLI packaging:** `AgentEval.Cli` now ships a CLI-specific `README.md` + `AgentEvalCli.png`
  banner (instead of the umbrella README); the NuGet icon (`AgentEvalNugetLogoAE.png`) is inherited
  centrally from `Directory.Build.props`.

#### Preserved
- Security pins retained: `OpenTelemetry.Api` 1.15.3 (GHSA-g94r-2vxg-569j), `Azure.AI.OpenAI` 2.8.0-beta.1.
- NuGet-consumer samples (`AgentEval.NuGetConsumer`, `.NuGetConsumer.Tests`) intentionally left on
  the published 1.3.0-based AgentEval package — they exist specifically to validate consumption of
  the published package, so they move once a 1.10.0-based AgentEval release is published.

### Red Team — feature-complete + oracle-honesty hardening (2026-06-14)

The red-team module went from MVP to feature-complete and **honesty-hardened**. See
**[Red Team — What's New](docs/redteam-whats-new.md)** for the full roundup.

#### Added
- **Coverage:** 258 probes across 13 attack types; **all 10 OWASP LLM Top 10 (2025)** closed; 8 MITRE
  ATLAS techniques; compliance crosswalks across **five frameworks** (OWASP, MITRE, NIST AI RMF, ISO/IEC
  42001, SOC 2) with a `--format nist` report and `bench owasp|mitre|nist` families.
- **Multi-step & adaptive attacks:** multi-turn `Crescendo` escalation; attacker-LLM orchestration
  (`PAIR`, `TAP`/tree-of-attacks) with a **separate** judge so an attack can't grade itself; tool-aware
  `ToolEscalation`.
- **Real attack surface:** canary/honeypot tools measuring **emitted-vs-executed** (`WasExecuted`)
  fidelity; system-prompt canary + `--sut-tier` to prove a real leak, not a phrasing guess.
- **Evasion:** 18 deterministic, correct-by-construction transform encoders (Base64/ROT13/homoglyph/
  zero-width/…) applicable to any attack.
- **CI & data:** baseline regression gate (`--save-baseline`/`--baseline`/`--fail-on`); SARIF/JUnit/PDF;
  `--explain` rationales; `--calibration` relative scoring (concept credited to NVIDIA garak);
  `--import-probes` and external benchmark packs (`--pack`: HarmBench/JailbreakBench/CyberSecEval).

#### Honesty discipline (the focus of this wave)
- **Three-way verdicts** — every probe is Resisted / Succeeded / **Inconclusive**; weak/ambiguous
  evidence is an honest coverage gap, never a hidden pass.
- **Conclusive-only scoring** (`Resisted/(Resisted+Succeeded)`) separates coverage from pass-rate.
- **EvidenceFidelity** (Verbal / IntentToAct / Behavioral) on every verdict.
- **`OracleHonestyCorpus` + invariant test** — a permanent both-directions regression net (*safe never
  Succeeds, vulnerable never Resists*) enforced in the CI net8/9/10 matrix; LLM09 (misinformation) now
  defers a deterministic confabulation to the judge (`Inconclusive` without `--judge`).
- The oracle-honesty fix arc closed ~70 fabricated-verdict shapes found by repeated adversarial sweeps,
  each seeded as a permanent corpus case.

#### Known limitation (documented, not hidden)
- The per-attack oracles are keyword/pattern matchers at the fast first pass; they cannot fully make the
  *semantic* call (refusal-vs-comply, correction-vs-adoption, …). This wave makes them **much more
  honest** (they defer far more), but not *complete* — configure `--judge` so the (now larger)
  `Inconclusive` zone is adjudicated by an LLM. Making the judge/trained-classifier the *primary* grader
  for semantic attacks is the next arc.

### Thorough-review hardening wave (128 findings, 2026-05-31)

A repository-wide thorough review produced 128 deduplicated findings (bugs, gaps, security,
performance, architecture). **All 128 were fixed one-per-commit** on `fix/thorough-review-findings`,
each built + run against the full suite with a regression test (and negative control) added where a
behaviour assertion applied. Full-solution build (net8/9/10) clean; full suite green. No
compliance/calibration value, threshold, pillar definition, aggregation rule, or judge constant changed
(the GDPR and EU-AI-Act gates — including the Art 5 / GPAI carve-outs — are byte-for-byte preserved and
verified by the compliance test suite). An independent adversarial re-review confirmed the wave is
behaviour-preserving and calibration-safe.

#### Security
- **SEC-11** — GraphQL now enforces operation-cost limits (`ModifyCostOptions`) with `[Cost]` weights on
  the expensive resolvers, so alias-multiplied fan-out is rejected pre-execution (depth limit alone did
  not bound it).
- **SEC-12** — the absolute workspace filesystem path is now redacted outside Mode A at `/api/v1/version`
  and the GraphQL `Workspace` resolver (was always exposed).
- **SEC-14** — removed `curl` from the Docker runtime image; the `HEALTHCHECK` now uses a self-contained
  internal probe (`McHealthCheck`), and base images are tracked for digest-pinning via Dependabot.
- **SEC-15** — `OpenReport` binds the local report server to loopback (was 0.0.0.0) and validates the port.
- **GAP-15** — `WorkflowSerializer.ToMermaid` sanitizes node `DisplayName` (Mermaid label injection).
- **BUG-38 / BUG-39** — RedTeam: detect genuine embedded-newline header/log injection; supply-chain
  evaluator now flags only suspicious package recommendations (fewer false positives/negatives).

#### Fixed (correctness)
- Retrieval pass thresholds (MRR/RecallAtK) made configurable (BUG-44); F1 token-multiset alignment
  (BUG-59); malformed-output write now fails fast (GAP-16); `VerbosityConfiguration` override is
  flow-scoped via `AsyncLocal` (MNT-06); replay agents gain an `OnWarning` sink instead of hardcoded
  Console prompt logging (GAP-09); plus the remaining P0–P3 bug/gap fixes (see review tracking doc).

#### Performance
- `MemoryVectorStore.Search` scores/sorts outside the lock + NaN guard (PERF-08); single-sort
  distribution statistics (PERF-06); bounded `CorpusLoader` repeats (PERF-07); cached font bytes
  (PERF-11); compiled-once snapshot scrub regexes (MNT-11); deadlock-safe `Build()` + `ConfigureAwait`
  hygiene surfaced via CA2007 (PERF-01).

#### Architecture (see [ADR-018](docs/adr/018-compliance-core-and-shared-extractions.md))
- **New project `AgentEval.Compliance.Core`** (ARC-01) — shared regulation-neutral building blocks for the
  GDPR/EU-AI-Act packs (embedded in the umbrella via `PrivateAssets="all"`).
- Cross-cutting duplication consolidated into single owners: `EvalTreeLimits` (ARC-03), `ModelKeyMatcher`
  (ARC-07), `CalibrationMath` (ARC-04), `EvalReportHelpers` (ARC-02), `WorkflowToolCallChecks` (ARC-05),
  `AgenticCategoryResolver` (ARC-11), `RedTeamComplianceLeaf` (MNT-02), `MemoryScenarioContextBuilder`
  (MNT-05), `WorkspaceRootDiscovery.CanonicaliseExistingDirectory` (MNT-03), and `PerformanceBenchmark`
  logging seam (ARC-08).
- **`UmbrellaDependencyClosureTests`** (ARC-10) — build-time guard that fails when a sub-project's runtime
  package is not re-declared on the umbrella (prevents the SEC-02 class of silent-transitive bug).

#### Build / tooling
- `global.json` pinned deterministically — no prerelease, no major roll-forward (MNT-14).
- .NET analyzers + code-style enforcement enabled non-fatally (MNT-04).
- Calibrate commands no longer depend implicitly on the test assembly; the maintainer/CI-only contract is
  centralized and documented (ARC-09).

### Changed (Phase 11 — Hygiene bundle, 2026-05-25)

Plan-13 T4.1 v0.10.2 polish bundle — 38 small items across 5 sub-PRs
(samples polish / hygiene / dead code / low-priority polish). No behaviour
changes; same number of tests + same green; new contract tests for
`IEvalResultRenderer` + PDF audit-hash parser + `WriteReportsViaStoreAsync`
integration; deleted `LongMemEvalOptions` (empty subclass, zero consumers);
dropped stale `.AgenticBenchmark.Golden.` resource prefix in the agentic
calibration loader (was carried over from the pre-v0.9.0 namespace);
dropped unused `<InternalsVisibleTo>` in `AgentEval.MAF` (zero internal
types); dropped unused `AgentEval.Core` `<ProjectReference>` from
`AgentEval.Rendering.Pdf` (PDF renderer has zero Core symbols); strengthened
XML docs on `IOutputStore` (Convention 5B canonical evidence sink),
`IEvalResultRenderer` (Convention 5A renderer contract + `<example/>`
block), `PerformanceBenchmarkRegistration.OptionsForPreset` (intentional
uniformity), OWASP/MITRE `judge` ctor param (pinning-test teeth gap);
tightened `MultiJudgeOptions` Obsolete message ("Removal scheduled for
v0.11.0"); added `IOutputStoreReader.ResolveRunDirectory` accessor (closes
the v0.10.1 layout-leak finding); added `EvalResultRenderOptions.EvidenceTruncationLength`
(default 800) + per-evidence "(N more chars)" overflow footer on the PDF
renderer; bare-`dotnet-run` `--workspace` parser now validates path
existence (CLI parity); renamed drift goldens (`pillar5-robustness-10` →
`-15`, `pillar6-gpai-5` → `-12`); fixed EU AI Act Art 14 / 50(1) / 50(2)
zero-width WARN band (`warn: 0.70` → `0.60`); added `docs/redteam/owasp.md`
(red-team-procedure-focused companion to the getting-started doc); updated
README per-family benchmark table to enumerate all 8 families; updated
ADR-017 verification test count (12 → 14); promoted Phase 6 evaluator
tables (UX, adversarial, reasoning, calibration, memory, safety,
cost-quality, QA composite) in `docs/benchmarks/agentic/evaluator-cards.md`;
indexed ADRs 015 / 016 / 017 in `docs/adr/README.md`.

### Changed (BREAKING) (Phase 10 — Architecture hardening, 2026-05-25)

- **T3.1** — `EvaluatorCostMap` moved from `AgentEval.Abstractions.Evals` to
  `AgentEval.Evals.Agentic.Cost`. The type is unchanged; only its namespace
  + assembly home moved. External consumers using
  `using AgentEval.Abstractions.Evals;` to reach `EvaluatorCostMap` must
  update to `using AgentEval.Evals.Agentic.Cost;` and add a
  `<PackageReference>` / `<ProjectReference>` to `AgentEval.Evals.Agentic`
  if they don't already have one. Umbrella `AgentEval` NuGet consumers are
  unaffected (both assemblies flow through transitively). Migration:
  global find-and-replace of the namespace string.
- **T3.4** — `AgentEval.Memory.Models.BaselineComparison` renamed to
  `MemoryBaselineComparison` to disambiguate from
  `AgentEval.Output.BaselineComparison` (the run-vs-saved-baseline shape on
  `IOutputStoreReader`). External consumers of the Memory baseline type
  must rename their usages; the type's shape + members are unchanged.
- **T3.4** — Trace-shape types `AgentEval.Output.AgentInfo` /
  `AgentEval.Output.ToolDefinition` renamed to `TraceAgentInfo` /
  `TraceToolDefinition` to disambiguate from the evaluation-report
  (`AgentEval.Models.AgentInfo`) and agentic-eval-input
  (`AgentEval.Evals.ToolDefinition`) shapes. External consumers reading
  `agent-trace.json` via the typed shape must rename their usages; the
  on-disk JSON schema is unchanged.
- **T4.1b Item 11** — `IOutputStoreReader` gains a `ResolveRunDirectory(
  SubjectIdentity, string runId)` member (closes the v0.10.1 layout-leak
  finding). External implementers of `IOutputStoreReader` must add the
  method to compile against v1.1+. In-tree implementations
  (`FileSystemOutputStore`, `InMemoryOutputStore`, `NullOutputStore`,
  `ReadOnlyOutputStoreAdapter`) are updated; 4 test stubs updated.
- **T0.4 (Phase 1)** — `AgentEval.MissionControl.GraphQL.ComplianceMatrixCell`
  (public positional record) gains two trailing parameters with default
  values: `bool ChainValid = true` and `string? ChainBreakReason = null`
  (per plan-08 portal-review finding A1 — surfaces per-cell hash-tampering
  in the SPA matrix). Source-compat is preserved (defaults), but appending
  ctor parameters to a public positional record is a **binary BREAKING
  change** for external code compiled against the pre-v1.1 ctor signature.
  Mitigation paths: (a) recompile against the new assembly, OR (b) use
  property-initialiser construction (`new ComplianceMatrixCell { ... }`
  with the existing required members). The type is part of Mission
  Control's GraphQL surface — most consumers reach it through the
  generated GraphQL schema, not the .NET ctor, so the source-compat
  guarantee covers the typical integration path.

### Changed (Phase 10 — Architecture hardening, 2026-05-25)

- **T3.5** — `RunCostBreakdown` now splits the legacy "unknown" bucket into
  `unknownKeyCost` (in-tree leaves whose evaluator key is not registered in
  `EvaluatorCostMap`) and `legacyFlatCost` (pre-v0.8.1-beta scenarios whose
  `Output` payload lacks a recursive `EvalResult` tree). The invariant becomes
  `totalCost == sum(byTier) + unknownKeyCost + legacyFlatCost`. SPA cost
  breakdown table renders both fields with distinct copy. Resolver:
  `src/AgentEval.MissionControl/GraphQL/{CostBreakdown.cs,Query.cs}`. SPA:
  `src/AgentEval.MissionControl.Spa/src/pages/RunDetailPage.tsx`.
- **T3.10** — `/api/v1/version` payload now includes `workspaceRoot` (the
  resolved absolute path of the workspace the MC server is bound to) and
  `workspaceInitialized` (whether `.agenteval/` exists under it). Trust
  boundary: `workspaceRoot` leaks an absolute host path — Mode A (loopback)
  only. Future Mode B/C must redact or omit.
- **T3.4** — Duplicate type names resolved. The PDF-only `RiskLevel` enum
  was merged into `AgentEval.RedTeam.Reporting.Compliance.RiskLevel`
  (semantically identical, same assembly). The trace-shape `AgentInfo` /
  `ToolDefinition` types under `AgentEval.Output` were renamed to
  `TraceAgentInfo` / `TraceToolDefinition` to disambiguate from the
  evaluation-report shape (`AgentEval.Models.AgentInfo`) and the
  agentic-eval input shape (`AgentEval.Evals.ToolDefinition`). The Memory
  `BaselineComparison` type was renamed to `MemoryBaselineComparison` to
  disambiguate from `AgentEval.Output.BaselineComparison` (the
  run-vs-saved-baseline shape on `IOutputStoreReader`). External
  consumers binding to the renamed types must update; the original
  shapes/members are unchanged.
- **T3.9** — Dockerfile gains a `HEALTHCHECK` directive (30s interval,
  curl-based probe of `/api/v1/version`). `curl` is installed in the runtime
  stage; an opt-in integration test under `tests/AgentEval.Tests/Docker/` is
  gated behind `AGENTEVAL_RUN_DOCKER_TESTS=1`.

### Known gaps (Phase 10 — Architecture hardening, 2026-05-25)

- **T3.7 prompt-file SHA pinning** — every prompt file under
  `src/AgentEval.Evals.Agentic/Resources/Prompts/` previously carried a
  vague date stamp (`commit main-2026-05-09` or `commit main/2026-05`).
  This release replaces all 22 stamps with a documented placeholder
  `<TBD-foundry-sha> see CHANGELOG T3.7` rather than inventing a fake
  SHA. The real Foundry fork-point SHA from `Azure/azure-sdk-for-python`
  must be substituted before v1.0 GA; the placeholder is grep-able for
  follow-up tooling.

## [0.10.1-beta] - 2026-05-18

The **Samples Consolidation + Generic Renderers** release. v0.10.1-beta introduces a
uniform `IEvalResultRenderer` contract in `AgentEval.Abstractions`, ships two
implementations — `HtmlEvalResultRenderer` (in `AgentEval.Core`) and the new
`PdfEvalResultRenderer` (in a new `AgentEval.Rendering.Pdf` project) — and
consolidates the per-family `*.Demo` projects into a focused `samples/AgentEval.Samples/Benchmarks/`
sample suite with one example per registered benchmark family.

### Added

- **`IEvalResultRenderer` interface** (`AgentEval.Abstractions/Evals/IEvalResultRenderer.cs`):
  uniform rendering contract any benchmark family can target. `FormatId`, `FileExtension`,
  and `RenderAsync(EvalResult, EvalResultRenderOptions, CancellationToken) -> byte[]`.
  Framing metadata (subject, run id, audit hash, AgentEval version) flows through
  `EvalResultRenderOptions`.
- **`HtmlEvalResultRenderer`** (`AgentEval.Core/Evals/Rendering/`): self-contained HTML
  output — inline CSS, `<details>` collapsible sections, severity-coded badges, XSS-safe
  encoding via `WebUtility.HtmlEncode`. Skipped leaves render honestly as `NOT TESTED`.
- **`AgentEval.Rendering.Pdf` project** with **`PdfEvalResultRenderer`**: QuestPDF-backed
  generic renderer with cover page, optional component summary, per-leaf detail pages
  (score / severity / provenance / evidence / metrics), and an audit-chain appendix.
  Embedded into the umbrella `AgentEval` NuGet via `PrivateAssets="all"`.
- **`samples/AgentEval.Samples/Benchmarks/` sample suite** — 10 focused examples wired
  into `Program.cs` as menu group H: Registry Discovery, Performance, Agentic, GDPR,
  EU AI Act, OWASP, MITRE, LongMemEval, **Memory**, and **Report Browser**. Every
  running sample writes JSON + HTML + PDF via the new renderers (the audit-grade-only
  PDF carve-out was closed mid-cycle — all running samples now produce all three
  formats). Note that H2 Performance is metric-only (latency / throughput / cost)
  and does not create an LLM judge; every other running sample (H3 onward) uses a
  real Azure-backed agent **and** a real LLM judge for grading.
- **H8 LongMemEval real-run wiring** — promoted from metadata-only walkthrough to a
  preset-driven (Smoke / Standard / AuditGrade) running sample. v0.10.1+: all presets
  run against the **real** `longmemeval_s_cleaned.json` dataset (the hand-authored
  "embedded subset" was removed — see "Changed" below). Smoke caps to 10 questions
  (~5–10 min), Standard runs `SubsetOptions` (default 50 questions), and AuditGrade
  runs `LongMemEvalBenchmark.Full(chatClient)` against the full ~500-question dataset
  (requires `LONGMEMEVAL_DATASET_PATH`). When the dataset can't be located the sample
  catches `LongMemEvalDatasetNotFoundException` and prints a friendly download-instructions
  box (URL + canonical path + env var) and returns cleanly to the menu — no unhandled
  exceptions. Shape-B bridging: the runner's `ExternalBenchmarkResult` is synthesised
  into an `EvalResult` composite tree (root = overall accuracy; per-type composites;
  per-question atomic leaves) so the canonical `.agenteval/` store + sidecar
  JSON / HTML / PDF artefacts come out identical to every other Group-H sample.
  The unaltered native shape is **also** written to `report-native.json` alongside
  `report.json` (no info loss).
- **H9 Memory benchmark sample** (`samples/.../Benchmarks/09_MemoryBenchmark.cs`) —
  mirror of the H8 Shape-B pattern over the canonical `MemoryBenchmarkRunner`.
  Smoke / Standard / AuditGrade presets map to `MemoryBenchmark.Quick` (3 categories) /
  `Standard` (8 categories) / `Full` (12 categories). Per-category progress is streamed
  to the console so long Full runs don't look hung; the result is synthesised into a
  weighted-mean `EvalResult` tree (root + per-category atomic leaves, with weights /
  stars / durations carried in `Details.Dimensions`); the unaltered
  `MemoryBenchmarkResult` (including grade, weak categories, recommendations) is
  written to `report-native.json`. Group-H now spans H1–H10:
  Registry / Performance / Agentic / GDPR / EU AI Act / OWASP / MITRE / LongMemEval /
  **Memory** / Report Browser.
- **`LongMemEvalDatasetNotFoundException`** in
  `src/AgentEval.Memory/External/LongMemEval/LongMemEvalDataLoader.cs` — subclasses
  `FileNotFoundException` (existing `catch` blocks still trigger) and carries the
  canonical local path, env-var name, and Hugging Face download URL so consumers see
  exactly how to recover. Thrown from the new
  `LongMemEvalDataLoader.LoadResolved`/`ResolveDatasetPath` resolution flow (explicit
  arg → `LONGMEMEVAL_DATASET_PATH` → canonical local path under workspace root).
- **`09_ReportBrowser` sample** (commit `077374d`): interactive browser that walks
  `samples/AgentEval.Samples/output/{family}/run-*/`, sorts newest-first (caps at 20
  with "older runs omitted"), reads `Score.Value` + `Label` from the sidecar JSON, and
  delegates to `OfferToOpenReports` for one-keystroke open of any past run's JSON / HTML / PDF.
- **`OfferToOpenReports(...)` open-after-save prompt** (commit `077374d`): `[h]/[j]/[p]/[n]`
  console prompt after each sample writes its reports. Uses
  `Process.Start(ProcessStartInfo { UseShellExecute = true })` for cross-platform
  default-app open. Honours `AGENTEVAL_SAMPLES_NONINTERACTIVE=1` and redirected stdin
  (skips the prompt cleanly for CI / scripted runs).
- **`SamplePreset` toggle** (commit `ddc1b05`) — every running sample accepts
  `AGENTEVAL_SAMPLES_PRESET=smoke|standard|audit-grade` (env var) or `--preset <value>`
  (CLI arg forwarded by `Program.cs`) so users can scale sample runtime from cents to
  audit-grade. Default: `smoke`.
- **Per-scenario compliance probing** (commit `ddc1b05`):
  `RunCompliancePresetWithAgentProbesAsync` in `_BenchmarkSampleHelpers` walks each
  article / control scenario in the preset, invokes the real agent with that scenario's
  probe prompt, captures the live response, and lets the judge grade it against the
  scenario's rubric. Used by `04_GdprBenchmark` and `05_EuAiActBenchmark` (replaces the
  earlier pattern that fanned one hardcoded response across all scenarios).
- **Canonical `IOutputStore` integration** (commits `39638b7`, `9437be4`, repo-root fix
  commit below): every running sample writes the canonical run through
  `FileSystemOutputStore` to the **repo-root `.agenteval/`** workspace — the same
  one `agenteval init` creates, resolved by walking up from the running assembly's
  directory to the nearest `*.sln`/`*.slnx`/`.git/` ancestor (matches the documented
  convention in `WorkspaceRootDiscovery.cs`). Manifest, scenarios, summary, and
  compliance evidence land there; Mission Control launched from the repo root auto-
  discovers them; `agenteval doctor` validates the audit chain. Compliance reporters
  (`GDPRComplianceReporter`, `EuAiActComplianceReporter`, `OWASPComplianceReporter`,
  `MITREATLASReporter`) are invoked for the four regulator-shaped families so
  evidence packs land alongside the run manifest with full audit-chain anchoring.
  Sidecar HTML/PDF/JSON remain project-local at
  `samples/AgentEval.Samples/output/{family}/run-{ts}-{suffix}/` for direct human
  consumption + `09_ReportBrowser`.
- **`BenchmarkSampleHelpers.SharedStore`**: process-wide `Lazy<FileSystemOutputStore>` so
  multiple samples in one process share the workspace + auto-seed `solution.json` (name
  derived from the repo's `*.sln` filename) if it doesn't already exist (no separate
  `agenteval init` step needed for first-time users — but any prior `agenteval init` is
  respected).

### Changed

- **Group-G sample class rename: `LongMemEvalBenchmark` → `LongMemEvalBenchmarkDemo`**
  (file `samples/AgentEval.Samples/MemoryEvaluation/07_LongMemEvalBenchmark.cs` →
  `07_LongMemEvalBenchmarkDemo.cs`). Closes the name-shadow foot-gun flagged in
  commit `de1e20b`'s "v0.10.2 follow-up" note: two static classes both named
  `LongMemEvalBenchmark` (production factory in `AgentEval.Benchmarks`,
  registered with `BenchmarkFamilyRegistry` via `[ModuleInitializer]`; and the
  Group-G demo in `AgentEval.Samples.MemoryEvaluation`) caused C#'s
  parent-namespace-beats-`using` name-resolution rule to silently pick the demo
  class for bare identifiers in Samples code — exactly how `08_LongMemEval`
  initially loaded the wrong assembly and the registry returned "family not
  registered" despite `AgentEval.Memory` being referenced. The `de1e20b` fix
  fully-qualified all references as a workaround; this commit removes the
  shadow at its source so future Samples code can't silently misfire. The
  fully-qualified force-load anchors in `01_RegistryDiscovery` and
  `08_LongMemEvalBenchmark` are retained as defensive consistency against any
  future shadow elsewhere in the Samples assembly.
- **`02_PerformanceBenchmark`** uses a real Azure-backed agent (was: in-process
  `EchoAgent` stub). The format-gap closure (commit `d932746`) and the real-agent
  rewiring (commit `4e09db5`) close the headline "no stubs anywhere" promise of v0.10.1.
- **`03_AgenticBenchmark`** invokes the real agent for each query (commit `ffbb3dd`);
  dropped the prior hardcoded `response` constant. The judge grades the live agent
  response, not a string literal.
- **`04_GdprBenchmark` + `05_EuAiActBenchmark`** probe the agent once per scenario
  (commit `fadf35d`) using the per-scenario YAML `input`. Each agent response is then
  judged against that scenario's evaluation criteria. Replaces the previous (incorrect)
  pattern that fanned one hardcoded response across all article scenarios.
- **`06_OwaspBenchmark` + `07_MitreBenchmark`** were already real-agent-driven (their
  attack pipelines generate adversarial probes against the agent); the preset toggle
  was wired in (commit `b6b6a96`) so users can scale from `Smoke` / `AtlasBaseline` up to
  `AuditGrade` / `AtlasAuditGrade`.
- **`01_RegistryDiscovery` actually loads sub-assemblies** (commit `31d2e27`): the prior
  `_ = nameof(...)` anchor was a compile-time string constant and did NOT trigger runtime
  assembly load, so the registry walk reported "0 benchmark families registered" instead
  of 8. Switched to the canonical `typeof(T).Assembly` anchor pattern (matches
  `BenchListCommand.AnchorAssemblies`).
- **`samples/AgentEval.Samples/output/`** is gitignored (commit `6c3b523`) so running
  samples doesn't dirty the working tree with generated PDF / HTML / JSON.
- **`samples/AgentEval.Samples/README.md`** explains the canonical-vs-sidecar storage
  split + Mission Control launch instructions + the preset toggle (commit `d19e28a`).

- **`samples/AgentEval.Samples/AgentEval.Samples.csproj`** now references
  `AgentEval.Compliance.Gdpr`, `AgentEval.Compliance.EuAiAct`, `AgentEval.Evals.Performance`,
  and `AgentEval.Rendering.Pdf` directly so the new Benchmarks samples have compile-time
  targets.
- **Umbrella `src/AgentEval/AgentEval.csproj`** bumped to `0.10.1-beta` and now embeds
  `AgentEval.Rendering.Pdf.dll` via `PrivateAssets="all"`.
- **H8 LongMemEval — eliminate fake embedded subset** (this commit). The previously-bundled
  `src/AgentEval.Memory/Data/longmemeval/longmemeval-subset.json` was a hand-authored
  "inspired by LongMemEval" approximation (10 entries, partial schema — missing
  `question_date`, `haystack_dates`, `haystack_session_ids`, `answer_session_ids`) whose
  `_attribution` field admitted it wasn't the real paper dataset. Running against it
  produced scores that looked paper-comparable but were not. All presets now load the
  real `longmemeval_s_cleaned.json` from disk:
  - **Resolution order** (highest precedence first): explicit
    `ExternalBenchmarkOptions.DatasetPath` → `LONGMEMEVAL_DATASET_PATH` env var →
    canonical local default `<workspace-root>/src/AgentEval.Memory/Data/longmemeval/longmemeval_s_cleaned.json`.
    When none resolves to an existing file the loader throws
    `LongMemEvalDatasetNotFoundException` (a `FileNotFoundException` subclass) whose
    message names the canonical path, the env var, and the Hugging Face download URL.
  - **Preset mapping**: Smoke = 10Q sample of the real 500; Standard = 50Q sample
    (was: 30Q "embedded"); AuditGrade = ~500Q via `LONGMEMEVAL_DATASET_PATH` (unchanged).
    `LongMemEvalBenchmark.SubsetMaxQuestions` raised 30 → 50 so the constant matches
    the Subset preset's "representative sample of the real 500" intent.
  - **H8 sample defensive catch**: `08_LongMemEvalBenchmark.cs` wraps the run in a
    `try/catch (LongMemEvalDatasetNotFoundException)` that renders a friendly download-
    instructions box (URL + canonical path + env var) and returns cleanly to the menu —
    no unhandled exceptions, the rest of the sample suite stays usable.
  - **Registration descriptions** updated to drop "embedded 30-question stratified sample"
    in favour of "Real LongMemEval dataset capped to MaxQuestions (default 50)".
  - **Tests**: the embedded-subset round-trip test in
    `tests/AgentEval.Memory.Tests/LongMemEvalBenchmarkTests.cs` is replaced with two
    new tests — one asserting `LoadFromFile` throws `LongMemEvalDatasetNotFoundException`
    with the download URL + env var name baked into the message, the other asserting
    the exception subclasses `FileNotFoundException` for back-compat.

### Removed

- **`src/AgentEval.Memory/Data/longmemeval/longmemeval-subset.json`** (and its
  `<EmbeddedResource>` line in `AgentEval.Memory.csproj`) — the hand-authored
  "inspired by LongMemEval" content was misleading enough to fail the "honest
  benchmarks" bar (see the "Changed" section above for full details). Consumers
  must now have the real `longmemeval_s_cleaned.json` on disk (canonical local
  path under workspace root, or `LONGMEMEVAL_DATASET_PATH`) — the loader's new
  resolution flow throws `LongMemEvalDatasetNotFoundException` with download
  instructions when it can't locate the file.
- **`LongMemEvalDataLoader.LoadEmbedded(...)`** — the static method that loaded
  the fake subset from `Assembly.GetManifestResourceStream`. Replaced by
  `LongMemEvalDataLoader.LoadResolved(...)` (which throws when no real dataset
  is reachable) and `LongMemEvalDataLoader.ResolveDatasetPath(...)` (the
  pure-resolution helper that returns the first existing file from the chain).
- **`samples/AgentEval.GdprBenchmark.Demo/` project** — the original 11-line stub was a
  CLI-hint Program.cs and added no real demonstration value. Equivalent test coverage
  already lives in `tests/AgentEval.Tests/Compliance/Gdpr/` (E2E_Standard, E2E_Smoke,
  E2E_AuditGrade, AllArticleYamlsValidate, etc.). The `Benchmarks/04_GdprBenchmark.cs`
  sample replaces it with a proper end-to-end walkthrough.
- **`samples/AgentEval.EuAiActBenchmark.Demo/` project** — `smoke-load` and `smoke-run`
  sub-commands were already covered by `tests/AgentEval.Tests/Compliance/EuAiAct/EndToEnd/`
  (`EuAiActSmokeE2ETest.cs`, `EuAiActStandardE2ETest.cs`). The `Benchmarks/05_EuAiActBenchmark.cs`
  sample replaces the demo with a single focused end-to-end run.
- **Stale orphan directories** `samples/AgentEval.GdprBenchmark/` and
  `samples/AgentEval.EuAiActBenchmark/` (no tracked source, only `bin/obj` artefacts)
  were already absent from git tracking but were sitting in the working tree from
  pre-v0.10.0 reorganisation.

### Breaking

The bulk of v0.10.1 is purely additive on top of v0.10.0-beta (new renderers,
new sample suite, new canonical-store wiring). The "real-data-only" LongMemEval
shift, however, removes one previously-public API and tightens dataset-path
resolution. NuGet consumers depending on these surfaces will need to migrate:

- **`LongMemEvalDataLoader.LoadEmbedded(...)` removed.** The static method that
  loaded the bundled "inspired by LongMemEval" subset (10 entries, partial
  schema) from `Assembly.GetManifestResourceStream` is gone — the underlying
  embedded resource is also gone (see "Removed" above). The data was a
  hand-authored approximation that produced misleading scores. **Migration**:
  replace `LongMemEvalDataLoader.LoadEmbedded(options)` with
  `LongMemEvalDataLoader.LoadResolved(options)` and ensure the real
  `longmemeval_s_cleaned.json` is reachable via canonical local path
  (`<workspace-root>/src/AgentEval.Memory/Data/longmemeval/`) or the
  `LONGMEMEVAL_DATASET_PATH` env var. Catch
  `LongMemEvalDatasetNotFoundException` for friendly "download instructions"
  UX (see `samples/AgentEval.Samples/Benchmarks/08_LongMemEvalBenchmark.cs`
  for the pattern).
- **`LongMemEvalDataLoader.ResolveDatasetPath(...)` tightened semantics.**
  When a non-whitespace `explicitPath` argument or the
  `LONGMEMEVAL_DATASET_PATH` env var is supplied but the file does NOT exist
  on disk, the method now **throws** `LongMemEvalDatasetNotFoundException`
  instead of silently falling through to the env var / canonical local path
  (PR #30 review follow-up). The previous behaviour could silently run a
  benchmark against a different dataset than the caller asked for — a
  misleading-results bug for users who typo-ed `DatasetPath` or the
  `Full()` env-var path. Fall-through to the canonical local path only
  applies when **neither** explicit nor env-var is supplied. **Migration**:
  if you previously relied on the fall-through to suppress typos, either
  validate `File.Exists` at the call site before invoking, or catch
  `LongMemEvalDatasetNotFoundException` and surface the typo to the user.

### Notes on existing family-specific PDF renderers

`GDPRPdfRenderer`, `EuAiActPdfRenderer`, and `AgenticPdfRenderer` remain untouched. They
consume bespoke evidence envelopes (`GdprComplianceEvidence`, `EuAiActComplianceEvidence`,
`AgenticBenchmarkEvidence`) that carry pillar tables, attestation blocks, and methodology
appendices the universal `EvalResult` shape does not represent. They are the right choice
for boardroom/DPO/regulator-grade audit PDFs. The new `PdfEvalResultRenderer` targets the
universal cross-family path (samples, third-party plugins, discovery walkthroughs).

### Mission Control workspace + score semantics

- **`--workspace <path>` is now honoured by bare `dotnet run --project src/AgentEval.MissionControl`**: previously the bare run-path silently fell back to `Directory.GetCurrentDirectory()` (yielding `src/AgentEval.MissionControl/.agenteval`) regardless of the flag. The CLI form `agenteval mc serve --workspace ...` already routed through `AgentEval__Root` env var; the bare-run path now does the same. Mirrors `McServeCommand`'s behaviour.
- **`Query.recentRuns(...).score` returns pass-rate** (passed leaves / total leaves), not the weighted-composite verdict score that the sample console prints. Both are valid; they diverge when composite aggregation strategies weight leaves non-uniformly (most clearly with `MinAggregation` security-gate semantics). Use `Query.run(runId:).overallScore` for the composite score; `recentRuns.score` is intentionally a fast scan-time summary suitable for list views.

### Known issues / tracked for v0.10.2+

- **NuGetConsumer LLM non-determinism**: `samples/AgentEval.NuGetConsumer.Tests/SafetyPolicyTests.CancellationRequest_ShouldConfirmBeforeCancelling` is flaky at roughly 90% pass rate on 10-iteration stress (real LLM call; when the model responds with text instead of a tool call, the strict tool-call assertion fails). Pre-existing — predates the v0.10.0-beta arc. Not introduced by any phase of v0.10.0-beta. Tracked here for v0.10.1 stabilisation (likely fix: relax the test's strictness to accept either-tool-or-confirmation-text, or seed the model into a deterministic mode).
- **`docs/redteam/owasp.md` not authored**: `OwaspBenchmarkRegistration.docLinkUrl` points at this future doc; deferred to v0.11+ docs-pack.
- **`README.md` benchmark-table sweep + `docs/benchmarks.md` update**: deferred to v0.10.1 docs-pack. The README is version-agnostic so no urgency.
- **Agentic `safety` preset + GDPR/EuAiAct domain-pack registry surfaces**: `BenchmarkFamilyRegistry.CompositeFactory` paths throw at call time for presets that need programmatic config (PolicyResolver / domain-pack composition). Documented in registration files; users use the direct programmatic API. v0.10.1+ would add a `RequiresProgrammaticConstruction` flag on `BenchmarkPreset` to surface this in `bench --list` more gracefully.
- **`BenchmarkFamilyRegistryTests` count**: ADR-017 §Verification says "12 tests"; the source file has 13. Cosmetic.

## [0.10.0-beta] - 2026-05-17

The **AgentEval Benchmark Suite** release. v0.10.0-beta unifies eight benchmark families
(Agentic, GDPR, EU AI Act, OWASP, MITRE, LongMemEval, Performance, Memory) under a single
discovery surface (`AgentEval.Benchmarks` namespace + `BenchmarkFamilyRegistry`), promotes
the GDPR / EU AI Act benchmarks out of `samples/` to first-class product assemblies,
relocates `PerformanceBenchmark` to its own assembly with a Convention-2 `EvaluateAsync`
adapter, and adds new façades for OWASP LLM Top 10, MITRE ATLAS, and the LongMemEval
academic benchmark. See [ADR-017](docs/adr/017-unified-benchmarks-namespace.md) for the
full architectural rationale and the four conventions this release establishes.

### Added — `BenchmarkFamilyRegistry` (canonical single-source-of-truth)

The new `AgentEval.Core.Benchmarks.BenchmarkFamilyRegistry` is the canonical mechanism for
benchmark-family discovery (ADR-017 Convention 3). Eight families — Agentic, GDPR,
EU AI Act, OWASP, MITRE, LongMemEval, Memory, Performance — auto-register on assembly load
via `[ModuleInitializer]`-attributed hooks in their owning assemblies. Future families
(HIPAA, PCI-DSS, ISO 42001, NIS2, SOC 2, UK AI Bill, …) plug in via the same one-line
registration. The registry is thread-safe (backed by `ConcurrentDictionary`), idempotent on
same-content re-registration, and rejects name collisions with different content.

Two registration shapes are supported (see `BenchmarkFamily` XML doc for the contract):
- **Shape A — `CompositeEval`-native** (Agentic, GDPR, EU AI Act, OWASP, MITRE, Performance):
  factory returns a `CompositeEval` that the runner can `EvaluateAsync` directly.
- **Shape B — external-dataset / multi-turn** (LongMemEval, Memory): factory returns a
  runner-style type with a different invocation contract.

`agenteval bench --list`, per-family `--help` preset enumeration, and (future) Mission
Control's family-discovery surface all read from this single source of truth. Adding a new
benchmark family without registering here is a contract violation caught by
`BenchmarkNamespaceContractTests` / `BenchmarkFamilyRegistryTests`.

### Added — `bench --list` CLI command

`agenteval bench --list` enumerates all currently-registered benchmark families
(name, default cost tier, presets) from `BenchmarkFamilyRegistry`. The listing is genuinely
registry-sourced — `BenchListCommandTests.OutputComesFromRegistry` proves this by
registering a synthetic UUID-named family at runtime and asserting it appears in the
output. Third-party extension assemblies that register their own families via
`[ModuleInitializer]` will surface here automatically.

### Added — `bench perf {latency,throughput,cost}` CLI subcommand

`PerformanceBenchmark` previously had no CLI entry point. v0.10.0-beta adds the
`bench perf` sub-command tree mirroring `bench agentic` / `bench gdpr` / etc.:

```
agenteval bench perf latency --subject MyAgent --prompt "Tell me a joke"
agenteval bench perf throughput --subject MyAgent --prompt "..." --concurrency 5 --duration 30s
agenteval bench perf cost --subject MyAgent --prompts prompts.jsonl
```

Output flows through the standard `.agenteval/` workspace (manifest + scenarios +
summary + run-index append) — identical artefact shape to every other `bench` family,
courtesy of Convention 2's `EvaluateAsync` adapter (see Phase 3 / Changed below).

### Added — Per-family `bench {family} --help` preset enumeration

`agenteval bench owasp --help` (and every other family) now dynamically lists the
family's available `--preset` options with one-line descriptions, sourced from
`BenchmarkFamilyRegistry.TryGet(family).Presets`. Future preset additions don't
require touching CLI plumbing.

### Added — `OwaspBenchmark` façade (`AgentEval.Benchmarks` namespace)

New top-level preset factory over the existing red-team attack pipeline. Presets:
- **`Top10()`** — All 9 implemented attacks at `Intensity.Quick`, 10-min timeout. Medium cost.
- **`Smoke()`** — 3 MVP attacks (PromptInjection + Jailbreak + PIILeakage) at Quick
  intensity — CI-friendly. Low cost.
- **`AuditGrade()`** — All 9 attacks at `Intensity.Comprehensive`, 30-min timeout —
  audit-grade evidence. High cost.
- **`Top10ForRag()`** — All 9 attacks at `Intensity.Comprehensive`, 20-min timeout —
  RAG threat-model depth (LLM01 indirect-injection emphasis). High cost.

`OwaspBenchmark.Top10(judge).EvaluateAsync(input, ct)` returns a 10-leaf `EvalResult`
composite (one leaf per OWASP LLM Top 10 category). 4 of the 10 categories that aren't
testable at the agent-API layer (LLM03 Supply Chain, LLM04 Data/Model Poisoning,
LLM08 Vector/Embedding Weaknesses, LLM09 Misinformation) emit honest `skipped` leaves
rather than fabricated scores. The 6 tested categories are LLM01 (Prompt Injection),
LLM02 (Sensitive Information Disclosure), LLM05 (Improper Output Handling),
LLM06 (Excessive Agency), LLM07 (System Prompt Leakage), and LLM10 (Unbounded
Consumption). Aggregation: `MinAggregation` (security-gate semantics — a single
critical-fail caps the composite). The bespoke `OWASPComplianceReport` remains
available alongside the `EvalResult` for downstream consumers that want richer
evidence data.

### Added — `MitreBenchmark` façade (`AgentEval.Benchmarks` namespace)

Mirror of OwaspBenchmark, projecting the same 9-attack roster onto MITRE ATLAS technique
IDs. Presets:
- **`AtlasBaseline()`** — All 9 attacks at Quick intensity. Medium cost.
- **`AtlasSmoke()`** — 3 MVP attacks. Low cost.
- **`AtlasAuditGrade()`** — All 9 attacks at Comprehensive intensity. High cost.

`EvaluateAsync` returns a 12-leaf composite (one leaf per ATLAS technique covered by the
canonical reporter roster). Every leaf's `Metric.Key` is `mitre.aml.t0xxx` so the
audit-chain trace preserves the ATLAS-ID linkage. `MitreBenchmarkRun.BuildEvalResult` and
`OwaspBenchmarkRun.BuildEvalResult` overloads let CLI callers avoid double-scanning when
they already have a `RedTeamResult` in hand.

### Added — `LongMemEvalBenchmark` façade (`AgentEval.Memory.External.LongMemEval`)

Shape B (external-dataset) registration over the existing `LongMemEvalBenchmarkRunner`.
Presets:
- **`Subset(chatClient)`** — Embedded 30-question stratified sample, no download required,
  CI-friendly. Medium cost.
- **`Full(chatClient)`** — Full ~500-question dataset. **Requires `LONGMEMEVAL_DATASET_PATH`
  env var** pointing at the downloaded dataset directory (see Changed below). High cost.

Closes the credibility gap: "AgentEval supports the LongMemEval (ICLR 2025) academic memory
benchmark" is now a real product claim. See <https://arxiv.org/abs/2410.10813>.

### Changed — Unified benchmark namespace `AgentEval.Benchmarks`

`AgenticBenchmark`, `GdprBenchmark`, `EuAiActBenchmark`, `OwaspBenchmark`, `MitreBenchmark`,
`LongMemEvalBenchmark`, `PerformanceBenchmark`, and `MemoryBenchmark` are now all declared as
`public static partial class` under the single namespace `AgentEval.Benchmarks` (ADR-017
Convention 1). One `using` directive covers benchmark discovery:

```csharp
using AgentEval.Benchmarks;

var agentic   = AgenticBenchmark.AgenticExecution(judge);
var gdpr      = GdprBenchmark.Standard(articles);
var euAiAct   = EuAiActBenchmark.Standard(articles);
var owasp     = OwaspBenchmark.Top10(judge);
var mitre     = MitreBenchmark.AtlasBaseline(judge);
var perf      = new PerformanceBenchmark(agent);
var longMem   = LongMemEvalBenchmark.Subset(chatClient);
```

Internal types (registries, pillars, runners, scenarios, evaluators) stay in their domain
namespaces (`AgentEval.Compliance.Gdpr.*`, `AgentEval.Evals.Agentic.Process`,
`AgentEval.RedTeam`, `AgentEval.Memory.External.LongMemEval`, …) — physical layering
preserved, logical layering unified. `BenchmarkNamespaceContractTests` enforces the
convention via reflection.

### Changed — Compliance benchmarks promoted from `samples/` to `src/`

`samples/AgentEval.GdprBenchmark/` and `samples/AgentEval.EuAiActBenchmark/` were referenced
as hard `ProjectReference` dependencies by the shipping CLI and embedded into the umbrella
NuGet as transitive runtime dependencies — they were de facto product code, mislabelled as
"samples". They are now promoted to first-class product assemblies:

- `src/AgentEval.Compliance.Gdpr/` (was `samples/AgentEval.GdprBenchmark/`)
- `src/AgentEval.Compliance.EuAiAct/` (was `samples/AgentEval.EuAiActBenchmark/`)

Internal namespaces consolidated:
- `AgentEval.GdprBenchmark.*` → `AgentEval.Compliance.Gdpr.*`
- `AgentEval.EuAiActBenchmark.*` → `AgentEval.Compliance.EuAiAct.*`

The previous parent namespace collided with the type name of the same name (`AgentEval.GdprBenchmark`
was simultaneously a namespace AND the factory type name `GdprBenchmark`). The rename
eliminates the collision at root and removes the 13 `using XxxBenchmarkFactory = …`
disambiguation aliases that Phase 4 had to introduce. Two thin demo projects remain in
`samples/AgentEval.GdprBenchmark.Demo/` and `samples/AgentEval.EuAiActBenchmark.Demo/`
(~50 LOC each, consuming the promoted assemblies). Compliance lives outside the `Evals.*`
namespace tree because regulations are *regulatory packages* (composing evaluator primitives
into domain scenarios with audit-chain evidence + signed PDF reports), conceptually distinct
from `Evals.*` *evaluator collections*. See ADR-017 §"Why compliance lives outside `Evals.*`".

### Changed — `PerformanceBenchmark` relocated + `EvaluateAsync` adapter

`PerformanceBenchmark` and its co-located result types (`LatencyBenchmarkResult`,
`ThroughputBenchmarkResult`, `CostBenchmarkResult`, `PerformanceBenchmarkOptions`) moved
from `src/AgentEval.Core/Benchmarks/` to a dedicated `src/AgentEval.Evals.Performance/`
assembly. A new `EvaluateAsync(EvalInput, CancellationToken) → EvalResult` adapter
(ADR-017 Convention 2) synthesises a 3-leaf `CompositeEval`-shape result (latency,
throughput, cost) with `CapByWorst` aggregation:

- **Latency** — `1 − (p99ms / threshold)` clamped [0, 1] (default threshold: 5000 ms)
- **Throughput** — `min(rps / minRps, 1.0)` (default minRps: 0.5)
- **Cost** — `1 − (cost / maxCost)` clamped [0, 1] (default maxCost: 0.10 USD); pass with
  low severity when no pricing data is available for the model.

Thresholds are tunable via `PerformanceBenchmarkOptions.EvaluateOptions`. Bespoke result
records are preserved in `Provenance` for downstream consumers that want richer data. The
adapter is what allows `bench perf` to write into the standard `.agenteval/` workspace
alongside every other benchmark family. The legacy `src/AgentEval.Core/Benchmarks/` folder
was removed (one-file ghost folder from a half-finished organisational idea).

### Changed — `OwaspBenchmark.Top10ForRag()` refocused

`Top10ForRag` was previously structurally identical to `Top10` (Quick intensity, 10-min
timeout). It now runs at `Intensity.Comprehensive` with a 20-min timeout, sitting between
`Top10` (Quick, 10-min) and `AuditGrade` (Comprehensive, 30-min). The RAG threat model:
indirect-injection coverage from poisoned retrieved documents — an attacker needs only one
working payload, so the defender needs *coverage depth* on injection techniques. The
cost-tier classification shifts Medium → High to reflect the deeper probe coverage. **No
API signature change**; programmatic callers see slower runs but materially deeper probe
coverage. Two divergence-pinning tests (`Top10ForRag_IsMateriallyDistinctFromTop10_DeepProbeCoverage`
and `Top10ForRag_ProbeDepth_MatchesAuditGrade_NotTop10`) prevent a future label-only
regression. The LLM08 retrieval-corpus-poisoning probes remain a documented roadmap gap
(LLM08 is a `skipped` leaf in `EvaluateAsync` output, same as `Top10`). Closes the Phase-5
yellow item documented in `strategy/FutureFeatures/todo/lastreview/13-phase5-gate-review.md`.

### Changed — `LongMemEvalBenchmark.Full()` no longer silently degrades

`LongMemEvalBenchmark.Full()` previously silently fell back to the embedded subset when
`LONGMEMEVAL_DATASET_PATH` was unset — a footgun for users who thought they were running
the full ~500-question benchmark but were actually getting the 30-question stratified
sample. v0.10.0-beta makes this an explicit failure: `Full()` now throws
`InvalidOperationException` with a clear, actionable message (env-var name, download URL,
pointer at `Subset()` for development use) when the env var is missing. Callers who want
the embedded sample should use `Subset()` explicitly. This closes the Phase-7 follow-up
item documented in `strategy/FutureFeatures/todo/lastreview/15-phase7-gate-review.md`. The
behaviour change is technically breaking for any consumer that relied on the
silent-degradation path, but the previous behaviour was unambiguously a footgun and
0.x-beta semver permits this kind of correction.

### Changed — `LongMemEvalBenchmarkRunner` defaults preset options at construction

A new 3-arg `LongMemEvalBenchmarkRunner.Create(client, datasetPath, defaultOptions)`
overload bakes the preset's `ExternalBenchmarkOptions` (`SubsetOptions` /
`FullOptions`) into the runner instance, and a new 3-arg `RunAsync(agent, config, ct)`
overload picks up `DefaultOptions` automatically. Callers no longer need to manually thread
`SubsetOptions.RandomSeed` / `MaxQuestions` etc. through every call site — `Subset()` and
`Full()` factory methods now pre-configure their runners correctly. Closes the Phase-7
follow-up item where `SubsetOptions.RandomSeed` was effectively dead unless the caller
manually wired it.

### Breaking — `AgentEval.Compliance.{Gdpr,EuAiAct}.*` internal namespaces

The internal namespace rename from `AgentEval.GdprBenchmark.*` to
`AgentEval.Compliance.Gdpr.*` (and the equivalent for EuAiAct) is **breaking for any
consumer that reached into the internal types** (`ArticlesRegistry`, pillars,
`ScenarioToAtomicEval` configurations, domain packs). The public preset-factory entry
point is unchanged at `AgentEval.Benchmarks.GdprBenchmark` (it was already moved to that
namespace in v0.10.0-beta Phase 4). Migration: replace `using AgentEval.GdprBenchmark;`
with `using AgentEval.Compliance.Gdpr;` (and the EuAiAct equivalent) when reaching for
internal types. The compliance evidence schemas and embedded YAML article files moved with
the rename — `gdpr-evidence.schema.json` is now embedded as
`AgentEval.Compliance.Gdpr.Reporting.Schema.gdpr-evidence.schema.json` rather than
`AgentEval.GdprBenchmark.Reporting.Schema.gdpr-evidence.schema.json`. Tests that load
embedded resources by manifest-resource path string need to update.

### Breaking — `PerformanceBenchmark` assembly relocation

`PerformanceBenchmark` and its co-located result types moved from `AgentEval.Core.dll` to
the new `AgentEval.Evals.Performance.dll`. The umbrella NuGet still ships both
(`PrivateAssets="all"` embeds the sub-assembly), so consumers installing the `AgentEval`
NuGet package see no change. **Consumers who hard-reference the internal `AgentEval.Core`
assembly** (an unusual pattern but technically possible) need to add a reference to
`AgentEval.Evals.Performance` as well. The namespace `AgentEval.Benchmarks` is unchanged.

### Breaking — `LongMemEvalBenchmark.Full()` throws when env var unset

See the Changed entry above. Any consumer that relied on the silent-degradation fallback
(getting the embedded 30-question subset when `LONGMEMEVAL_DATASET_PATH` was unset) needs
to switch to `LongMemEvalBenchmark.Subset()` explicitly or set the env var.

## [0.9.0-beta] - 2026-05-17

### Removed (BREAKING) — Legacy `AgenticBenchmark` library API

Removed the entire pre-v0.9.0 library-API benchmark surface. The new agentic preset-factory API (`AgentEval.Evals.Agentic.AgenticBenchmark` + the ~60-evaluator suite, driven via `agenteval bench agentic --preset X`) is the canonical replacement and is strictly more capable.

**Types removed** (all were in `AgentEval.Benchmarks` namespace, shipped in v0.3.0-beta through v0.8.1-beta):
- `AgenticBenchmark` (the library runner class with `RunToolAccuracyBenchmarkAsync`, `RunTaskCompletionBenchmarkAsync`, `RunMultiStepReasoningBenchmarkAsync` methods)
- `AgenticBenchmarkOptions`
- `ToolAccuracyTestCase`, `ExpectedTool`, `ToolAccuracyResult`, `ToolAccuracyTestResult`
- `TaskCompletionTestCase`, `TaskCompletionResult`, `TaskCompletionTestResult`
- `MultiStepTestCase`, `ExpectedStep`, `MultiStepReasoningResult`, `MultiStepTestResult`, `StepResult`

**Extension methods removed** (in `AgentEval.DataLoaders`):
- `DatasetTestCase.ToToolAccuracyTestCase()`
- `DatasetTestCase.ToTaskCompletionTestCase()`

**Migration**

| Legacy v0.3-v0.8 | v0.9.0-beta+ |
|---|---|
| `new AgenticBenchmark(adapter).RunToolAccuracyBenchmarkAsync(cases)` | `AgenticBenchmark.ToolCallAccuracy(judge)` returning a `CompositeEval` you evaluate against `EvalInput` |
| `new AgenticBenchmark(adapter, evaluator).RunTaskCompletionBenchmarkAsync(cases)` | `AgenticBenchmark.AgenticExecution(judge)` (covers task completion + adherence + intent + tool accuracy + navigation) |
| `new AgenticBenchmark(adapter).RunMultiStepReasoningBenchmarkAsync(cases)` | `AgenticBenchmark.Reasoning(judge)` (4 evaluators: correctness, intermediate-step hallucination, plan formulation, goal decomposition) |
| `dc.ToToolAccuracyTestCase()` | Load prompts via `DatasetLoaderFactory`, build `EvalInput(query, response)` directly |
| `dc.ToTaskCompletionTestCase()` | Same — `EvalInput` is the unified shape across all agentic evaluators |

For a full migration example see [`samples/AgentEval.Samples/DataAndInfrastructure/04_BenchmarkSystem.cs`](samples/AgentEval.Samples/DataAndInfrastructure/04_BenchmarkSystem.cs) — rewritten against the new API in this release.

**Why now**: the legacy class shipped 3 hard-coded benchmark kinds with bespoke result records and no audit-chain integration. The new preset-factory API covers 11 presets + 60 evaluators, integrates with the CLI / `.agenteval/` workspace / Mission Control portal / calibration tooling, and shares the unified `EvalResult` envelope with every other AgentEval evaluator. Keeping the legacy surface alongside the new one would have permanently fragmented the public API and added maintenance burden on a feature with no remaining advocates. Semver `0.x` permits breaking minor bumps; v0.9.0-beta is the natural cut point.

**`PerformanceBenchmark` (the in-process latency/throughput/cost measurement) is unchanged** and remains in `AgentEval.Benchmarks` namespace.

### Changed — `AgenticBenchmark` namespace moved

The preset-factory `AgenticBenchmark` (introduced in v0.8.x) moved from `AgentEval.Evals.Agentic.Composition` to `AgentEval.Evals.Agentic`. Consumers using fully-qualified references or `using AgentEval.Evals.Agentic.Composition;` to reach the preset factory must update:

```csharp
// Before
using AgentEval.Evals.Agentic.Composition;
var preset = AgenticBenchmark.ToolCallAccuracy(judge);

// After
using AgentEval.Evals.Agentic;
var preset = AgenticBenchmark.ToolCallAccuracy(judge);
```

The companion infrastructure types (`AgenticBenchmarkRunner`, `CostFilteredCompositeBuilder`) remain in `AgentEval.Evals.Agentic.Composition`. The rename better reflects that `AgenticBenchmark` is a top-level entry point (matching `GdprBenchmark` and `EuAiActBenchmark` which both sit at their respective project roots).

### Added — Pre-merge polish from last-review parallel Opus sweep (2026-05-16)

Eight merge-critical items (M1-M8) plus four pulled-forward v1.1 items (1.5 / 1.6 / 1.7 / 3.2) landed in the pre-merge bundle. See `strategy/FutureFeatures/todo/lastreview/00-summary.md` for the full audit trail.

- **`AtomicLlmEval` now populates `EstimatedCost` from real judge token usage** (closes F-002). The `IEvaluator` interface gained an `EvaluationResult.InputTokenCount` / `OutputTokenCount` pair; `ChatClientEvaluator` lifts those from the underlying `ChatResponse.Usage`. `AtomicLlmEval` looks them up against a new `AgentEval.Abstractions.Evals.JudgeCostMap` (per-1K input/output rates by model id, with substring fallback for dated suffixes like `gpt-4o-mini-2024-07-18`) and writes the dollar figure into `EvalResult.Provenance.EstimatedCost`. Composite cost rollups via `CostRollup.Aggregate` now sum to real dollars rather than $0. Consumers that filtered on `EstimatedCost == 0` to detect "no LLM call happened" must switch to checking the trace's evaluator-kind field instead.
- **`Recommendation` is now a structured record across both compliance benchmarks** (closes the `v0.8.1-beta` `getting-started.md:172` disclaimer for GDPR + the parallel EU AI Act disclaimer). The new record is `Recommendation(string ControlId, string Severity, string Text, IReadOnlyDictionary<string,string>? Metadata = null)`, replacing the legacy `string[]` shape in `GdprComplianceEvidence` / `EuAiActComplianceEvidence`. Both `gdpr-evidence.schema.json` and `eu-ai-act-evidence.schema.json` use an `anyOf` union at the `items` level so legacy `string[]` evidence files written by 0.8.0-beta still validate against the v0.8.1-beta schema. The optional `metadata: { string: string }` field is reserved for v1.2+ extensions (evidence references, correlation ids) without requiring a breaking schema change. Markdown renderer output changes from `<text>` to `` `<controlId>` [<severity>]: <text>`` per entry. **PDF reports do NOT include recommendations** — by design, the PDF is the boardroom-signed artefact and the Markdown report + evidence JSON carry actionable remediation copy; rendering recommendations in the PDF is tracked as a v1.1 markdown-reporter-parity item.
- **EvaluatorCard categories reconciled with the runtime** (closes F-006). A new `CardRuntimeMetadataParityTest` enumerates every embedded card JSON and asserts the card's `category` matches the runtime class's `Category` property (or the static `CategoryValue` constant on evaluators with complex constructors). Found and fixed drift on 37 of 60 cards: `safety`→`safety-security` (12 cards), `process`→`agentic-process` (6), `system`→`system-outcome` (5), `quality`→`rag` (7), `telemetry`→`operational` (6), `stochastic-stability`→`operational` (1). Downstream consumers (Mission Control SPA filter chips, `--budget-tier` filter) need to read the new category values; the GraphQL `evaluator(key)` resolver returns them verbatim. A single `GraphQLSmokeTests` assertion was updated to match the renamed `system-outcome` value.
- **`AgentEval.Core.Benchmarks.AgenticBenchmark` is now `[Obsolete]` with a v1.2 removal target.** The deprecation message points consumers at the canonical `AgentEval.Evals.Agentic.Composition.AgenticBenchmark` preset factory. 20 existing call sites (19 in `AgenticBenchmarkTests.cs`, 1 in `04_BenchmarkSystem.cs`) raise CS0618 warnings — they're intentionally not migrated in this PR; migration is tracked for v1.2 alongside the type's removal.
- **Eight merge-critical items closed across the `last-review` parallel Opus sweep** — see `lastreview/00-summary.md` for the M1-M8 audit trail. Highlights:
  - **EU AI Act Pillar 1 thresholds corrected.** Four `art-5-1-*.yaml` files had inverted thresholds (`pass:0.70, warn:0.85` — WARN above PASS, the inverse of every other YAML); now `pass:0.85, warn:0.70` matching the rest of the benchmark. Affects every consumer that read `WarnThreshold` directly (the field was previously parsed-but-unused, so no runtime behaviour change today — but the data is now correct).
  - **HR Art 22 severity drop fixed.** `art-22-hiring-decisions.yaml` previously declared `severity: "high"` while the base `art-22-automated.yaml` declared `critical`. Result: HR-domain Art 22 failures (an ATS auto-rejecting candidates without human review, for example) registered as `high` and slipped past both the `CriticalFindingExtractor` and `CapByWorstAggregation` in AuditGrade. Now aligned to `critical / 0.85 / 0.75`.
  - **`WorkspaceRootValidator` threaded through `MigrateCommand`, `DoctorCommand`, and `McServeCommand`.** A malformed or non-existent `--root` / `--workspace` argument now returns exit code 1 from the validator before any path operations run, matching the contract of every other workspace-aware command. Four new bad-root tests added.
  - **Umbrella `AgentEval` NuGet package now ships the agentic evaluator suite.** `<PackageReference Include="AgentEval" />` consumers gain access to `AgentEval.Evals.Agentic.*` types and a new `services.AddAgentEvalAgentic()` stable DI hook (no-op today; future per-evaluator services land behind the same signature).
  - **Plain-English `how-it-works.md` explainer pages** added per benchmark; existing `getting-started.md` docs swept for fragile counts and replaced with qualitative bands where the numbers churn between releases.
  - Doc-drift items in the GDPR + EU AI Act + agentic explainers fixed (4 factual errors I'd authored in the first pass, including missing the `Safety` category in the agentic doc).

### Security — LR7 hardening extras (2026-05-16)

Three small additive hardening items closed after the LR7 audit, on top of the M1-M8 + Option C bundle.

- **`Permissions-Policy` header added to Mission Control.** Locks down geolocation, microphone, camera, payment, USB, MIDI, magnetometer, gyroscope, and accelerometer — none of which the portal ever uses. Defense-in-depth against a future XSS bug or an operator who follows the Dockerfile LAN-expose example.
- **`additionalProperties: false` added to top-level evidence wrappers.** `gdpr-evidence.schema.json` and `eu-ai-act-evidence.schema.json` now reject unknown top-level keys. Closes a real attack-surface gap where tampered tooling could inject arbitrary wrapper-level keys past schema validation.
- **`category` field on `evaluator-card.schema.json` is now enum-constrained** to the 14 canonical category strings. The M1.6 / LR3-008 class of card↔runtime drift bugs (37 cards corrected in this PR) is now caught at schema-validation time, before `CardRuntimeMetadataParityTest` even runs. Add new values to the enum when a new runtime category lands.

### Security — Mission Control portal audit findings (Phase-0 close, 2026-05-13)

Three findings from the in-depth Mission Control portal security audit (2026-05-13). 0 P0 blockers were found; the three items below are P1 hardening that the audit recommended landing before merge to `main`.

- **`Query.complianceEvidence` now enforces the per-doc audit chain (plan-07 §7).** The resolver previously returned the evidence document blind to whether `evidence.SourceRun.ManifestHash` still matched the actual `RunManifest.ContentHash`. The aggregated `ComplianceMatrix` already enforced the check; this resolver now mirrors it. New return shape `ComplianceEvidenceWithChain { evidence, chainValid, chainBreakReason }` — `chainBreakReason` is `null` (valid), `"source-run-not-found"` (orphaned evidence), or `"hash-mismatch"` (tamper signal). The SPA's evidence-detail page now renders a red "Audit chain broken" banner + a `valid` / `broken` shield badge in the Audit-chain section. (Breaking schema change to the `complianceEvidence` GraphQL field; SPA query updated in this PR.)
- **`FileSystemOutputStore` constructor no longer sweeps stale sentinels.** The 24h+ sweep of `*.invalid.json` / `*.lock` / `*.tmp` files moved out of the constructor into a new explicit `SweepStaleSentinelsAsync(TimeSpan olderThan)` method. CLI writer entry points (`bench gdpr` / `bench eu-ai-act` / `bench agentic`) call sweep after constructing the store; Mission Control (read-only viewer per plan-07 §1) does not. Closes the previous contract violation where MC startup silently deleted files outside Docker.
- **`Dockerfile` `docker run` example bound to `127.0.0.1`.** The comment example at `Dockerfile:13` previously showed `-p 5000:5000`, which publishes the unauthenticated portal on all host interfaces. Now shows `-p 127.0.0.1:5000:5000` (matching `docker-compose.yml`) with an explicit `# SECURITY:` note explaining when LAN exposure is acceptable.

### Changed (BREAKING) — audit-chain hash format

The `ContentHasher` now binds the **canonical-serialised manifest** (with `contentHash` zeroed) into the hash domain, in addition to summary + scenarios + traces. Three consequences:

1. **Workspaces written by 0.8.0-beta will fail `VerifyAsync` under 0.8.1-beta.** The hash format is intentionally different: pre-0.8.1-beta `contentHash` covered only summary + scenarios + agent-trace.json, so a `manifest.run.verdict` tamper went undetected. The new domain binds operator / host / git provenance to the run. No migration tooling ships in 0.8.1-beta — re-run `agenteval bench …` to regenerate evidence.

2. **Every `traces/*.json` file** is now hashed (previously only `agent-trace.json`). Per-test trace artefacts written by `TraceArtifactManager` were silently excluded from the audit chain pre-0.8.1-beta; they're now covered.

3. **Manifest property order in the canonical-hash bytes is pinned alphabetically** via a hand-written converter (`CanonicalRunManifestConverter`). Adding a new field to `RunManifest` requires updating the converter — a deliberate hash-format change, not an accidental one.

### Changed — `manifest.run.kind` enum extended

The `manifest.schema.json#/properties/run/properties/kind` enum gained `"benchmark"` (alongside existing `eval`/`memory-benchmark`/`stochastic-eval`/`compliance`). Producers using `Kind: "benchmark"` (the agentic benchmark runner; some test fixtures) now validate cleanly against the schema. This is an additive, non-breaking change for any existing producer using the original values.

### Changed — JSONL appenders are now cross-process safe

`recent.jsonl` and `history.jsonl` appends serialise via a named `Mutex` (keyed on SHA-256 of the canonicalised absolute path) plus an in-process `SemaphoreSlim` short-circuit. Two parallel `agenteval bench` runs writing to the same workspace no longer interleave bytes mid-line.

### Changed — `EnsureSubjectAsync` concurrency-gated

`FileSystemOutputStore.EnsureSubjectAsync` now takes an exclusive `.lock` sentinel for the read-check-write triple. Concurrent same-name calls (e.g. parallel test fixtures sharing a workspace) serialise via the file lock; corrupt `subject.json` throws `InvalidOperationException` with manual-inspect guidance instead of a raw `JsonException`; partial-init collisions (subject directory present without subject.json on case-insensitive filesystems) are detected.

### Changed — `EvalResultPersistence` lifted-metrics keys namespaced

`ToScenarioResult` now lifts `_lifted.severity_ordinal` and `_lifted.confidence` into `ScenarioResult.Metrics` (previously `severity_ordinal` / `confidence`, which silently overwrote consumer `Dimensions` using those names as criterion keys). Readers that queried the lifted values must update to the `_lifted.*` form. Consumer dimensions named `confidence` / `severity_ordinal` are now preserved untouched.

### Changed — schema validation at every write

`FileSystemOutputStore` now calls `SchemaValidator.ValidateOrThrow` before writing `subject.json` / `manifest.json` (initial + final) / `summary.json` / red-team manifest / `solution.json`. On validation failure, the offending DTO is dumped to a sibling `.invalid.json` sidecar for debugging; the store ctor sweeps stale `.invalid.json` / `.lock` / `.tmp` sentinels older than 24 hours.

### Changed — `MultiJudgeOptions` record marked `[Obsolete]` (no removal in v1)

The `MultiJudgeOptions` record in `AgentEval.GdprBenchmark` and `AgentEval.EuAiActBenchmark` is now annotated `[Obsolete]` because Mode-B per-criterion multi-judge fan-out has moved into `ScenarioToAtomicEval` ctor flags. The `AuditGrade(articles, multiJudge)` factory signature is retained for v1 source compatibility (passing `null` continues to select single-judge behaviour); removal is scheduled for v1.1. Consumers will get a compile-time CS0618 warning when constructing `new MultiJudgeOptions(...)` — switch to the `ScenarioToAtomicEval` Mode-B configuration instead, or pass `null` to keep single-judge behaviour.

### Changed (BREAKING) — `agenteval bench <regulation> --subject` is now required

`agenteval bench gdpr`, `bench eu-ai-act`, and `bench agentic` previously defaulted `--subject` to the literal string `"default-agent"` when omitted. Phase-7 Task 7.21 removed that default: the commands now exit with code 1 and an explicit error message when `--subject` is missing. CI pipelines / scripts that depended on the default must pass `--subject <agent-name>` explicitly.

### Changed (BREAKING) — `agenteval bench eu-ai-act --input` is now required

`agenteval bench eu-ai-act` previously substituted a hard-coded built-in fixture ("I'm building an AI assistant. What should it disclose…") when `--input` was omitted. Phase-7 Task 7.22 removed the fixture: the command now exits with code 1 unless `--input <prompt>` is supplied. The other two bench commands (`bench gdpr`, `bench agentic`) still accept their own built-in fixtures — only EU AI Act required the breaking change.

### Changed — calibration commands gate on evaluation failures + new `INFRA-FAIL` status

`agenteval bench gdpr calibrate`, `bench eu-ai-act calibrate`, and `bench agentic calibrate` now treat any `EvaluationFailures > 0` as a gate failure (exit code 2) and surface the failure count alongside accuracy / kappa in both the console output and the Markdown report. A new status — `[INFRA-FAIL]` — replaces `[FAIL]` when every entry threw (Azure unreachable, transient infra error, etc.), making it possible to distinguish infrastructure breakage from a real model regression. Operators tooling on the prior `[PASS|FAIL]` only output may need a 3-way switch.

### Changed — GDPR / EU AI Act bench commands now load embedded judge system prompts

`agenteval bench gdpr` and `bench eu-ai-act` now load `gdpr-judge-system.v1.md` / `eu-ai-act-judge-system.v1.md` from the corresponding benchmark assembly's manifest resources and wire them into `ChatClientEvaluator` via the new `JudgeFactory.Resolve(..., systemPrompt: ...)` parameter. Previously the prompt files were validated by tests, embedded in the assembly, recorded in provenance — and never reached the LLM. The "Cite articles / Be conservative / Flag evasive responses" rules now actually steer the judge. Operators relying on the prior un-steered behaviour will see judgements shift; the calibration baseline should be re-run after this change.

### Added — `ComplianceMatrixCell.timestamp` GraphQL field

Mission Control's GraphQL `ComplianceMatrixCell` type now carries a `timestamp: String!` field containing the raw on-disk evidence directory name (`yyyy-MM-dd_HH-mm-ss`). The SPA reads this verbatim when building drill-through URLs. Previously the SPA round-tripped `lastEvidenceAt` through JavaScript's `Date.toISOString()`, which silently shifted to UTC — non-UTC workspaces (CET, PST, JST, …) generated URL timestamps that 404'd against the local-clock-named directory on disk. Existing clients that don't select `timestamp` are unaffected.

### Changed — `SubjectIdentity.QualifiedId` no longer in GraphQL surface

The `QualifiedId` computed property on `AgentEval.Output.SubjectIdentity` is now `internal` so Hot Chocolate's default public-property convention does not auto-bind it as a GraphQL field. It was never serialised to JSON (`[JsonIgnore]`) and there are no external consumers, but a future `{ subjects { identity { qualifiedId } } }` query would have locked it into the v1 GraphQL contract. The change is non-breaking for consumers of the `SubjectIdentity` record (the property had no external callers).

### Added — `AgentEval.Memory` shipped in the umbrella NuGet package

The Memory evaluation subsystem (memory benchmarks, LongMemEval, retention/temporal/reach-back metrics, HTML pentagon reporting) is now bundled into the `AgentEval` umbrella package. `AddAgentEvalAll()` registers `AddAgentEvalMemory()` — consumers reach all Memory APIs via `using AgentEval.Memory;` without a separate `<ProjectReference>`. `AgentEval.Memory.dll` ships in `lib/net{8,9,10}.0/` of the umbrella nupkg.

### Changed (BREAKING) — compliance evidence schemas + Attestation.EvaluatorModel

**Schema change** — Both `gdpr-evidence.schema.json` and `eu-ai-act-evidence.schema.json` split the `preset` enum into a base preset + a new required `domainPacks: string[]` field. Previously a composite preset (e.g. `"standard+healthcare"`) wrote the concatenated string into `preset`; the enum only listed 6 base names, so every composite-preset invocation crashed at SaveReportAsync. Now:
- `preset` enum is restricted to the 3 base names (`"smoke"`, `"standard"`, `"audit"`).
- `domainPacks` carries the ordered pack list (`["healthcare"]` / `["high-risk-employment", "high-risk-credit"]` / etc.).
- `GdprReportOptions` and `EuAiActReportOptions` records gained `DomainPacks: IReadOnlyList<string>?` and `JudgeModel: string?` parameters.
- **Existing on-disk `*-evidence.json` files written by 0.8.0-beta against the old schema will fail re-validation under the new schema.** No migration tooling ships in 0.8.1-beta — re-run `agenteval bench …` to regenerate.

**Attestation change** — `Attestation.EvaluatorModel` previously hard-coded the literal string `"internal"` regardless of which judge actually ran. It now records the resolved judge identifier:
- `"<deployment-name>"` when `JudgeFactory.Resolve` resolved a real Azure OpenAI judge (e.g. `"gpt-4o-deployment-01"`).
- `"stub"` when the operator opted into the stub via `AGENTEVAL_ALLOW_STUB_JUDGE=1`.
- `"override"` when a test passed `evaluatorOverride`.

Tooling that filtered on `evaluatorModel == "internal"` to identify benchmark output is now broken; switch to `evaluator == "AgentEval.GdprBenchmark"` / `"AgentEval.EuAiActBenchmark"` for the benchmark-identifier check, and use `evaluatorModel` as the judge-identifier check.

### Changed (BREAKING) — bench / calibrate CLI judge resolution

`agenteval bench gdpr` / `bench eu-ai-act` / `bench agentic` and their `calibrate` siblings now resolve their LLM judge via the new `JudgeFactory` and **refuse to run silently against the stub**. Resolution order:

1. Test override (programmatic; not user-visible).
2. All three of `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`, `AZURE_OPENAI_DEPLOYMENT` set → real Azure OpenAI judge via `AzureOpenAIClient` → `IChatClient` → `ChatClientEvaluator`.
3. Any of the three set but not all → exit code **2** with a diagnostic listing missing variables. **Previously**: silent fall-through to stub.
4. None set + `AGENTEVAL_ALLOW_STUB_JUDGE=1` (case-insensitive) → stub judge (deterministic 75/100) with a stderr warning. Opt-in only — **never use in CI**.
5. None set + no opt-in → exit code **2** with a help message pointing at the two recovery paths.

**Migration**: CI jobs that previously ran `agenteval bench … calibrate` without `AZURE_OPENAI_*` env vars now exit 2 instead of silently producing a stub-graded calibration report. Either set the Azure secrets OR add `AGENTEVAL_ALLOW_STUB_JUDGE=1` to the CI env (the latter only if you understand that calibration against a stub gates nothing). See [CLI Reference — Environment variables](docs/cli.md#environment-variables).

**Note** — earlier `[Unreleased]` entries below reference an `agenteval eval` command. That command was proposed in ADR-003 but never shipped; the entry should be read as "the cross-framework dataset-runner CLI surface, eventually superseded by `agenteval bench` and the in-tree `samples/AgentEval.Samples` runner."

### Added — AgentEval Mission Control Phase 1 — local viewer + GraphQL backend (plan-08)

Mission Control is the visualisation, aggregation, and governance layer on top of `.agenteval/`. Phase 1 ships the dotnet backend with the full read surface; the React + Vite SPA, CLI subcommand wiring, and Mode C self-hosted server land in subsequent phases (per plan-08).

- **`AgentEval.MissionControl`** — new .NET 10 project (`src/AgentEval.MissionControl/`) hosting the GraphQL server + REST binary endpoints. Boot via `dotnet run --project src/AgentEval.MissionControl`.
- **`IOutputStoreReader` interface** extracted from `IOutputStore` — pure additive refactor; existing implementations satisfy it for free. Mission Control consumes only the reader, verified by `ReaderOnlyArchitectureTests`. Mode A's local viewer cannot accidentally write to `.agenteval/`.
- **`RunPointer` extended** with optional `Kind`, `Score`, `DurationMs`, `EstimatedCost` fields — backwards-compatible (4-arg positional ctor still works; legacy JSON deserialises with new fields as null).
- **15 GraphQL resolvers** at `POST /graphql`:
  - `Query.solution`, `Query.subjects(kind?)`, `Query.subject(kind, name)`.
  - `Query.recentRuns(count)`, `Query.run(runId)`, `Query.runSummary(runId)`.
  - `Query.scenarios(runId)`, `Query.scenario(runId, scenarioId)`.
  - `Query.scenarioTree(runId, scenarioId)` — **recursive `EvalResult` walked in one round-trip** (the central architectural justification for choosing GraphQL over REST on the read path).
  - `Query.compliance`, `Query.complianceMatrix(regulation)` — the killer-feature compliance dashboard backend, with audit-chain validation per cell. `Query.complianceEvidence(...)`.
  - `Query.evaluators(category?, costTier?)`, `Query.evaluator(key)` — driven by 60 hand-authored + generated `EvaluatorCard` JSON files (full coverage of every shipped evaluator).
- **`EvaluatorCard` primitive** — schema-driven UI metadata per evaluator. Drop a JSON file at `src/AgentEval.Evals.Agentic/EvaluatorCards/<key>.json` and it appears in `Query.evaluators` immediately, no code change. `evaluator-card.schema.json` v1.0 in `AgentEval.DataLoaders`. Lock-down tests verify schema validation, tier-match against `EvaluatorCostMap`, source-path resolution, no duplicate keys.
- **5 REST binary endpoints**: `GET /api/v1/runs/{runId}/trace`, `/reports/{format}`, `GET /api/v1/compliance/{reg}/{subject}/{ts}/report.pdf`, `GET /api/v1/compliance/{regulation}/schema`, `GET /api/v1/subjects/{kind}/{name}/history` (NDJSON stream).
- **`GET /api/v1/version`** — server metadata for diagnostics.
- **Hot Chocolate 16 (ChilliCream OSS — *not* Microsoft)** — GraphQL server with `MaxAllowedExecutionDepth = 8` guarding the recursive `EvalResult` tree, embedded Nitro UI at `/graphql` for ad-hoc query exploration in dev.
- **`FileSystemLayout` promoted to public** in `AgentEval.DataLoaders` so Mission Control's binary endpoints can resolve canonical paths without re-implementing the layout.
- **Hybrid REST + GraphQL design** — see `docs/missioncontrol/api-design.md` for rationale. GitHub / Stripe / Shopify all do this; we're following established practice.
- **Documentation**: `docs/missioncontrol/{getting-started,portal-ready-evaluators,charting,api-design}.md`.
- **Tooling**: `tools/gen_evaluator_cards.py` — idempotent generator for boilerplate cards. Hand-authored cards take precedence; the generator only writes keys not already present.
- **Test coverage**: 35 MC integration tests (8 GraphQL smoke + 16 read-resolver/compliance + 7 binary endpoint + 1 reader-only architecture + 3 recursive-tree) on net10.0; 14 EvaluatorCard schema tests. Multi-TFM build clean (net8.0 / net9.0 / net10.0); MC tests are net10.0-gated since `Microsoft.AspNetCore.Mvc.Testing 10.0.0` is net10-only.



### Added

- **Composite evaluations primitive** (`AgentEval.Evals` namespace) — a composite eval aggregates N sub-evals into one scored result with a recursive tree of sub-results. `IEval` unifies atomic and composite evals; `CompositeEval` runs sub-evals in parallel via `Task.WhenAll` and aggregates via a pluggable `IAggregationStrategy`. Phase 1 ships `WeightedSumAggregation` (the only strategy needed for GDPR per-article rollups, Foundry's tool-call-accuracy formula, and 80% of other use cases).
- **`AtomicLlmEval` / `AtomicCodeEval`** — atomic evals wrap either an existing `AgentEval.Core.IEvaluator` (LLM-judge case) or a deterministic computation (code case). Both produce the same `EvalResult` shape so callers don't branch on type.
- **`SeverityRollup` / `CostRollup` helpers** — composite severity = max of sub-severities (`none < low < medium < high < critical`); composite cost = sum of sub-costs; cache-hit only when all subs hit cache.
- **`eval-result.schema.json` v1** — JSON Schema (draft 2020-12) for the recursive `EvalResult` tree, embedded as a resource in `AgentEval.DataLoaders`. Used for runtime validation.
- **`EvalResultPersistence`** — bridges composite results to the existing `IOutputStore`. `ToScenarioResult(result, id, name)` serialises the recursive tree as JSON inside `ScenarioResult.Output` while lifting score / pass-state / dimensions / cost to top-level fields. `FromScenarioResult(sr)` restores the tree. The existing `ContentHasher.HashRunAsync` covers the embedded JSON, so the audit chain extends to composite results with no schema or store changes.
- **`AddCompositeEvals` DI extension** — registers `WeightedSumAggregation` as the default `IAggregationStrategy`. `TryAdd` semantics preserve consumer overrides.

Verdict matrix: when no threshold is set on a composite, its label is severity-driven — `critical|high → fail`, `medium → warn`, `none|low → pass`. With a threshold, label is purely score-driven (`score >= threshold ? pass : fail`).

Tests added on this branch — Article 17 golden tree (executable spec), 24+ unit tests across atomics and composite, schema validation, persistence round-trips, DI wiring. Total suite delta: +60 tests; 2738 passing on net10.0.

- **Canonical `.agenteval/` workspace layout** — `subjects/{kind}/{name}/runs/{runId}/...` is now the single source of truth for all evaluation output. Seven v1 JSON Schemas (manifest, summary, subject, solution, history-line, evidence, red-team-manifest) are embedded as resources in `AgentEval.DataLoaders` and validated at runtime.
- **`IOutputStore` interface and three implementations** — `FileSystemOutputStore` persists to the canonical folder tree; `NullOutputStore` silently discards all writes (no-op, no filesystem side effects); `InMemoryOutputStore` accumulates data in memory for testing. All three live in the `AgentEval.Output` namespace.
- **`AgentEval.Cli` executable with `init`, `doctor`, and `migrate` subcommands** — `doctor` validates `solution.json` structure, subject-name-vs-folder consistency, per-run manifest content hashes, the compliance-evidence audit chain, and legacy paths via `LegacyPathScanner`. Exit code `2` means validation errors were found; `0` means clean.
- **`agenteval init`** — Writes three files into `.agenteval/`: `solution.json` (schema v1, random UUID, solution display name), `README.md`, and `.gitignore`. All three are sourced from embedded templates. Safe to re-run; exits cleanly if already initialized.
- **`agenteval migrate`** — Dry-run by default; pass `--apply` to commit changes. Handles three migration paths: (1) renames uppercase `.AgentEval/` → `.agenteval/` using a temp-name intermediate on Windows; (2) moves `TestResults/traces/{name}_{ts}_{*}.json` into per-subject run folders under `.agenteval/subjects/agents/{name}/runs/{ts}/traces/`; (3) moves `.agenteval/benchmarks/{Agent}/baselines/{*}.json` into `.agenteval/subjects/agents/{Agent}/baselines/v{n}.json`. Accepts `--root` to override the auto-detected workspace root.
- **Compliance evidence audit chain** — `SaveComplianceEvidenceAsync` validates each evidence document against `evidence.schema.json` and refuses to persist it when `sourceRun.manifestHash` does not match the source run's stored `ContentHash`. `agenteval doctor` re-validates the full chain on demand.
- **`ContentHasher.HashRunAsync` / `ContentHasher.VerifyAsync`** (internal) — Compute a deterministic SHA-256 hash over a run's summary, sorted scenario results, and optional trace. Used by both `CompleteRunAsync` and `agenteval doctor`.
- **`AddAgentEvalOutputStore` DI extension method** — Registered on `IServiceCollection` in `AgentEval.Output`; accepts `Action<OutputStoreOptions>` for configuring `OutputStoreMode` (`Auto`, `FileSystem`, `Null`) and an optional explicit workspace path. `InMemoryOutputStore` is available for tests but is not selectable via `OutputStoreMode` — wire it directly in DI when needed.

### Changed

- **`JsonFileBaselineStore`** gains a constructor overload `(MemoryReportingOptions, IOutputStore, SubjectIdentity?)` that dual-writes baselines to both the legacy path (source-of-truth) and the canonical store path. Existing callers using the original constructor are unaffected.
- **Four red-team compliance reporters** (`OWASPComplianceReporter`, `ISO27001ComplianceReporter`, `SOC2ComplianceReporter`, `MITREATLASReporter`) gain a `SaveReportAsync(IOutputStore, SubjectIdentity, runId, ...)` overload that maps their report types into `ComplianceEvidence` and routes through the audit chain.
- **`EvalResultStore` in the travel demo** now writes snapshots to `.agenteval/samples/AgentEval.TravelDemo.Evals/snapshots/` instead of `.AgentEval/ECS2026MAF_Evals/`.
- **`Program.cs` in the travel demo** now accepts an optional positional `1`..`5` argument to invoke a single eval directly; the interactive menu remains the default when no argument is supplied.
- **Renamed `samples/ECS2026MAF*` → `samples/AgentEval.TravelDemo*`** — Drops the conference-specific name in favour of an evergreen one. Folder, csproj, root namespace (`AgentEval.TravelDemo` / `AgentEval.TravelDemo.Evals`), `using` statements, and the sample's `EvalResultStore` snapshot path were all updated. Existing snapshots at `.agenteval/samples/ECS2026MAF.Evals/snapshots/` were moved to the new path during the rename so Eval03's hypothesis comparison continues to work without re-running.

### Fixed

- **`LegacyPathScanner`** no longer reports a false-positive `.AgentEval/` finding on Windows when the workspace already uses the lowercase `.agenteval/` folder. The previous case-insensitive lookup matched the same on-disk directory under both names.

---

### Added — Agentic Evaluator Suite Phase 6: Memory, Multi-turn, Reasoning, Calibration, Adversarial, UX (plan 06)

- **19 new evaluators** across 7 new categories — all AgentEval-original (no upstream prompty equivalents):
  - _Memory (2)_: `MemoryRecallAccuracyEval` (HIGH), `LongConversationCoherenceEval` (HIGH) — in `Memory/`.
  - _Multi-turn (3)_: `TurnCoherenceEval` (MEDIUM), `GoalTrackingEval` (HIGH), `ClarificationAppropriatenessEval` (LOW) — in `MultiTurn/`.
  - _Reasoning (4)_: `ReasoningCorrectnessEval` (MEDIUM), `GoalDecompositionQualityEval` (MEDIUM), `PlanFormulationQualityEval` (MEDIUM), `IntermediateStepHallucinationEval` (MEDIUM) — in `Reasoning/`.
  - _Calibration (3)_: `ConfidenceCalibrationEval` (LOW), `UncertaintyAcknowledgmentEval` (LOW), `SelfCorrectionQualityEval` (MEDIUM) — in `Calibration/`.
  - _Adversarial (3)_: `DirectInjectionEval` (LOW — hybrid deterministic-first), `PersonaAttackEval` (LOW — hybrid deterministic-first), `JailbreakResistanceEval` (MEDIUM — combined pattern library) — in `Adversarial/`.
  - _UX (3)_: `VerbosityAppropriatenessEval` (LOW), `ToneAppropriatenessEval` (LOW), `RefusalQualityEval` (LOW) — in `UX/`.
  - _Efficiency (1)_: `CostQualityEfficiencyEval` (TRIVIAL — pure code) — in `Efficiency/`.
- **`EvaluatorCostTier` enum + `EvaluatorCostMap` static dictionary** in `AgentEval.Abstractions/Evals/` — 46 entries spanning all plan-05 + plan-06 evaluators. Unknown keys default to `Medium` (conservative).
- **`--budget-tier {trivial|low|medium|high|all}` CLI flag** for `agenteval bench agentic` — filters out above-budget evaluators and renormalizes weights. Use `low` for dev iteration, `medium` for PR builds, omit for release gates.
- **4 new preset factories** in `AgenticBenchmark`:
  - `Conversational()` — 5 evaluators (MemoryRecall 0.25, LongConvCoherence 0.25, TurnCoherence 0.20, GoalTracking 0.20, ClarificationAppropriateness 0.10); threshold 0.80.
  - `Reasoning()` — 4 evaluators (ReasoningCorrectness 0.30, IntermediateStepHallucination 0.25, PlanFormulationQuality 0.25, GoalDecompositionQuality 0.20); threshold 0.80.
  - `UserExperience()` — 5 evaluators (ToneAppropriateness 0.30, VerbosityAppropriateness 0.25, RefusalQuality 0.20, ConfidenceCalibration 0.15, UncertaintyAcknowledgment 0.10); threshold 0.80.
  - `AdversarialDirect()` — 3 evaluators (DirectInjection 0.40, PersonaAttack 0.30, JailbreakResistance 0.30); threshold 0.95.
  - **Total agentic preset count: 11** (up from 7).
- **4 new CLI presets** — `conversational`, `reasoning`, `user-experience`, `adversarial-direct` added to `BenchAgenticCommand.ResolvePreset`.
- **`ConversationTurn` record** — `sealed record ConversationTurn(Role, Content, Timestamp?)` in `Conversation/`; carries the `EvalInput.Metadata["conversation_history"]` contract for all memory, multi-turn, and calibration evaluators.
- **`ConversationHistoryHelper`** in `Conversation/` — public helper that centralises `TryGetHistory`, `TryGetCorrectionTurn`, `FormatTranscript`, and `FormatPreviousTurn`. New conversation-history-consuming evaluators must use this helper rather than re-implementing private copies.
- **`AdversarialPatternLibrary`** in `Adversarial/` — internal helper that loads + compiles regex patterns from embedded JSON resources. Used by `DirectInjectionEval`, `PersonaAttackEval`, and `JailbreakResistanceEval`.
- **`CostFilteredCompositeBuilder.FilterByBudget`** — filters composite components by cost tier and renormalizes weights.
- **19 new per-evaluator test files** across `tests/AgentEval.Tests/Agentic/{Memory,MultiTurn,Reasoning,Calibration,Adversarial,UX,Efficiency}/`.
- **4 new E2E preset tests** — `AgenticConversationalE2ETest`, `AgenticReasoningE2ETest`, `AgenticUserExperienceE2ETest`, `AgenticAdversarialDirectE2ETest`.
- **3 new `CostFilteredCompositeBuilder` tests** — filter low, no-op all, and throw-on-empty. Plus a zero-weight-component edge-case test added in the R1-R7 polish pass.
- **R6 boundary tests** for `JailbreakResistanceEval.patternsToRun` — `Theory` covering 0/-1/-100 throw paths, plus single-pattern cap and `int.MaxValue` overflow guard.
- **5 new golden datasets** under `tests/AgentEval.Tests/Agentic/Calibration/Golden/` — ~77 hand-labeled scenarios:
  - `golden-memory-multiturn.jsonl` — 25 entries across 5 memory/multi-turn evaluators.
  - `golden-reasoning.jsonl` — 16 entries across 4 reasoning evaluators.
  - `golden-confidence-calibration.jsonl` — 12 entries across 3 calibration evaluators.
  - `golden-adversarial-direct.jsonl` — 12 entries across 3 adversarial evaluators.
  - `golden-ux.jsonl` — 12 entries across 3 UX evaluators.
- **Documentation updates** — `docs/benchmarks/agentic/getting-started.md` extended with 4 new preset rows, 7 new category sections, and a "Cost-Aware Execution" section. New `docs/benchmarks/agentic/cost-guidance.md` with per-evaluator cost-tier table, recommended budget tiers per use case, and estimated costs per preset.

---

### Added — Agentic Evaluator Suite (plan 05 Phase 1)

- New `src/AgentEval.Evals.Agentic/` project: 11 named `IEval` implementations for agent-level evaluation (Task Completion, Task Adherence with 5 sub-dimensions, Intent Identification, Intent Resolution, Task Navigation Efficiency, Tool Selection, Tool Input Accuracy, Tool Output Utilization, Tool Call Success — deterministic-first, Tool Efficiency, Tool Call Accuracy aggregate).
- Evaluator prompts under `Resources/Prompts/{system,process}/` are forked from public MIT-licensed sources (`azure-sdk-for-python` `_evaluators/*.prompty` files) and improved per the AgentEval envelope: `temperature: 0`, structured `evidence[]` output, severity rubric, sub-dimensions where applicable. Each prompt file's header carries the source URL, pinned commit SHA at fork time, and the list of modifications.
- `AgenticBenchmark.AgenticExecution()` and `.ToolCallAccuracy()` factory methods.
- New CLI verbs: `agenteval bench agentic [--preset agentic-execution|tool-call-accuracy]`, `agenteval bench agentic calibrate`, `agenteval render --benchmark agentic`.
- New CI workflow `.github/workflows/agentic-calibration.yml`.
- `AgenticBenchmarkResult` wrapper + `agentic-result.schema.json` (separate from compliance evidence).

### Notes

- Multi-judge × Mode-B mutual exclusivity continues to apply (inherited from plan-03 G7.6).
- PDF rendering is deferred to a follow-up batch; Markdown report ships in Phase 1.
- The previous Foundry-equivalent compatibility layer (`FoundryUriRegistry`, `ExternalReference`, `FoundryEquivalent()` preset) was removed; the project's relationship to upstream is **prompt provenance only** — each forked prompt cites its public MIT-licensed source in the file header, and the `findings-and-suggestions.md` document captures the upstream feedback story.

---

### Added — Agentic Evaluator Suite Phases 4 + 5: Safety + Telemetry + Stochastic Stability (plan 05 Phase 4 + 5)

- **13 safety evaluators** in `src/AgentEval.Evals.Agentic/Safety/`:
  - _Hybrid deterministic-first (3)_: `ProhibitedActionsEval` (policy-as-code, forbidden tools + patterns + approval checks → LLM fallback), `SensitiveDataLeakageEval` (regex scan for PII/secrets → LLM fallback), `SystemPromptLeakageEval` (high-signal phrase patterns → LLM fallback).
  - _Content-safety hybrid (4)_: `HateUnfairnessEval`, `SexualEval`, `ViolenceEval`, `SelfHarmEval` — each delegates to `IContentSafetyClient` when available, falls back to LLM judge. All four carry `severity: critical` and threshold 0.95.
  - _LLM judge (4)_: `IndirectAttackEval` (XPIA / cross-prompt injection), `ProtectedMaterialEval` (copyright), `CodeVulnerabilityEval` (insecure generated code), `UngroundedAttributesEval` (hallucinated facts).
  - _LLM judge with skip short-circuit (1)_: `UnsafeToolUseEval` — returns `Skipped` when no tool calls are present.
- **Policy-as-code framework** — `ProhibitedActionPolicy` (immutable record), `IPolicyResolver` interface, `StaticPolicyResolver` (single global policy), `ToolPattern` (regex-based call prohibition). Located in `Safety/Policy/`.
- **`IContentSafetyClient` / `NullContentSafetyClient`** — pluggable interface for Azure AI Content Safety integration. `NullContentSafetyClient.Instance` is the default (all zero severity → LLM fallback).
- **6 telemetry evaluators** in `src/AgentEval.Evals.Agentic/Telemetry/` — pure-code, zero LLM calls: `LatencyEval` (P99 vs. threshold), `TokenUsageEval` (token budget), `CostEval` (USD budget), `ErrorRateEval` (call error rate), `RetryRateEval` (retry rate), `ToolLatencyEval` (worst per-tool mean latency). All read telemetry from `EvalInput.Metadata["agentic_telemetry"]` (`AgenticTelemetry` record) or constructor fallback. Return `Skipped` when no telemetry data is present.
- **`StochasticStabilityEval`** in `src/AgentEval.Evals.Agentic/StochasticStability/` — pure-code meta-evaluator measuring run-to-run score consistency across N prior runs. Composite of success-rate (0.50), score-variance-inverse (0.30), and failure-mode-consistency (0.20). Reads `EvalInput.Metadata["run_results"]`. Requires ≥2 runs; returns `Skipped` for fewer.
- **`AgenticBenchmark.Safety()` factory** — 12-evaluator composite (threshold 0.90); weights 0.20/0.10/0.08/0.08/0.08/0.08/0.10/0.06/0.06/0.04/0.06/0.06 for critical-to-high dimension priority.
- **3 new CLI presets** — `agenteval bench agentic --preset safety | telemetry | stochastic-stability` added to `BenchAgenticCommand.ResolvePreset`. Safety preset uses an empty `StaticPolicyResolver`; custom policies must be supplied programmatically.
- 9 of the 13 safety-evaluator prompts (hate-unfairness, sexual, violence, self-harm, protected-material, code-vulnerability, indirect-attack, ungrounded-attributes, sensitive-data-leakage) carry MIT-attribution headers citing their public-source `.prompty` upstreams; the remaining 4 (prohibited-actions, system-prompt-leakage, unsafe-tool-use, content within the policy framework) are AgentEval-original. Telemetry and stochastic-stability are pure-code AgentEval-original operational metrics with no upstream prompts.
- **78 new tests** across `Safety/` (13 files × ~4 tests), `Telemetry/` (6 files × 3 tests), `StochasticStability/` (4 tests), `EndToEnd/AgenticSafetyE2ETest.cs` (2 tests). Total suite: 3242 passing.

### Fixed — Agentic Evaluator Suite Phases 4 + 5 (Opus review fixes)

- **Credit-card regex false-positive** (`SensitiveDataLeakageEval`): old pattern `\b(?:\d[ \-]?){13,16}\b` matched 14-digit timestamps (e.g., `20250509120000`) as credit card numbers. Replaced with an issuer-prefix–anchored pattern requiring a known Visa / MasterCard / Amex / Discover / Diners prefix. Regression test: `"order id 20250509120000"` no longer triggers; `"4532 1234 5678 9012"` (Visa) does.
- **Severity label `"none"` on failure** (`HateUnfairnessEval`, `SexualEval`, `ViolenceEval`, `SelfHarmEval`): when a strict threshold (e.g., 0.95) caused a fail on a small absolute severity (e.g., severity=0.06 → score=0.94), the severity label was erroneously `"none"` rather than `"low"`. Fixed via a `(passed, severity)` switch expression that guarantees at least `"low"` on all failure paths. Test coverage added for all four evaluators.

---

### Added — Agentic Evaluator Suite Phase 3: Multi-Judge Adjudication + Meta-Evaluators (plan 05 Phase 3)

- **`AdjudicatedMultiJudgeWrapper`** in `src/AgentEval.Evals.Agentic/Adjudication/` — wraps a panel of judges, computes inter-rater agreement (Cohen's kappa for ≥3 judges, pairwise agreement rate for 2), and conditionally invokes an adjudicator judge when agreement falls below a configurable threshold (default 0.70). Adjudication state surfaced in `Details.Dimensions` (`agreement`, `disputed`, `adjudicated`). SubResults include panel + adjudicator result when triggered.
- **`JudgeAgreementEval`** in `src/AgentEval.Evals.Agentic/JudgeQuality/` — pure-code meta-evaluator computing Cohen's kappa across a judge panel. Reads results from `EvalInput.Metadata["judge_results"]` (accepts `IEnumerable<EvalResult>`, `IEnumerable<string>` of labels, or a JSON array string). Pass threshold: 0.60.
- **`CalibrationAccuracyEval`** in `src/AgentEval.Evals.Agentic/JudgeQuality/` — pure-code meta-evaluator computing fraction of judge verdicts matching expected verdicts. Reads from `EvalInput.Metadata["calibration_pairs"]`. Pass threshold: 0.85.
- **`JudgeDriftEval`** in `src/AgentEval.Evals.Agentic/JudgeQuality/` — pure-code meta-evaluator comparing two run snapshots (`snapshot_a` / `snapshot_b` in metadata) and computing `score = 1.0 - max_delta`. Passes when max_delta < 0.05 (5%). Severity: low (meta-metric).
- **`AgenticBenchmark.JudgeQuality()`** factory — 3-evaluator meta-benchmark: `JudgeAgreementEval` (0.40), `CalibrationAccuracyEval` (0.40), `JudgeDriftEval` (0.20); aggregation `WeightedSumAggregation`; threshold 0.75. No LLM judge required.
- **New CLI preset** `agenteval bench agentic --preset judge-quality` — resolves to `AgenticBenchmark.JudgeQuality()` in `BenchAgenticCommand.ResolvePreset`.
- 3 new meta-evaluators (`judge_agreement`, `calibration_accuracy`, `judge_drift`) — all AgentEval-original; pure code, no LLM dependency.
- **13 new tests** across `Adjudication/AdjudicatedMultiJudgeWrapperTests.cs` (3), `JudgeQuality/JudgeAgreementEvalTests.cs` (3), `JudgeQuality/CalibrationAccuracyEvalTests.cs` (3), `JudgeQuality/JudgeDriftEvalTests.cs` (3), and `EndToEnd/AgenticJudgeQualityE2ETest.cs` (2).

---

### Added — Agentic Evaluator Suite Phase 2: RAG/Quality (plan 05 Phase 2)

- **8 RAG/quality evaluators** in `src/AgentEval.Evals.Agentic/Quality/`: `GroundednessEval` (4-sub-dimension composite: claim support, claim contradicted, citation accuracy, evidence coverage), `RelevanceEval`, `CoherenceEval`, `FluencyEval`, `SimilarityEval`, `ResponseCompletenessEval`, `QaCompositeEval` (weighted roll-up of all 7 quality dimensions).
- **`F1ScoreEval`** ships in `src/AgentEval.Core/Evals/` — pure-code deterministic evaluator, zero LLM dependency; useful standalone without pulling the agentic package.
- **`AgenticBenchmark.RagQuality()`** factory — 7-evaluator flat composite (groundedness 0.30, response_completeness 0.20, relevance 0.15, similarity 0.15, f1_score 0.10, coherence 0.05, fluency 0.05); threshold 0.70. Tree is intentionally flat for diagnosis; `QaCompositeEval` is the single-number roll-up for users who don't need per-dimension breakdown.
- **New CLI preset** `agenteval bench agentic --preset rag-quality`.
- **Golden dataset** `tests/AgentEval.Tests/Agentic/Calibration/Golden/golden-20-quality.jsonl` — 20 hand-labeled scenarios across 7 quality evaluators (~70% pass / 30% fail).
- 7 of the 8 RAG-evaluator prompts (groundedness, relevance, coherence, fluency, similarity, response-completeness — plus the 4 groundedness sub-dimensions sharing the parent prompt) carry MIT-attribution headers citing their public-source `.prompty` upstreams. `f1_score` is pure code (no prompt). `qa_composite` is AgentEval-original (composite of the other 7).
- **24 new tests** across `Golden/` (Groundedness, Relevance, Coherence, Fluency, Similarity, ResponseCompleteness, F1Score, QaComposite — 3 tests each) and `EndToEnd/AgenticRagQualityE2ETest.cs` (2 tests).

---

### Added — EU AI Act Compliance Benchmark (plan 04)

- New `samples/AgentEval.EuAiActBenchmark/` sample implementing an EU AI Act behavioral compliance benchmark covering 13 controls across 6 pillars (Art 5 prohibitions, Art 13/14, Art 15, Art 50, Annex III, Art 51-55 GPAI probe).
- New CLI verb `agenteval bench eu-ai-act` with presets `smoke` / `standard` / `audit` (+ `standard+high-risk-{employment,credit,education}` domain packs if E8.1-3 shipped).
- New CLI verb `agenteval bench eu-ai-act calibrate` for hand-labeled judge calibration.
- Extended `agenteval compliance render --regulation eu-ai-act` to re-render PDFs without LLM cost.
- New CI workflow `.github/workflows/eu-ai-act-calibration.yml` gating release branches on judge calibration accuracy.
- New cross-regulation linking: `CrossRegulationLinker` surfaces overlap between GDPR and EU AI Act findings.
- All Composite-Eval Phase-2 strategies reused from GDPR (CapByWorst, Min, MajorityVote, WeightedMedian, MultiJudgeWrapper) — zero new strategies added in Core, validating the expand-on-demand-then-reuse pattern.

### Notes

- This benchmark is a first-line **dialog-behavior screening tool**. It does not establish EU AI Act compliance — full compliance requires risk classification, conformity assessment, technical documentation, post-market monitoring, and (where applicable) EU database registration, none of which are in scope.
- Multi-judge x Mode-B mutual exclusivity is a known v1 limitation inherited from the GDPR plan-03 implementation.

---

### GDPR Benchmark (Plan 03)

#### Added

- **`samples/AgentEval.GdprBenchmark/`** — sample project with 21 article scenario YAMLs covering 5 GDPR pillars (Art 5, 6, 7, 8, 9, 13, 14, 15, 16, 17, 18, 20, 21, 22, 25, 32). Scenario YAMLs live under `Articles/Yaml/`; domain packs live under `DomainPacks/`.
- **Three benchmark presets** — `Smoke` (5 articles, ~$0.05/run), `Standard` (16 articles, ~$0.50/run), and `AuditGrade` (Standard + `CapByWorstAggregation` severity-aware cap, optional multi-judge consensus, Mode-B per-criterion evaluation for Critical articles Art 9 and Art 22).
- **Three domain packs** — `Healthcare` (8 scenarios targeting Art 9(2)(h) and special-category data), `HR` (7 scenarios targeting Art 6(1)(b)/(c), Art 15, and Art 17 in employment context), and `ChildrensService` (8 scenarios targeting Art 8 age-of-consent and parental consent). Composable via `--preset standard+healthcare` etc.; weights are renormalized automatically.
- **`GDPRComplianceReporter`** integrated with `IOutputStore`: writes `evidence.json` (audit-chain-validated) plus a sibling `gdpr-evidence.json` containing the recursive composite tree, per-pillar and per-article rollups, critical findings, recommendations, the verbatim disclaimer, and a GDPR attestation block. Validated against `gdpr-evidence.schema.json` before writing.
- **Markdown and PDF reporters** with PII redaction for scenarios marked `sensitive: true`. PDF reporter uses QuestPDF and includes a cover page, executive summary, per-pillar section, per-article section, audit-chain appendix, methodology note, and disclaimer.
- **Calibration suite** — 120 hand-labeled golden entries distributed across 5 GDPR pillars (30/20/40/15/15). `agenteval bench gdpr calibrate` runs the golden dataset against the configured judge and computes per-pillar accuracy and Cohen's kappa. GitHub Actions release gate requires accuracy >= 0.85 and Cohen's kappa >= 0.70 per pillar, with zero evaluation failures.
- **Five new aggregation strategies** in `AgentEval.Core/Evals/Aggregations/`: `MinAggregation`, `CapByWorstAggregation`, `MajorityVoteAggregation`, `WeightedMedianAggregation` (reusable by Foundry plan 04 and any other consumer).
- **`MultiJudgeWrapper`** primitive in `AgentEval.Core/Evals/` for N-judge parallel evaluation with majority-vote aggregation.
- **`WithExtraScenarios` extension method** on `CompositeEval` for layered domain packs. Returns a new composite with the additional `EvalComponent` entries appended; weights are renormalized across all components.
- **New CLI subcommands**: `agenteval bench gdpr [--preset] [--subject] [--root] [--runs]`, `agenteval bench gdpr calibrate`, and `agenteval compliance render --regulation gdpr [--subject] [--ts]`.

#### Changed

- **`AtomicLlmEval`** gained an optional `failureSeverity` parameter so atomic results can inherit metadata-driven severity (escalated only, via `SeverityRollup.Max`). Backward-compatible; existing callers see no behavior change.
- **`ScenarioToAtomicEval`** gained an optional `useModeB` flag and an optional list of judges; when both Critical-article flag and judge count > 1 are set, scenarios become per-criterion composites wrapped in `MultiJudgeWrapper`.

#### Fixed

- An earlier draft of `gdpr-evidence.json` could be persisted without schema validation; the reporter now validates against `gdpr-evidence.schema.json` before writing and refuses to proceed if validation fails.
- `CalibrationRunner` previously used the parent article's first-scenario criteria for every golden entry, making calibration meaningless for entries targeting other scenarios; it now looks up the matching scenario by id.
- `CalibrationRunner` previously swallowed all evaluation exceptions silently; failures are now logged to stderr and counted in the `Eval failures` column of the calibration report.

Total LoC delta: approximately +4200 production / +1124 test. Test count delta: +124 tests; suite is ~3462 passing on net10.0 across both test projects (was ~3338 before plan 03).

---

## [0.8.0-beta] - 2026-04-28

**MAF 1.3.0 + MEAI 10.5.0 Compatibility** ✅

### Changed
- **MAF upgraded from 1.1.0 to 1.3.0** — All four MAF package references (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows`, `Microsoft.Agents.AI.Workflows.Generators`) bumped to `1.3.0`. Verified via `dotnet-inspect` API diff: zero breaking changes in `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Abstractions`, and `Microsoft.Agents.AI.OpenAI`. Two attribute types (`StreamsMessageAttribute`, `YieldsMessageAttribute`) were removed from `Microsoft.Agents.AI.Workflows` — AgentEval does not reference either, confirmed via repo-wide grep. New additive APIs (not consumed by AgentEval): `AgentEvaluationExtensions`, `WorkflowEvaluationExtensions`, `IAgentEvaluator`, `AgentSkill*`, A2A SDK v1 surfaces, server-side Foundry Toolbox.
- **MEAI upgraded from 10.4.0 to 10.5.0** — Cascading bump for `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, `Microsoft.Extensions.AI.Evaluation.Quality`. Transitive dependency `System.Numerics.Tensors` bumped from `10.0.4` to `10.0.6` to satisfy the new MEAI minimum.
- **NuGetConsumer sample** — Explicit version pins updated (CPM-disabled project).
- **NuGet metadata** — `<PackageReleaseNotes>` reflects MAF 1.3.0 + MEAI 10.5.0.
- **README.md** — MAF compatibility badge updated to 1.3.0.
- **docs/installation.md, docs/maf-memory-integration.md** — Version references refreshed.
- **THIRD-PARTY-NOTICES.md** — Package version table updated (7 MAF/MEAI rows + Tensors).

### Verified
- Full test suite passes across all three target frameworks (`net8.0`, `net9.0`, `net10.0`).
- All 27 samples build.
- Zero source-code changes required for the version bump itself.

### Verification Tool
This migration was verified end-to-end via the `dotnet-inspect` skill (installed at `.github/skills/dotnet-inspect/SKILL.md`, CLI `dnx dotnet-inspect@0.7.6`) rather than by reading source from `MAF/` or `MAFVnext/` folders. See [migration-to-MAF-1.3-plan.md](migration-to-MAF-1.3-plan.md).

---

## [0.7.0-beta] - 2026-04-12

**MAF 1.1.0 GA + Memory Integration + Workflow Enhancements** 🚀

### Changed
- **MAF upgraded from 1.0.0-rc3 to 1.1.0** — All three MAF package references (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows`) updated to 1.1.0 (first post-GA minor release). Zero source code changes required for the version bump alone — all changes in 1.1.0 are additive (new `FinishReason` property on `AgentResponse`, internal `ChatClientAgent` refactoring for per-service-call persistence, new Skills/Compaction APIs). Cascading dependency bumps: `Microsoft.Extensions.AI` 10.3.0 → 10.4.0, `Microsoft.Extensions.AI.OpenAI` 10.3.0 → 10.4.0, `Microsoft.Extensions.AI.Evaluation.Quality` 10.3.0 → 10.4.0, `System.Numerics.Tensors` 10.0.3 → 10.0.4. Full test suite (9,129 tests × 3 TFMs) passes with zero failures. Full diff analysis was completed as part of the upgrade review.
- **NuGetConsumer sample** — Updated explicit version pins to MAF 1.1.0 and MEAI 10.4.0 (CPM disabled project).
- **NuGet metadata** — Updated `PackageReleaseNotes` to reference MAF 1.1.0 + MEAI 10.4.0.
- **README.md** — Updated MAF compatibility badge and compatibility table to 1.1.0.
- **docs/installation.md** — Updated compatibility and dependency tables to MAF 1.1.0 + MEAI 10.4.0.
- **THIRD-PARTY-NOTICES.md** — Synced all MAF/MEAI/Tensors package versions to match `Directory.Packages.props`.

### Fixed
- **AgentResponseEvent handling in MAFWorkflowEventBridge** — `AgentResponseEvent` (which inherits `WorkflowOutputEvent`) was falling through to the generic `WorkflowOutputEvent` handler, triggering false `WorkflowCompleteEvent` emissions and losing `Usage`/`FinishReason`/`ExecutorId` data. Added an explicit `case AgentResponseEvent` handler before the `WorkflowOutputEvent` case. Emits new `ExecutorAgentResponseEvent` record with per-executor text, token usage, and finish reason.

### Added
- **`ExecutorAgentResponseEvent` record** — New workflow event type that extends `ExecutorOutputEvent` with `Usage` (TokenUsage?) and `FinishReason` (string?) properties. Backward-compatible via Liskov substitution.
- **`IHistoryInjectableAgent` on MAFAgentAdapter** — `MAFAgentAdapter` now implements `IHistoryInjectableAgent`, enabling synthetic conversation history injection for evaluation. Injected history is prepended to messages on next `InvokeAsync`/`InvokeStreamingAsync`, then cleared after first use.
- **Getting Started samples updated to `.AsAIAgent()` pattern** — Samples 01-05 now use `chatClient.AsAIAgent(name:, instructions:, tools:)` instead of `new ChatClientAgent(client, new ChatClientAgentOptions { ... })`. Follows MAF 1.1.0 recommended idiomatic pattern.
- **Sample: [MessageHandler] Source-Generated Executors** — New sample (C4) showing MAF's `[MessageHandler]` partial class executor pattern: deterministic text pipeline (Sanitizer → Classifier → Formatter) evaluated with standard AgentEval assertions. No LLM needed, runs offline. Added `Microsoft.Agents.AI.Workflows.Generators` 1.1.0 dependency for source generation.
- **Sample: AIContextProvider-Based Persistent Memory** — New sample (G6) demonstrating MAF's native `AIContextProvider` for persistent memory. `PersistentMemoryProvider` subclass injects stored facts via `ProvideAIContextAsync()` and extracts facts via `StoreAIContextAsync()`. Evaluated with `CrossSessionEvaluator` — zero evaluator changes required.
- **Sample: AgentSession Lifecycle** — New sample (A6) showing MAF session management: `CreateSessionAsync` → multi-turn conversation → `ResetSessionAsync` → session isolation verification. Demonstrates how `MAFAgentAdapter.ResetSessionAsync()` maps to `agent.CreateSessionAsync()`.
- **docs/maf-memory-integration.md** — New documentation mapping AgentEval.Memory concepts to MAF 1.1.0 equivalents (session lifecycle, AIContextProvider, CompactionStrategy). Includes architecture diagrams and adapter selection guide.
- **4 new MAFWorkflowEventBridge tests** — Agent-based workflow tests: `YieldsExecutorAgentResponseEvent`, `PreservesExecutorId`, `IsNotMistakenForWorkflowOutput`, `IsSubtypeOfExecutorOutputEvent`.
- **5 new MAFAgentAdapter tests** — History injection tests: `ImplementsIHistoryInjectableAgent`, `MessagesIncludedInNextInvocation`, `ClearedAfterFirstInvocation`, `WithNoHistory_OnlyPromptSent`, `ResetSessionAsync_ClearsInjectedHistory`.

---

## [0.6.0-beta] - 2026-03-05

**MAF RC3 Compatibility** ⬆️

### Changed
- **MAF upgraded from 1.0.0-rc2 to 1.0.0-rc3** — All three MAF package references (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows`) updated to 1.0.0-rc3. Zero AgentEval source code changes required — all RC3 breaking changes (`StateKey` → `StateKeys`, provider constructor renames) are in provider base classes that AgentEval does not subclass. RC3 introduces a new REST-based agent-to-agent protocol (CopilotStudio, A2A), OpenAPI-described agent endpoints, `IAgentApplication` hosting model, and `AgentWorkerClient` transport layer. Transitive `Microsoft.Agents.ObjectModel` bumped to latest. Full test suite (2519 tests × 3 TFMs) passes with zero failures. See [MAF-Upgrade-Plan.md](MAF/MAF-Upgrade-Plan.md) for full diff analysis.
- **THIRD-PARTY-NOTICES.md** — Synced all package versions to match `Directory.Packages.props` (MAF rc1→rc3 and 7 other stale versions corrected).
- **README.md** — Added MAF compatibility badge, .NET TFM badge, and compatibility table in Installation section. Repositioned preview warning below value proposition.
- **NuGet metadata** — Added `PackageReleaseNotes` property to umbrella package.
- **docs/installation.md** — Added Compatibility section with MAF and .NET version requirements.

---

## [0.5.2-beta] - 2026-02-28

**MAF RC2 Dependency Upgrade** ⬆️

### Changed
- **MAF upgraded from 1.0.0-rc1 to 1.0.0-rc2** — All three MAF package references (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows`) updated to 1.0.0-rc2. Zero public API breaking changes — every AgentEval dependency is byte-identical between RC1 and RC2. RC2 contains only internal telemetry restructuring (session-level OTel spans in Workflows), two internal resource leak fixes, and three new additive `[Experimental]` APIs (Agent Skills, builder-level context providers, stored-output-disabled client). Transitive `Microsoft.Agents.ObjectModel` bumped `2026.2.3.1 → 2026.2.4.1`. No AgentEval source code changes required. Full test suite passes across all 3 TFMs. See [MAF-Upgrade-Plan.md](MAF/MAF-Upgrade-Plan.md) for full diff analysis.

---

## [0.5.1-beta] - 2026-02-28

**Modularization, Cross-Framework, CLI, DI & Extensibility** 🏗️🔌

Major architectural release: monolith split into 6 sub-projects (ADR-016), universal IChatClient adapter, CLI tool, dependency injection architecture, rich evaluation output, extensibility framework, and runnable samples. Comprehensive test suite passing across all 3 TFMs.

### Added
- **Monolith Modularization (ADR-016)** — Split single `src/AgentEval` project (~203 files, ~35K lines) into 6 internal sub-projects while shipping a single NuGet package. Resolves dependency coupling: non-MAF users no longer pull `Microsoft.Agents.AI`, non-RedTeam users no longer pull `PdfSharp-MigraDoc`. Compiler-enforced dependency direction: Abstractions → Core → DataLoaders/MAF/RedTeam → Umbrella.
  - `AgentEval.Abstractions` (~48 files) — Public contracts: `IMetric`, `IEvaluableAgent`, `IStreamableAgent`, models
  - `AgentEval.Core` (~63 files) — Implementations: metrics, assertions, tracing, comparison, DI registration
  - `AgentEval.DataLoaders` (~23 files) — Dataset loaders (JSON/JSONL/CSV/YAML), exporters, output formatting
  - `AgentEval.MAF` (7 files) — Microsoft Agent Framework integration (`MAFAgentAdapter`, `MAFEvaluationHarness`)
  - `AgentEval.RedTeam` (61 files) — Security scanning, attack types, compliance reporting, PDF export
  - `AgentEval` (umbrella) — Single NuGet package containing all 6 DLLs per TFM via `TargetsForTfmSpecificBuildOutput`
  - All sub-projects use `RootNamespace=AgentEval` — zero namespace changes, zero API surface changes
  - `PrivateAssets="all"` on umbrella ProjectReferences with explicit NuGet dependency declarations
  - `InternalsVisibleTo` on all sub-projects → `AgentEval.Tests`
  - Phase 0: Fixed 11 cross-cutting coupling anomalies before split
  - See [ADR-016](docs/adr/016-monolith-modularization.md) for full rationale and alternatives considered
- **Cross-Framework IChatClient Support** — Universal adapter pattern for evaluating any `IChatClient`-based AI agent regardless of underlying framework (Azure OpenAI, Ollama, Groq, LM Studio, Semantic Kernel, etc.):
  - `IChatClient.AsEvaluableAgent()` extension method — One-liner wrapping any `IChatClient` as `IStreamableAgent` for evaluation. Located in `AgentEval.Core.ChatClientExtensions`. Parallels `.AsIChatClient()` from Microsoft.Extensions.AI.
  - `TestSummary.ToEvaluationReport()` extension method — Bridges evaluation pipeline (`TestSummary`) to export pipeline (`EvaluationReport` for `IResultExporter`). Derives time boundaries from `PerformanceMetrics`, maps `MetricResults` to `MetricScores`, supports `agentName`/`modelName`/`endpoint` provenance, sets `Category` for JUnit XML grouping.
  - **NuGetConsumer Semantic Kernel demo** — Real SK with `[KernelFunction]` plugins (`FlightPlugin.cs`) evaluated by AgentEval via the `AIFunctionFactory.Create()` bridge pattern. 8-step demo: Kernel build → plugin registration → SK↔M.E.AI bridge → tool assertions → code metrics → LLM-as-judge → performance summary. Isolated project with `Microsoft.SemanticKernel 1.72.0` and `Azure.AI.OpenAI 2.7.0-beta.2`. Located in `samples/AgentEval.NuGetConsumer/`.
  - **Sample 27: Cross-Framework Evaluation** — Universal IChatClient adapter pattern: `IChatClient` → `AsEvaluableAgent()` → evaluate → `ToEvaluationReport()` → export to Markdown.
  - **Documentation** — `docs/cross-framework.md` with capability table, SK bridge code example, NuGetConsumer link.
- **AgentEval CLI (`agenteval eval`)** — Evaluate any OpenAI-compatible AI agent from the command line without writing C#. Supports all providers (OpenAI, Ollama, Groq, vLLM, LM Studio, Azure OpenAI, etc.) via the Chat Completions API standard. Features: 15 CLI options, 7 export formats (json, junit, xml, markdown, md, trx, csv), LLM-as-judge via `--judge`, system prompt from file, stderr progress reporting for Unix piping, and CI/CD exit codes (0=pass, 1=fail, 2=usage error, 3=runtime error). Packaged as a .NET tool (`dotnet tool install AgentEval.Cli`). Located in `src/AgentEval.Cli/`.
- **Dependency Injection architecture (ADR-006)** — All core services registered via `services.AddAgentEval()`, `services.AddAgentEvalDataLoaders()`, `services.AddAgentEvalRedTeam()`, or `services.AddAgentEvalAll()`. Interface-first design: `IStochasticRunner`, `IModelComparer`, `IStatisticsCalculator`, `IToolUsageExtractor`, `ISnapshotComparer`, `ISnapshotStore`, and all exporters/loaders registered with appropriate lifetimes. Configurable via `AgentEvalServiceOptions` (lifetime, harness factory, logger factory). See `AgentEvalServiceCollectionExtensions`.
- **Rich Evaluation Output subsystem** — Structured output formatting moved to `AgentEval.DataLoaders/Output/` during modularization, contracts split to `AgentEval.Abstractions/Output/`:
  - `TableFormatter` — `PrintTable()`, `PrintComparisonTable()`, `PrintPerformanceSummary()`, `PrintToolSummary()` with dynamic column selection and ANSI variance color-coding.
  - `StochasticResultExtensions` — Fluent `result.PrintTable("Metrics")`, `result.PrintSummary()`, `result.PrintPerformanceSummary()`, `result.PrintToolSummary()`, `result.ToTableString()`.
  - `ComparisonResultExtensions` — `modelResults.PrintComparisonTable()`, `modelResults.ToComparisonTableString()`.
  - `OutputOptions` — 15+ toggle properties (`ShowScore`, `ShowPassRate`, `ShowDuration`, `ShowTTFT`, `ShowTokens`, `ShowCost`, `ShowToolCalls`, `ShowConfidenceInterval`, etc.) with `Default`, `Minimal`, `Full` static presets and fluent `With()` copy method.
  - `VerbosityLevel` enum (`None`/`Summary`/`Detailed`/`Full`), `VerbositySettings`, `VerbosityConfiguration` with environment variable support (`AGENTEVAL_VERBOSITY`, `AGENTEVAL_SAVE_TRACES`, `AGENTEVAL_TRACE_DIR`).
  - `EvaluationOutputWriter` — 4-mode writer (Summary/Detailed/Full/None) producing tool timelines, performance sections, metric sections, and full JSON trace to any `TextWriter`.
  - `AgentEvalTestBase` — xUnit test base class with automatic tracing, `RecordResult()`, `SaveTrace()`, `CreateResult()` fluent builder pattern (`TestResultBuilder`).
  - `TimeTravelTrace` — 22+ model classes for time-travel debugging (`ExecutionStep`, 13 `StepType` values, `ToolCallStepData`, `AgentHandoffStepData`, etc.).
  - `TraceArtifactManager` — `SaveTestResult()`, `SaveTrace()`, `LoadTrace()`, `ListTraceFiles()`, `GetMostRecentTrace()`, `CleanupOldTraces()`.
- **Exporter registry and DI auto-discovery** — Extensible exporter system with runtime registration:
  - `IExporterRegistry` interface (in Abstractions) — `Register()`, `Get()`, `GetRequired()`, `GetAll()`, `GetRegisteredFormats()`, `Contains()`, `Remove()`, `Clear()`.
  - `ExporterRegistry` implementation — Thread-safe `ConcurrentDictionary`, pre-populated with 5 built-in exporters (JSON, JUnit XML, Markdown, TRX, CSV) via DI.
  - DI auto-discovery: custom `IResultExporter` services registered in DI are automatically picked up by the registry.
  - `FormatName` default interface member on `IResultExporter` for string-based lookup.
  - `ResultExporterFactory` — Static factory with `Create(ExportFormat)` and `CreateFromExtension(string)`.
- **DataLoader factory and DI architecture** — Extensible dataset loading with runtime registration:
  - `IDatasetLoaderFactory` interface (in Abstractions) — `CreateFromExtension()`, `Create()`, `Register()`.
  - `DefaultDatasetLoaderFactory` implementation — Dictionary-based registry for `.jsonl`, `.ndjson`, `.json`, `.csv`, `.tsv`, `.yaml`, `.yml`. Constructor accepts `IEnumerable<IDatasetLoader>` for DI auto-discovery of custom loaders.
  - `DatasetLoaderFactory` refactored to static convenience façade delegating to `DefaultDatasetLoaderFactory`.
  - `IsTrulyStreaming` property on `IDatasetLoader` — distinguishes JSONL/CSV true streaming from JSON/YAML buffered loading.
  - `.ndjson` and `.tsv` file extension support added.
  - `DatasetTestCaseBenchmarkExtensions` — `ToToolAccuracyTestCase()` and `ToTaskCompletionTestCase()` bridging dataset test cases to benchmark types with `required_params` metadata mapping.
- **Benchmarking improvements** — DI integration and multi-prompt support:
  - `AgenticBenchmark` now accepts `IToolUsageExtractor?` via DI (defaults to `DefaultToolUsageExtractor.Instance` for non-DI usage).
  - `PerformanceBenchmark.RunLatencyBenchmarkAsync()` gained multi-prompt overload (`IEnumerable<string> prompts`) to avoid server-side caching and produce more representative latency measurements.
  - `AgenticBenchmarkOptions.AddDefaultCompletionCriteria` — boolean controlling auto-appended standard criteria.
  - Throughput benchmark `Task.Yield()` fixes for both success and error paths preventing deadlocks with synchronous agents.
- **Extensibility framework** — Plugin system and registry pattern for custom extensions:
  - `IMetricRegistry` — now DI-registered as singleton with auto-population from `IMetric` services.
  - `IAgentEvalPlugin` lifecycle interface — `InitializeAsync()`, `OnBeforeEvaluationAsync()`, `OnAfterEvaluationAsync()`, `ShutdownAsync()`, with `PluginId`, `Name`, `Version`, `Dependencies`.
  - `IPluginContext` — provides `Metrics` (IMetricRegistry), `Logger`, `Configuration`, `GetConfig<T>()`.
  - `IResultTransformer` — Post-processing with `Priority` ordering for composable result pipelines.
  - See Sample 26 for custom metrics, exporters, loaders, and attack registration via DI.
- **Sample 22: Responsible AI** — Toxicity, bias, misinformation metrics with counterfactual testing.
- **Sample 23: Benchmark System** — JSONL-loaded benchmarks: tool accuracy, latency, cost analysis with `DatasetTestCaseBenchmarkExtensions`.
- **Sample 24: Calibrated Evaluator** — Multi-model consensus evaluation with calibrated scoring.
- **Sample 25: Dataset Loaders** — Multi-format dataset pipeline: JSONL, JSON, YAML, CSV with `IDatasetLoaderFactory`.
- **Sample 26: Extensibility** — DI registries, custom metrics/exporters/loaders/attacks demonstrating all extension points.

### Changed
- **Snapshot Evaluation comprehensive review (28+ fixes)** — Major audit and hardening of the snapshot comparison and storage system:
  - *Interfaces & DI:* Added `ISnapshotComparer` and `ISnapshotStore` interfaces with DI registration (ADR-006 compliance). Added `InternalsVisibleTo` for test project access to internal helpers.
  - *Security:* Sanitized suffix parameter in `GetSnapshotPath` to prevent path traversal (CODE-22). Added `basePath` validation in `SnapshotStore` constructor (CODE-21). Fixed `SanitizeFileName` collision resistance with SHA256 hash suffix (CODE-17).
  - *Correctness:* Fixed `JsonValueKind.Null` handling in element comparison (CODE-12). Fixed boolean type guard treating `True`/`False` as compatible types (CODE-30). Fixed `SemanticComparisonResult` to store scrubbed values (CODE-33). Fixed `ComputeSimpleSimilarity` to split on all whitespace (CODE-32). Fixed `CompareArrays` to continue comparing after length mismatch (CODE-23). Fixed `LoadAsync` TOCTOU with try/catch pattern (CODE-26/35). Fixed GUID regex word boundaries (CODE-16). Fixed duration regex word boundaries to prevent false positives (CODE-15). Fixed field name passed as parameter through recursion (CODE-20/34).
  - *Validation:* Added `SemanticThreshold` [0.0, 1.0] range validation (CODE-31). Added null guards on `Compare` method (TEST-12).
  - *New features:* Added `AllowExtraProperties` option (CODE-6). Added `Delete`, `ListSnapshots`, and `Count` to `SnapshotStore` (CODE-9/18). Added epsilon-based floating-point comparison (CODE-10). Added `CancellationToken` support on all async methods (CODE-7).
  - *Performance:* Added `RegexOptions.Compiled` on all default patterns (CODE-13). Made `JsonSerializerOptions` static in `SnapshotStore` (CODE-14).
  - *Testing:* Expanded test coverage from 23 to 51+ tests. Moved tests from `Benchmarks/` to `Snapshots/` directory (TEST-1/7). Added thread safety documentation (CODE-19). Documentation aligned with code defaults and APIs.
- **Sample 27 simplified** — Removed redundant MAF flight agent (Part B, ~350 lines) already demonstrated in Samples 2-3, 9-10, and NuGetConsumer. Now focused solely on the unique Universal IChatClient Adapter pattern.
- **Cross-framework documentation fixed** — Fixed broken Semantic Kernel code example in `docs/cross-framework.md` (replaced non-existent `AsChatClient()` method with working `AIFunctionFactory.Create()` bridge pattern). Added NuGetConsumer SK demo link. Fixed capability table footnote.
- **README updated** — Sample count corrected from 26 to 27 with Sample 27 row added. Test counts now use qualitative descriptions instead of hard-coded numbers. Added CLI, DI, and cross-framework to Key Features. Expanded documentation table.
- **Roadmap updated** — Marked Red Team and CLI as shipped; added CLI Phase 2, MCP Server, Benchmark runner, and Verify.Xunit to "What's Next". Updated version history table through 0.6.0-beta.
- **System.CommandLine upgraded from 2.0.0-beta4 to 2.0.3 stable** — Breaking API change: `SetHandler` → `SetAction`, `IsRequired` → `Required`, `AddOption()` → `Options.Add()`, `AddAlias()` → constructor aliases, `root.InvokeAsync(args)` → `root.Parse(args)` then `parseResult.InvokeAsync()`. Only affects the new CLI project; no existing code referenced System.CommandLine.
- **Expanded test coverage** — New tests for DI service registration, snapshot evaluation improvements, CLI commands, cross-framework adapter, and export pipeline bridging across all 3 TFMs.

### Fixed
- **Streaming tool extraction for ChatClientAgentAdapter** — `InvokeStreamingAsync` now yields `ToolCallStarted` and `ToolCallCompleted` chunks when the underlying `IChatClient` streams `FunctionCallContent`/`FunctionResultContent`. Previously, streaming evaluations via `RunEvaluationStreamingAsync` produced empty `ToolUsageReport` for all `IChatClient`-based agents. Non-streaming path was unaffected.

---

## [0.4.0-beta] - 2026-02-22

**Security, Responsible AI & MAF RC1** 🛡️🤖

Major feature release: Red Team security scanning, Responsible AI metrics, Calibrated multi-model evaluation, MAF RC1 upgrade, and comprehensive tracing improvements. Comprehensive test suite passing across all 3 TFMs.

### ⚠️ BREAKING CHANGES

- **MAF RC1 Upgrade** - Upgraded from `Microsoft.Agents.AI 1.0.0-preview.251110.2` to `1.0.0-rc1`
  - `Microsoft.Extensions.AI` upgraded from `10.0.0` to `10.3.0`
  - `Microsoft.Extensions.AI.OpenAI` upgraded from `10.0.0-preview.1.25559.3` to `10.3.0` (preview → stable)
  - `Microsoft.Extensions.AI.Evaluation.Quality` upgraded from `9.5.0` to `10.3.0`
  - `System.Numerics.Tensors` bumped from `10.0.0` to `10.0.3` (transitive compatibility)
  - Event hierarchy fix: `AgentResponseUpdateEvent` now inherits `WorkflowOutputEvent` (critical switch restructuring in `MAFWorkflowEventBridge`)
  - Type renames: `AgentThread` → `AgentSession`, `GetNewThread()` → `CreateSessionAsync()` (sync → async)
  - Method renames: `StreamAsync` → `RunStreamingAsync`, `AddFanInEdge` → `AddFanInBarrierEdge`
  - Naming conflict resolved: `using AgentResponse = AgentEval.Core.AgentResponse;` alias in adapter files
  - `ChatClientAgentOptions.Instructions` → `ChatOptions.Instructions` across all samples (26 occurrences in 14 files)
  - **Breaking change (MAF adapters only):** Helper methods on `MAFAgentAdapter` and `MAFIdentifiableAgentAdapter` were renamed and made async: `ResetThread()` → `ResetSessionAsync()`, `GetNewThread()` → `CreateSessionAsync()`, and constructor parameter type `AgentThread?` → `AgentSession?`. Core evaluation interfaces (`IEvaluableAgent`, `IStreamableAgent`) are unchanged; only code that calls these helper methods directly must be updated.

### Added
- **Red Team Security Testing Module** - Comprehensive AI agent security evaluation
  - **9 attack types**: PromptInjection, Jailbreak, PIILeakage (LLM02), SystemPromptExtraction (LLM07), IndirectInjection, ExcessiveAgency (LLM06), InsecureOutput (LLM05), InferenceAPIAbuse (LLM10), EncodingEvasion
  - **192 total probes** across all attack categories (expanded InsecureOutput from 18→33)
  - **60% OWASP LLM Top 10 2025 coverage** (6/10): LLM01, LLM02, LLM05, LLM06, LLM07, LLM10
  - **6 MITRE ATLAS techniques**: AML.T0024, AML.T0037, AML.T0043, AML.T0045, AML.T0051, AML.T0054
  - **6 export formats**: JSON, JUnit XML, SARIF (GitHub Security), Markdown, PDF, CSV
  - **4 compliance reports**: OWASP, MITRE, SOC2, ISO27001
  - Fluent assertions: `result.Should().HaveOverallScoreAbove(85)`
  - Attack pipeline API: `AttackPipeline.Create().WithAllAttacks().ScanAsync(agent)`
  - Baseline comparison for CI/CD regression tracking
  - Real-time progress reporting with `ScanProgress` callback
  - Rich console output with emoji, colors, and detailed breakdowns
- **Responsible AI Metrics** (`AgentEval.Metrics.ResponsibleAI` namespace)
  - `ToxicityMetric` - Pattern + LLM hybrid toxicity detection
  - `BiasMetric` - LLM-based bias detection with counterfactual testing
  - `MisinformationMetric` - Claim verification and calibration assessment
- **Calibrated Evaluator** - Multi-model criteria-based evaluation with `CalibratedEvaluator` for consensus-driven scoring
- **CSV Export Format** - New `CsvExporter` for Excel and business intelligence tools
- **Sample 23: Responsible AI** - Toxicity, bias, misinformation metrics with counterfactual testing
- **Sample 24: Benchmark System** - Performance, agentic, standard, and cost benchmarks with comparative analysis
- **SPDX License Identifiers** - Added to all source and test files for compliance

### Changed
- **Trace Record & Replay Improvements** (9 improvements from comprehensive audit)
  - Added `IsComplete` property to `TraceReplayingAgent` for cleaner replay loops
  - Implemented `RecordStreamingChunks` conditional check — streaming chunks now only recorded when option is enabled
  - Wired up `SanitizeToolResult` in streaming recording — tool results are sanitized consistently
  - Implemented `MaxTurns` enforcement in `ChatTraceRecorder` — throws `InvalidOperationException` when limit reached
  - Fixed documentation API names across `docs/tracing.md`, `docs/conversations.md`, `docs/workflows.md`, and `docs/adr/004-trace-recording-replay.md`
  - Added cross-reference sections in `docs/conversations.md` and `docs/workflows.md` linking to tracing guide
  - Updated ADR-004 phase status to reflect current implementation state
  - Sample 13 Demos 3 & 4 rewritten from mocked to fully operational real AI workflows
  - Added 12 new tracing tests (Contains matching, Warn/Ignore mismatch, sanitization, MaxTurns)
- **Sample 13 Audit Fixes** — fixed prompt display mismatch, added `DelayMultiplier = 0.1` for fast workflow replay, removed unused `System.Text.Json` import, corrected Key Takeaways API names
- **docs/tracing.md** Performance Baseline example fixed: `Entries[0].Duration` → `Entries.First(e => e.Type == TraceEntryType.Response).DurationMs`
- Added `ConfigureAwait(false)` to MAF adapter async calls for reliability
- Replaced `Assert.True` with `Assert.Contains` for improved test readability
- Removed hardcoded version strings from documentation

---

## [0.3.0-beta] - 2026-01-25

**Brand Alignment: Evaluation-First Naming** 🎯

This release implements comprehensive renamed APIs to better reflect AgentEval's primary identity as an **AI Agent Evaluation Toolkit**. All "Test" terminology in public APIs has been renamed to "Evaluation" to align with the framework's positioning.

### ⚠️ BREAKING CHANGES

#### Interface Renames
| Old Name | New Name |
|----------|----------|
| `ITestHarness` | `IEvaluationHarness` |
| `IStreamingTestHarness` | `IStreamingEvaluationHarness` |
| `ITestableAgent` | `IEvaluableAgent` |
| `IWorkflowTestableAgent` | `IWorkflowEvaluableAgent` |

#### Class Renames
| Old Name | New Name |
|----------|----------|
| `MAFTestHarness` | `MAFEvaluationHarness` |
| `WorkflowTestHarness` | `WorkflowEvaluationHarness` |
| `TestOptions` | `EvaluationOptions` |
| `TestOutputWriter` | `EvaluationOutputWriter` |
| `TestMetadata` | `EvaluationMetadata` |

#### Method Renames
| Old Name | New Name |
|----------|----------|
| `RunTestAsync()` | `RunEvaluationAsync()` |
| `RunTestStreamingAsync()` | `RunEvaluationStreamingAsync()` |
| `RunTestSuiteAsync()` | `RunEvaluationSuiteAsync()` |
| `TestHarnessFactory` property | `EvaluationHarnessFactory` property |

#### File Renames
| Old Name | New Name |
|----------|----------|
| `ITestHarness.cs` | `IEvaluationHarness.cs` |
| `ITestableAgent.cs` | `IEvaluableAgent.cs` |
| `MAFTestHarness.cs` | `MAFEvaluationHarness.cs` |
| `WorkflowTestHarness.cs` | `WorkflowEvaluationHarness.cs` |
| `TestModels.cs` | `EvaluationModels.cs` |
| `TestOutputWriter.cs` | `EvaluationOutputWriter.cs` |
| `stochastic-testing.md` | `stochastic-evaluation.md` |
| `Sample14_StochasticTesting.cs` | `Sample14_StochasticEvaluation.cs` |

### Unchanged (Universal Terminology)
The following names are **intentionally kept** as they represent universal industry terminology:
- `TestCase` - Standard testing terminology used across all frameworks
- `TestResult` - Conflict resolution with existing `Core.EvaluationResult` type
- `TestSummary` - Consistent with TestResult
- `AgentEvalTestBase` - xUnit integration base class
- `StochasticRunner` - Neutral name, not test-specific
- `*Tests.cs` files - xUnit naming convention

### Changed
- **Terminology:** "stochastic testing" → "stochastic evaluation" throughout codebase and documentation
- **Terminology:** "test harness" → "evaluation harness" throughout codebase and documentation
- **XML Documentation:** Updated all public API comments with evaluation-first language
- **C# Naming Conventions:** Fixed parameter names to use camelCase (`evaluationOptions` instead of `EvaluationOptions`)
- **Documentation:** Title case capitalization fixes in markdown headers
- **Documentation:** Fixed all broken links to `stochastic-testing.md` (now `stochastic-evaluation.md`)
- **TOC:** API Reference section now renders consistently with other menu items

### Migration Guide

Update your code to use the new names:

```csharp
// Before (0.2.x)
var harness = new MAFTestHarness(evaluatorClient);
var result = await harness.RunTestAsync(agent, testCase, options);

// After (0.3.0)
var harness = new MAFEvaluationHarness(evaluatorClient);
var result = await harness.RunEvaluationAsync(agent, testCase, options);
```

```csharp
// Before (0.2.x)
public class MyAgent : ITestableAgent { }

// After (0.3.0)
public class MyAgent : IEvaluableAgent { }
```

### Documentation
- Brand Positioning Guidelines created at `strategy/plans/Implementation-Plan-Brand-Positioning-Guidelines.md`
- All documentation files updated with evaluation-first messaging
- Code examples in documentation updated to use new API names

---

## [0.2.1-beta] - 2026-01-24

**Features + Documentation & Messaging Refresh** 🚀📝

This release adds new features (enhanced token tracking, Sample 19) and updates AgentEval's positioning to better reflect its core value as an **evaluation toolkit** for AI agents.

### Added (Features)
- **Enhanced Token Usage Tracking** - Improved token usage extraction and cost estimation in `MAFTestHarness` and `PerformanceMetrics`
  - More accurate cost calculation across streaming and async scenarios
  - Better handling of model pricing for cost estimation
- **Sample 19: Streaming vs Async Performance Comparison** - New sample demonstrating:
  - Side-by-side streaming vs async performance measurement
  - Time-to-first-token (TTFT) tracking for streaming scenarios
  - Token usage comparison between execution modes
- **Interactive Demo Menu** - Enhanced samples with interactive selection and demo inputs
- **NuGetConsumer Sample Project Enhancements** - Additional demos and offline testing patterns

### Added (Documentation)
- **"Who Is AgentEval For?"** section to README.md and docs/index.md
  - .NET Teams Building AI Agents
  - Microsoft Agent Framework (MAF) Developers
  - ML Engineers Evaluating LLM Quality
- **".NET Advantage"** comparison table to README.md showing AgentEval vs Python alternatives
- **CLI Tool & Samples** section to docs/index.md
- License badge to docs/index.md

### Changed
- **New Positioning:** "The .NET Evaluation Toolkit for AI Agents" (previously "testing framework")
  - Evaluation leads (50% of codebase), followed by testing (25%) and benchmarking (25%)
  - Clearer differentiation vs Python alternatives (RAGAS, DeepEval)
- Updated test count badge across 3 TFMs
- Fixed version references from 1.0.0-alpha to 0.2.0-beta in all documentation
- Updated NuGet tags: added `rag` and `agentic` keywords
- Simplified `docs/roadmap.md` - removed internal planning details, shows only shipped features and general direction

### Removed
- `src/AgentEval/AgentEval-Design.md` - Internal design document with outdated information
- `docs/why-agenteval.md` - Content merged into docs/index.md for unified landing page

### Fixed
- Removed inaccurate "Native xUnit/NUnit/MSTest support" claim (AgentEval works WITH test frameworks, doesn't provide native integration)
- Removed fabricated testimonials from documentation
- Fixed trace replay description accuracy
- Documentation site toc.yml updated for removed files

### Documentation
- All 18+ documentation files updated with consistent messaging
- NuGet README now shows correct positioning tagline
- Strategy documents aligned with new positioning

---

## [0.2.0-beta] - 2026-01-24

**AgentEval Public Beta Release** 🎉

This release marks the transition from alpha to beta. The framework is now feature-complete for core scenarios and ready for community feedback.

### Added
- **Codecov Badge** - Coverage visibility in README.md
- **NuGet Consumer Sample** (`samples/AgentEval.NuGetConsumer/`) - Standalone project showcasing all major features
  - Tool chain assertions (HaveCalledTool, WithArgument, BeforeTool, AfterTool)
  - Performance assertions (Duration, TTFT, Cost, Token limits)
  - Behavioral policies (NeverCallTool, MustConfirmBefore, NeverPassArgumentMatching)
  - Response assertions (Contain, NotContain, length validation)
  - Mock testing with FakeChatClient
  - Stochastic testing examples
  - Model comparison patterns
  - Agentic metrics overview
  - Works offline with mock data - no Azure OpenAI required
- **Custom Domain** - AgentEval.dev documentation site with GitHub Pages
- **Comprehensive Documentation** - 25+ documentation pages with zero DocFX warnings
- **Security Scanning** - Enhanced pipeline with secret detection and dependency scanning

### Changed
- Updated README test count badge to 3000+ (reflecting 1000+ tests × 3 TFMs)
- Documentation navigation reorganized with improved feature grouping
- Security scanning patterns refined to reduce false positives
- Version bumped from 0.1.3-alpha to 0.2.0-beta signaling production readiness

### Documentation
- Getting Started, Assertions, Metrics Reference, Model Comparison guides
- Trace Record & Replay, Stochastic Testing, Benchmarks documentation
- CI/CD Integration guide with GitHub Actions examples
- Migration guide for Python/Node.js developers

---

## [0.1.3-alpha] - 2026-01-18

### Added
- **Security Scanning Pipeline** - Comprehensive automated security analysis
  - DevSkim static analysis integrated into CI/CD
  - NuGet dependency vulnerability scanning
  - Secret detection to prevent credential leaks
  - SARIF output to GitHub Security tab
  - Weekly scheduled scans plus on push/PR triggers
- **CLI Baseline Comparison** - Compare against golden files
  - `--baseline` option for snapshot testing workflow
  - Human-readable diff output with color coding
  - Exit code 2 for baseline mismatches (distinct from test failures)
- **Security Documentation** - Comprehensive security guidance
  - [SECURITY.md](SECURITY.md) - Vulnerability reporting process
  - [docs/security-scanning.md](docs/security-scanning.md) - Tech stack and architecture
  - [strategy/Implementation-Plan-Security-Hardening.md](strategy/Implementation-Plan-Security-Hardening.md) - Security roadmap
- **Input Validation Hardening** - Defense against path traversal attacks
  - CLI file path validation with directory allowlist
  - Path normalization and canonicalization
  - Extension validation for dataset files
- **Security Workflow** (`.github/workflows/security.yml`)
  - Runs on all pushes to main/develop branches
  - Runs on all pull requests
  - Scheduled weekly Monday scans for dependency updates

### Changed
- Project version bumped to 0.1.3-alpha across all packages
- Enhanced CI/CD with security gate requirements

### Security
- Implemented OWASP Top 10 mitigations for web-adjacent attack vectors
- Added anti-glassworm protections in development workflow
- PII detection in `NeverPassArgumentMatching` uses redaction by default

---

## [0.1.2-alpha] - 2026-01-04

### Added
- **Behavioral Policy Assertions** - Safety-critical assertions for enterprise compliance
  - `NeverCallTool(toolName, because)` - Assert forbidden tools were never called
  - `NeverPassArgumentMatching(pattern, because, options)` - Detect PII/secrets via regex with automatic redaction
  - `MustConfirmBefore(toolName, because, confirmationToolName)` - Require confirmation before risky actions
  - `BehavioralPolicyViolationException` with structured properties (PolicyName, ViolationType, ViolatingAction, RedactedValue)
  - 16 unit tests for behavioral policy assertions
  - Updated Sample12 with new behavioral policy examples
  - See [ADR-008](docs/adr/008-calibrated-judge-multi-model.md) for design decisions
- **Judge Calibration** - Multi-model consensus for reliable LLM-as-judge evaluations
  - `CalibratedJudge` - Wrapper for running evaluations with multiple LLM judges
  - `VotingStrategy` enum: Median, Mean, Unanimous, Weighted
  - `CalibratedResult` with Agreement %, Confidence Intervals, per-judge scores
  - `ICalibratedJudge` interface for testability
  - `CalibratedJudgeOptions` with configurable timeouts, parallelism, consensus tolerance
  - Factory pattern: `metricFactory(judgeName)` for per-judge metric instantiation
  - Parallel judge execution with graceful degradation
  - 17 unit tests for calibrated judge
  - Sample18_JudgeCalibration demonstration
  - See [ADR-008](docs/adr/008-calibrated-judge-multi-model.md) for design decisions
- **Model Comparison Markdown Export** - Shareable comparison reports
  - `ToMarkdown()` extension for `ModelComparisonResult` - Full report with all sections
  - `ToRankingsTable()` - Compact table with medal emojis (🥇🥈🥉)
  - `ToDetailedMetricsTable()` - Pass rate, latency, cost metrics
  - `ToStatisticsTable()` - Mean, median, percentiles, confidence intervals
  - `ToGitHubComment()` - Collapsible PR comment format
  - `SaveToMarkdownAsync()` - File export
  - `MarkdownExportOptions` with Default and Minimal presets
  - Batch comparison support for multiple test cases
  - 20 unit tests for markdown export
  - Updated Sample15 with markdown export demonstration
- **Trace Record & Replay (Phase 8)** - Deterministic testing and time-travel debugging
  - `TraceRecordingAgent` - Wraps any agent to capture all executions with full fidelity
  - `TraceReplayingAgent` - Replays recorded traces deterministically without LLM calls
  - `ChatTraceRecorder` - Records multi-turn conversations with turn tracking
  - `ChatExecutionResult` - Complete conversation result with aggregate performance
  - `WorkflowTraceRecorder` - Records multi-agent workflow orchestrations
  - `WorkflowTraceReplayingAgent` - Replays workflow traces step-by-step
  - `TraceSerializer` / `WorkflowTraceSerializer` - JSON serialization for traces
  - `AgentTrace`, `WorkflowTrace` - Rich trace models with metadata and performance
  - `TraceEntry`, `WorkflowTraceStep` - Detailed per-invocation/step records
  - `TraceTokenUsage`, `TraceToolCall`, `TraceError` - Supporting models
  - Streaming support for recording/replaying chunked responses
  - 168 new tests covering all tracing functionality
  - Comprehensive [tracing documentation](docs/tracing.md)
  - Sample 13: Trace Record & Replay demonstration
- **Enhanced Fluent Assertions** - Improved xUnit assertion failure experience inspired by FluentAssertions/Shouldly
  - **`because` parameter** on all assertions for documenting test intent (e.g., `HaveCalledTool("SearchTool", because: "user query requires search")`)
  - **`AgentEvalScope`** for collecting multiple assertion failures into a single exception with all failures listed
  - **Rich structured error messages** with Expected/Actual values, context, tool timeline, and actionable suggestions
  - **`[StackTraceHidden]`** attribute on assertion methods for cleaner failure stack traces
  - **`CallerArgumentExpression`** for automatic subject name capture in ResponseAssertions
  - New `AgentEvalScopeException` for batch failure reporting
  - Comprehensive [assertions documentation](docs/assertions.md) with examples
- **CLI eval command** with real dataset validation
  - Loads datasets from YAML, JSON, JSONL, and CSV files
  - Validates test case completeness, ground truth, expected tools, and context
  - Outputs results in JSON, JUnit XML, Markdown, or TRX formats
  - Cross-platform color support with NO_COLOR environment variable respect
- **Sample datasets** for quick start
  - `samples/datasets/travel-agent.yaml` - agentic evaluation with tool usage
  - `samples/datasets/rag-qa.yaml` - RAG evaluation with context documents
  - `samples/datasets/README.md` - comprehensive dataset format documentation
- **YAML dataset loader** with flexible field aliasing
  - Supports both `expected_output` and `expectedOutput` naming conventions
  - Supports `ground_truth`, `expected_tools`, and `context` fields
  - Full YAML 1.2 compliance via YamlDotNet
- **Workflow Testing Support (Phase 6B)** - Per-executor visibility for multi-agent workflows
  - `WorkflowExecutionResult` - Captures per-executor output, timing, and tool calls
  - `ExecutorStep` and `WorkflowError` models for detailed workflow analysis
  - `IWorkflowEvaluableAgent` - Extended interface for workflow-aware agents
  - `MAFWorkflowAdapter` - Adapter for MAF Workflows with streaming event capture
  - `WorkflowEvaluationHarness` - evaluation harness for workflow testing with assertions
  - `WorkflowAssertions` - Fluent assertion API for workflow execution results
  - Supports executor order validation, step timing, tool call tracking
  - 71 new tests for workflow components
- **Workflow Edge/Graph Support (Phase 6B+)** - Full DAG structure for complex workflows
  - `EdgeType` enum - Sequential, Conditional, Switch, ParallelFanOut, ParallelFanIn, Loop, Error, Terminal
  - `WorkflowEdge` - Static edge definitions with conditions and switch labels
  - `EdgeExecution` - Runtime edge traversal with routing decisions and data transfer
  - `ParallelBranch` - Tracks parallel execution branches
  - `WorkflowNode` - Node definitions with entry/exit point markers
  - `WorkflowGraphSnapshot` - Complete DAG topology with nodes, edges, and execution path
  - `RoutingDecision` - Captures conditional/switch routing decisions
  - New workflow events: `EdgeTraversedEvent`, `RoutingDecisionEvent`, `ParallelBranchStartEvent`, `ParallelBranchEndEvent`
  - Edge assertions: `HaveTraversedEdge()`, `HaveConditionalRouting()`, `HaveParallelExecution()`, `ForEdge().BeOfType()`
  - Step edge assertions: `HaveIncomingEdge()`, `HaveBeenConditionallyRouted()`, `BeInParallelBranch()`
  - `MAFWorkflowAdapter.WithGraph()` and `FromConditionalSteps()` factory methods
  - 66 new tests for edge models and assertions

### Changed
- **Test project reorganization** into logical folder structure:
  - `Core/` - AgentEvalBuilder, Logger, MetricRegistry, Retry, Normalizer, Concurrency tests
  - `Metrics/RAG/` - Faithfulness, Relevance, Context Precision/Recall, Answer Correctness
  - `Metrics/Agentic/` - Tool Selection, Arguments, Success, Efficiency, Task Completion
  - `DataLoaders/` - Dataset loader and serialization tests
  - `Exporters/` - Result exporter tests
  - `Testing/` - FakeChatClient, ConversationRunner, ConversationalTestCase tests
  - `Assertions/` - Tool usage and response assertion tests
  - `Models/` - Domain model tests
  - `Benchmarks/` - Performance and agentic benchmark tests
  - `MAF/` - Microsoft Agent Framework integration tests
- **CLI ConsoleHelper** for improved cross-platform terminal support
  - Detects NO_COLOR environment variable
  - Detects TERM=dumb terminals
  - Gracefully handles output redirection (piping to files)

### Fixed
- YAML loader tests now use correct 4-space indentation matching YAML standards
- Removed invalid `include-prerelease` input from CI workflow (actions/setup-dotnet@v4 compatibility)

---

## [0.1.2-alpha] - 2026-01-04

### Added
- Additional test coverage for core components
- XML documentation generation enabled in project configuration
- DocFX build scripts (PowerShell and Batch) for automated API documentation generation
- Comprehensive documentation guides (GENERATE-DOCS.md, DOCUMENTATION-SUMMARY.md)

### Changed
- Project now generates XML documentation files for all target frameworks (net8.0, net9.0, net10.0)
- Suppressed CS1591 warnings for undocumented members

---

## [0.1.1-alpha] - 2026-01-03

### Added
- SourceLink support for debugging into source code
- Symbol packages (.snupkg) published to NuGet.org
- NuGet package icon (AgentEvalNugetLogoAE.png)
- Azure OpenAI environment variables in CI/CD workflows

### Changed
- Repository restructured to standard .NET layout (src/, samples/, tests/, docs/)
- Central package management with `Directory.Packages.props`
- Shared build configuration with `Directory.Build.props`
- GitHub Actions CI now tests on .NET 8, 9, and 10 across Ubuntu and Windows
- CI workflow optimized with NuGet caching and fail-fast disabled

### Infrastructure
- GitHub Actions CI workflow for automated build and test
- GitHub Actions release workflow for NuGet publishing
- DocFX documentation scaffolding
- EditorConfig for consistent code style

---

## [0.1.0-alpha] - 2026-01-02

### Added

#### Core Framework
- First .NET-native AI agent testing, evaluation, and benchmarking framework
- Full Microsoft Agent Framework (MAF) integration via `MAFAgentAdapter` and `MAFTestHarness`
- Extensible adapter pattern supporting `IChatClient` and other frameworks
- Plugin system with `IAgentEvalPlugin` interface

#### Tool Usage Tracking & Assertions
- `ToolCallRecord` for capturing tool invocations with timing, arguments, results, and errors
- `ToolCallTimeline` for visualizing parallel tool execution
- Fluent assertions: `HaveCalledTool()`, `BeforeTool()`, `WithArgument()`, `HaveNoErrors()`
- Tool usage reports with success/failure metrics

#### Performance Metrics
- Real-time performance tracking with TTFT (Time To First Token)
- Per-tool timing and execution waterfall data
- Token counting (prompt/completion/total)
- Cost estimation for 8+ models (GPT-4o, GPT-4o-mini, Claude 3.5, Claude 3 Opus, GPT-4 Turbo, GPT-3.5 Turbo, o1-preview, o1-mini)
- Performance assertions: `HaveTotalDurationUnder()`, `HaveTimeToFirstTokenUnder()`, `HaveEstimatedCostUnder()`

#### RAG Metrics
- Faithfulness metric (grounded in context)
- Relevance metric (response addresses query)
- Context Precision metric
- Context Recall metric
- Answer Correctness metric

#### Agentic Metrics
- Tool Selection metric (chose appropriate tools)
- Tool Arguments metric (correct arguments passed)
- Tool Success metric (tools executed successfully)
- Task Completion metric (agent completed the task)
- Efficiency metric (minimal steps, tokens, time)

#### Benchmarks
- `PerformanceBenchmark` for latency/throughput/cost analysis
- `AgenticBenchmark` for multi-step agentic task evaluation
- Percentile statistics (p50, p90, p95, p99)
- Summary statistics (mean, min, max, standard deviation)

#### Testing Infrastructure
- `FakeChatClient` for zero-dependency unit testing
- `TestCase` model with inputs, expected outputs, evaluation criteria
- `TestResult` with comprehensive run data
- Trace-first failure reporting with structured diagnostics

#### Observability
- `IAgentEvalLogger` abstraction with console and Microsoft.Extensions.Logging adapters
- Run artifacts for debugging and "time travel" inspection
- Designed for OpenTelemetry (OTel) integration

### Technical Details
- Comprehensive unit test coverage across all target frameworks
- Multi-target framework support: .NET 8.0, 9.0, 10.0
- Zero-dependency core (optional integrations for MAF, Azure OpenAI)

---

## Future Releases

### Planned Packages
- `AgentEval` (core) ✅ This release
- `AgentEval.Maf` (MAF integration) - planned
- `AgentEval.TestKit` (fixtures/builders/helpers) - planned
- `AgentEval.Tracing` (OTel + run artifacts) - planned
- `AgentEval.Studio` (workflow visualizer / time-travel UI) - future

[Unreleased]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.13.2-beta...HEAD
[0.13.2-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.13.1-beta...v0.13.2-beta
[0.13.1-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.13.0-beta...v0.13.1-beta
[0.13.0-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.12.2-beta...v0.13.0-beta
[0.12.2-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.12.1-beta...v0.12.2-beta
[0.12.1-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.12.0-beta...v0.12.1-beta
[0.12.0-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.10.1-beta...v0.12.0-beta
[0.10.1-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.10.0-beta...v0.10.1-beta
[0.10.0-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.9.0-beta...v0.10.0-beta
[0.9.0-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.8.1-beta...v0.9.0-beta
[0.8.0-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.7.0-beta...v0.8.0-beta
[0.7.0-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.6.0-beta...v0.7.0-beta
[0.6.0-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.5.4-beta...v0.6.0-beta
[0.5.2-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.5.1-beta...v0.5.2-beta
[0.5.1-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.4.0-beta...v0.5.1-beta
[0.4.0-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.3.0-beta...v0.4.0-beta
[0.3.0-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.2.1-beta...v0.3.0-beta
[0.2.1-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.2.0-beta...v0.2.1-beta
[0.2.0-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.1.3-alpha...v0.2.0-beta
[0.1.3-alpha]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.1.2-alpha...v0.1.3-alpha
[0.1.2-alpha]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.1.1-alpha...v0.1.2-alpha
[0.1.1-alpha]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.1.0-alpha...v0.1.1-alpha
[0.1.0-alpha]: https://github.com/AgentEvalHQ/AgentEval/releases/tag/v0.1.0-alpha
