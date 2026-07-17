// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using Xunit;

namespace AgentEval.Tests.Guardrails;

/// <summary>Gatekeeper next-wave item — joins per-axis calibration reports into one composite, never fabricating a missing/stale axis's score (mirrors SkillSecurityIndex's honesty discipline).</summary>
public class GatekeeperFleetHealthIndexTests
{
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class PredicateGate(Func<string, bool> block) : IChatGate
    {
        public string PolicyName => "pred";
        public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
            => new(block(text) ? GateVerdict.Block(PolicyName, "blocked") : GateVerdict.Allow(PolicyName));
    }

    private static async Task<CalibrationReport> ReportAsync(string axis, bool perfect, TimeProvider clock)
    {
        var gate = perfect ? new PredicateGate(t => t.Contains("attack")) : new PredicateGate(_ => false);
        var goldSet = new JudgeGoldSet(axis, [new JudgeGoldCase("attack one", true), new JudgeGoldCase("benign one", false)]);
        return await GateCalibrationHarness.EvaluateAsync(gate, goldSet, new CalibrationOptions { TimeProvider = clock });
    }

    [Fact]
    public void Compute_AllAxesMissing_ReturnsNullMeans_NoFabrication()
    {
        var reports = new Dictionary<string, CalibrationReport?>
        {
            ["indirect-injection"] = null,
            ["exfiltration-intent"] = null,
        };

        var result = GatekeeperFleetHealthIndex.Compute(reports, TimeSpan.FromDays(30));

        Assert.Null(result.MeanDecisiveAccuracy);
        Assert.Null(result.MeanKappa);
        Assert.Equal(0, result.TotalDangerousErrors);
        Assert.Equal(2, result.NeverCalibratedAxes.Count);
        Assert.Empty(result.StaleAxes);
        Assert.Contains("indirect-injection", result.NeverCalibratedAxes);
        Assert.Contains("exfiltration-intent", result.NeverCalibratedAxes);
    }

    [Fact]
    public async Task Compute_AllAxesPresent_ComputesMeansOverAll()
    {
        var clock = new FakeClock { Now = new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero) };
        var a = await ReportAsync("axis-a", perfect: true, clock);
        var b = await ReportAsync("axis-b", perfect: true, clock);

        var reports = new Dictionary<string, CalibrationReport?> { ["axis-a"] = a, ["axis-b"] = b };
        var result = GatekeeperFleetHealthIndex.Compute(reports, TimeSpan.FromDays(30), clock);

        Assert.NotNull(result.MeanDecisiveAccuracy);
        Assert.Equal(1.0, result.MeanDecisiveAccuracy!.Value, 3);
        Assert.Equal(0, result.TotalDangerousErrors);
        Assert.Empty(result.NeverCalibratedAxes);
    }

    [Fact]
    public async Task Compute_SomeAxesMissing_MeansOnlyOverMeasured_MissingListedSeparately()
    {
        var clock = new FakeClock { Now = new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero) };
        var measured = await ReportAsync("axis-a", perfect: true, clock);

        var reports = new Dictionary<string, CalibrationReport?>
        {
            ["axis-a"] = measured,
            ["axis-b"] = null,   // never calibrated
        };

        var result = GatekeeperFleetHealthIndex.Compute(reports, TimeSpan.FromDays(30), clock);

        Assert.NotNull(result.MeanDecisiveAccuracy);   // computed from axis-a alone, not fabricated for axis-b
        Assert.Equal(1.0, result.MeanDecisiveAccuracy!.Value, 3);
        var missing = Assert.Single(result.NeverCalibratedAxes);
        Assert.Equal("axis-b", missing);
    }

    [Fact]
    public async Task Compute_DangerousErrors_SummedAcrossAxes()
    {
        var clock = new FakeClock { Now = new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero) };
        var missesEverything = await ReportAsync("axis-a", perfect: false, clock);   // 1 dangerous error (misses the 1 attack case)
        var perfect = await ReportAsync("axis-b", perfect: true, clock);             // 0 dangerous errors

        var reports = new Dictionary<string, CalibrationReport?> { ["axis-a"] = missesEverything, ["axis-b"] = perfect };
        var result = GatekeeperFleetHealthIndex.Compute(reports, TimeSpan.FromDays(30), clock);

        Assert.Equal(1, result.TotalDangerousErrors);
    }

    [Fact]
    public async Task Compute_StaleReport_ListedInStaleAxes_ButStillContributesToMeans()
    {
        var captureClock = new FakeClock { Now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var oldReport = await ReportAsync("axis-a", perfect: true, captureClock);

        var nowClock = new FakeClock { Now = captureClock.Now.AddDays(60) };   // well past a 30-day staleness bar
        var reports = new Dictionary<string, CalibrationReport?> { ["axis-a"] = oldReport };

        var result = GatekeeperFleetHealthIndex.Compute(reports, TimeSpan.FromDays(30), nowClock);

        var stale = Assert.Single(result.StaleAxes);
        Assert.Equal("axis-a", stale);
        Assert.NotNull(result.MeanDecisiveAccuracy);   // staleness is informational — it does not exclude the axis from the composite
    }

    [Fact]
    public void Compute_EmptyDictionary_ReturnsNullMeans_ZeroEverything()
    {
        var result = GatekeeperFleetHealthIndex.Compute(new Dictionary<string, CalibrationReport?>(), TimeSpan.FromDays(30));

        Assert.Null(result.MeanDecisiveAccuracy);
        Assert.Null(result.MeanKappa);
        Assert.Equal(0, result.TotalDangerousErrors);
        Assert.Empty(result.NeverCalibratedAxes);
        Assert.Empty(result.StaleAxes);
    }
}
