// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Testing;
using AgentEval.Tests.TestHelpers;
using Microsoft.Extensions.AI;
using Xunit;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Tests.Testing;

/// <summary>
/// <see cref="ConversationRunner"/> lists the tools a turn called and checks <c>ExpectedTools</c> by name. On MAF 1.25+
/// (#9152) the run after an approval request that nobody answered returns the call MAF re-created for the rejected
/// request, with a "rejected" result. That tool never ran, so it must not be listed or satisfy an expected tool.
/// </summary>
public class ConversationRunnerRejectedApprovalTests
{
    private static ConversationalTestCase ExpectRefund(params string[] userTurns)
    {
        var builder = ConversationalTestCase.Create("refund");
        foreach (var turn in userTurns)
            builder.AddUserTurn(turn);
        return builder.ExpectTools(UnansweredApprovalRun.ToolName).Build();
    }

    // Fails on HEAD 143696a9: every FunctionCallContent was listed, so the re-created call satisfied ExpectedTools and
    // the case passed. Synthetic response in the MAF 1.27 shape.
    [Fact]
    public async Task RejectedApprovalCall_IsNotListed_AndDoesNotSatisfyExpectedTools()
    {
        var runner = new ConversationRunner(new FixedAgent(UnansweredApprovalRun.Response()));

        var result = await runner.RunAsync(ExpectRefund(UnansweredApprovalRun.NewText));

        Assert.DoesNotContain(UnansweredApprovalRun.ToolName, result.ToolsCalled);
        Assert.Empty(Assert.Single(result.ActualTurns, t => t.Role == "assistant").ToolCalls ?? []);
        Assert.False(Assert.Single(result.Assertions, a => a.Name == "ExpectedTools").Passed);
        Assert.False(result.Success);
    }

    // Guard (passes on HEAD too): an approved decision followed by the call and its real result is still listed.
    [Fact]
    public async Task ApprovedApprovalCall_IsStillListed()
    {
        var runner = new ConversationRunner(new FixedAgent(UnansweredApprovalRun.Response(approved: true)));

        var result = await runner.RunAsync(ExpectRefund(UnansweredApprovalRun.NewText));

        Assert.Equal(new[] { UnansweredApprovalRun.ToolName }, result.ToolsCalled);
        Assert.True(Assert.Single(result.Assertions, a => a.Name == "ExpectedTools").Passed);
    }

    // Guard (passes on HEAD too): a request still waiting for a decision carries no FunctionCallContent of its own and
    // stays unlisted. Reading through the approval-aware extractor path must not turn it into a call.
    [Fact]
    public async Task PendingApprovalRequest_IsNotListed()
    {
        var request = new ToolApprovalRequestContent(
            "ficc_" + UnansweredApprovalRun.CallId,
            new FunctionCallContent(UnansweredApprovalRun.CallId, UnansweredApprovalRun.ToolName, UnansweredApprovalRun.RefundArgs()));
        var paused = new AgentResponse
        {
            Text = string.Empty,
            RawMessages = new List<object> { new ChatMessage(ChatRole.Assistant, [request]) },
        };
        var runner = new ConversationRunner(new FixedAgent(paused));

        var result = await runner.RunAsync(ExpectRefund(UnansweredApprovalRun.Pause));

        Assert.Empty(result.ToolsCalled);
        Assert.False(Assert.Single(result.Assertions, a => a.Name == "ExpectedTools").Passed);
    }

    // Fails on HEAD 143696a9. Pins MAF #9152 (1.25+): a real MAF pipeline (scripted model, offline) behind
    // MAFAgentAdapter, which reuses one session. The first turn pauses on the approval request; the second sends new
    // text, MAF rejects the saved request and returns the re-created call. ExpectedTools must not pass on it.
    [Fact]
    public async Task RealMafPipeline_UnansweredApprovalThenNewText_DoesNotSatisfyExpectedTools()
    {
        var (agent, executed) = UnansweredApprovalRun.BuildApprovalGatedAgent();
        var capture = new CapturingAgent(new MAFAgentAdapter(agent));

        var result = await new ConversationRunner(capture)
            .RunAsync(ExpectRefund(UnansweredApprovalRun.Pause, UnansweredApprovalRun.NewText));

        Assert.Null(result.Error);
        Assert.Equal(2, capture.Responses.Count);
        UnansweredApprovalRun.AssertMafAutoRejected(UnansweredApprovalRun.ContentsOf(capture.Responses[1]));
        Assert.Equal(0, executed());
        Assert.DoesNotContain(UnansweredApprovalRun.ToolName, result.ToolsCalled);
        Assert.False(Assert.Single(result.Assertions, a => a.Name == "ExpectedTools").Passed);
        Assert.False(result.Success);
    }

    private sealed class FixedAgent(AgentResponse response) : IEvaluableAgent
    {
        public string Name => "FixedAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(response);
    }

    // Forwards to the wrapped agent and keeps every response, so a test can check what MAF returned.
    private sealed class CapturingAgent(IEvaluableAgent inner) : IEvaluableAgent
    {
        public List<AgentResponse> Responses { get; } = new();

        public string Name => inner.Name;

        public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            var response = await inner.InvokeAsync(prompt, cancellationToken);
            Responses.Add(response);
            return response;
        }
    }
}
