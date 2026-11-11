// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Runtime.CompilerServices;
using AgentEval.Core;
using AgentEval.MAF;
using Xunit;

namespace AgentEval.Tests.MAF;

public sealed class CachedWorkflowUsageTests
{
    [Theory]
    [InlineData(80L, 70L, 150L)]
    [InlineData(null, 70L, null)]
    [InlineData(101L, 0L, -1L)]
    public async Task ExecuteWorkflow_RepeatedUsage_RetainsCacheCompleteness(long? first, long? second, long? expected)
    {
        async IAsyncEnumerable<WorkflowEvent> Events(string prompt, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new ExecutorAgentResponseEvent("a", "first", new TokenUsage
            {
                PromptTokens = 100, CompletionTokens = 10, CachedInputTokenCount = first
            });
            yield return new ExecutorAgentResponseEvent("a", "second", new TokenUsage
            {
                PromptTokens = 100, CompletionTokens = 10, CachedInputTokenCount = second
            });
            yield return new WorkflowCompleteEvent();
            await Task.CompletedTask;
        }
        var result = await new MAFWorkflowAdapter("cache", Events).ExecuteWorkflowAsync("go");
        var usage = Assert.Single(result.Steps).TokenUsage!;
        Assert.Equal(200, usage.ReportedInputTokenCount);
        Assert.Equal(20, usage.ReportedOutputTokenCount);
        Assert.Equal(expected, usage.CachedInputTokenCount);
    }
}
