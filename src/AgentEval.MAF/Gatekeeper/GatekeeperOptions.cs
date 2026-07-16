// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
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

    /// <summary>Run-pre chat gates, inspecting the input text before the model sees it.</summary>
    public IList<IChatGate> PreGates { get; } = new List<IChatGate>();

    /// <summary>Run-post chat gates, inspecting the response text.</summary>
    public IList<IChatGate> PostGates { get; } = new List<IChatGate>();

    /// <summary>Tool-approval gates (human-in-the-loop escalation via MAF's <c>UseToolApproval</c>). Left empty ⇒ the approval layer is not wired at all.</summary>
    public IList<IToolApprovalGate> ApprovalGates { get; } = new List<IToolApprovalGate>();

    /// <summary>Adds a tool gate. Sugar for <c>ToolGates.Add(gate)</c> — matches the shape shown in the Gatekeeper hardening review's own example.</summary>
    public void Add(IToolGate gate) => ToolGates.Add(gate ?? throw new ArgumentNullException(nameof(gate)));

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

    /// <summary>Optional shadow-judge pump (caller-owned) for asynchronous, off-hot-path judgement.</summary>
    public ShadowJudgePump? ShadowJudgePump { get; set; }

    /// <summary>
    /// The tool list to run the Phase-1 coverage check (<see cref="GatekeeperCoverageAnalyzer"/>) against, when
    /// <see cref="RefuseUnprotectedHighRiskTools"/> is set. <c>UseGatekeeper</c> cannot read an agent's tool
    /// list at registration time (it runs before <c>.Build()</c>) — pass the same list you set on
    /// <see cref="ChatOptions.Tools"/>.
    /// </summary>
    public IReadOnlyList<AITool>? KnownTools { get; set; }

    /// <summary>
    /// When <see langword="true"/>, <c>UseGatekeeper</c> runs <see cref="GatekeeperCoverageAnalyzer.AnalyzeOrThrow(IEnumerable{AITool}, IReadOnlyList{IToolGate}?, AnalyzeOptions?)"/>
    /// eagerly at registration time and throws <see cref="UnprotectedHighRiskToolException"/> if any high-risk
    /// tool has zero protecting gate. Requires <see cref="KnownTools"/> to be set.
    /// </summary>
    public bool RefuseUnprotectedHighRiskTools { get; set; }

    /// <summary>Optional override of the coverage analyzer's risk heuristic (see <see cref="AnalyzeOptions.IsHighRisk"/>).</summary>
    public AnalyzeOptions? CoverageAnalyzeOptions { get; set; }

    /// <summary>
    /// Where the observe-mode startup banner is written. Defaults to <see cref="Console.Out"/>; set to
    /// <see langword="null"/> to suppress it (e.g. under test, or when your host already surfaces the
    /// enforcement mode through structured logging).
    /// </summary>
    public TextWriter? BannerWriter { get; set; } = Console.Out;
}
