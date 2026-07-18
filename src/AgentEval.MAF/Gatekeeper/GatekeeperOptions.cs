// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.MAF.Skills;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Phase 1, P0-5) — the configuration surface for <c>UseGatekeeper</c>, the safe composite builder
/// that installs run-scope, tool gates, approval interop, and tracing together, in the correct order, in one
/// call. Populate this via the <c>configure</c> callback passed to <c>UseGatekeeper</c>/
/// <c>ObserveWithAgentEvalGates</c>/<c>EnforceAgentEvalGates</c>.
/// </summary>
public sealed class GatekeeperOptions
{
    /// <summary>Tool gates, run in order on every tool call (via <c>UseAgentEvalToolGate</c>).</summary>
    public IList<IToolGate> ToolGates { get; } = new List<IToolGate>();

    /// <summary>
    /// Tool RESULT gates (Phase 2, P0-3), run in order on every already-executed tool call's result, AFTER
    /// every <see cref="ToolGates"/> entry has allowed the call and the tool has actually run. See
    /// <see cref="IToolResultGate"/> for why this is a separate list from <see cref="ToolGates"/> rather than a
    /// mode on the same gate.
    /// </summary>
    public IList<IToolResultGate> ToolResultGates { get; } = new List<IToolResultGate>();

    /// <summary>Run-pre chat gates, inspecting the input text before the model sees it.</summary>
    public IList<IChatGate> PreGates { get; } = new List<IChatGate>();

    /// <summary>Run-post chat gates, inspecting the response text.</summary>
    public IList<IChatGate> PostGates { get; } = new List<IChatGate>();

    /// <summary>Tool-approval gates (human-in-the-loop escalation via MAF's <c>UseToolApproval</c>). Left empty ⇒ the approval layer is not wired at all.</summary>
    public IList<IToolApprovalGate> ApprovalGates { get; } = new List<IToolApprovalGate>();

    /// <summary>Adds a tool gate. Sugar for <c>ToolGates.Add(gate)</c> — matches the shape shown in the Gatekeeper hardening review's own example.</summary>
    public void Add(IToolGate gate) => ToolGates.Add(gate ?? throw new ArgumentNullException(nameof(gate)));

    /// <summary>Adds a tool RESULT gate (Phase 2, P0-3). Sugar for <c>ToolResultGates.Add(gate)</c>.</summary>
    public void AddResultGate(IToolResultGate gate) => ToolResultGates.Add(gate ?? throw new ArgumentNullException(nameof(gate)));

    /// <summary>Adds a run-pre chat gate. Sugar for <c>PreGates.Add(gate)</c>.</summary>
    public void AddPreGate(IChatGate gate) => PreGates.Add(gate ?? throw new ArgumentNullException(nameof(gate)));

    /// <summary>Adds a run-post chat gate. Sugar for <c>PostGates.Add(gate)</c>.</summary>
    public void AddPostGate(IChatGate gate) => PostGates.Add(gate ?? throw new ArgumentNullException(nameof(gate)));

    /// <summary>Adds a tool-approval gate. Sugar for <c>ApprovalGates.Add(gate)</c>.</summary>
    public void AddApprovalGate(IToolApprovalGate gate) => ApprovalGates.Add(gate ?? throw new ArgumentNullException(nameof(gate)));

    /// <summary>
    /// Whether to establish an <see cref="AgentRunScope"/> for every run (via <c>UseAgentEvalGate</c>), even
    /// when no <see cref="PreGates"/>/<see cref="PostGates"/> are configured. Defaults to
    /// <see langword="true"/> — the safe default, since several tool gates (<see cref="RunBudgetGate"/>,
    /// <see cref="MonetaryLimitGate"/>, <see cref="PerToolCallBudgetGate"/>, <see cref="SequenceGate"/>) need a
    /// run scope for correct per-run isolation (see <see cref="GateRequirements.RunScope"/>). Set to
    /// <see langword="false"/> only when you are establishing the scope yourself outside this call — if you do
    /// and a <see cref="GateRequirements.RunScope"/> gate is registered under a non-<see cref="GatekeeperEnforcement.Observe"/>
    /// enforcement level, <c>UseGatekeeper</c> refuses to construct (Phase 1, P0-6) rather than silently accept
    /// the shared-fallback-state behavior those gates self-document.
    /// </summary>
    public bool EstablishRunScope { get; set; } = true;

    /// <summary>Optional Glass Box trace shared by every mechanism this builder composes.</summary>
    public AgentTrace? Trace { get; set; }

    /// <summary>Optional gate-effectiveness telemetry sink (Phase 1, #18), wired into the tool-gate loop.</summary>
    public GateTelemetry? Telemetry { get; set; }

    /// <summary>
    /// How much of a <see cref="ToolGateAction.Mutate"/> verdict's before/after arguments are captured into
    /// the trace (Phase 1, #13). Defaults to <see cref="TraceCaptureMode.Redacted"/>.
    /// </summary>
    public TraceCaptureMode MutationCaptureMode { get; set; } = TraceCaptureMode.Redacted;

    /// <summary>Optional shadow-judge pump (caller-owned) for asynchronous, off-hot-path judgement.</summary>
    public ShadowJudgePump? ShadowJudgePump { get; set; }

    /// <summary>
    /// The tool list to run the Phase-1 coverage check (<see cref="GatekeeperCoverageAnalyzer"/>) against, when
    /// <see cref="RefuseUnprotectedHighRiskTools"/> is set. <c>UseGatekeeper</c> cannot read an agent's tool
    /// list at registration time (it runs before <c>.Build()</c>) — pass the SAME list reference you set on
    /// <see cref="ChatOptions.Tools"/>, not a separately-maintained copy. There is no validation that the two
    /// stay in sync: if this list is stale or partial (e.g. missing a tool added to <c>ChatOptions.Tools</c>
    /// later), <see cref="RefuseUnprotectedHighRiskTools"/> checks only what you passed here and can pass a
    /// build that actually exposes an unprotected high-risk tool. It also cannot see a tool an
    /// <see cref="Microsoft.Agents.AI.AIContextProvider"/> contributes dynamically — see
    /// <see cref="GatekeeperCoverageAnalyzer"/> remarks.
    /// <para><b>This staleness risk cannot be structurally eliminated at THIS call site</b> — re-deriving from
    /// the live agent instead of trusting <see cref="KnownTools"/> would need a built <see cref="AIAgent"/>,
    /// which does not exist yet here (same reason <see cref="EstablishRunScope"/>'s companion check cannot
    /// inspect <see cref="AgentRunScope.Current"/> at this point — see <c>UseAgentEvalToolGate</c> remarks for
    /// that parallel). The staleness-free version of this check DOES exist — it just cannot run from inside
    /// <c>UseGatekeeper</c>: call <see cref="GatekeeperCoverageAnalyzer.AnalyzeOrThrow(AIAgent,IReadOnlyList{IToolGate}?,AnalyzeOptions?)"/>
    /// yourself, on the AGENT overload, right after <c>.Build()</c> — it reads <c>ChatOptions.Tools</c> directly
    /// off the live agent, with no separately-tracked list to drift. Treat <see cref="RefuseUnprotectedHighRiskTools"/>
    /// as an early, best-effort warning and that post-<c>.Build()</c> call as the real safety net.</para>
    /// </summary>
    public IReadOnlyList<AITool>? KnownTools { get; set; }

    /// <summary>
    /// When <see langword="true"/>, <c>UseGatekeeper</c> runs <see cref="GatekeeperCoverageAnalyzer.AnalyzeOrThrow(IEnumerable{AITool}, IReadOnlyList{IToolGate}?, AnalyzeOptions?)"/>
    /// eagerly at registration time and throws <see cref="UnprotectedHighRiskToolException"/> if any high-risk
    /// tool has zero protecting gate. Requires <see cref="KnownTools"/> to be set, and is only as trustworthy as
    /// that list — see the <see cref="KnownTools"/> caveats above.
    /// <para><b>Deliberately has no <see cref="GatekeeperEnforcement.Observe"/> carve-out</b> (unlike the
    /// <see cref="EstablishRunScope"/>/<see cref="GateRequirements.RunScope"/> guard, which IS exempt under
    /// Observe). The two look similar but aren't: a RunScope-requiring gate registered without a run scope
    /// still produces evidence under Observe — degraded (shared, not per-run, state), but not nothing. A
    /// high-risk tool with ZERO protecting gate produces NO evidence at all, at any enforcement level — there
    /// is nothing for Observe to observe for that tool. Since Observe mode's whole purpose is establishing a
    /// baseline of what a gate WOULD do, a coverage gap this total defeats that purpose regardless of
    /// enforcement level, so this check applies uniformly across all three <see cref="GatekeeperEnforcement"/>
    /// values.</para>
    /// </summary>
    public bool RefuseUnprotectedHighRiskTools { get; set; }

    /// <summary>Optional override of the coverage analyzer's risk heuristic (see <see cref="AnalyzeOptions.IsHighRisk"/>).</summary>
    public AnalyzeOptions? CoverageAnalyzeOptions { get; set; }

    /// <summary>
    /// The AUTHORITATIVE coverage report — populated by <c>UseGatekeeper</c> itself, computed from the SAME
    /// <see cref="ToolGates"/> snapshot that was actually registered (not a separately-supplied list that could
    /// drift from it), whenever <see cref="KnownTools"/> is set. <see langword="null"/> if <see cref="KnownTools"/>
    /// was never set (nothing to analyze against). Read this AFTER the <c>UseGatekeeper(...)</c> call returns
    /// (the <c>configure</c> callback runs before gates are finalized, so it is still <see langword="null"/>
    /// inside <c>configure</c> itself) — prefer this over calling <see cref="GatekeeperCoverageAnalyzer.Analyze(IEnumerable{AITool},IReadOnlyList{IToolGate}?,AnalyzeOptions?)"/>
    /// yourself with your own gate list, which has no such guarantee of matching what was actually wired.
    /// </summary>
    public GatekeeperCoverageReport? CoverageReport { get; internal set; }

    /// <summary>
    /// Where the observe-mode startup banner is written. Defaults to <see cref="Console.Out"/>; set to
    /// <see langword="null"/> to suppress it (e.g. under test, or when your host already surfaces the
    /// enforcement mode through structured logging).
    /// </summary>
    public TextWriter? BannerWriter { get; set; } = Console.Out;

    /// <summary>
    /// Current prompt-template content to check for drift at construction time (Gatekeeper next-wave item —
    /// the third application of the <see cref="PromptTemplateDriftGate"/>/<see cref="ManifestFingerprint"/>
    /// primitive). Keyed by a caller-chosen template identifier (typically a file path) — usually just the
    /// agent's system-prompt file, but the dictionary shape supports pinning more than one template (e.g. a
    /// system prompt PLUS a separate tool-use prompt) in one call. Paired with
    /// <see cref="PromptTemplateBaseline"/>; both must be set TOGETHER for the check to run (leave BOTH
    /// unset to disable it — setting exactly one throws <see cref="InvalidOperationException"/> rather than
    /// silently no-op-ing, since that is almost certainly a caller mistake that would otherwise leave drift
    /// protection silently disabled).
    /// </summary>
    public IReadOnlyDictionary<string, string>? PromptTemplates { get; set; }

    /// <summary>
    /// The trust-time pinned fingerprints to check <see cref="PromptTemplates"/> against — typically
    /// <see cref="PromptTemplateDriftGate.CaptureBaseline"/>'s output from a prior, reviewed run, persisted
    /// by the caller (e.g. committed alongside the prompt file). When both this and <see cref="PromptTemplates"/>
    /// are set, <c>UseGatekeeper</c> computes drift EAGERLY at construction time and throws
    /// <see cref="PromptTemplateDriftException"/> immediately if any template's content changed since it was
    /// pinned (fail-closed, the same pattern as <see cref="RefuseUnprotectedHighRiskTools"/>). Setting only
    /// ONE of this and <see cref="PromptTemplates"/> throws <see cref="InvalidOperationException"/> at
    /// construction — same fail-loud-on-half-configuration discipline as <see cref="RefuseUnprotectedHighRiskTools"/>
    /// + a missing <see cref="KnownTools"/>. Unlike every other Gatekeeper primitive, this is a
    /// CONSTRUCTION-TIME-ONLY guard, not a new <see cref="IToolGate"/>/<see cref="IChatGate"/> — a template
    /// doesn't change mid-run, so a per-turn check would be pure waste. A template present in one dictionary
    /// but not the other (added/removed) is NOT treated as drift — only a CHANGED fingerprint for a template
    /// present in both is; that's the actual tamper signal this guard exists to catch.
    /// </summary>
    public IReadOnlyDictionary<string, string>? PromptTemplateBaseline { get; set; }

    /// <summary>
    /// SkillGate Tier 1 (runtime governance) — the skills this agent will expose, to check for drift at
    /// construction time. Typically <c>MafSkillScanner.ScanFileSkillsWithInfoAsync(...).Skills</c>, run by the
    /// caller BEFORE this call (<c>UseGatekeeper</c> executes before <c>.Build()</c>, so it cannot read a live
    /// agent's own skill source — same reason <see cref="KnownTools"/> is caller-supplied, see its remarks).
    /// Paired with <see cref="SkillBaselinePath"/>; both must be set TOGETHER for the check to run (leave BOTH
    /// unset to disable it — setting exactly one throws <see cref="InvalidOperationException"/>, same
    /// fail-loud-on-half-configuration discipline as <see cref="PromptTemplates"/>/<see cref="PromptTemplateBaseline"/>).
    /// </summary>
    public IReadOnlyList<ScannedSkillInfo>? Skills { get; set; }

    /// <summary>
    /// Path to the pinned <see cref="AgentEval.Skills.SkillManifestBaseline"/> JSON file to check
    /// <see cref="Skills"/> against. Unlike <see cref="PromptTemplateBaseline"/> (an in-memory dictionary the
    /// caller manages), this is a FILE PATH — <see cref="SkillGateMode.Bootstrap"/> needs to write an extended
    /// baseline back to the same file it read, so a persistent location is load-bearing here, not just a
    /// convenience. When both this and <see cref="Skills"/> are set, <c>UseGatekeeper</c> computes drift
    /// EAGERLY at construction time via <see cref="SkillGateConstructionCheck.CheckAndEnforce"/> and throws
    /// <see cref="SkillDriftException"/> immediately on a blocking finding (fail-closed, the same pattern as
    /// <see cref="PromptTemplateBaseline"/>). A path that does not exist yet is treated as an EMPTY baseline,
    /// not an error — under <see cref="SkillGateMode.Strict"/> (the default) that refuses every skill until an
    /// initial baseline is captured (via <c>agenteval skills baseline approve</c> or a one-time
    /// <see cref="SkillGateMode.Bootstrap"/> run); under <see cref="SkillGateMode.Bootstrap"/> it seeds the
    /// file from this first construction.
    /// </summary>
    public string? SkillBaselinePath { get; set; }

    /// <summary>
    /// How <see cref="SkillGateConstructionCheck"/> handles a skill in <see cref="Skills"/> with no entry at
    /// all in the baseline at <see cref="SkillBaselinePath"/>. Defaults to <see cref="SkillGateMode.Strict"/> —
    /// see that enum for what each mode does and why Strict is the safe default.
    /// </summary>
    public SkillGateMode SkillGateMode { get; set; } = SkillGateMode.Strict;

    /// <summary>
    /// Fluent sugar for setting <see cref="Skills"/>/<see cref="SkillBaselinePath"/>/<see cref="SkillGateMode"/>
    /// together — SkillGate Tier 1's one-call configuration entry point, matching the shape shown in its own
    /// design doc. Returns <see langword="this"/> so it can chain inside the <c>configure</c> callback; not
    /// required — setting the three properties individually has the identical effect.
    /// </summary>
    public GatekeeperOptions WithSkillGate(IReadOnlyList<ScannedSkillInfo> skills, string baselinePath, SkillGateMode mode = SkillGateMode.Strict)
    {
        Skills = skills ?? throw new ArgumentNullException(nameof(skills));
        SkillBaselinePath = string.IsNullOrWhiteSpace(baselinePath)
            ? throw new ArgumentException("baselinePath must be non-empty.", nameof(baselinePath))
            : baselinePath;
        SkillGateMode = mode;
        return this;
    }
}
