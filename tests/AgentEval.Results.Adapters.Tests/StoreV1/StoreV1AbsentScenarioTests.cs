// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Integrity;

namespace AgentEval.Results.Adapters.Tests.StoreV1;

public sealed class StoreV1AbsentScenarioTests : IDisposable
{
    private readonly string _root = AefTestRuns.TempPath("nullable-scenario");
    public void Dispose() => AefTestRuns.Delete(_root);

    private async Task<System.Text.Json.Nodes.JsonObject> Export(string? measurement, double? cost = null)
    {
        var store = new FileSystemOutputStore(Path.Combine(_root, "store"));
        await store.InitializeSolutionAsync("test");
        var manifest = await store.StartRunAsync(new(SubjectKind.Agent, "test"), new("test", ".", "offline", null, null, "eval"));
        await store.WriteScenarioResultAsync(manifest.Run.RunId,
            new("case", "Case", "in", "plain text", false, 0, new Dictionary<string, double>(), [], TimeSpan.Zero, cost)
            { Measurement = measurement });
        await store.CompleteRunAsync(manifest, new("1.0", manifest.Run.RunId, "FAIL", new(1, 0, 1, 0), new Dictionary<string, double>()));
        var output = Path.Combine(_root, "aef");
        var conversion = await StoreV1Exporter.ExportAsync(store, manifest.Run.RunId, output);
        Assert.Equal(AefOutcome.Intact, conversion.Verification.Outcome);
        return AefTestRuns.Results(output).Single().AsObject();
    }

    [Theory]
    [InlineData(ScenarioMeasurement.NotMeasured)]
    [InlineData(ScenarioMeasurement.NotApplicable)]
    public async Task Export_PlainUnmeasuredScenario_PreservesAbsence(string measurement)
    {
        var result = await Export(measurement);
        Assert.Equal(measurement, (string?)result["state"]);
        Assert.Null(result["scores"]);
        Assert.Null(result["ext"]!["agenteval.store-v1"]!["score"]);
        Assert.NotNull(result["reason"]);
    }

    [Fact]
    public async Task Export_RecordedZeroDurationAndScore_PreservesBoth()
    {
        var result = await Export(null);
        Assert.Equal(0.0, (double?)result["durationMs"]);
        Assert.Equal(0.0, (double?)result["scores"]![0]!["value"]);
        Assert.Equal("failed", (string?)result["state"]);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(0.001d)]
    public async Task Export_RecordedNonnegativeCost_PreservesUsage(double cost)
    {
        var result = await Export(null, cost);
        var usage = Assert.Single(Assert.IsType<System.Text.Json.Nodes.JsonArray>(result["usage"]));
        Assert.Equal(cost, (double?)usage!["costUsd"]);
        Assert.Equal("other", (string?)usage["role"]);
        Assert.Contains("ScenarioResult.EstimatedCost", (string?)usage["costSource"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-0.001d)]
    public async Task Export_AbsentOrNegativeCost_DoesNotInventUsage(double? cost)
    {
        var result = await Export(null, cost);
        Assert.Null(result["usage"]);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task Export_InMemoryNonFiniteCost_DoesNotInventUsage(double cost)
    {
        // The filesystem store cannot serialize non-finite JSON numbers; the public in-memory
        // export boundary must still guard custom readers that supply them.
        using var fixture = await StoreV1Fixture.CreateAsync();
        var manifest = (await fixture.Store.GetRunManifestAsync(fixture.RunId))!;
        ScenarioResult scenario = new("case", "Case", "in", "plain text", false, 0,
            new Dictionary<string, double>(), [], TimeSpan.Zero, cost);
        var output = Path.Combine(_root, "nonfinite");
        var conversion = StoreV1Exporter.Export(new(manifest, null, [scenario], null, []), output);
        Assert.Equal(AefOutcome.Intact, conversion.Verification.Outcome);
        Assert.Null(AefTestRuns.Results(output).Single()["usage"]);
    }
}
