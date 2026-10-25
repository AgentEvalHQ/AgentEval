// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Exporters;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Extensions;
using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using AgentEval.Output;
using AgentEval.Samples;
using AgentEval.Testing;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using Xunit;

namespace AgentEval.Memory.Tests.Reporting;

public class MemoryCompletenessBoundaryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"memory-completeness-{Guid.NewGuid():N}");
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly AgentBenchmarkConfig Config = new() { AgentName = "completeness" };

    [Fact]
    public async Task ToBaseline_ActualPartialRun_PreservesObservedScoreAndMachineReadableGapsAfterReload()
    {
        var result = await RunAsync(100, partial: true);
        Assert.False(result.IsComplete);
        Assert.Equal(100, result.OverallScore);
        Assert.True(result.UnmeasuredQueries > 0);
        var baseline = result.ToBaseline("partial", Config);
        var store = Store();

        await store.SaveAsync(baseline);
        var restored = Assert.IsType<MemoryBaseline>(await store.LoadAsync(baseline.Id));
        var json = JsonSerializer.SerializeToNode(restored, Json)!;

        Assert.Equal(false, json["is_complete"]?.GetValue<bool>());
        Assert.Equal(true, json["has_measured_score"]?.GetValue<bool>());
        Assert.Equal(100, restored.OverallScore);
        Assert.Equal(result.UnmeasuredQueries, json["category_results"]!["Basic"]!["unmeasured_queries"]?.GetValue<int>());
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, "completeness", "manifest.json")))!;
        Assert.Equal(false, manifest["benchmarks"]![0]!["baselines"]![0]!["is_complete"]?.GetValue<bool>());
    }

    [Fact]
    public async Task SaveAsync_IncompleteSnapshot_PreservesExistingCurrentAndPinnedCanonicalReferences()
    {
        var canonicalRoot = Path.Combine(_directory, "canonical");
        Directory.CreateDirectory(canonicalRoot);
        var output = new FileSystemOutputStore(canonicalRoot);
        await output.InitializeSolutionAsync("completeness");
        var subject = new SubjectIdentity(SubjectKind.Agent, Config.AgentName);
        var logger = new RecordingLogger();
        var store = Store(output, logger);
        var complete = (await RunAsync(80)).ToBaseline("complete", Config);
        await store.SaveAsync(complete);
        Assert.Equal(complete.Id, (await output.LoadBaselineAsync(subject))!.RunId);
        var previousFiles = Directory.GetFiles(canonicalRoot, "*.json", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllText);
        Assert.Equal(3, previousFiles.Count); // Solution metadata, current and version-pinned reference.
        var partial = (await RunAsync(100, partial: true)).ToBaseline("partial", Config);

        await store.SaveAsync(partial);

        Assert.NotNull(await store.LoadAsync(partial.Id));
        Assert.Equal(complete.Id, (await output.LoadBaselineAsync(subject))!.RunId);
        Assert.Equal(previousFiles.Keys.Order(), Directory.GetFiles(canonicalRoot, "*.json", SearchOption.AllDirectories).Order());
        Assert.All(previousFiles, file => Assert.Equal(file.Value, File.ReadAllText(file.Key)));
        Assert.Contains(logger.Messages, message => message.Contains(partial.Id, StringComparison.Ordinal)
            && message.Contains("saved", StringComparison.Ordinal) && message.Contains("promotion withheld", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Compare_PartialHighScoreAndCompleteLowerScore_KeepsObservationsButRanksOnlyCompleteEvidence()
    {
        var partial = (await RunAsync(100, partial: true)).ToBaseline("partial", Config);
        var complete = (await RunAsync(80)).ToBaseline("complete", Config);

        var comparison = new BaselineComparer().Compare([partial, complete]);

        Assert.Equal(complete.Id, comparison.BestBaselineId);
        var recall = comparison.Dimensions.Single(dimension => dimension.DimensionName == "Recall");
        Assert.Equal(100, recall.Scores[partial.Id]);
        Assert.Equal(complete.Id, recall.BestBaselineId);
        Assert.Equal(80, recall.BestScore);
        Assert.Equal(complete.Id, Assert.Single(MemoryComparisonLeaders.Overall([partial, complete])).Id);
        Assert.Equal(complete.Id, Assert.Single(MemoryComparisonLeaders.OnDimension([partial, complete], "Recall")).Id);
        Assert.Empty(MemoryComparisonLeaders.Overall([partial]));
    }

    [Fact]
    public async Task SaveAndCompare_LegacyCompletenessUnrecorded_DoesNotInventPromotionOrWinner()
    {
        var original = (await RunAsync(100)).ToBaseline("legacy", Config);
        var json = JsonSerializer.SerializeToNode(original, Json)!.AsObject();
        json.Remove("is_complete");
        json.Remove("has_measured_score");
        var legacy = json.Deserialize<MemoryBaseline>(Json)!;
        var output = new InMemoryOutputStore();
        var store = Store(output);

        await store.SaveAsync(legacy);

        Assert.Equal(100, (await store.LoadAsync(legacy.Id))!.OverallScore);
        Assert.Null(await output.LoadBaselineAsync(new SubjectIdentity(SubjectKind.Agent, Config.AgentName)));
        Assert.Null(new BaselineComparer().Compare([legacy]).BestBaselineId);
    }

    [Fact]
    public async Task ToEvaluationReport_PartialCategory_CannotExportAPassButKeepsObservedScore()
    {
        var result = await RunAsync(100, partial: true);
        var report = result.ToEvaluationReport();
        var category = Assert.Single(report.TestResults);

        Assert.True(category.NotMeasured);
        Assert.False(category.Passed);
        Assert.Equal(100, category.Score); // Observed subset score survives; NotMeasured withholds the full-category verdict.
        Assert.Null(report.OverallScore);
        Assert.Contains(category.MetricScores, metric => metric.Value == 100);
        Assert.Contains(result.UnmeasuredQueries.ToString(), category.Error);
        using var stream = new MemoryStream();
        await new JUnitXmlExporter().ExportAsync(report, stream);
        stream.Position = 0;
        var xml = XDocument.Load(stream);
        Assert.NotEmpty(xml.Descendants("error"));
        Assert.Empty(xml.Descendants("failure"));
    }

    [Fact]
    public async Task ToEvaluationReport_CrashKnownFailureAndUnsupported_ExportsDistinctReconciledOutcomes()
    {
        var result = await RunAsync(0, crash: true, unsupported: true);
        Assert.Equal(1, result.ErroredCategories.Count);
        var snapshot = JsonSerializer.SerializeToNode(result.ToBaseline("crashed", Config), Json)!;
        Assert.Equal(true, snapshot["category_results"]!["Crash"]!["errored"]?.GetValue<bool>());
        Assert.StartsWith("Error:", snapshot["category_results"]!["Crash"]!["skip_reason"]!.GetValue<string>());
        var report = result.ToEvaluationReport();
        Assert.Equal(3, report.TotalTests);
        Assert.Equal(1, report.FailedTests);
        Assert.Equal(2, report.SkippedTests); // Includes the executed-but-unmeasured category, per report convention.
        Assert.True(report.TestResults.Single(row => row.Name == "Crash").NotMeasured);
        Assert.False(report.TestResults.Single(row => row.Name == "Unsupported").NotMeasured);
        using var junit = new MemoryStream();
        using var trx = new MemoryStream();
        await new JUnitXmlExporter().ExportAsync(report, junit);
        await new TrxExporter().ExportAsync(report, trx);
        junit.Position = trx.Position = 0;
        var j = XDocument.Load(junit);
        var t = XDocument.Load(trx);
        Assert.Single(j.Descendants("error"));
        Assert.Single(j.Descendants("failure"));
        Assert.Single(j.Descendants("skipped"));
        var outcomes = t.Descendants().Where(node => node.Name.LocalName == "UnitTestResult").Select(node => (string?)node.Attribute("outcome"));
        Assert.Equal(new[] { "Error", "Failed", "NotExecuted" }, outcomes.Order());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    public async Task ToBaseline_NoMeasurementVersusMeasuredZero_RemainsDistinguishable(double? score)
    {
        var result = await RunAsync(score);
        var baseline = result.ToBaseline("zero", Config);
        var json = JsonSerializer.SerializeToNode(baseline, Json)!;

        Assert.Equal(score.HasValue, json["has_measured_score"]?.GetValue<bool>());
        Assert.Equal(score.HasValue, json["is_complete"]?.GetValue<bool>());
        Assert.Equal(!score.HasValue, json["category_results"]!["Basic"]!["errored"]?.GetValue<bool>());
        var output = new InMemoryOutputStore();
        var store = Store(output);
        await store.SaveAsync(baseline);
        Assert.NotNull(await store.LoadAsync(baseline.Id));
        var current = await output.LoadBaselineAsync(new SubjectIdentity(SubjectKind.Agent, Config.AgentName));
        if (score.HasValue)
            Assert.Equal("FAIL", Assert.IsType<RunSummary>(current).Verdict);
        else
            Assert.Null(current);
    }

    private JsonFileBaselineStore Store(IOutputStore? output = null, ILogger? logger = null)
    {
        var options = new MemoryReportingOptions { OutputPath = Path.Combine(_directory, "{AgentName}") };
        return output is null ? new(options) : new(options, output, logger: logger);
    }

    [Fact]
    public async Task Recommendations_PartialPerfectObservation_DoesNotClaimEveryCategoryPerformedWell()
    {
        var result = await RunAsync(100, partial: true);

        Assert.DoesNotContain(result.Recommendations, recommendation => recommendation.Contains("All categories performing well", StringComparison.Ordinal));
        Assert.Contains(result.Recommendations, recommendation => recommendation.Contains("unscored", StringComparison.OrdinalIgnoreCase));
    }

    private static Task<MemoryBenchmarkResult> RunAsync(double? score, bool partial = false, bool crash = false, bool unsupported = false)
    {
        var client = new FakeChatClient(Enumerable.Range(0, 100)
            .Select(i => score.HasValue && (!partial || i == 0) ? $"{{\"score\":{score.Value},\"found_facts\":[],\"missing_facts\":[],\"forbidden_found\":[]}}" : "No score").ToArray());
        var categories = new List<MemoryBenchmarkCategory>
        {
            new() { Name = "Basic", Weight = 1, ScenarioType = BenchmarkScenarioType.BasicRetention },
        };
        if (crash) categories.Add(new() { Name = "Crash", Weight = 1, ScenarioType = (BenchmarkScenarioType)999 });
        if (unsupported) categories.Add(new() { Name = "Unsupported", Weight = 1, ScenarioType = BenchmarkScenarioType.CrossSession });
        return MemoryBenchmarkRunner.Create(client).RunBenchmarkAsync(new RespondingAgent(), new MemoryBenchmark { Name = "Quick", Categories = categories });
    }

    private sealed class RespondingAgent : IEvaluableAgent
    {
        public string Name => "offline";
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse { Text = "offline response" });
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
