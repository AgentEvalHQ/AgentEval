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
    public async Task GetResponseAsync_WithReportedUsage_SumsPlainAndStreamingTokens()
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
    public async Task GetResponseAsync_WithOnlyInputReported_LeavesTheOutputTotalNull()
    {
        var ledger = new JudgeCallLedger(new ReportingJudge(new UsageDetails { InputTokenCount = 50 }));

        await ledger.GetResponseAsync([new ChatMessage(ChatRole.User, "grade")]);

        Assert.Equal(50, ledger.InputTokens);
        Assert.Null(ledger.OutputTokens);   // unreported, not a false zero
    }

    [Fact]
    public async Task GetResponseAsync_WhenOneCallReportsNoUsage_LeavesTheTotalUnknown()
    {
        var reporting = new ReportingJudge(new UsageDetails { InputTokenCount = 100, OutputTokenCount = 7 });
        var silent = new ReportingJudge(null);
        var calls = 0;
        var ledger = new JudgeCallLedger(new DelegatingChatClientSwitch(() => calls++ == 0 ? reporting : silent));

        await ledger.GetResponseAsync([new ChatMessage(ChatRole.User, "grade")]);
        await ledger.GetResponseAsync([new ChatMessage(ChatRole.User, "grade")]);

        Assert.Null(ledger.InputTokens);   // 100 is not the total of two calls
        Assert.False(ledger.ReportedUsage);
    }

    private sealed class DelegatingChatClientSwitch(Func<IChatClient> next) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            next().GetResponseAsync(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            next().GetStreamingResponseAsync(messages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task GetResponseAsync_WithoutReportedUsage_LeavesTotalsNull()
    {
        var ledger = new JudgeCallLedger(new ReportingJudge(null));

        await ledger.GetResponseAsync([new ChatMessage(ChatRole.User, "grade")]);

        Assert.False(ledger.ReportedUsage);   // the run then names no judge usage rather than a false zero
        Assert.Null(ledger.InputTokens);
    }
}
