// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Judges;
using Xunit;

namespace AgentEval.Tests.Guardrails;

/// <summary>
/// The calibration report's Wilson interval (<see cref="WilsonInterval"/> in <c>AgentEval.Guardrails.Judges</c>).
/// </summary>
public sealed class WilsonIntervalTests
{
    [Fact]
    public void TheDefaultCriticalValue_GivesThe95PercentInterval()
    {
        // 0/8: [0, 0.324], the case a bare 0 % would hide.
        var interval = WilsonInterval.Compute(0, 8);

        Assert.Equal(0d, interval.Lower);
        Assert.Equal(0.3244, interval.Upper, 4);
    }

    [Theory]
    [InlineData(-1.96)]   // would swap the bounds
    [InlineData(0d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ACriticalValueThatIsNotFiniteAndPositive_IsRejected(double z)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => WilsonInterval.Compute(3, 10, z));
        Assert.Equal("z", ex.ParamName);
    }
}
