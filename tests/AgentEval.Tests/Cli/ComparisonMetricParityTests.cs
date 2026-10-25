// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;
using AgentEval.Results.Adapters.Comparison;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Writing;
using AgentEval.Tests.Output;

namespace AgentEval.Tests.Cli;

public sealed class ComparisonMetricParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compare_OriginalEvalResultAndItsImport_PreserveNormalizedMetric(bool absent)
    {
        using var temp = TempWorkspace.Create("MetricParity");
        var result = Result();
        if (absent) result = result with { Score = new(0, null, "not_measured", false, .7, "none", null) { Measurement = MeasurementState.NotMeasured } };
        var imported = await Import(temp, [Store(result)]);
        var now = DateTimeOffset.UtcNow;
        var directory = AefCompositeRunWriter.Write(Path.Combine(temp.Path, "native"), result, AefCompositeRunWriter.Undescribed,
            new() { SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked, Lane = "test", SuiteRef = "suite:test", ContentCapture = AefContentCapture.On }, now, now).Directory;
        var native = new AefComparisonReader().Read(directory);
        Assert.Equal(native.Metrics["case"], imported.Metrics["case"]);
        Assert.Equal(ComparisonVerdict.Comparable, imported.CompareTo(native).Verdict);
        Assert.Equal(absent ? null : .9, imported.Scenarios.Single().Score);
    }

    [Theory]
    [InlineData("plain-output")]
    [InlineData("unreadable-output")]
    [InlineData("scalar")]
    [InlineData("key")]
    [InlineData("version")]
    [InlineData("bar")]
    [InlineData("passed")]
    [InlineData("measurement")]
    [InlineData("outside-domain")]
    public async Task Import_UnprovenOrConflictingNormalizedContract_KeepsGenericScale(string conflict)
    {
        using var temp = TempWorkspace.Create("GenericMetric");
        var result = Result();
        var scenario = Store(result);
        scenario = conflict switch
        {
            "plain-output" => scenario with { Output = "answer" },
            "unreadable-output" => scenario with { Output = "{\"metric\":null}" },
            "scalar" => scenario with { Score = .8 },
            "key" => scenario with { Comparability = scenario.Comparability! with { EvalKey = "other" } },
            "version" => scenario with { Comparability = scenario.Comparability! with { EvalVersion = "2.0" } },
            "bar" => scenario with { Comparability = scenario.Comparability! with { EffectiveBar = .6 } },
            "passed" => scenario with { Passed = false },
            "measurement" => scenario with { Measurement = ScenarioMeasurement.NotMeasured },
            _ => Store(result with { Score = result.Score with { Value = 2 } }),
        };
        var metric = (await Import(temp, [scenario])).Metrics["case"];
        Assert.Null(metric.Minimum);
        Assert.Null(metric.Maximum);
    }

    [Fact]
    public async Task Import_MixedNormalizedAndGenericScenarios_DoesNotRelabelGenericScores()
    {
        using var temp = TempWorkspace.Create("MixedMetric");
        var scenario = Store(Result());
        var imported = await Import(temp, [scenario, scenario with { Id = "generic", Output = "answer", Score = 42 }]);
        Assert.All(imported.Metrics.Values, metric => { Assert.Null(metric.Minimum); Assert.Null(metric.Maximum); });
        Assert.Equal(42, imported.Scenarios.Single(s => s.Id == "generic").Score);
    }

    private static EvalResult Result() => new(new("case", "Case", "test", "1.0"),
        new(.9, null, "pass", true, .7, "none", null), new(null, null, null, null, null),
        new("code", null, null, null, null, 0, false), DateTimeOffset.UtcNow) { Observation = new("actual prompt", null) };

    private static ScenarioResult Store(EvalResult result) => EvalResultPersistence.ToScenarioResult(result, "case", "Case");

    private static async Task<AefComparisonRun> Import(TempWorkspace temp, IReadOnlyList<ScenarioResult> scenarios)
    {
        var source = await CompareCommandTests.WriteRunAsync(temp, "subject", scenarios);
        var destination = Path.Combine(temp.Path, "imported");
        await StoreV1Exporter.ExportAsync(new FileSystemOutputStore(temp.Path), Path.GetFileName(source), destination,
            new() { ContentCapture = AefContentCapture.On });
        return new AefComparisonReader().Read(destination);
    }
}
