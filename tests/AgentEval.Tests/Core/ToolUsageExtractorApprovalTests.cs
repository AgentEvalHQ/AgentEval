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

    // ── MAF 1.25+ auto-rejection of an unanswered request (#9152) ────────────

    // MAF 1.25+ (#9152): a saved approval request that was never answered is rejected when the session runs again
    // (on 1.24 that run threw). That run's response starts with the generated decision, then the call
    // FunctionInvokingChatClient re-creates for it, its generated rejection result, and the answer. These lists are
    // synthetic, built in the shape read from the MAF 1.27 and MEAI 10.10 sources; they are not a recorded MAF run.
    private const string NoAnswerReason = "No approval response was provided for this approval required tool call.";
    private const string NoAnswerResult = "Tool call invocation rejected. " + NoAnswerReason;

    private static ToolApprovalRequestContent PlaceOrderRequest() => Gated("req-1", Call("c1", "PlaceOrder", OrderArgs));

    private static List<object> AutoRejectedNextRun() => new()
    {
        User(PlaceOrderRequest().CreateResponse(approved: false, reason: NoAnswerReason)),
        Assistant(Call("c1", "PlaceOrder", OrderArgs)),
        Tool(new FunctionResultContent("c1", NoAnswerResult)),
        Assistant(new TextContent("I did not place the order.")),
    };

    [Fact]
    public void AutoRejectedNextRun_DefaultPath_RecordsNoCall_AndCountsOneDrop()
    {
        // Pins MAF #9152 (1.25+). Before the fix the re-created call was recorded and its rejection result marked it
        // executed: a tool that never ran read as one that ran, and could satisfy an expected-tool check.
        var report = ToolUsageExtractor.Extract(AutoRejectedNextRun());

        Assert.Empty(report.Calls);
        Assert.False(report.WasToolCalled("PlaceOrder"));
        Assert.Equal(1, report.DroppedApprovalRequestCount);
    }

    [Fact]
    public void AutoRejectedNextRun_OptIn_IsOneRejectedCall_NotExecuted()
    {
        // Pins MAF #9152 (1.25+) on the opt-in path, which already read this shape correctly (regression guard).
        var report = ToolUsageExtractor.Extract(AutoRejectedNextRun(), includeApprovalGatedCalls: true);

        var call = Assert.Single(report.Calls);
        Assert.Equal("PlaceOrder", call.Name);
        Assert.Equal("c1", call.CallId);
        Assert.False(call.WasExecuted);
        Assert.Equal(ToolCallRecord.ApprovalRejected, call.ApprovalState);
        Assert.Equal(NoAnswerResult, call.Result);
        Assert.Equal(0, report.DroppedApprovalRequestCount);
    }

    [Fact]
    public void AutoRejectedNextRun_WithTheRequestEarlierInTheList_IsStillOneDrop_AndNoCall()
    {
        // Pins MAF #9152 (1.25+). The pause and the next run read as one list: the request is the one drop, the
        // decision on it is not a second, and the re-created call is not recorded.
        var messages = new List<object> { Assistant(PlaceOrderRequest()) };
        messages.AddRange(AutoRejectedNextRun());

        var blind = ToolUsageExtractor.Extract(messages);
        var opted = ToolUsageExtractor.Extract(messages, includeApprovalGatedCalls: true);

        Assert.Empty(blind.Calls);
        Assert.Equal(1, blind.DroppedApprovalRequestCount);
        var call = Assert.Single(opted.Calls);
        Assert.False(call.WasExecuted);
        Assert.Equal(ToolCallRecord.ApprovalRejected, call.ApprovalState);
        Assert.Equal(0, opted.DroppedApprovalRequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApprovedDecision_ThenRecreatedCallAndResult_KeepsTodaysBehaviour_OnBothPaths(bool requestInTheList)
    {
        // Regression guard: only a rejected decision hides the re-created call. An approved call ran.
        var request = PlaceOrderRequest();
        var messages = new List<object>();
        if (requestInTheList) messages.Add(Assistant(request));
        messages.Add(User(request.CreateResponse(approved: true)));
        messages.Add(Assistant(Call("c1", "PlaceOrder", OrderArgs)));
        messages.Add(Tool(new FunctionResultContent("c1", "order-778")));
        messages.Add(Assistant(new TextContent("Order placed.")));

        var blind = ToolUsageExtractor.Extract(messages);
        var opted = ToolUsageExtractor.Extract(messages, includeApprovalGatedCalls: true);

        var blindCall = Assert.Single(blind.Calls);
        Assert.True(blindCall.WasExecuted);
        Assert.Null(blindCall.ApprovalState);
        Assert.Equal("order-778", blindCall.Result);
        Assert.Equal(1, blind.DroppedApprovalRequestCount);

        var optedCall = Assert.Single(opted.Calls);
        Assert.True(optedCall.WasExecuted);
        Assert.Equal(ToolCallRecord.ApprovalApproved, optedCall.ApprovalState);
        Assert.Equal("order-778", optedCall.Result);
        Assert.Equal(0, opted.DroppedApprovalRequestCount);
    }

    [Fact]
    public void AutoRejectedCall_DoesNotHideALaterCallThatReusesItsId()
    {
        // Synthetic edge, not a MAF shape: one rejected decision hides exactly one re-created call. A later call
        // that reuses the id is a call of its own and is recorded.
        var messages = AutoRejectedNextRun();
        messages.Add(Assistant(Call("c1", "PlaceOrder", OrderArgs)));
        messages.Add(Tool(new FunctionResultContent("c1", "order-778")));

        var report = ToolUsageExtractor.Extract(messages);

        var call = Assert.Single(report.Calls);
        Assert.True(call.WasExecuted);
        Assert.Equal("order-778", call.Result);
        Assert.Equal(1, report.DroppedApprovalRequestCount);
    }

    [Fact]
    public void AutoRejectedResult_NeverPairsWithAnEarlierUnpairedCallOfTheSameId()
    {
        // Synthetic edge, not a MAF shape: an earlier call with the same id never got a result. The generated
        // rejection result belongs to the skipped re-created call, so it must not mark the earlier call executed.
        var messages = new List<object> { Assistant(Call("c1", "PlaceOrder", OrderArgs)) };
        messages.AddRange(AutoRejectedNextRun());

        var report = ToolUsageExtractor.Extract(messages);

        var call = Assert.Single(report.Calls);
        Assert.False(call.WasExecuted);
        Assert.Null(call.Result);
        Assert.Equal(1, report.DroppedApprovalRequestCount);
    }

    [Fact]
    public void ApprovalGatedToolNames_NamesTheToolOfADecisionWhoseRequestIsNotInTheList()
    {
        // Pins MAF #9152 (1.25+). The drop warning names the dropped tools; in this shape the only gated content is
        // the generated decision, so the name has to come from it.
        var lone = new List<object> { User(PlaceOrderRequest().CreateResponse(approved: false, reason: NoAnswerReason)) };
        var withRequest = new List<object> { Assistant(PlaceOrderRequest()) };
        withRequest.AddRange(AutoRejectedNextRun());

        Assert.Equal(new[] { "PlaceOrder" }, ToolUsageExtractor.ApprovalGatedToolNames(lone).ToArray());
        Assert.Equal(new[] { "PlaceOrder" }, ToolUsageExtractor.ApprovalGatedToolNames(AutoRejectedNextRun()).ToArray());
        // The decision on a request already named is not named again (it is not a second drop either).
        Assert.Equal(new[] { "PlaceOrder" }, ToolUsageExtractor.ApprovalGatedToolNames(withRequest).ToArray());
    }

    [Fact]
    public void DefaultToolUsageExtractor_OnTheAutoRejectedShape_RecordsNoCall_AndTheWarningNamesTheTool()
    {
        // Pins MAF #9152 (1.25+). Before the fix the warning said a call was NOT recorded while the report held it as
        // executed, and it named no tool.
        var logger = new CapturingLogger();

        var report = new DefaultToolUsageExtractor(logger).Extract(AutoRejectedNextRun());

        Assert.Empty(report.Calls);
        Assert.Equal(1, report.DroppedApprovalRequestCount);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("1 approval-gated tool call(s) [PlaceOrder] were seen and NOT recorded", warning.Message, StringComparison.Ordinal);
    }

    // MAF 1.25+ (#9152) when only one of two pending requests is answered: the caller approves c1 (that decision is input,
    // so it is not in the response) and MAF rejects the unanswered c2. Synthetic, in the shape read from the MAF 1.27
    // and MEAI 10.10 sources: the generated decision on c2, both re-created calls, c1's real result, c2's "rejected" one.
    private static readonly Dictionary<string, object?> CancelArgs = new() { ["order"] = "order-500" };

    private static List<object> MixedNextRun() => new()
    {
        User(Gated("req-2", Call("c2", "CancelOrder", CancelArgs)).CreateResponse(approved: false, reason: NoAnswerReason)),
        Assistant(Call("c1", "PlaceOrder", OrderArgs), Call("c2", "CancelOrder", CancelArgs)),
        Tool(new FunctionResultContent("c1", "order-778"), new FunctionResultContent("c2", NoAnswerResult)),
        Assistant(new TextContent("Order placed. The cancellation was not approved.")),
    };

    [Fact]
    public void MixedNextRun_DefaultPath_ApprovedCallIsExecuted_RejectedCallIsHidden_OneDrop()
    {
        // Pins MAF #9152 (1.25+). Fails on HEAD 143696a9: the re-created c2 was recorded and read as executed.
        var report = ToolUsageExtractor.Extract(MixedNextRun());

        var call = Assert.Single(report.Calls);
        Assert.Equal("c1", call.CallId);
        Assert.True(call.WasExecuted);
        Assert.Equal("order-778", call.Result);
        Assert.False(report.WasToolCalled("CancelOrder"));
        Assert.Equal(1, report.DroppedApprovalRequestCount);
        Assert.Equal(new[] { "CancelOrder" }, ToolUsageExtractor.ApprovalGatedToolNames(MixedNextRun()).ToArray());
    }

    [Fact]
    public void MixedNextRun_OptIn_ApprovedCallIsExecuted_RejectedCallIsOneRejectedCall()
    {
        // Pins MAF #9152 (1.25+). Regression guard: the opt-in path read this shape correctly on HEAD 143696a9 too.
        var report = ToolUsageExtractor.Extract(MixedNextRun(), includeApprovalGatedCalls: true);

        Assert.Equal(2, report.Count);
        var placed = Assert.Single(report.Calls, c => c.CallId == "c1");
        Assert.True(placed.WasExecuted);
        Assert.Null(placed.ApprovalState);   // its decision was input, not in these messages
        var cancelled = Assert.Single(report.Calls, c => c.CallId == "c2");
        Assert.False(cancelled.WasExecuted);
        Assert.Equal(ToolCallRecord.ApprovalRejected, cancelled.ApprovalState);
        Assert.Equal(0, report.DroppedApprovalRequestCount);
    }

    [Fact]
    public void RejectedDecisionWithNoCallId_RecreatedCallIsRecorded_ButNeverExecuted()
    {
        // Guard, synthetic (same result on HEAD 143696a9): with no CallId the decision cannot be keyed, so the re-created
        // call is recorded as a plain call. With no CallId its result cannot pair either, so it never reads as executed.
        var messages = new List<object>
        {
            User(Gated("req-1", Call("", "PlaceOrder", OrderArgs)).CreateResponse(approved: false, reason: NoAnswerReason)),
            Assistant(Call("", "PlaceOrder", OrderArgs)),
            Tool(new FunctionResultContent("", NoAnswerResult)),
        };

        var report = ToolUsageExtractor.Extract(messages);

        var call = Assert.Single(report.Calls);
        Assert.False(call.WasExecuted);
        Assert.Null(call.ApprovalState);
        Assert.Equal(1, report.DroppedApprovalRequestCount);
    }

    [Fact]
    public void RejectedNonFunctionDecision_IsOneDrop_OnBothPaths()
    {
        // Guard, synthetic (same result on HEAD 143696a9): a rejected decision on a hosted MCP tool call in the #9152
        // position cannot be a ToolCallRecord on either path, so it is counted as a drop and names no function.
        var mcp = new McpServerToolCallContent("m1", "search_docs", "docs-server");
        var messages = new List<object>
        {
            User(new ToolApprovalRequestContent("req-m1", mcp).CreateResponse(approved: false, reason: NoAnswerReason)),
            Assistant(new TextContent("I could not search the docs.")),
        };

        var blind = ToolUsageExtractor.Extract(messages);
        var opted = ToolUsageExtractor.Extract(messages, includeApprovalGatedCalls: true);

        Assert.Empty(blind.Calls);
        Assert.Equal(1, blind.DroppedApprovalRequestCount);
        Assert.Empty(opted.Calls);
        Assert.Equal(1, opted.DroppedApprovalRequestCount);
        Assert.Empty(ToolUsageExtractor.ApprovalGatedToolNames(messages));
    }

    [Fact]
    public void RejectedDecisionAfterItsCall_DefaultPath_TheRejectedResultIsNotAnExecution()
    {
        // Synthetic order, not a MAF shape (MAF puts the decision first). Fails on HEAD 143696a9, where the default path
        // paired the "rejected" result with the call. It now follows the same rule as the streaming adapters: the call
        // is recorded, and its rejected result is not an execution.
        var messages = new List<object>
        {
            Assistant(Call("c1", "PlaceOrder", OrderArgs)),
            User(PlaceOrderRequest().CreateResponse(approved: false, reason: NoAnswerReason)),
            Tool(new FunctionResultContent("c1", NoAnswerResult)),
        };

        var report = ToolUsageExtractor.Extract(messages);

        var call = Assert.Single(report.Calls);
        Assert.False(call.WasExecuted);
        Assert.Null(call.Result);
        Assert.Equal(1, report.DroppedApprovalRequestCount);
    }

    private sealed class ScriptedTurnsAgent : IEvaluableAgent
    {
        private readonly Queue<List<object>> _turns;

        public ScriptedTurnsAgent(params List<object>[] turns) => _turns = new Queue<List<object>>(turns);

        public string Name => "ScriptedTurnsAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse { Text = "ok", RawMessages = _turns.Dequeue() });
    }

    [Fact]
    public async Task ChatTraceRecorder_PauseThenAutoRejectedNextTurn_HasNoExecutedCall_AndCarriesEveryTurnsDrops()
    {
        // Pins MAF #9152 (1.25+) through the multi-turn recorder. Turn 1 pauses on a request nobody answers; turn 2 is
        // the run MAF rejects it in. Before the fix the combined report held the re-created call as executed and never
        // carried a drop count, so a check that discards a report with dropped calls saw 0.
        var pause = new List<object> { Assistant(PlaceOrderRequest()) };
        var nextRun = AutoRejectedNextRun();
        await using var recorder = new AgentEval.Tracing.ChatTraceRecorder(new ScriptedTurnsAgent(pause, nextRun), "auto-reject");

        await recorder.AddUserTurnAsync("Order two of ABC-123");
        await recorder.AddUserTurnAsync("Never mind. What is the weather?");
        var result = recorder.GetResult();

        var combined = result.CombinedToolUsage!;
        Assert.DoesNotContain(combined.Calls, c => c.WasExecuted);
        Assert.Empty(combined.Calls);
        var perTurnDrops = new[] { pause, nextRun }.Select(t => ToolUsageExtractor.Extract(t).DroppedApprovalRequestCount).ToArray();
        Assert.Equal(new[] { 1, 1 }, perTurnDrops);
        Assert.Equal(perTurnDrops.Sum(), combined.DroppedApprovalRequestCount);
        Assert.All(result.Turns.Where(t => t.IsAgentTurn), t => Assert.Null(t.ToolCalls));
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
