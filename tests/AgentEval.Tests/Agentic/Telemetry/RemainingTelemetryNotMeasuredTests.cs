// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Efficiency;
using AgentEval.Evals.Agentic.Telemetry;
using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Agentic.Telemetry;

public sealed class RemainingTelemetryNotMeasuredTests
{
    // Runs against the old constructor too: reflection maps null to zero for old int fields.
    private static AgenticTelemetry Data(int? errors = 0, int? calls = 10, int? retries = 0,
        double? cost = .02, IReadOnlyDictionary<string, double>? tools = null, bool toolsTracked = true) =>
        (AgenticTelemetry)typeof(AgenticTelemetry).GetConstructors().Single().Invoke(
            [350d, 900d, 2000d, 4200, cost, errors, calls, retries,
                toolsTracked ? tools ?? new Dictionary<string, double> { ["search"] = 450 } : null]);

    private static IEval Eval(string key, AgenticTelemetry data) => key switch
    {
        "error_rate" => new ErrorRateEval(data),
        "retry_rate" => new RetryRateEval(data),
        "tool_latency" => new ToolLatencyEval(data),
        "cost" => new CostEval(data),
        "cost_quality_efficiency" => new CostQualityEfficiencyEval(),
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };

    private static EvalInput Input(AgenticTelemetry data) => new("q", Metadata: new Dictionary<string, object>
    {
        [LatencyEval.MetadataKey] = data,
        [CostQualityEfficiencyEval.ScenarioScoreMetadataKey] = .9
    });

    [Theory]
    [InlineData(nameof(AgenticTelemetry.ErrorCount))]
    [InlineData(nameof(AgenticTelemetry.TotalCalls))]
    [InlineData(nameof(AgenticTelemetry.RetryCount))]
    public void Counts_PublicContract_AllowsNotTracked(string property) =>
        Assert.NotNull(Nullable.GetUnderlyingType(typeof(AgenticTelemetry).GetProperty(property)!.PropertyType));

    [Fact]
    public void Tools_PublicContract_AllowsNotTracked() => Assert.Equal(
        System.Reflection.NullabilityState.Nullable,
        new System.Reflection.NullabilityInfoContext().Create(
            typeof(AgenticTelemetry).GetProperty(nameof(AgenticTelemetry.ToolLatencyMs))!).ReadState);

    [Theory]
    [InlineData("error_rate", null, 0, "skipped")]
    [InlineData("retry_rate", null, 0, "skipped")]
    [InlineData("error_rate", null, null, "skipped")]
    [InlineData("retry_rate", null, null, "skipped")]
    [InlineData("error_rate", -1, null, "error")]
    [InlineData("retry_rate", -1, null, "error")]
    [InlineData("error_rate", null, -1, "error")]
    [InlineData("retry_rate", null, -1, "error")]
    public async Task Counts_PartlyMissing_PreservesMissingOrInvalidState(string key, int? count, int? calls, string label)
    {
        var data = Data(errors: key == "error_rate" ? count : 0,
            retries: key == "retry_rate" ? count : 0, calls: calls);
        Absent(await Eval(key, data).EvaluateAsync(Input(data)), label);
    }

    [Theory]
    [InlineData("error_rate")]
    [InlineData("retry_rate")]
    public async Task Counts_EqualPositiveTotal_IsMeasuredFailure(string key)
    {
        var data = Data(errors: 10, retries: 10);
        var result = await Eval(key, data).EvaluateAsync(Input(data));
        Assert.True(result.Score.CountsTowardAggregate());
        Assert.False(result.Score.Passed);
        Assert.Equal(0, result.Score.Value);
    }

    [Theory]
    [InlineData("error_rate", true)]
    [InlineData("error_rate", false)]
    [InlineData("retry_rate", true)]
    [InlineData("retry_rate", false)]
    [InlineData("tool_latency", true)]
    public async Task Reading_NotTracked_OverridesConstructorAndSkips(string key, bool numerator)
    {
        var missing = key == "tool_latency" ? Data(toolsTracked: false) : numerator
            ? Data(errors: key == "error_rate" ? null : 0, retries: key == "retry_rate" ? null : 0)
            : Data(calls: null);
        var result = await Eval(key, Data()).EvaluateAsync(Input(missing));
        Absent(result, "skipped");
        Assert.Contains("not tracked", result.Details.Summary);
        var preset = await AgenticBenchmark.Telemetry().EvaluateAsync(Input(missing));
        Assert.Equal("warn", preset.Score.Label);
        Assert.False(preset.Score.Passed);
    }

    [Theory]
    [InlineData("error_rate")]
    [InlineData("retry_rate")]
    [InlineData("tool_latency")]
    public async Task Reading_RecordedNoCalls_IsNotApplicable(string key)
    {
        var data = Data(calls: 0, tools: new Dictionary<string, double>());
        var result = await Eval(key, data).EvaluateAsync(Input(data));
        Absent(result, "inapplicable", MeasurementState.NotApplicable);
        var preset = await AgenticBenchmark.Telemetry().EvaluateAsync(Input(data));
        Assert.True(preset.Score.Passed);
    }

    [Theory]
    [InlineData("error_rate", -1, 10)]
    [InlineData("error_rate", 0, -1)]
    [InlineData("error_rate", 11, 10)]
    [InlineData("error_rate", 1, 0)]
    [InlineData("retry_rate", -1, 10)]
    [InlineData("retry_rate", 0, -1)]
    [InlineData("retry_rate", 11, 10)]
    [InlineData("retry_rate", 1, 0)]
    public async Task Counts_Invalid_AreErrors(string key, int count, int calls)
    {
        var data = Data(errors: key == "error_rate" ? count : 0,
            retries: key == "retry_rate" ? count : 0, calls: calls);
        Absent(await Eval(key, data).EvaluateAsync(Input(data)), "error");
        var preset = await AgenticBenchmark.Telemetry().EvaluateAsync(Input(data));
        Assert.False(preset.Score.Passed);
    }

    public static IEnumerable<object[]> InvalidReadings() =>
        from key in new[] { "cost", "cost_quality_efficiency", "tool_latency" }
        from value in new[] { -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity }
        select new object[] { key, value };

    [Theory]
    [MemberData(nameof(InvalidReadings))]
    public async Task Reading_Invalid_IsErrorEvenWhenAnotherToolIsValid(string key, double value)
    {
        var data = key == "tool_latency"
            ? Data(tools: new Dictionary<string, double> { ["valid"] = 450, ["invalid"] = value })
            : Data(cost: value);
        Absent(await Eval(key, data).EvaluateAsync(Input(data)), "error");
        if (key != "cost_quality_efficiency")
            Assert.False((await AgenticBenchmark.Telemetry().EvaluateAsync(Input(data))).Score.Passed);
    }

    [Theory]
    [InlineData("error_rate", "error_count")]
    [InlineData("retry_rate", "retry_count")]
    [InlineData("tool_latency", "worst_tool_latency_ms")]
    [InlineData("cost", "cost_usd")]
    [InlineData("cost_quality_efficiency", "estimated_cost_usd")]
    public async Task Reading_MeasuredZero_RemainsPassingMeasurement(string key, string dimension)
    {
        var data = Data(cost: 0, tools: new Dictionary<string, double> { ["search"] = 0 });
        var result = await Eval(key, data).EvaluateAsync(Input(data));
        Assert.True(result.Score.Passed);
        Assert.True(result.Score.CountsTowardAggregate());
        Assert.Equal(1, result.Score.Value);
        Assert.Equal(0, result.Details.Dimensions![dimension]);
    }

    private static void Absent(EvalResult result, string label,
        MeasurementState state = MeasurementState.NotMeasured)
    {
        Assert.Equal(label, result.Score.Label);
        Assert.Equal(state, result.Score.CensusBucket());
        Assert.False(result.Score.Passed);
        Assert.False(result.Score.CountsTowardAggregate());
        Assert.Null(result.Details.Dimensions);
        Assert.False(string.IsNullOrWhiteSpace(result.Details.Summary));
    }
}
