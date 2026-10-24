// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Telemetry;
using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Agentic.Telemetry;

public sealed class TokensAndLatencyNotTrackedTests
{
    // Reflection makes the same behavioral tests runnable before the nullable contract change:
    // null becomes zero in the old value-type constructor, reproducing the false pass.
    private static AgenticTelemetry Telemetry(double? p50 = 350, double? p95 = 900,
        double? p99 = 2000, int? tokens = 4200) =>
        (AgenticTelemetry)typeof(AgenticTelemetry).GetConstructors().Single().Invoke(
            [p50, p95, p99, tokens, 0.02, 0, 10, 0, new Dictionary<string, double> { ["search"] = 450 }]);

    [Theory]
    [InlineData(nameof(AgenticTelemetry.TokensUsed))]
    [InlineData(nameof(AgenticTelemetry.LatencyMsP50))]
    [InlineData(nameof(AgenticTelemetry.LatencyMsP95))]
    [InlineData(nameof(AgenticTelemetry.LatencyMsP99))]
    public void Telemetry_UsageReading_HasNotTrackedState(string property) =>
        Assert.NotNull(Nullable.GetUnderlyingType(typeof(AgenticTelemetry).GetProperty(property)!.PropertyType));

    [Fact]
    public async Task Tokens_NotTracked_IsNotMeasured()
    {
        var result = await new TokenUsageEval(Telemetry(tokens: null)).EvaluateAsync(new EvalInput("q"));
        AssertAbsent(result, "skipped");
        Assert.Contains("not tracked", result.Details.Summary);
    }

    [Theory]
    [InlineData("token_usage")]
    [InlineData("latency")]
    public async Task Metadata_UntrackedReading_OverridesMeasuredConstructor(string key)
    {
        IEval eval = key == "token_usage" ? new TokenUsageEval(Telemetry()) : new LatencyEval(Telemetry());
        var telemetry = key == "token_usage" ? Telemetry(tokens: null) : Telemetry(p99: null);
        var result = await eval.EvaluateAsync(Input(telemetry));
        AssertAbsent(result, "skipped");
    }

    [Theory]
    [InlineData(900)]
    [InlineData(50000)]
    public async Task P99_NotTracked_IsNotReplacedByP95(double p95)
    {
        var result = await new LatencyEval(Telemetry(p99: null, p95: p95)).EvaluateAsync(new EvalInput("q"));
        AssertAbsent(result, "skipped");
        Assert.Contains("P99", result.Details.Summary);
        Assert.Contains("not tracked", result.Details.Summary);
    }

    [Fact]
    public async Task Percentiles_NotTracked_AreOmittedFromDimensions()
    {
        var result = await new LatencyEval(Telemetry(p50: null, p95: null, p99: 5000)).EvaluateAsync(new EvalInput("q"));
        Assert.True(result.Score.Passed);
        Assert.Equal(5000, result.Details.Dimensions!["p99_ms"]);
        Assert.DoesNotContain("p50_ms", result.Details.Dimensions.Keys);
        Assert.DoesNotContain("p95_ms", result.Details.Dimensions.Keys);
        Assert.Contains("P95 not tracked", Assert.Single(result.Details.Evidence!).Message);
    }

    [Theory]
    [InlineData("token_usage", "tokens_used")]
    [InlineData("latency", "p99_ms")]
    public async Task Usage_ReportedZero_RemainsMeasured(string key, string dimension)
    {
        IEval eval = key == "token_usage" ? new TokenUsageEval(Telemetry(tokens: 0)) : new LatencyEval(Telemetry(p99: 0));
        var result = await eval.EvaluateAsync(new EvalInput("q"));
        Assert.True(result.Score.Passed);
        Assert.Equal(1, result.Score.Value);
        Assert.True(result.Score.CountsTowardAggregate());
        Assert.Equal(0, result.Details.Dimensions![dimension]);
    }

    [Fact]
    public async Task Tokens_Negative_IsAnError() => AssertAbsent(
        await new TokenUsageEval(Telemetry(tokens: -1)).EvaluateAsync(new EvalInput("q")), "error");

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task P99_Invalid_IsAnError(double p99) => AssertAbsent(
        await new LatencyEval(Telemetry(p99: p99)).EvaluateAsync(new EvalInput("q")), "error");

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task SupportingPercentiles_Invalid_DoNotBecomeMeasurements(double reading)
    {
        var result = await new LatencyEval(Telemetry(p50: reading, p95: reading)).EvaluateAsync(new EvalInput("q"));
        Assert.True(result.Score.Passed); // P99 alone is the scoring policy.
        Assert.DoesNotContain("p50_ms", result.Details.Dimensions!.Keys);
        Assert.DoesNotContain("p95_ms", result.Details.Dimensions.Keys);
        Assert.Contains("P95 invalid", Assert.Single(result.Details.Evidence!).Message);
    }

    [Theory]
    [InlineData("token_usage")]
    [InlineData("latency")]
    public async Task TelemetryPreset_RequiredReadingNotTracked_Warns(string key)
    {
        var telemetry = key == "token_usage" ? Telemetry(tokens: null) : Telemetry(p99: null);
        var result = await AgenticBenchmark.Telemetry().EvaluateAsync(Input(telemetry));
        Assert.Equal("warn", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Contains(key, result.Details.Summary);
    }

    private static EvalInput Input(AgenticTelemetry telemetry) => new("q",
        Metadata: new Dictionary<string, object> { [LatencyEval.MetadataKey] = telemetry });

    private static void AssertAbsent(EvalResult result, string label)
    {
        Assert.Equal(label, result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.NotMeasured, result.Score.CensusBucket());
        Assert.Null(result.Details.Dimensions);
        Assert.False(string.IsNullOrWhiteSpace(result.Details.Summary));
    }
}
