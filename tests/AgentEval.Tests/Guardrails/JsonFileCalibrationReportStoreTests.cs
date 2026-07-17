// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using Xunit;

namespace AgentEval.Tests.Guardrails;

/// <summary>Gatekeeper next-wave item — the Fleet Health Index's persistence prerequisite: one report per axis, single-pin (overwrite), not a historical ledger.</summary>
public class JsonFileCalibrationReportStoreTests : IDisposable
{
    private readonly string _root;

    public JsonFileCalibrationReportStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-calibration-store-test-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static JudgeGoldSet Gold() => new("test-axis",
    [
        new JudgeGoldCase("attack one", ShouldBlock: true),
        new JudgeGoldCase("benign one", ShouldBlock: false),
    ]);

    private sealed class PredicateGate(Func<string, bool> block) : IChatGate
    {
        public string PolicyName => "pred";
        public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
            => new(block(text) ? GateVerdict.Block(PolicyName, "blocked") : GateVerdict.Allow(PolicyName));
    }

    private static Task<CalibrationReport> MakeReportAsync(string axis, TimeProvider? clock = null) =>
        GateCalibrationHarness.EvaluateAsync(
            new PredicateGate(t => t.Contains("attack")),
            new JudgeGoldSet(axis, [new JudgeGoldCase("attack one", true), new JudgeGoldCase("benign one", false)]),
            new CalibrationOptions { TimeProvider = clock });

    [Fact]
    public async Task SaveThenLoadLatest_RoundTripsAllFields()
    {
        var store = new JsonFileCalibrationReportStore(_root);
        var original = await MakeReportAsync("indirect-injection");

        await store.SaveAsync(original);
        var loaded = await store.LoadLatestAsync("indirect-injection");

        Assert.NotNull(loaded);
        Assert.Equal(original.Axis, loaded!.Axis);
        Assert.Equal(original.TruePositives, loaded.TruePositives);
        Assert.Equal(original.TrueNegatives, loaded.TrueNegatives);
        Assert.Equal(original.FalsePositives, loaded.FalsePositives);
        Assert.Equal(original.FalseNegatives, loaded.FalseNegatives);
        Assert.Equal(original.KappaVsGold, loaded.KappaVsGold);
        Assert.Equal(original.MeetsThresholds, loaded.MeetsThresholds);
        Assert.Equal(original.CapturedAt, loaded.CapturedAt);
        Assert.Equal(original.Cases.Count, loaded.Cases.Count);
    }

    [Fact]
    public async Task LoadLatest_NeverSaved_ReturnsNull()
    {
        var store = new JsonFileCalibrationReportStore(_root);
        var loaded = await store.LoadLatestAsync("never-calibrated-axis");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task Save_SecondTimeSameAxis_Overwrites_NotAppends()
    {
        var store = new JsonFileCalibrationReportStore(_root);
        var first = await MakeReportAsync("indirect-injection");
        await store.SaveAsync(first);

        var second = await MakeReportAsync("indirect-injection");
        await store.SaveAsync(second);

        var all = await store.LoadLatestAllAsync();
        var onlyOne = Assert.Single(all);
        Assert.Equal("indirect-injection", onlyOne.Key);

        // Exactly one file on disk for this axis — confirms overwrite, not a growing ledger.
        var calibrationDir = Path.Combine(_root, "calibration");
        Assert.Single(Directory.GetFiles(calibrationDir, "*.json"));
    }

    [Fact]
    public async Task LoadLatestAll_MultipleAxes_ReturnsEveryOne()
    {
        var store = new JsonFileCalibrationReportStore(_root);
        await store.SaveAsync(await MakeReportAsync("indirect-injection"));
        await store.SaveAsync(await MakeReportAsync("exfiltration-intent"));
        await store.SaveAsync(await MakeReportAsync("over-refusal"));

        var all = await store.LoadLatestAllAsync();

        Assert.Equal(3, all.Count);
        Assert.Contains("indirect-injection", all.Keys);
        Assert.Contains("exfiltration-intent", all.Keys);
        Assert.Contains("over-refusal", all.Keys);
    }

    [Fact]
    public async Task LoadLatestAll_NothingSavedYet_ReturnsEmpty()
    {
        var store = new JsonFileCalibrationReportStore(_root);
        var all = await store.LoadLatestAllAsync();
        Assert.Empty(all);
    }

    [Fact]
    public async Task Save_AxisWithUnsafeCharacters_StillRoundTrips()
    {
        // Axis names are caller-supplied strings — the on-disk slug must not corrupt the round-trip
        // even if the axis string itself contains filesystem-unsafe characters.
        var store = new JsonFileCalibrationReportStore(_root);
        const string axis = "weird/axis:name*with?chars";
        await store.SaveAsync(await MakeReportAsync(axis));

        var loaded = await store.LoadLatestAsync(axis);
        Assert.NotNull(loaded);
        Assert.Equal(axis, loaded!.Axis);
    }
}
