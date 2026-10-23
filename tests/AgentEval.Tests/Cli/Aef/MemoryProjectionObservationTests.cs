// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Cli.Commands;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;

namespace AgentEval.Tests.Cli.Aef;

public sealed class MemoryProjectionObservationTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("memory-observations-");
    public void Dispose() => _root.Delete(true);
    private static MemoryBenchmarkResult Memory() => new()
    {
        BenchmarkName = "Memory", Duration = TimeSpan.FromMilliseconds(50),
        CategoryResults = [new() { CategoryName = "Category", ScenarioType = BenchmarkScenarioType.BasicRetention,
            Weight = 1, Score = 80, Duration = TimeSpan.FromMilliseconds(34.5) }],
    };
    private JsonNode[] Write(AgentEval.Evals.EvalResult tree, AefComponentShape? shape = null)
    {
        var now = DateTimeOffset.Parse("2026-10-01T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var directory = AefCompositeRunWriter.Write(_root.FullName, tree, shape ?? AefCompositeRunWriter.Undescribed,
            new() { SubjectRef = "agent:test", SuiteRef = "bench:test", Lane = "memory", TargetMode = AefTargetMode.Mocked }, now, now.AddSeconds(1)).Directory;
        return File.ReadAllLines(Path.Combine(directory, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToArray();
    }

    [Fact]
    public void MemoryTree_RecordedBoundaryDurations_AreNotLostOrRecomputed()
    {
        var (tree, shape) = BenchAef.MemoryTree(Memory(), "quick", "PASS", null);
        var lines = Write(tree, shape);
        Assert.Equal(50, (double?)Assert.Single(lines, l => l["parentResultId"] is null)["durationMs"]);
        Assert.Equal(34.5, (double?)Assert.Single(lines, l => l["parentResultId"] is not null)["durationMs"]);
    }

    [Fact]
    public void StochasticRollup_NewObservation_DoesNotBorrowFirstTrialsInterval()
    {
        var (first, _) = BenchAef.MemoryTree(Memory(), "quick", "PASS", null);
        var (second, _) = BenchAef.MemoryTree(Memory(), "quick", "PASS", null);
        var rollup = StochasticBenchRunner.RollupTree([first, second]);
        Assert.Null(rollup.ObservedDuration);
        var benchmark = Assert.Single(rollup.Details.SubResults!);
        Assert.Null(benchmark.ObservedDuration);
        Assert.Null(Assert.Single(benchmark.Details.SubResults!).ObservedDuration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("configured-model")]
    public void MemoryTree_NoCategoryInstrumentObservation_DoesNotGuessFromConfiguration(string? model)
    {
        var (tree, shape) = BenchAef.MemoryTree(Memory(), "quick", "PASS", model);
        var leaf = Assert.Single(Write(tree, shape), l => l["parentResultId"] is not null);
        Assert.Equal("unknown", (string?)leaf["ext"]!["agenteval.comparison"]!["facts"]!["instrumentKind"]);
        Assert.Null(leaf["ext"]!["agenteval.comparison"]!["facts"]!["judge"]);
        Assert.Equal("OTHER", (string?)leaf["annotator"]!["kind"]);
    }

    [Theory]
    [InlineData(false, null, 1, true, "model")]
    [InlineData(false, "known-judge", 1, true, "model")]
    [InlineData(false, null, 0, true, "unknown")]
    [InlineData(false, "configured-only", 0, true, "unknown")]
    [InlineData(false, "configured-only", 1, false, "unknown")]
    [InlineData(true, null, 1, true, "model")]
    [InlineData(true, "known-judge", 1, true, "model")]
    [InlineData(true, null, 0, true, "unknown")]
    [InlineData(true, "configured-only", 0, true, "unknown")]
    [InlineData(true, "configured-only", 1, false, "unknown")]
    public void QuestionTrees_ObservedJudgeCalls_KeepInstrumentAndIdentitySeparate(bool typed, string? model, int calls, bool measured, string instrument)
    {
        var question = new QuestionResult
        {
            QuestionId = "q", QuestionType = "test", Question = "q", GoldAnswer = "g", AgentResponse = "a",
            Correct = measured ? true : null, RawScore = null, JudgeLlmCallCount = calls,
            TypedOutcome = typed ? new() { Shape = "recency", Outcome = measured ? TypedMemEvalOutcome.Correct : TypedMemEvalOutcome.Inconclusive,
                Attribution = TypedMemEvalEvidenceAttribution.Unobserved } : null,
        };
        AgentEval.Evals.EvalResult tree;
        if (typed) (tree, _) = BenchAef.TypedTree([question], measured ? 1 : null, "episodic", model);
        else tree = BenchAef.QuestionTree(new ExternalBenchmarkResult
        {
            BenchmarkId = "longmemeval", BenchmarkName = "LongMemEval", QuestionResults = [question],
            OverallAccuracy = measured ? 100 : null, TaskAveragedAccuracy = null, PerTypeResults = [],
            Duration = TimeSpan.FromSeconds(1), Options = new(),
        }, "longmemeval", "quick", measured ? "PASS" : "INCONCLUSIVE", model);
        var leaf = Assert.Single(Write(tree));
        var facts = leaf["ext"]!["agenteval.comparison"]!["facts"]!;
        Assert.Equal(instrument, (string?)facts["instrumentKind"]);
        Assert.Equal(measured && calls > 0 ? model : null, (string?)facts["judge"]?["modelId"]);
        if (!measured) Assert.Null(leaf["annotator"]);
        else Assert.Equal(calls > 0 ? "LLM" : "OTHER", (string?)leaf["annotator"]!["kind"]);
    }
}
