// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Phase 1, P0-2 + P0-5 + P0-6) — the safe composite builder. Installs run-scope, tool gates,
/// approval interop, and tracing together, in the correct order, in one call — replacing the pattern where a
/// developer manually chains <c>UseAgentEvalGate()</c> → <c>UseAgentEvalToolGate(...)</c> →
/// <c>UseAgentEvalToolApproval(...)</c> → <c>UseAgentEvalShadowJudge(...)</c> themselves and can get the order
/// wrong (confirmed real: <c>docs/gatekeeper/examples.md</c> carries an explicit comment explaining why
/// <c>UseAgentEvalGate()</c> must come first).
/// <para><b>No implicit enforcement default (P0-2).</b> <see cref="GatekeeperEnforcement"/> is a required
/// parameter, not a default — the previous, still-supported low-level <c>UseAgentEvalToolGate</c>/
/// <c>UseAgentEvalGate</c> silently defaulted to WarnOnly, so a gate's <c>Block</c> verdict was, by default,
/// only logged. There is no equivalent silent path here.</para>
/// <para><b>Composition order:</b> run gate (establishes <see cref="AgentRunScope"/>, runs pre/post chat
/// gates) → tool gate (blocks/mutates live tool calls) → approval interop (escalates borderline calls to a
/// human) → shadow judge (off-hot-path async judgement). Matches the order every runnable Gatekeeper sample in
/// <c>docs/gatekeeper/examples.md</c> already uses.</para>
/// </summary>
public static class AgentEvalGatekeeperExtensions
{
    /// <summary>
    /// The one-call Gatekeeper composite: run-scope + tool gates + approval interop + shadow judge, correctly
    /// ordered, sharing one trace. See <see cref="GatekeeperOptions"/> for what you can configure.
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="enforcement">
    /// REQUIRED — how a gate's <c>Block</c> finding is enforced across every mechanism this builder composes.
    /// There is no default; see <see cref="GatekeeperEnforcement"/>.
    /// </param>
    /// <param name="configure">Populates a <see cref="GatekeeperOptions"/> — gates, trace, telemetry, shadow-judge pump, coverage check.</param>
    public static AIAgentBuilder UseGatekeeper(
        this AIAgentBuilder builder,
        GatekeeperEnforcement enforcement,
        Action<GatekeeperOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new GatekeeperOptions();
        configure(options);

        // P0-1 (bundled): refuse construction now, eagerly, if a high-risk tool has zero protecting gate.
        if (options.RefuseUnprotectedHighRiskTools)
        {
            if (options.KnownTools is null)
            {
                throw new InvalidOperationException(
                    "GatekeeperOptions.RefuseUnprotectedHighRiskTools is set but GatekeeperOptions.KnownTools is " +
                    "null — UseGatekeeper cannot read an agent's tool list at registration time (it runs before " +
                    ".Build()). Pass the same list you set on ChatOptions.Tools.");
            }

            GatekeeperCoverageAnalyzer.AnalyzeOrThrow(options.KnownTools, options.ToolGates.ToArray(), options.CoverageAnalyzeOptions);
        }

        // P0-6: will a run scope actually be established? Same condition that decides whether UseAgentEvalGate
        // gets called below — calling it ALWAYS establishes a scope (there is no way to call it "scopeless"),
        // so this is exact, not a heuristic.
        var scopeWillBeEstablished = options.EstablishRunScope || options.PreGates.Count > 0 || options.PostGates.Count > 0;

        if (!scopeWillBeEstablished && enforcement != GatekeeperEnforcement.Observe)
        {
            var offenders = options.ToolGates
                .Where(g => g.Requirements.HasFlag(GateRequirements.RunScope))
                .Select(g => g.PolicyName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (offenders.Length > 0)
            {
                throw new InvalidOperationException(
                    $"UseGatekeeper: {string.Join(", ", offenders)} declare GateRequirements.RunScope, but " +
                    "GatekeeperOptions.EstablishRunScope is false, no pre/post gate implicitly establishes one, " +
                    $"and Enforcement is {enforcement} (not Observe). Without a run scope, these gates silently " +
                    "fall back to ONE process-wide shared state (self-documented on RunLedger/SequenceGate) — " +
                    "safe for advisory Observe mode, not for enforcement. Set EstablishRunScope = true (the " +
                    "default), or establish AgentRunScope yourself before this middleware runs.");
            }
        }

        if (enforcement == GatekeeperEnforcement.Observe)
        {
            options.BannerWriter?.WriteLine("AGENTEVAL GATEKEEPER IS RUNNING IN OBSERVE-ONLY MODE. NO TOOL CALLS WILL BE BLOCKED.");
            options.BannerWriter?.WriteLine(
                $"  ({options.ToolGates.Count} tool gate(s), {options.PreGates.Count + options.PostGates.Count} run gate(s) " +
                $"registered — findings are recorded to the trace but never enforced.)");
        }

        var toolPolicy = ToToolGatePolicy(enforcement);
        var evalPolicy = ToEvalGatePolicy(enforcement);

        var result = builder;

        if (scopeWillBeEstablished)
        {
            result = result.UseAgentEvalGate(
                pre: options.PreGates.Count > 0 ? options.PreGates.ToArray() : null,
                post: options.PostGates.Count > 0 ? options.PostGates.ToArray() : null,
                policy: evalPolicy,
                trace: options.Trace);
        }

        if (options.ToolGates.Count > 0)
        {
            result = result.UseAgentEvalToolGate(options.ToolGates.ToArray(), toolPolicy, options.Trace, options.Telemetry, options.MutationCaptureMode);
        }

        if (options.ApprovalGates.Count > 0)
        {
#pragma warning disable AEGK001 // Gatekeeper ⇄ MAF tool-approval interop is itself an AgentEval-experimental API — deliberately composed here when the caller opts in by registering an approval gate.
            result = result.UseAgentEvalToolApproval(options.ApprovalGates.ToArray(), options.Trace);
#pragma warning restore AEGK001
        }

        if (options.ShadowJudgePump is not null)
        {
            result = result.UseAgentEvalShadowJudge(options.ShadowJudgePump);
        }

        return result;
    }

    /// <summary>
    /// Sugar for <c>UseGatekeeper(builder, GatekeeperEnforcement.Observe, configure)</c> — the review's
    /// suggested split-API shape (P0-2). Nothing is ever blocked; a startup banner makes that explicit.
    /// </summary>
    public static AIAgentBuilder ObserveWithAgentEvalGates(this AIAgentBuilder builder, Action<GatekeeperOptions> configure)
        => builder.UseGatekeeper(GatekeeperEnforcement.Observe, configure);

    /// <summary>
    /// Sugar for <c>UseGatekeeper(builder, level, configure)</c> under a genuinely enforcing level (P0-2).
    /// Defaults to <see cref="GatekeeperEnforcement.Terminate"/> (the strongest level) — "enforce" unqualified
    /// should mean "actually protect me"; pass <see cref="GatekeeperEnforcement.ReplaceResult"/> explicitly for
    /// the softer level. <see cref="GatekeeperEnforcement.Observe"/> is rejected here — use
    /// <see cref="ObserveWithAgentEvalGates"/> for that (the whole point of the split is that the method name
    /// alone tells you which mode you're in).
    /// </summary>
    public static AIAgentBuilder EnforceAgentEvalGates(
        this AIAgentBuilder builder,
        Action<GatekeeperOptions> configure,
        GatekeeperEnforcement level = GatekeeperEnforcement.Terminate)
    {
        if (level == GatekeeperEnforcement.Observe)
        {
            throw new ArgumentException(
                $"EnforceAgentEvalGates requires an enforcing level (ReplaceResult or Terminate); got {level}. " +
                "Use ObserveWithAgentEvalGates for observe-only mode.", nameof(level));
        }

        return builder.UseGatekeeper(level, configure);
    }

    private static ToolGatePolicy ToToolGatePolicy(GatekeeperEnforcement enforcement) => enforcement switch
    {
        GatekeeperEnforcement.Observe => ToolGatePolicy.WarnOnly,
        GatekeeperEnforcement.ReplaceResult => ToolGatePolicy.ReplaceResult,
        GatekeeperEnforcement.Terminate => ToolGatePolicy.Terminate,
        _ => throw new ArgumentOutOfRangeException(nameof(enforcement), enforcement, "unrecognized GatekeeperEnforcement value."),
    };

    private static EvalGatePolicy ToEvalGatePolicy(GatekeeperEnforcement enforcement) => enforcement switch
    {
        GatekeeperEnforcement.Observe => EvalGatePolicy.WarnOnly,
        GatekeeperEnforcement.ReplaceResult => EvalGatePolicy.Redact,
        GatekeeperEnforcement.Terminate => EvalGatePolicy.ThrowOnFail,
        _ => throw new ArgumentOutOfRangeException(nameof(enforcement), enforcement, "unrecognized GatekeeperEnforcement value."),
    };
}
