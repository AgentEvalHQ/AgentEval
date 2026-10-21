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
    /// <summary>The input and output counts, or null when <paramref name="usage"/> is null or lacks either one.</summary>
    public static (int Prompt, int Completion)? Counts(UsageDetails? usage) =>
        usage is { InputTokenCount: { } input, OutputTokenCount: { } output }
            ? (Saturate(input), Saturate(output))
            : null;

    /// <summary>A <see cref="TokenUsage"/> when both counts were reported, otherwise null.</summary>
    public static TokenUsage? FromUsage(UsageDetails? usage) =>
        Counts(usage) is { } c ? new TokenUsage { PromptTokens = c.Prompt, CompletionTokens = c.Completion } : null;

    private static int Saturate(long count) => count > int.MaxValue ? int.MaxValue : count < 0 ? 0 : (int)count;
}
