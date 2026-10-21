// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Cli.Commands;
using AgentEval.Evals;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

[Collection("ConsoleTests")]
public sealed class ScenarioScoreNullableTests
{
    internal static ScenarioResult Scenario(string? measurement = null, double score = 0) =>
        new("case", "Case", "in", "plain output", false, score, new Dictionary<string, double>(), [], TimeSpan.Zero, null)
        {
            Measurement = measurement,
            StimulusHash = "sha256:aaa",
            Comparability = new ComparabilityFacts("eval.k", "1.0.0")
            {
                EffectiveBar = 0.7, Judge = new JudgeFingerprint("offline", "sha256:rubric"),
            },
        };

    [Theory]
    [InlineData(ScenarioMeasurement.NotMeasured)]
    [InlineData(ScenarioMeasurement.NotApplicable)]
    public void Score_UnmeasuredProducer_NormalizesPlaceholder(string measurement)
    {
        var result = Scenario(measurement, 0.9);
        Assert.Null(result.Score);
        Assert.False(ScenarioMeasurement.IsMeasured(result));
        Assert.Null((result with { Score = 0 }).Score);
    }

    [Fact]
    public void Compare_UnmeasuredSide_HasNoScoreOrDelta()
    {
        var comparison = RunComparison.Of([Scenario(ScenarioMeasurement.NotMeasured)], [Scenario(score: 0.9)]);
        var pair = Assert.Single(comparison.Scenarios);
        Assert.Null(pair.BaselineScore);
        Assert.Null(pair.ScoreDelta);
        Assert.Null(comparison.MeanScoreDelta);
        Assert.Equal(0.9, pair.CandidateScore);
    }

    [Fact]
    public void Compare_Empty_HasNoMean() => Assert.Null(RunComparison.Of([], []).MeanScoreDelta);

    [Fact]
    public void Score_MeasuredZero_RemainsMeasured()
    {
        var result = Scenario();
        Assert.Equal(0, result.Score);
        Assert.True(ScenarioMeasurement.IsMeasured(result));
        var comparison = RunComparison.Of([result], [result]);
        Assert.Equal(0, comparison.MeanScoreDelta);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Store_UnmeasuredScenario_RoundTripsWithoutPlaceholder(bool legacy)
    {
        using var temp = TempWorkspace.Create();
        var store = new FileSystemOutputStore(temp.Path);
        var subject = new SubjectIdentity(SubjectKind.Agent, "test");
        var manifest = await store.StartRunAsync(subject, new("test", ".", "offline", null, null, "eval"));
        await store.WriteScenarioResultAsync(manifest.Run.RunId, Scenario(ScenarioMeasurement.NotMeasured));
        var path = Directory.GetFiles(store.ResolveRunDirectory(subject, manifest.Run.RunId), "*.json", SearchOption.AllDirectories)
            .Single(p => Path.GetFileName(Path.GetDirectoryName(p)) == "scenarios");
        if (legacy)
        {
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            json["score"] = 0;
            await File.WriteAllTextAsync(path, json.ToJsonString());
        }
        var scenarios = new List<ScenarioResult>();
        await foreach (var scenario in store.GetScenarioResultsAsync(manifest.Run.RunId)) scenarios.Add(scenario);
        Assert.Null(Assert.Single(scenarios).Score);
        if (!legacy)
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("score").ValueKind);
        }
    }

    [Fact]
    public void Persist_UnmeasuredEval_HasNeitherScoreNorInventedDuration()
    {
        var leaf = new EvalResult(new("eval.k", "K", "test", "1.0.0"),
            new EvalScore(0, null, "error", false, null, "none", null),
            new(null, null, null, null, null), new("code", null, null, null, null, null, false), DateTimeOffset.UtcNow);
        var scenario = EvalResultPersistence.ToScenarioResult(leaf, "case", "Case");
        Assert.Null(scenario.Duration);
        Assert.Null(scenario.Score);
    }

    [Fact]
    public async Task CompareJson_UnmeasuredSide_WritesExplicitNull()
    {
        using var temp = TempWorkspace.Create();
        var store = new FileSystemOutputStore(temp.Path);
        var subject = new SubjectIdentity(SubjectKind.Agent, "test");
        async Task<string> Write(ScenarioResult scenario)
        {
            var manifest = await store.StartRunAsync(subject, new("test", ".", "offline", null, null, "eval"));
            await store.WriteScenarioResultAsync(manifest.Run.RunId, scenario);
            return store.ResolveRunDirectory(subject, manifest.Run.RunId);
        }
        var baseline = await Write(Scenario(ScenarioMeasurement.NotMeasured));
        var candidate = await Write(Scenario(score: 0.9));
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, CompareCommand.Run(baseline, candidate, asJson: true));
        }
        finally { Console.SetOut(original); }
        using var json = JsonDocument.Parse(output.ToString());
        var delta = json.RootElement.GetProperty("deltas")[0];
        Assert.Equal(JsonValueKind.Null, delta.GetProperty("baseline").ValueKind);
        Assert.Equal(JsonValueKind.Null, delta.GetProperty("delta").ValueKind);
        Assert.Equal(0.9, delta.GetProperty("candidate").GetDouble());
    }
}
