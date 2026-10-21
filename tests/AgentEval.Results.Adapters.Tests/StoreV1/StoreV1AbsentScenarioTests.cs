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

    private async Task<System.Text.Json.Nodes.JsonObject> Export(string? measurement)
    {
        var store = new FileSystemOutputStore(Path.Combine(_root, "store"));
        await store.InitializeSolutionAsync("test");
        var manifest = await store.StartRunAsync(new(SubjectKind.Agent, "test"), new("test", ".", "offline", null, null, "eval"));
        await store.WriteScenarioResultAsync(manifest.Run.RunId,
            new("case", "Case", "in", "plain text", false, 0, new Dictionary<string, double>(), [], TimeSpan.Zero, null)
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
}
