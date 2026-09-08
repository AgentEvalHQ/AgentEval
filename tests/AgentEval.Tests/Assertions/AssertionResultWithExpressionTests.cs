// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Assertions;

/// <summary>
/// AE-01 follow-up. The three-valued <see cref="AssertionResult"/> guarded its invariant
/// (<c>Outcome == Passed ⇔ Passed == true</c>) in <c>Outcome</c>'s init accessor only, so a
/// <c>with { Passed = true }</c> copy of an <see cref="AssertionOutcome.Inconclusive"/> result kept
/// <c>Outcome == Inconclusive</c> while reporting <c>Passed == true</c> — a pass, in the flattering
/// direction, on a check that never decided. These tests pin the fix from the copy side.
/// </summary>
public class AssertionResultWithExpressionTests
{
    [Fact]
    public void Undecidable_WithPassedTrue_IsRefused()
    {
        var undecidable = AssertionResult.Undecidable("NeverCallTool(PlaceOrder)", "never available");

        var ex = Assert.Throws<ArgumentException>(() => undecidable with { Passed = true });

        Assert.Contains("Inconclusive", ex.Message);
        Assert.Equal(AssertionOutcome.Inconclusive, undecidable.Outcome);
        Assert.False(undecidable.Passed);
    }

    [Fact]
    public void Undecidable_WithPassedTrueAndOutcomePassed_IsStillRefused()
    {
        // Promoting an undecided result to a pass is exactly the operation that must go through
        // AssertionResult.Pass(...), not through a copy — regardless of how many members the copy
        // also rewrites.
        var undecidable = AssertionResult.Undecidable("HaveCallCount", "timing was never recorded");

        Assert.Throws<ArgumentException>(() =>
            undecidable with { Passed = true, Outcome = AssertionOutcome.Passed });
    }

    [Fact]
    public void Fail_WithPassedTrue_IsAllowed_BecauseNoExplicitOutcomeWasStated()
    {
        // A plain Fail carries no explicit outcome, so a copy that flips Passed is a legitimate
        // (if unusual) re-statement, not a promotion of an undecided check. Outcome follows Passed.
        var failed = AssertionResult.Fail("HaveCalledTool(Search)", "not called");

        var flipped = failed with { Passed = true };

        Assert.True(flipped.Passed);
        Assert.Equal(AssertionOutcome.Passed, flipped.Outcome);
    }

    [Fact]
    public void Undecidable_WithMessageOnly_KeepsInconclusiveAndPassedFalse()
    {
        var undecidable = AssertionResult.Undecidable("MustConfirmBefore(PlaceOrder)", "tool absent");

        var reworded = undecidable with { Message = "tool absent — chance floor 1.0" };

        Assert.Equal(AssertionOutcome.Inconclusive, reworded.Outcome);
        Assert.False(reworded.Passed);
        Assert.True(reworded.IsInconclusive);
    }

    [Fact]
    public void Pass_And_Fail_AndUndecidable_ConstructorPaths_AreUnchanged()
    {
        var pass = AssertionResult.Pass("a");
        var fail = AssertionResult.Fail("b", "why");
        var undecidable = AssertionResult.Undecidable("c", "reason");

        Assert.True(pass.Passed);
        Assert.Equal(AssertionOutcome.Passed, pass.Outcome);
        Assert.False(fail.Passed);
        Assert.Equal(AssertionOutcome.Failed, fail.Outcome);
        Assert.False(undecidable.Passed);
        Assert.Equal(AssertionOutcome.Inconclusive, undecidable.Outcome);
    }
}
