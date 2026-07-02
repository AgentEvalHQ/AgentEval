// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Evaluators;
using Xunit;

namespace AgentEval.Tests.MAF.Evaluators;

/// <summary>Unit coverage for <see cref="CircuitBreaker"/> — threshold, reset, cooldown (fake clock), per-source isolation.</summary>
public class CircuitBreakerTests
{
    [Fact]
    public void NotOpen_Initially()
    {
        var b = new CircuitBreaker(threshold: 2);
        Assert.False(b.IsOpen("x"));
    }

    [Fact]
    public void Opens_AtThreshold()
    {
        var b = new CircuitBreaker(threshold: 2);
        b.OnFailure("x");
        Assert.False(b.IsOpen("x"));   // 1 < 2
        b.OnFailure("x");
        Assert.True(b.IsOpen("x"));    // 2 >= 2 -> open
    }

    [Fact]
    public void Resets_OnSuccess()
    {
        var b = new CircuitBreaker(threshold: 2);
        b.OnFailure("x");
        b.OnSuccess("x");              // resets the count
        b.OnFailure("x");
        Assert.False(b.IsOpen("x"));   // only 1 failure since reset
    }

    [Fact]
    public void Reopens_AfterCooldown_WithFakeClock()
    {
        var now = DateTimeOffset.UtcNow;
        var b = new CircuitBreaker(threshold: 1, cooldown: TimeSpan.FromMinutes(1), now: () => now);
        b.OnFailure("x");
        Assert.True(b.IsOpen("x"));    // open
        now = now.AddMinutes(2);       // cooldown elapsed
        Assert.False(b.IsOpen("x"));   // closed again
    }

    [Fact]
    public void IsPerSource()
    {
        var b = new CircuitBreaker(threshold: 1);
        b.OnFailure("a");
        Assert.True(b.IsOpen("a"));
        Assert.False(b.IsOpen("b"));   // independent per source
    }

    [Fact]
    public void Ctor_RejectsThresholdBelowOne()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreaker(threshold: 0));
}
