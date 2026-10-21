// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.MAF.Evaluators;
using AgentEval.Models;
using Microsoft.Extensions.AI;
using Xunit;

using AgentEvalEvaluationContext = AgentEval.Core.EvaluationContext;

namespace AgentEval.Tests.MAF.Evaluators;

/// <summary>
/// T10: <c>ConversationExtractor.ExtractToolUsage</c> was a parallel copy of <see cref="ToolUsageExtractor"/>. It never marked a call executed, read every earlier turn of a
/// MAF per-turn slice as this turn's, lost a call whose CallId was reused, invented <c>unknown_{callId}</c> records from
/// stray results, never saw an approval-gated call, and returned "not recorded" for a turn the caller knew was complete.
/// It now delegates. Every test here except the guards fails on the old extractor.
/// </summary>
public class ConversationExtractorToolUsageTests
{
    private static ChatMessage User(string text) => new(ChatRole.User, text);
    private static ChatMessage User(params AIContent[] contents) => new(ChatRole.User, contents);
    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);
    private static ChatMessage Assistant(params AIContent[] contents) => new(ChatRole.Assistant, contents);
    private static ChatMessage Tool(params AIContent[] contents) => new(ChatRole.Tool, contents);
    private static ChatResponse Final(string text) => new([new ChatMessage(ChatRole.Assistant, text)]);

    // ── Executed is set when the result is there ─────────────────────────────

    [Fact]
    public void APairedCall_IsMarkedExecuted()
    {
        var report = ConversationExtractor.ExtractToolUsage(
            [User("Search for flights"), Assistant(new FunctionCallContent("call-1", "SearchFlights")),
             Tool(new FunctionResultContent("call-1", "3 flights found"))],
            Final("Found 3 flights."));

        var call = Assert.Single(report!.Calls);
        Assert.True(call.WasExecuted);
        Assert.Equal("3 flights found", call.Result?.ToString());
    }

    [Fact]
    public void AToolThatReturnsNothing_StillRan()
    {
        // A null result is a void tool that ran, not a call that did not.
        var report = ConversationExtractor.ExtractToolUsage(
            [User("Clear the cache"), Assistant(new FunctionCallContent("c1", "ClearCache")), Tool(new FunctionResultContent("c1", null))],
            Final("Done."));

        var call = Assert.Single(report!.Calls);
        Assert.True(call.WasExecuted);
        Assert.Null(call.Result);
    }

    [Fact]
    public void ACallWithNoResult_DidNotRun()
    {
        // Guard (passes before and after): emitted only.
        var report = ConversationExtractor.ExtractToolUsage(
            [User("Book it"), Assistant(new FunctionCallContent("c1", "BookFlight"))],
            Final("Booking now."));

        Assert.False(Assert.Single(report!.Calls).WasExecuted);
    }

    // ── Calls are never lost or invented ─────────────────────────────────────

    [Fact]
    public void AReusedCallId_KeepsBothCalls_AndPairsTheResultWithTheNearest()
    {
        var report = ConversationExtractor.ExtractToolUsage(
            [User("q"), Assistant(new FunctionCallContent("c1", "A")), Assistant(new FunctionCallContent("c1", "B")),
             Tool(new FunctionResultContent("c1", "ok"))],
            Final("done"));

        Assert.Equal(2, report!.Count);
        Assert.Equal("A", report.Calls[0].Name);
        Assert.False(report.Calls[0].WasExecuted);
        Assert.Equal("B", report.Calls[1].Name);
        Assert.True(report.Calls[1].WasExecuted);
    }

    [Fact]
    public void AStrayResult_IsNotInventedIntoACall()
    {
        var messages = new List<ChatMessage> { User("q"), Tool(new FunctionResultContent("x1", "data")) };

        Assert.Null(ConversationExtractor.ExtractToolUsage(messages, Final("done")));
        var complete = ConversationExtractor.ExtractToolUsage(messages, Final("done"), conversationComplete: true);
        Assert.NotNull(complete);
        Assert.Empty(complete!.Calls);
    }

    [Fact]
    public void ARejectedApproval_IsACallThatNeverRan_NotAnInventedRecord()
    {
        // MAF wraps an approval-required call in ToolApprovalRequestContent and generates a result for the rejection.
        // The old extractor never saw the call and turned the generated result into "unknown_c1".
        var request = new ToolApprovalRequestContent("req-1", new FunctionCallContent("c1", "PlaceOrder"));

        var report = ConversationExtractor.ExtractToolUsage(
            [User("Order two of ABC-123"), Assistant(request), User(request.CreateResponse(approved: false, reason: "not today")),
             Tool(new FunctionResultContent("c1", "Tool call invocation rejected."))],
            Final("The order was not placed."));

        var call = Assert.Single(report!.Calls);
        Assert.Equal("PlaceOrder", call.Name);
        Assert.False(call.WasExecuted);
        Assert.Equal(ToolCallRecord.ApprovalRejected, call.ApprovalState);
        Assert.DoesNotContain(report.Calls, c => c.Name.StartsWith("unknown_", StringComparison.Ordinal));
    }

    [Fact]
    public void AResponseMessageAlreadyInTheConversation_IsReadOnce()
    {
        // MAF's BuildEvalItem puts the run's messages in the conversation and the last of them in RawResponse.
        var call = Assistant(new FunctionCallContent("c1", "SearchFlights"));
        var result = Tool(new FunctionResultContent("c1", "found"));

        var report = ConversationExtractor.ExtractToolUsage([User("q"), call, result], new ChatResponse([call, result]));

        Assert.Equal(1, report!.Count);
    }

    // ── The turn, and what "none" means ──────────────────────────────────────

    [Fact]
    public void APerTurnSlice_CountsOnlyItsOwnTurnsCalls()
    {
        // EvalItem.PerTurnItems hands turn 2 the whole conversation so far; turn 1's lookup is not turn 2's.
        var report = ConversationExtractor.ExtractToolUsage(
            [User("Who is the customer?"), Assistant(new FunctionCallContent("c0", "LookupCustomer")),
             Tool(new FunctionResultContent("c0", "Ada")), Assistant("It is Ada."),
             User("Find her flights"), Assistant(new FunctionCallContent("c1", "SearchFlights")),
             Tool(new FunctionResultContent("c1", "2 flights"))],
            Final("Ada has two flights."));

        var call = Assert.Single(report!.Calls);
        Assert.Equal("SearchFlights", call.Name);
        Assert.Equal(1, call.Order);
    }

    [Fact]
    public void ATurnWithNoCall_IsUnknown_UnlessTheCallerVouchesForTheWholeTurn()
    {
        // Without the caller's word a transcript with no call cannot tell "called nothing" from "only the query half was
        // passed" (MAF's own MEAI adapter), so it stays unknown. With it, none called is a recorded, measured zero.
        var messages = new List<ChatMessage>
        {
            User("Look her up"), Assistant(new FunctionCallContent("c0", "LookupCustomer")), Tool(new FunctionResultContent("c0", "Ada")),
            User("Hello"),
        };

        Assert.Null(ConversationExtractor.ExtractToolUsage(messages, Final("Hi!")));
        var complete = ConversationExtractor.ExtractToolUsage(messages, Final("Hi!"), conversationComplete: true);
        Assert.NotNull(complete);
        Assert.Equal(0, complete!.Count);
    }

    // ── End to end through the light-path adapter ────────────────────────────

    private sealed class CapturingMetric : IMetric
    {
        public string Name => "test_capturing_metric";
        public string Description => "Captures the context it is given.";
        public AgentEvalEvaluationContext? Captured { get; private set; }

        public Task<MetricResult> EvaluateAsync(AgentEvalEvaluationContext context, CancellationToken cancellationToken = default)
        {
            Captured = context;
            return Task.FromResult(MetricResult.Pass(Name, 100));
        }
    }

    [Fact]
    public async Task TheLightPathAdapter_HandsTheMetric_ACallMarkedExecuted()
    {
        var metric = new CapturingMetric();

        await new AgentEvalMetricAdapter(metric).EvaluateAsync(
            [User("Search flights"), Assistant(new FunctionCallContent("c1", "SearchFlights")), Tool(new FunctionResultContent("c1", "found"))],
            Final("Found flights."));

        Assert.True(Assert.Single(metric.Captured!.ToolUsage!.Calls).WasExecuted);
    }
}
