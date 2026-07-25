// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using Xunit;

namespace AgentEval.Tests.Guardrails;

/// <summary>P6-3: the single ReDoS-timeout surface — pins the three tiers so a change is a deliberate, reviewed edit.</summary>
public class GateRegexTimeoutsTests
{
    [Fact]
    public void Tiers_HaveTheExpectedMilliseconds()
    {
        Assert.Equal(50, GateRegexTimeouts.Trivial.TotalMilliseconds);
        Assert.Equal(100, GateRegexTimeouts.Standard.TotalMilliseconds);
        Assert.Equal(300, GateRegexTimeouts.Extended.TotalMilliseconds);
    }

    [Fact]
    public void Tiers_AreOrdered_AndUnderTheBoundedCostCeiling()
    {
        Assert.True(GateRegexTimeouts.Trivial < GateRegexTimeouts.Standard);
        Assert.True(GateRegexTimeouts.Standard < GateRegexTimeouts.Extended);
        Assert.True(GateRegexTimeouts.Extended < TimeSpan.FromMilliseconds(500));   // stays under the GateCost.Bounded ceiling
    }
}
