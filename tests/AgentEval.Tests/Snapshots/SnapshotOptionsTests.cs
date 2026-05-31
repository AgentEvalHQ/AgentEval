// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Snapshots;
using Xunit;

namespace AgentEval.Tests.Snapshots;

/// <summary>
/// Tests for <see cref="SnapshotOptions"/> defaults (MNT-11): the compiled scrub patterns are compiled
/// once per process and shared across instances, while each instance still gets its own mutable list.
/// </summary>
public class SnapshotOptionsTests
{
    [Fact]
    public void Defaults_ScrubPatterns_ShareCompiledRegexInstancesAcrossInstances()
    {
        var a = new SnapshotOptions();
        var b = new SnapshotOptions();

        Assert.Equal(a.ScrubPatterns.Count, b.ScrubPatterns.Count);
        Assert.True(a.ScrubPatterns.Count > 0);

        // MNT-11: the Regex objects are the SAME shared, pre-compiled instances — not recompiled per
        // `new SnapshotOptions()`.
        for (int i = 0; i < a.ScrubPatterns.Count; i++)
            Assert.Same(a.ScrubPatterns[i].Pattern, b.ScrubPatterns[i].Pattern);
    }

    [Fact]
    public void Defaults_ScrubPatterns_ListIsPerInstance_MutationDoesNotLeak()
    {
        var a = new SnapshotOptions();
        var b = new SnapshotOptions();
        var originalCount = b.ScrubPatterns.Count;

        a.ScrubPatterns.Add((new System.Text.RegularExpressions.Regex("extra"), "[X]"));

        // Adding to one instance's list must not affect the other instance's list.
        Assert.Equal(originalCount, b.ScrubPatterns.Count);
        Assert.Equal(originalCount + 1, a.ScrubPatterns.Count);
    }

    [Fact]
    public void Defaults_ScrubPatterns_StillMatchKnownTokens()
    {
        var opts = new SnapshotOptions();

        // Functional sanity — the default patterns still recognize the canonical tokens.
        Assert.Contains(opts.ScrubPatterns, p => p.Pattern.IsMatch("chatcmpl-ABC123"));
        Assert.Contains(opts.ScrubPatterns, p => p.Pattern.IsMatch("2026-05-31T12:00:00Z"));
        Assert.Contains(opts.ScrubPatterns, p => p.Pattern.IsMatch("550e8400-e29b-41d4-a716-446655440000"));
    }
}
