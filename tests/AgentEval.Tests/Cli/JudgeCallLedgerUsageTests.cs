// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>The judge ledger totals the tokens the judge's responses report, for the AEF run's usage.</summary>
public class JudgeCallLedgerUsageTests
{
    private sealed class ReportingJudge(UsageDetails? usage) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"score\": 90}")) { Usage = usage });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "{\"score\": 90}");
            if (usage is not null)
            {
                yield return new ChatResponseUpdate { Contents = [new UsageContent(usage)] };
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task ItSumsTheReportedTokens_OfPlainAndStreamingCalls()
    {
        var ledger = new JudgeCallLedger(new ReportingJudge(new UsageDetails { InputTokenCount = 100, OutputTokenCount = 7 }));

        await ledger.GetResponseAsync([new ChatMessage(ChatRole.User, "grade")]);
        await foreach (var _ in ledger.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "grade")]))
        {
        }

        Assert.True(ledger.ReportedUsage);
        Assert.Equal(200, ledger.InputTokens);
        Assert.Equal(14, ledger.OutputTokens);
        Assert.Equal(2, ledger.Calls);
    }

    [Fact]
    public async Task AJudgeThatReportsNoUsage_LeavesNoTotals()
    {
        var ledger = new JudgeCallLedger(new ReportingJudge(null));

        await ledger.GetResponseAsync([new ChatMessage(ChatRole.User, "grade")]);

        Assert.False(ledger.ReportedUsage);   // the run then names no judge usage rather than a false zero
    }
}
