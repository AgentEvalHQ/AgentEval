// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Core/EvaluationResultTests.cs
using AgentEval.RedTeam;

namespace AgentEval.Tests.RedTeam.Core;

/// <summary>
/// Tests for the EvaluationResult record struct.
/// </summary>
public class EvaluationResultTests
{
    [Fact]
    public void Succeeded_SetsOutcomeCorrectly()
    {
        var result = EvaluationResult.Succeeded("Attack worked");

        Assert.Equal(EvaluationOutcome.Succeeded, result.Outcome);
        Assert.True(result.AttackSucceeded);
        Assert.False(result.AttackResisted);
        Assert.False(result.IsInconclusive);
        Assert.Equal("Attack worked", result.Reason);
        Assert.Equal(1.0, result.Confidence);
    }

    [Fact]
    public void Succeeded_WithMatchedItems_IncludesEvidence()
    {
        var matched = new[] { "PWNED", "HACKED" };

        var result = EvaluationResult.Succeeded(
            "Found markers",
            matchedItems: matched);

        Assert.Equal(2, result.MatchedItems!.Count);
        Assert.Contains("PWNED", result.MatchedItems);
        Assert.Contains("HACKED", result.MatchedItems);
    }

    [Fact]
    public void Succeeded_WithMetadata_IncludesMetadata()
    {
        var metadata = new Dictionary<string, object> { ["confidence"] = 0.95 };

        var result = EvaluationResult.Succeeded(
            "LLM judge says compromised",
            metadata: metadata);

        Assert.NotNull(result.Metadata);
        Assert.Equal(0.95, result.Metadata["confidence"]);
    }

    [Fact]
    public void Resisted_SetsOutcomeCorrectly()
    {
        var result = EvaluationResult.Resisted("Agent refused");

        Assert.Equal(EvaluationOutcome.Resisted, result.Outcome);
        Assert.False(result.AttackSucceeded);
        Assert.True(result.AttackResisted);
        Assert.False(result.IsInconclusive);
        Assert.Equal("Agent refused", result.Reason);
        Assert.Equal(1.0, result.Confidence);
    }

    [Fact]
    public void Inconclusive_SetsOutcomeCorrectly()
    {
        var result = EvaluationResult.Inconclusive("Unable to determine");

        Assert.Equal(EvaluationOutcome.Inconclusive, result.Outcome);
        Assert.False(result.AttackSucceeded);
        Assert.False(result.AttackResisted);
        Assert.True(result.IsInconclusive);
        Assert.Equal("Unable to determine", result.Reason);
        Assert.Equal(0.5, result.Confidence); // Default for inconclusive
    }

    [Fact]
    public void Confidence_CanBeCustomized()
    {
        var succeeded = EvaluationResult.Succeeded("High confidence", confidence: 0.99);
        var resisted = EvaluationResult.Resisted("Low confidence", confidence: 0.6);
        var inconclusive = EvaluationResult.Inconclusive("Very uncertain", confidence: 0.3);

        Assert.Equal(0.99, succeeded.Confidence);
        Assert.Equal(0.6, resisted.Confidence);
        Assert.Equal(0.3, inconclusive.Confidence);
    }

    [Fact]
    public void RecordEquality_WorksCorrectly()
    {
        var result1 = EvaluationResult.Succeeded("Same reason");
        var result2 = EvaluationResult.Succeeded("Same reason");
        var result3 = EvaluationResult.Succeeded("Different reason");

        Assert.Equal(result1, result2);
        Assert.NotEqual(result1, result3);
    }

    [Fact]
    public void MatchedItems_IsNullByDefault()
    {
        var result = EvaluationResult.Resisted("No matches");

        Assert.Null(result.MatchedItems);
    }

    [Fact]
    public void Metadata_IsNullByDefault()
    {
        var result = EvaluationResult.Succeeded("Simple test");

        Assert.Null(result.Metadata);
    }
}
