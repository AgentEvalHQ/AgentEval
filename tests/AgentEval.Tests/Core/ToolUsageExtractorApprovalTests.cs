// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Output;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Core;

/// <summary>
/// ADR-030 Slice 0.5 (defect D-e, tracker AE-03). <c>ToolUsageExtractor</c> matched
/// <c>FunctionCallContent</c> only. MAF answers an approval-required call with a
/// <c>ToolApprovalRequestContent</c> that <i>wraps</i> the <c>FunctionCallContent</c> in <c>.ToolCall</c>
/// and emits no <c>FunctionCallContent</c> of its own — and the conversion is all-or-nothing across the
/// response — so every approval-gated call was invisible and <c>NeverCallTool("PlaceOrder")</c> had a
/// chance floor of 1.0. Seeing them is <b>opt-in</b> (it is the one change that can turn a red test
/// green); the default extractor now counts what it drops and the harness logs it.
/// </summary>
public class ToolUsageExtractorApprovalTests
{
    private static ChatMessage Assistant(params AIContent[] contents) => new(ChatRole.Assistant, contents);
    private static ChatMessage User(params AIContent[] contents) => new(ChatRole.User, contents);
    private static ChatMessage Tool(params AIContent[] contents) => new(ChatRole.Tool, contents);

    private static FunctionCallContent Call(string id, string name, IDictionary<string, object?>? args = null) => new(id, name, args);

    private static ToolApprovalRequestContent Gated(string requestId, FunctionCallContent call) => new(requestId, call);

    private static readonly Dictionary<string, object?> OrderArgs = new() { ["sku"] = "ABC-123", ["qty"] = 2 };

    // ── The floor ────────────────────────────────────────────────────────────

    [Fact]
    public void ApprovalGatedCall_IsVisible()
    {
        // The §8 acceptance test. Opt in, and the gated PlaceOrder is a call: not executed, approval requested.
        var messages = new List<object> { Assistant(Gated("req-1", Call("c1", "PlaceOrder", OrderArgs))) };

        var report = ToolUsageExtractor.Extract(messages, includeApprovalGatedCalls: true);

        var call = Assert.Single(report.Calls);
        Assert.Equal("PlaceOrder", call.Name);
        Assert.Equal("c1", call.CallId);
        Assert.False(call.WasExecuted);
        Assert.Equal(ToolCallRecord.ApprovalRequested, call.ApprovalState);
        Assert.Equal("ABC-123", call.GetArgument<string>("sku"));
        Assert.Equal(1, call.Order);
        Assert.Equal(0, report.DroppedApprovalRequestCount);
    }

    [Fact]
    public void NeverCallTool_OnAnApprovalGatedCall_CanNowFail()
    {
        // The floor test from the tracker: the agent DID call the forbidden tool; it was approval-gated.
        // Before the fix NeverCallTool passed here with probability 1.0 — zero bits of information.
        var messages = new List<object> { Assistant(Gated("req-1", Call("c1", "PlaceOrder", OrderArgs))) };
        var report = ToolUsageExtractor.Extract(messages, includeApprovalGatedCalls: true);

        Assert.Throws<BehavioralPolicyViolationException>(() =>
            report.Should().NeverCallTool("PlaceOrder", because: "orders require human approval"));
    }

    [Fact]
    public void DefaultExtractor_StillDrops_ButCountsWhatItDropped()
    {
        // Default path is unchanged for anyone's numbers — but it is no longer silent about it.
        var messages = new List<object> { Assistant(Gated("req-1", Call("c1", "PlaceOrder", OrderArgs))) };

        var report = ToolUsageExtractor.Extract(messages);

        Assert.Empty(report.Calls);
        Assert.Equal(1, report.DroppedApprovalRequestCount);
    }

    [Fact]
    public void DefaultExtractor_NeverCallTool_IsInconclusive_NotGreen()
    {
        // With the default (blind) extraction the AE-01 marker keeps the check from rendering as a pass:
        // the tool inventory is unknown, so the policy is undecidable — and the drop count says why.
        var messages = new List<object> { Assistant(Gated("req-1", Call("c1", "PlaceOrder", OrderArgs))) };
        var report = ToolUsageExtractor.Extract(messages);

        using var scope = AgentEvalScope.Collecting();
        report.Should().NeverCallTool("PlaceOrder", because: "orders require human approval");
        scope.Dispose();

        Assert.Equal(AssertionOutcome.Inconclusive, Assert.Single(scope.Results).Outcome);
        Assert.Equal(1, report.DroppedApprovalRequestCount);
    }

    // ── Not-executed is still a call ─────────────────────────────────────────

    [Fact]
    public void RejectedApproval_IsStillACall_AndNotExecuted()
    {
        // FunctionInvokingChatClient generates a FAILED FunctionResultContent for a rejected approval.
        // A rejection result is not an execution: "the agent tried" is exactly what NeverCallTool asks.
        var request = Gated("req-1", Call("c1", "PlaceOrder", OrderArgs));
        var messages = new List<object>
        {
            Assistant(request),
            User(request.CreateResponse(approved: false, reason: "not today")),
            Tool(new FunctionResultContent("c1", "Tool call invocation rejected.")),
        };

        var report = ToolUsageExtractor.Extract(messages, includeApprovalGatedCalls: true);

        var call = Assert.Single(report.Calls);
        Assert.Equal("PlaceOrder", call.Name);
        Assert.False(call.WasExecuted);
        Assert.Equal(ToolCallRecord.ApprovalRejected, call.ApprovalState);
        Assert.Equal("Tool call invocation rejected.", call.Result);
        Assert.Throws<BehavioralPolicyViolationException>(() =>
            report.Should().NeverCallTool("PlaceOrder", because: "orders require human approval"));
    }

    [Fact]
    public void ApprovedApproval_ThenResult_IsExecuted()
    {
        var request = Gated("req-1", Call("c1", "PlaceOrder", OrderArgs));
        var messages = new List<object>
        {
            Assistant(request),
            User(request.CreateResponse(approved: true)),
            Tool(new FunctionResultContent("c1", "order-778")),
        };

        var report = ToolUsageExtractor.Extract(messages, includeApprovalGatedCalls: true);

        var call = Assert.Single(report.Calls);
        Assert.True(call.WasExecuted);
        Assert.Equal(ToolCallRecord.ApprovalApproved, call.ApprovalState);
        Assert.Equal("order-778", call.Result);
    }

    [Fact]
    public void ApprovedCall_RecreatedByFunctionInvokingChatClient_IsNotDoubleCounted()
    {
        // FICC "recreates FunctionCallContent for any ToolApprovalResponseContent that hasn't been
        // executed yet" before invoking. The recreated call has the same CallId and name as the
        // request it came from; it is the same call, not a second one.
        var request = Gated("req-1", Call("c1", "PlaceOrder", OrderArgs));
        var messages = new List<object>
        {
            Assistant(request),
            User(request.CreateResponse(approved: true)),
            Assistant(Call("c1", "PlaceOrder", OrderArgs)),
            Tool(new FunctionResultContent("c1", "order-778")),
        };

        var report = ToolUsageExtractor.Extract(messages, includeApprovalGatedCalls: true);

        var call = Assert.Single(report.Calls);
        Assert.True(call.WasExecuted);
        Assert.Equal(ToolCallRecord.ApprovalApproved, call.ApprovalState);
    }

    [Fact]
    public void LoneApprovalResponse_YieldsTheCallItWraps()
    {
        // The response carries the FunctionCallContent the agent emitted in an earlier run. Unlike a
        // lone FunctionResultContent, it is evidence of a call, so it is recorded (never executed here).
        var request = Gated("req-1", Call("c1", "PlaceOrder", OrderArgs));
        var messages = new List<object> { User(request.CreateResponse(approved: false)) };

        var report = ToolUsageExtractor.Extract(messages, includeApprovalGatedCalls: true);

        var call = Assert.Single(report.Calls);
        Assert.Equal("PlaceOrder", call.Name);
        Assert.False(call.WasExecuted);
        Assert.Equal(ToolCallRecord.ApprovalRejected, call.ApprovalState);
    }

    // ── The contamination case ───────────────────────────────────────────────

    [Fact]
    public void OneApprovalRequiredTool_PlusTwoOrdinary_AllThreeVisible()
    {
        // Microsoft's all-or-nothing note: one ApprovalRequiredAIFunction in a response converts EVERY
        // FunctionCallContent in that response to an approval request, even the ordinary ones.
        var messages = new List<object>
        {
            Assistant(
                Gated("req-1", Call("c1", "SearchCatalog")),
                Gated("req-2", Call("c2", "PlaceOrder", OrderArgs)),
                Gated("req-3", Call("c3", "GetPrice"))),
        };

        var opted = ToolUsageExtractor.Extract(messages, includeApprovalGatedCalls: true);
        var blind = ToolUsageExtractor.Extract(messages);

        Assert.Equal(new[] { "SearchCatalog", "PlaceOrder", "GetPrice" }, opted.ToolNames.ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, opted.Calls.Select(c => c.Order).ToArray());
        Assert.All(opted.Calls, c => Assert.Equal(ToolCallRecord.ApprovalRequested, c.ApprovalState));
        Assert.Empty(blind.Calls);
        Assert.Equal(3, blind.DroppedApprovalRequestCount);
    }

    [Fact]
    public void OrdinaryCalls_AreUnaffected_OnBothPaths()
    {
        var messages = new List<object>
        {
            Assistant(Call("c1", "SearchCatalog")),
            Tool(new FunctionResultContent("c1", "[]")),
        };

        var opted = ToolUsageExtractor.Extract(messages, includeApprovalGatedCalls: true);
        var blind = ToolUsageExtractor.Extract(messages);

        foreach (var report in new[] { opted, blind })
        {
            var call = Assert.Single(report.Calls);
            Assert.True(call.WasExecuted);
            Assert.Null(call.ApprovalState);
            Assert.Equal(0, report.DroppedApprovalRequestCount);
        }
    }

    [Fact]
    public void AgentResponseOverload_HonoursTheOptIn()
    {
        var response = new AgentResponse
        {
            Text = "I need approval to place that order.",
            RawMessages = new List<object> { Assistant(Gated("req-1", Call("c1", "PlaceOrder", OrderArgs))) },
        };

        Assert.Single(ToolUsageExtractor.Extract(response, includeApprovalGatedCalls: true).Calls);
        Assert.Empty(ToolUsageExtractor.Extract(response).Calls);
        Assert.Equal(1, ToolUsageExtractor.Extract(response).DroppedApprovalRequestCount);
    }

    // ── The default extractor is loud ────────────────────────────────────────

    [Fact]
    public void DefaultToolUsageExtractor_WithLogger_WarnsWhenItDrops()
    {
        var logger = new CapturingLogger();
        var extractor = new DefaultToolUsageExtractor(logger);
        var messages = new List<object> { Assistant(Gated("req-1", Call("c1", "PlaceOrder", OrderArgs))) };

        var report = extractor.Extract(messages);

        Assert.Empty(report.Calls);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("approval", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PlaceOrder", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultToolUsageExtractor_OptedIn_SeesTheCall_AndDoesNotWarn()
    {
        var logger = new CapturingLogger();
        var extractor = new DefaultToolUsageExtractor(logger, includeApprovalGatedCalls: true);
        var messages = new List<object> { Assistant(Gated("req-1", Call("c1", "PlaceOrder", OrderArgs))) };

        var report = extractor.Extract(messages);

        Assert.Single(report.Calls);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void DefaultToolUsageExtractor_Singleton_IsStillTheBlindDefault()
    {
        var messages = new List<object> { Assistant(Gated("req-1", Call("c1", "PlaceOrder", OrderArgs))) };

        var report = DefaultToolUsageExtractor.Instance.Extract(messages);

        Assert.Empty(report.Calls);
        Assert.Equal(1, report.DroppedApprovalRequestCount);
    }

    // ── The harness ──────────────────────────────────────────────────────────

    private sealed class GatedAgent : IEvaluableAgent
    {
        public string Name => "GatedAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse
            {
                Text = "I need your approval to place the order.",
                RawMessages = new List<object> { Assistant(Gated("req-1", Call("c1", "PlaceOrder", OrderArgs))) },
            });
    }

    [Fact]
    public async Task Harness_DefaultOptions_DropsTheCall_AndWarns()
    {
        var logger = new CapturingLogger();
        var harness = new MAFEvaluationHarness(evaluator: null, logger);
        var testCase = new TestCase { Name = "gated", Input = "Order two of ABC-123" };

        var result = await harness.RunEvaluationAsync(new GatedAgent(), testCase);

        Assert.NotNull(result.ToolUsage);
        Assert.Empty(result.ToolUsage!.Calls);
        Assert.Equal(1, result.ToolUsage.DroppedApprovalRequestCount);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("approval", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(nameof(EvaluationOptions.IncludeApprovalGatedToolCalls), warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Harness_OptIn_SeesTheGatedCall()
    {
        var logger = new CapturingLogger();
        var harness = new MAFEvaluationHarness(evaluator: null, logger);
        var testCase = new TestCase { Name = "gated", Input = "Order two of ABC-123" };

        var result = await harness.RunEvaluationAsync(new GatedAgent(), testCase,
            new EvaluationOptions { IncludeApprovalGatedToolCalls = true });

        var call = Assert.Single(result.ToolUsage!.Calls);
        Assert.Equal("PlaceOrder", call.Name);
        Assert.False(call.WasExecuted);
        Assert.Equal(ToolCallRecord.ApprovalRequested, call.ApprovalState);
        Assert.Equal(0, result.ToolUsage.DroppedApprovalRequestCount);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("approval", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<BehavioralPolicyViolationException>(() =>
            result.ToolUsage.Should().NeverCallTool("PlaceOrder", because: "orders require human approval"));
    }

    // ── Test logger ──────────────────────────────────────────────────────────

    internal sealed class CapturingLogger : IAgentEvalLogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public void Log(LogLevel level, string message) => Entries.Add((level, message));
        public void Log(LogLevel level, Exception exception, string message) => Entries.Add((level, message + " " + exception.Message));
        public void Log(LogLevel level, string message, params (string Key, object? Value)[] properties) => Entries.Add((level, message));
        public void LogMetricResult(MetricResult result) { }
        public void LogFailure(FailureReport report) { }
        public void LogTimeline(ToolCallTimeline timeline) { }
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable BeginScope(string scopeName, params (string Key, object? Value)[] properties) => new NoopScope();

        private sealed class NoopScope : IDisposable { public void Dispose() { } }
    }
}
