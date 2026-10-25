// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Assertions;
using AgentEval.Core;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Assertions;

public class ConfirmationExecutionEvidenceTests
{
    [Theory]
    [InlineData("emitted", false)]
    [InlineData("emitted", true)]
    [InlineData("denied", false)]
    [InlineData("denied", true)]
    [InlineData("error", false)]
    [InlineData("error", true)]
    [InlineData("success", false)]
    [InlineData("success", true)]
    public void MustConfirmBefore_ExtractedConfirmation_RequiresExecutedSuccess(string state, bool immediate)
    {
        var confirmation = new FunctionCallContent("confirm", "Confirm", new Dictionary<string, object?> { ["target"] = "account" });
        var request = new ToolApprovalRequestContent("approval", confirmation);
        var messages = new List<object>
        {
            new ChatMessage(ChatRole.Assistant, [state == "denied" ? request : confirmation]),
        };
        if (state == "denied")
        {
            messages.Add(new ChatMessage(ChatRole.User, [request.CreateResponse(approved: false)]));
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("confirm", "Tool call invocation rejected.")]));
        }
        else if (state is "success" or "error")
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("confirm", null)
            {
                Exception = state == "error" ? new InvalidOperationException("confirmation failed") : null,
            }]));
        messages.Add(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("transfer", "TransferFunds", new Dictionary<string, object?> { ["account"] = "account" })]));
        var report = ToolUsageExtractor.Extract(messages, includeApprovalGatedCalls: true);

        var error = Record.Exception(() => report.Should().MustConfirmBefore("TransferFunds",
            because: "a request for confirmation is not a completed confirmation",
            requireImmediatePrecedence: immediate,
            confirmationMatches: (confirm, target) => Equals(confirm.Arguments!["target"], target.Arguments!["account"])));

        if (state == "success") Assert.Null(error);
        else Assert.IsType<BehavioralPolicyViolationException>(error);
    }
}
