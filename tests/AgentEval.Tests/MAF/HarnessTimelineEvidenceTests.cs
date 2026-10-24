// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using Microsoft.Extensions.AI;
using Xunit;
using AgentTrace = AgentEval.Tracing.AgentTrace;
using TraceEntry = AgentEval.Tracing.TraceEntry;

namespace AgentEval.Tests.MAF;

public sealed class HarnessTimelineEvidenceTests
{
    [Theory]
    [InlineData("emitted", false, false)]
    [InlineData("denied", false, false)]
    [InlineData("null-result", true, false)]
    [InlineData("failure", true, true)]
    [InlineData("denied-error", false, true)]
    public async Task OrdinaryHarness_ObservedTranscript_PreservesOutcomeAndTimingAvailability(
        string scenario, bool executed, bool error)
    {
        var result = await Run(scenario);
        var canonical = Assert.Single(result.ToolUsage!.Calls);
        Assert.Equal(executed, canonical.WasExecuted);
        Assert.Equal(error, canonical.HasError);
        Assert.False(canonical.HasTiming);

        var timeline = Assert.IsType<ToolCallTimeline>(result.Timeline);
        var invocation = Assert.Single(timeline.Invocations);
        var unavailableOutcome = !executed && !error;
        Assert.Equal(executed && !error, invocation.Succeeded);
        Assert.Equal(unavailableOutcome, invocation.OutcomeNotMeasuredReason is not null);
        Assert.NotNull(invocation.TimingNotMeasuredReason);
        Assert.Equal(error ? "observed failure" : null, invocation.ErrorMessage);
        Assert.Equal(unavailableOutcome ? 1 : 0, timeline.UnmeasuredOutcomeCount);
        Assert.Equal(1, timeline.UnmeasuredDurationCount);
        Assert.Equal(executed && !error ? 1 : 0, timeline.SuccessfulToolCalls);
        Assert.Equal(error ? 1 : 0, timeline.FailedToolCalls);
        Assert.Null(timeline.SlowestTool);

        var statistics = timeline.GetToolStatistics()["lookup"];
        Assert.Equal(timeline.UnmeasuredOutcomeCount, statistics.UnmeasuredOutcomeCount);
        Assert.Equal(1, statistics.UnmeasuredDurationCount);
        Assert.Equal(unavailableOutcome ? null : error ? 0d : 100d, statistics.SuccessRate);
        var marker = unavailableOutcome ? "?" : error ? "✗" : "✓";
        var row = Assert.Single(timeline.ToAsciiDiagram().Split('\n'), s => s.Contains($"│ {marker} lookup"));
        Assert.EndsWith("not measured", row.TrimEnd());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task OrdinaryHarness_ObservedTraceDuration_PreservesMeasuredZeroAndNonzero(int milliseconds)
    {
        var trace = new AgentTrace();
        trace.AddEntry(TraceEntry.ForToolExecution(0, null, "lookup", null, null,
            milliseconds, succeeded: true, error: null));
        var result = await Run("null-result", trace);

        Assert.True(Assert.Single(result.ToolUsage!.Calls).HasTiming);
        var timeline = Assert.IsType<ToolCallTimeline>(result.Timeline);
        var invocation = Assert.Single(timeline.Invocations);
        Assert.True(invocation.Succeeded);
        Assert.Null(invocation.OutcomeNotMeasuredReason);
        Assert.Null(invocation.TimingNotMeasuredReason);
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), invocation.Duration);
        Assert.Equal(0, timeline.UnmeasuredOutcomeCount);
        Assert.Equal(0, timeline.UnmeasuredDurationCount);
        Assert.NotNull(timeline.SlowestTool);
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), timeline.TotalToolTime);
        Assert.Equal(100d, timeline.GetToolStatistics()["lookup"].SuccessRate);
        var row = Assert.Single(timeline.ToAsciiDiagram().Split('\n'), s => s.Contains("│ ✓ lookup"));
        Assert.EndsWith($"{milliseconds}ms", row.TrimEnd());
    }

    private static Task<TestResult> Run(string scenario, AgentTrace? trace = null)
    {
        var call = new FunctionCallContent("c1", "lookup", null);
        var messages = new List<ChatMessage>();
        if (scenario.StartsWith("denied", StringComparison.Ordinal))
        {
            var request = new ToolApprovalRequestContent("approval", call);
            messages.Add(new ChatMessage(ChatRole.Assistant, [request]));
            messages.Add(new ChatMessage(ChatRole.User, [request.CreateResponse(approved: false)]));
        }
        else
            messages.Add(new ChatMessage(ChatRole.Assistant, [call]));

        if (scenario != "emitted")
            messages.Add(new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent("c1", null)
                {
                    Exception = scenario is "failure" or "denied-error"
                        ? new InvalidOperationException("observed failure") : null,
                },
            ]));
        messages.Add(new ChatMessage(ChatRole.Assistant, "answer"));
        return new MAFEvaluationHarness(verbose: false).RunEvaluationAsync(
            new ChatClientAgentAdapter(new TranscriptClient(messages)),
            new TestCase { Name = scenario, Input = "look up an answer" },
            new EvaluationOptions
            {
                EvaluateResponse = false, TrackTools = true, TrackPerformance = false,
                IncludeApprovalGatedToolCalls = true, GlassBoxTrace = trace, Verbose = false,
            });
    }

    private sealed class TranscriptClient(List<ChatMessage> transcript) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse(transcript));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
