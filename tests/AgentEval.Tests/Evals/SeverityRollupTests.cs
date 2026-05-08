// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

public class SeverityRollupTests
{
    [Fact]
    public void Max_EmptySequence_ReturnsNone()
    {
        var result = SeverityRollup.Max(Array.Empty<string>());
        Assert.Equal("none", result);
    }

    [Fact]
    public void Max_AllNone_ReturnsNone()
    {
        var result = SeverityRollup.Max(new[] { "none", "none", "none" });
        Assert.Equal("none", result);
    }

    [Fact]
    public void Max_Mixed_ReturnsHighest()
    {
        var result = SeverityRollup.Max(new[] { "none", "low", "high", "medium" });
        Assert.Equal("high", result);
    }

    [Fact]
    public void Max_UnknownString_TreatedAsNone()
    {
        var result = SeverityRollup.Max(new[] { "unknown", "low" });
        Assert.Equal("low", result);
    }

    [Fact]
    public void Max_CriticalBeatsEverything()
    {
        var result = SeverityRollup.Max(new[] { "critical", "high", "medium", "low", "none" });
        Assert.Equal("critical", result);
    }

    [Fact]
    public void Max_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SeverityRollup.Max(null!));
    }
}
