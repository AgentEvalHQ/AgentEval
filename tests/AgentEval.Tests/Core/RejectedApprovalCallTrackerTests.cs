// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Core;

/// <summary>
/// <see cref="RejectedApprovalCallTracker"/> is the one rule every tool reader applies to a call whose approval was
/// rejected (MAF 1.25+ #9152): the call re-created after a rejected decision, and its result, are parts of a call that
/// never ran. Each test reads a synthetic content sequence, not a recorded MAF run. The type is new in this change,
/// so none of these compile on HEAD 143696a9; the readers' tests pin the behaviour against HEAD.
/// </summary>
public class RejectedApprovalCallTrackerTests
{
    private static FunctionCallContent Call(string id, string name = "issue_refund") => new(id, name);

    private static ToolApprovalResponseContent Decision(string id, bool approved, string name = "issue_refund")
        => new ToolApprovalRequestContent("req_" + id, Call(id, name)).CreateResponse(approved);

    private static FunctionResultContent Result(string id, string text = "Tool call invocation rejected.") => new(id, text);

    private static bool[] Read(params AIContent[] contents)
    {
        var tracker = new RejectedApprovalCallTracker();
        return contents.Select(tracker.IsRejectedCallPart).ToArray();
    }

    [Fact]
    public void RejectedDecision_ThenCall_ThenResult_HidesTheCallAndTheResult_NotTheDecision()
    {
        Assert.Equal(new[] { false, true, true }, Read(Decision("c1", approved: false), Call("c1"), Result("c1")));
    }

    [Fact]
    public void RejectedDecision_ThenResultWithoutItsCall_HidesTheResult()
    {
        // The fallback: the call came before the decision (or never came), so only the result is left to hide.
        Assert.Equal(new[] { false, false, true }, Read(Call("c1"), Decision("c1", approved: false), Result("c1")));
        Assert.Equal(new[] { false, true }, Read(Decision("c1", approved: false), Result("c1")));
    }

    [Fact]
    public void ApprovedDecision_IsIgnored()
    {
        Assert.Equal(new[] { false, false, false }, Read(Decision("c1", approved: true), Call("c1"), Result("c1", "refunded")));
    }

    [Fact]
    public void EmptyCallId_IsIgnored_OnTheDecisionTheCallAndTheResult()
    {
        Assert.Equal(new[] { false, false, false }, Read(Decision("", approved: false), Call(""), Result("")));
    }

    [Fact]
    public void OneRejectedDecision_HidesOneCall_ALaterCallThatReusesTheIdIsItsOwn()
    {
        Assert.Equal(
            new[] { false, true, true, false, false },
            Read(Decision("c1", approved: false), Call("c1"), Result("c1"), Call("c1"), Result("c1", "refunded")));
    }

    [Fact]
    public void CallWithTheSameIdButAnotherName_IsNotTheRejectedCall()
    {
        // The result still pairs to the decision's id, so it is hidden by the fallback; the other tool's call is not.
        Assert.Equal(
            new[] { false, false, true },
            Read(Decision("c1", approved: false), Call("c1", "lookup_order"), Result("c1")));
    }

    [Fact]
    public void ResultOfAnotherCall_IsNotHidden()
    {
        Assert.Equal(
            new[] { false, true, false, true },
            Read(Decision("c2", approved: false), Call("c2"), Result("c1", "real"), Result("c2")));
    }
}
