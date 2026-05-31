// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Models;

/// <summary>
/// Tests for the shared <see cref="ModelKeyMatcher"/> (ARC-07) — the single longest-first matching
/// algorithm now used by both ModelPricing and JudgeCostMap.
/// </summary>
public class ModelKeyMatcherTests
{
    private static readonly Dictionary<string, int> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4"] = 1,
        ["gpt-4o"] = 2,
        ["gpt-4o-mini"] = 3,
    };

    [Fact]
    public void ExactMatch_Wins()
    {
        Assert.True(ModelKeyMatcher.TryMatchLongestFirst(Table, "gpt-4o", out var v));
        Assert.Equal(2, v);
    }

    [Fact]
    public void DeploymentName_ResolvesToLongestSubstringKey()
    {
        // "gpt-4o-mini" must win over "gpt-4o" and "gpt-4" even though all three are substrings —
        // this is the BUG-11 determinism guarantee, now shared.
        Assert.True(ModelKeyMatcher.TryMatchLongestFirst(Table, "my-prod_gpt-4o-mini-2024-07-18", out var v));
        Assert.Equal(3, v);
    }

    [Fact]
    public void CaseInsensitiveSubstring_Matches()
    {
        Assert.True(ModelKeyMatcher.TryMatchLongestFirst(Table, "Azure-GPT-4O-Deployment", out var v));
        Assert.Equal(2, v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("claude-3-opus")] // no substring match in this table
    public void NullBlankOrUnmatched_ReturnsFalse(string? model)
    {
        Assert.False(ModelKeyMatcher.TryMatchLongestFirst(Table, model, out _));
    }
}
