// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 5, P5-8 — the GateCost runtime watchdog: flags a gate whose measured latency persistently exceeds its declared cost class.</summary>
public class GateCostWatchdogTests
{
    private sealed class FakeGate(string name, GateCost cost) : IToolGate
    {
        public string PolicyName => name;
        public GateCost Cost => cost;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken ct = default)
            => new(ToolGateVerdict.Allow(PolicyName));
    }

    private static GateTelemetry TelemetryWith(string policy, TimeSpan each, int times)
    {
        var t = new GateTelemetry();
        for (var i = 0; i < times; i++)
        {
            t.Record(policy, ToolGateAction.Allow, each);
        }

        return t;
    }

    [Fact]
    public void Flags_PureCodeGate_PersistentlyOverCeiling()
    {
        var telemetry = TelemetryWith("SlowPure", TimeSpan.FromMilliseconds(50), times: 30);   // avg 50ms >> 10ms PureCode ceiling

        var violations = GateCostWatchdog.CheckViolations(new[] { new FakeGate("SlowPure", GateCost.PureCode) }, telemetry);

        Assert.Single(violations);
        Assert.Equal("SlowPure", violations[0].PolicyName);
        Assert.Equal(GateCost.PureCode, violations[0].DeclaredCost);
        Assert.Equal(30, violations[0].Invocations);
    }

    [Fact]
    public void NoViolation_ForFastGate()
        => Assert.Empty(GateCostWatchdog.CheckViolations(
            new[] { new FakeGate("FastPure", GateCost.PureCode) },
            TelemetryWith("FastPure", TimeSpan.FromMilliseconds(1), times: 30)));

    [Fact]
    public void NoViolation_BelowMinInvocations_EvenWhenSlow()
        => Assert.Empty(GateCostWatchdog.CheckViolations(
            new[] { new FakeGate("SlowButRare", GateCost.PureCode) },
            TelemetryWith("SlowButRare", TimeSpan.FromMilliseconds(500), times: 3)));   // only 3 samples < default 20

    [Fact]
    public void BoundedGate_UnderItsHigherCeiling_NoViolation()
        => Assert.Empty(GateCostWatchdog.CheckViolations(
            new[] { new FakeGate("RegexGate", GateCost.Bounded) },
            TelemetryWith("RegexGate", TimeSpan.FromMilliseconds(100), times: 30)));   // 100ms < 500ms Bounded ceiling

    [Fact]
    public void NetworkAndLlm_HaveNoInlineCeiling()
    {
        Assert.Equal(TimeSpan.MaxValue, GateCostWatchdog.CeilingFor(GateCost.Network));
        Assert.Equal(TimeSpan.MaxValue, GateCostWatchdog.CeilingFor(GateCost.Llm));
    }
}
