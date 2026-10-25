// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.Core;

/// <summary>
/// The one MEAI <see cref="UsageDetails"/> → AgentEval token-count mapping, shared by the chat-client and MAF
/// adapters, the MAF workflow bridge and the trace recorder.
/// </summary>
/// <remarks>
/// Usage is reported only when BOTH counts were: a provider that sends just <c>TotalTokenCount</c>, or one count, did
/// not report the other, and a missing count is not 0. Each site used to read it as 0, so the harness marked the
/// counts as real (<c>TokensAreEstimated = false</c>) and priced the 0. A null usage takes the harness's estimated
/// path instead, labelled as an estimate.
/// </remarks>
internal static class TokenUsageMapping
{
    /// <summary>The nonnegative input and output counts, or null when either is missing or invalid.</summary>
    public static (int Prompt, int Completion)? Counts(UsageDetails? usage) =>
        usage is { InputTokenCount: >= 0 and { } input, OutputTokenCount: >= 0 and { } output }
            ? (Saturate(input), Saturate(output))
            : null;

    /// <summary>A <see cref="TokenUsage"/> when both counts were reported and nonnegative, otherwise null.</summary>
    public static TokenUsage? FromUsage(UsageDetails? usage) =>
        Counts(usage) is { } c ? new TokenUsage { PromptTokens = c.Prompt, CompletionTokens = c.Completion } : null;

    // Absence, invalid contributions and overflow remain unavailable on subsequent attempts.
    internal static long? Add(long? total, long? contribution) =>
        total is >= 0 and { } sum && contribution is >= 0 and { } count && count <= long.MaxValue - sum
            ? sum + count
            : null;

    // Both components have already been validated. Bound before adding to avoid long overflow.
    internal static int SaturatingTotal(long input, long output) =>
        input >= int.MaxValue || output >= int.MaxValue ? int.MaxValue : Saturate(input + output);

    internal static int SaturatingSum(IEnumerable<int> counts) =>
        counts.Aggregate(0, (sum, count) => SaturatingTotal(sum, count));

    private static int Saturate(long count) => count > int.MaxValue ? int.MaxValue : (int)count;
}
