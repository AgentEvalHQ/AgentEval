// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Text.Json;
using AgentEval.Evals;
using Galaxus.RecommendationAgent.Evals.Graders;
using Galaxus.RecommendationAgent.Tools;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// Reads the SKUs an arm presented, off the PROJECTION rather than off the raw report.
/// </summary>
/// <remarks>
/// <para>
/// One copy, shared by every deterministic eval that asks "what did this arm put in front of the
/// customer?". It is deliberately NOT byte-identical to <see cref="PresentedCall.FromToolUsage"/>,
/// and the one difference is the point: <c>FromToolUsage(null)</c> returns <c>[]</c>
/// (<c>Graders/PresentedCall.cs:78</c>), so a run nobody recorded is indistinguishable from a run in
/// which the arm presented nothing. The projection hands a blind recorder over as
/// <see langword="null"/>, and the evals below decline to answer rather than score it.
/// </para>
/// <para>
/// Errored calls are dropped: the projection guarantees a failure marker leads the result text, so a
/// payload the tool wrote cannot suppress it. Emitted-but-unexecuted calls are KEPT — presenting is
/// an act, and dropping one would hide a leak.
/// </para>
/// </remarks>
internal static class PresentedSkuReader
{
    /// <summary>The SKUs presented in this run, in call order. Never <see langword="null"/>.</summary>
    public static IReadOnlyList<string> From(IReadOnlyList<ToolCall> toolCalls) =>
        [.. toolCalls
            .Where(c => string.Equals(c.Name, PresentedCall.ToolName, StringComparison.OrdinalIgnoreCase))
            .Where(c => c.Result is null
                     || !c.Result.StartsWith(TestRunEvalProjection.ToolErrorResultPrefix, StringComparison.Ordinal))
            .Select(SkuOf)
            .Where(sku => !string.IsNullOrEmpty(sku))];

    /// <summary>
    /// The SKU argument of one presentation call.
    /// </summary>
    /// <remarks>
    /// The same defensive read as <c>PresentedCall.ReadString</c>, for the same recorded reason: an
    /// <c>AIFunctionFactory</c>-marshalled argument arrives as a <see cref="JsonElement"/> far more
    /// often than as a <see cref="string"/>, and a <c>raw as string</c> test would silently drop
    /// every real call.
    /// </remarks>
    public static string SkuOf(ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (call.Arguments is null
            || !call.Arguments.TryGetValue(PresentRecommendationArguments.Sku, out var raw)
            || raw is null)
        {
            return string.Empty;
        }

        return raw switch
        {
            string s => s.Trim(),
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()?.Trim() ?? string.Empty,
            _ => raw.ToString()?.Trim() ?? string.Empty,
        };
    }

    /// <summary>The shared undecidable reason for a run no recorder could see.</summary>
    public static string BlindRecorderReason(string what) =>
        $"no tool recorder saw this run, so nothing here can say {what}. An absent record is not an "
        + "empty one, and scoring it would turn a blindness into a measurement.";
}
