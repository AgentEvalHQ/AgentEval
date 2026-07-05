// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Encodings.Web;
using System.Text.Json;
using AgentEval.MAF.Gatekeeper;

namespace AgentEval.RedTeam.Gatekeeper;

/// <summary>
/// Gatekeeper M3 (the moat) — runs a <b>deterministic</b> red-team <see cref="IProbeEvaluator"/> as a runtime
/// tool gate: the same evaluator that scores attack-success in an offline red-team run now BLOCKS a live tool
/// call whose arguments would trip it. This closes the eval → red-team → enforcement loop (a probe that
/// <em>attacks</em> now <em>guards</em>). Attack-succeeded ⇒ Block; Resisted/Inconclusive ⇒ Allow.
/// </summary>
/// <remarks>
/// <para><b>Cost (PERF/SEC).</b> An <see cref="IProbeEvaluator"/> exposes no cost, so the CALLER declares the
/// <see cref="GateCost"/>. Network/Llm evaluators are rejected at construction — an LLM-judge or package-registry
/// call on the hot tool path would stall every tool invocation (DoS) and risks a fabricated verdict. Only
/// <see cref="GateCost.PureCode"/>/<see cref="GateCost.Bounded"/> evaluators run inline. (This fixes the
/// unguarded-inline-LLM shape of the sibling <c>SafetyMetricGate</c>, which takes any metric with no cost check.)
/// Run expensive judges as a shadow/offline evaluator instead.</para>
/// <para><b>Adapter.</b> The tool-gate seam has no agent <em>response</em> yet — the call itself is the candidate
/// compromise (pre-execution). So the evaluator scans a SYNTHESIZED surface: the tool name + its serialized
/// arguments. This fits CONTENT evaluators (token/regex/decode marker scans). For forbidden-tool-NAME detection
/// use <see cref="AgentEval.MAF.Gatekeeper.ForbiddenToolGate"/> / <see cref="CanaryToolGate"/> instead.</para>
/// </remarks>
public sealed class ProbeEvaluatorGate : IToolGate
{
    private static readonly AttackProbe SentinelProbe = new()
    {
        Id = "gatekeeper-runtime",
        Prompt = string.Empty,
        Difficulty = Difficulty.Moderate,
    };

    // Relaxed encoder: default JSON escaping turns injection metacharacters (< > & ' +) into escape forms, so a
    // content evaluator scanning for them would never match (the same fail-open ArgumentPatternGate had).
    private static readonly JsonSerializerOptions ScanOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IProbeEvaluator _evaluator;

    /// <inheritdoc/>
    public string PolicyName { get; }

    /// <inheritdoc/>
    public GateCost Cost { get; }

    /// <summary>Adapts a deterministic probe evaluator as a tool gate. Network/Llm evaluators are rejected.</summary>
    /// <param name="evaluator">The red-team success oracle (must be pure-code/bounded).</param>
    /// <param name="cost">The caller-declared cost (IProbeEvaluator carries none). Must be PureCode or Bounded.</param>
    /// <param name="policyName">Optional policy name; defaults to <c>ProbeEvaluatorGate(&lt;evaluator name&gt;)</c>.</param>
    public ProbeEvaluatorGate(IProbeEvaluator evaluator, GateCost cost, string? policyName = null)
    {
        ArgumentNullException.ThrowIfNull(evaluator);

        if (cost is GateCost.Network or GateCost.Llm)
        {
            throw new ArgumentException(
                $"ProbeEvaluatorGate runs inline on the tool path; a {cost} evaluator (LLM judge / network " +
                "registry) would stall every tool call and risk a fabricated verdict. Only PureCode/Bounded " +
                "evaluators are allowed — run expensive judges as a shadow/offline evaluator instead.", nameof(cost));
        }

        // Belt-and-suspenders: reject known LLM-backed evaluators even if mis-declared as cheap.
        var typeName = evaluator.GetType().Name;
        if (typeName is "LLMJudgeEvaluator" or "LikertJudgeEvaluator")
        {
            throw new ArgumentException(
                $"{typeName} is LLM-backed and cannot run inline as a tool gate; declare it as a shadow " +
                "evaluator instead.", nameof(evaluator));
        }

        _evaluator = evaluator;
        Cost = cost;
        PolicyName = policyName ?? $"ProbeEvaluatorGate({evaluator.Name})";
    }

    /// <inheritdoc/>
    public async ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        var candidate = BuildCandidateText(call);
        var result = await _evaluator.EvaluateAsync(SentinelProbe, candidate, cancellationToken).ConfigureAwait(false);

        // Attack-succeeded ⇒ the call trips the evaluator ⇒ block. Resisted/Inconclusive ⇒ allow (never
        // fabricate a block on an uncertain verdict — honesty over false positives).
        return result.AttackSucceeded
            ? ToolGateVerdict.Block(PolicyName, result.Reason)
            : ToolGateVerdict.Allow(PolicyName);
    }

    private static string BuildCandidateText(GatedToolCall call)
    {
        var args = call.Arguments is null ? "{}" : JsonSerializer.Serialize(call.Arguments, ScanOptions);
        return $"{call.FunctionName} {args}";
    }
}
