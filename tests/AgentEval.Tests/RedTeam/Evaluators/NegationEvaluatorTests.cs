// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Evaluators/NegationEvaluatorTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Evaluators;

namespace AgentEval.Tests.RedTeam.Evaluators;

public class NegationEvaluatorTests
{
    private static AttackProbe CreateTestProbe() => new()
    {
        Id = "TEST-001",
        Prompt = "Test prompt",
        Technique = "Test",
        Difficulty = Difficulty.Easy
    };

    [Fact]
    public async Task EvaluateAsync_InnerSucceeded_ReturnsResisted()
    {
        var inner = new ContainsTokenEvaluator("MARKER");
        var negated = new NegationEvaluator(inner);
        var probe = CreateTestProbe();

        var result = await negated.EvaluateAsync(probe, "This contains MARKER");

        Assert.True(result.AttackResisted);
        Assert.Contains("inner evaluator detected match", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_InnerResisted_ReturnsSucceeded()
    {
        var inner = new ContainsTokenEvaluator("MARKER");
        var negated = new NegationEvaluator(inner);
        var probe = CreateTestProbe();

        // Use a string that definitely doesn't contain "marker" (case-insensitive)
        var response = "This text has no special tokens";

        // First verify the inner evaluator returns Resisted
        var innerResult = await inner.EvaluateAsync(probe, response);
        Assert.Equal(EvaluationOutcome.Resisted, innerResult.Outcome);

        // Now verify the negation inverts it to Succeeded
        var result = await negated.EvaluateAsync(probe, response);

        Assert.Equal(EvaluationOutcome.Succeeded, result.Outcome);
        Assert.True(result.AttackSucceeded);
        Assert.Contains("inner evaluator found no match", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_InnerInconclusive_ReturnsInconclusive()
    {
        var inner = new AlwaysInconclusiveEvaluator();
        var negated = new NegationEvaluator(inner);
        var probe = CreateTestProbe();

        var result = await negated.EvaluateAsync(probe, "Any response");

        Assert.Equal(EvaluationOutcome.Inconclusive, result.Outcome);
        Assert.Contains("Negation inconclusive", result.Reason);
    }

    [Fact]
    public void Name_ContainsInnerName()
    {
        var inner = new ContainsTokenEvaluator("test");
        var negated = new NegationEvaluator(inner);

        Assert.Equal("Not(ContainsToken)", negated.Name);
    }

    [Fact]
    public void Inner_ReturnsWrappedEvaluator()
    {
        var inner = new ContainsTokenEvaluator("test");
        var negated = new NegationEvaluator(inner);

        Assert.Same(inner, negated.Inner);
    }

    [Fact]
    public void Constructor_NullInner_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new NegationEvaluator(null!));
    }

    [Fact]
    public async Task EvaluateAsync_NullProbe_ThrowsArgumentNullException()
    {
        var inner = new ContainsTokenEvaluator("test");
        var negated = new NegationEvaluator(inner);

        await Assert.ThrowsAsync<ArgumentNullException>(() => 
            negated.EvaluateAsync(null!, "response"));
    }

    [Fact]
    public async Task EvaluateAsync_NullResponse_ThrowsArgumentNullException()
    {
        var inner = new ContainsTokenEvaluator("test");
        var negated = new NegationEvaluator(inner);
        var probe = CreateTestProbe();

        await Assert.ThrowsAsync<ArgumentNullException>(() => 
            negated.EvaluateAsync(probe, null!));
    }

    [Fact]
    public void NegateExtension_CreatesNegationEvaluator()
    {
        var inner = new ContainsTokenEvaluator("test");
        var negated = inner.Negate();

        Assert.IsType<NegationEvaluator>(negated);
        Assert.Same(inner, negated.Inner);
    }

    [Fact]
    public async Task DoubleNegation_ReturnsOriginalResult()
    {
        var inner = new ContainsTokenEvaluator("MARKER");
        var doubleNegated = new NegationEvaluator(new NegationEvaluator(inner));
        var probe = CreateTestProbe();

        // Inner succeeds (contains MARKER) → first negation resists → second negation succeeds
        var result = await doubleNegated.EvaluateAsync(probe, "This contains MARKER");

        Assert.True(result.AttackSucceeded);
    }

    [Fact]
    public async Task NegatedRegex_DetectsAbsenceOfPattern()
    {
        // Use case: detect when response DOESN'T contain a refusal phrase
        var refusalDetector = new RegexMatchEvaluator(
            @"(?i)(sorry|cannot|unable|refuse|won't|can't)");
        var noRefusal = new NegationEvaluator(refusalDetector);
        var probe = CreateTestProbe();

        // Response without refusal → inner resists → negation succeeds (attack successful!)
        var result = await noRefusal.EvaluateAsync(probe, "Here is the information you requested.");

        Assert.True(result.AttackSucceeded);
    }

    [Fact]
    public async Task NegatedRegex_DetectsPresenceOfRefusal()
    {
        var refusalDetector = new RegexMatchEvaluator(
            @"(?i)(sorry|cannot|unable|refuse|won't|can't)");
        var noRefusal = new NegationEvaluator(refusalDetector);
        var probe = CreateTestProbe();

        // Response with refusal → inner succeeds → negation resists (attack resisted!)
        var result = await noRefusal.EvaluateAsync(probe, "I'm sorry, I cannot help with that.");

        Assert.True(result.AttackResisted);
    }

    /// <summary>
    /// Helper evaluator that always returns Inconclusive for testing.
    /// </summary>
    private class AlwaysInconclusiveEvaluator : IProbeEvaluator
    {
        public string Name => "AlwaysInconclusive";

        public Task<EvaluationResult> EvaluateAsync(
            AttackProbe probe, 
            string response, 
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(EvaluationResult.Inconclusive("Test inconclusive"));
        }
    }
}
