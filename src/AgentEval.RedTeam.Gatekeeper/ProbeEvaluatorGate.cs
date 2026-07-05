// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgentEval.MAF.Gatekeeper;
using Microsoft.Extensions.AI;

namespace AgentEval.RedTeam.Gatekeeper;

/// <summary>
/// Gatekeeper M3 (the moat) — runs a <b>deterministic</b> red-team <see cref="IProbeEvaluator"/> as a runtime
/// tool gate: the same evaluator that scores attack-success in an offline red-team run now BLOCKS a live tool
/// call whose arguments would trip it. This closes the eval → red-team → enforcement loop (a probe that
/// <em>attacks</em> now <em>guards</em>).
/// <para><b>Fail closed.</b> Only a clear <c>Resisted</c> verdict allows the call; <c>Succeeded</c> (attack
/// detected) AND <c>Inconclusive</c> (cannot determine) both BLOCK. This deliberately inverts the grading
/// convention (don't score abstention as failure) because a runtime security gate that cannot prove a call safe
/// must not run it — and it turns an evaluator that silently no-ops at this seam into a loud block, not a leak.</para>
/// <para><b>Content evaluators only.</b> Intended for self-contained CONTENT oracles (token / regex / decode
/// marker scans) that read the response text. A <em>behavioral</em> oracle (detection on the
/// <c>AgentResponse</c>/RawMessages overload) or a <em>probe-dependent</em> one (reads <c>probe.ExpectedTokens</c>)
/// no-ops at this seam and — via the fail-closed rule above — blocks EVERYTHING, surfacing the misconfiguration
/// loudly rather than leaking. For forbidden-tool-NAME detection use <see cref="CanaryToolGate"/> /
/// <see cref="AgentEval.MAF.Gatekeeper.ForbiddenToolGate"/> instead.</para>
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

    /// <summary>
    /// This gate exists to ENFORCE (a tripped probe must stop the call). Running it observe-only would let the
    /// flagged call through, so <c>UseAgentEvalToolGate</c> refuses to register it under WarnOnly.
    /// </summary>
    public ToolGatePolicy MinimumPolicy => ToolGatePolicy.ReplaceResult;

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

        // Belt-and-suspenders: reject ANY LLM-backed evaluator even if mis-declared as cheap. Detected generically
        // by an IChatClient reachable from the instance — directly held OR wrapped inside child evaluators (the
        // DecomposedGraders composites hold their LLM leaves as IProbeEvaluator children, not a direct field).
        if (HoldsChatClient(evaluator, new HashSet<object>(ReferenceEqualityComparer.Instance)))
        {
            throw new ArgumentException(
                $"Evaluator '{evaluator.GetType().Name}' is LLM-backed (an IChatClient is reachable from it) and " +
                "cannot run inline as a tool gate — it would stall every tool call and risk a fabricated verdict. " +
                "Declare it as a shadow evaluator instead.", nameof(evaluator));
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

        // FAIL CLOSED on the enforcement path: only a clear RESISTED verdict allows the call. Succeeded blocks
        // (attack detected); Inconclusive also blocks (cannot-determine ⇒ deny). This is the opposite of the
        // GRADING convention (where abstention must not be scored as a failure) — a runtime security gate that
        // cannot prove a call safe must not run it. Crucially, it converts an evaluator that silently no-ops on
        // this seam (a behavioral or probe-dependent oracle returning Inconclusive) into a LOUD block, never a
        // silent allow.
        return result.AttackResisted
            ? ToolGateVerdict.Allow(PolicyName)
            : ToolGateVerdict.Block(PolicyName, result.Reason);
    }

    private static string BuildCandidateText(GatedToolCall call)
    {
        var args = call.Arguments is null ? "{}" : JsonSerializer.Serialize(call.Arguments, ScanOptions);
        return $"{call.FunctionName} {args}";
    }

    // True if an IChatClient is REACHABLE from the evaluator instance — either directly held, or wrapped inside
    // child IProbeEvaluator members (composed graders). Reads field VALUES (not just declared types) so a
    // CompositeEvaluator/OutcomeFilter/ConjunctionGate holding an LLMJudge leaf two levels deep is detected.
    private static bool HoldsChatClient(object evaluator, HashSet<object> visited)
    {
        if (!visited.Add(evaluator))
        {
            return false;   // cycle guard
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var t = evaluator.GetType(); t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(flags))
            {
                if (typeof(IChatClient).IsAssignableFrom(f.FieldType))
                {
                    return true;   // directly held
                }

                if (IsEvaluatorRef(f.FieldType) && ContainsLlmBackedChild(f.GetValue(evaluator), visited))
                {
                    return true;   // reachable through a child evaluator
                }
            }

            if (t.GetProperties(flags).Any(p => typeof(IChatClient).IsAssignableFrom(p.PropertyType)))
            {
                return true;
            }
        }

        return false;
    }

    // A field whose type could hold child evaluators worth recursing into (an IProbeEvaluator, or a collection of them).
    private static bool IsEvaluatorRef(Type t)
        => typeof(IProbeEvaluator).IsAssignableFrom(t)
        || (t.IsArray && t.GetElementType() is { } el && typeof(IProbeEvaluator).IsAssignableFrom(el))
        || (t.IsGenericType && t.GetGenericArguments().Any(a => typeof(IProbeEvaluator).IsAssignableFrom(a)));

    private static bool ContainsLlmBackedChild(object? value, HashSet<object> visited)
    {
        switch (value)
        {
            case null:
                return false;
            case IChatClient:
                return true;
            case IProbeEvaluator child:
                return HoldsChatClient(child, visited);
            case System.Collections.IEnumerable seq:
                foreach (var item in seq)
                {
                    if (item is IProbeEvaluator childItem && HoldsChatClient(childItem, visited))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }
}
