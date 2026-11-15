// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Prompts;
using AgentEval.Testing;
using Xunit;
using Gdpr = AgentEval.Compliance.Gdpr.Articles;
using Eu = AgentEval.Compliance.EuAiAct.Articles;
using static AgentEval.Tests.Evals.RubricJudgeTests;

namespace AgentEval.Tests.Prompts;

[Collection("ConsoleTests")]
public sealed class CalibrationPromptCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenteval-calibration-prompts-" + Guid.NewGuid().ToString("N"));
    public CalibrationPromptCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("agentic")]
    [InlineData("gdpr")]
    [InlineData("eu-ai-act")]
    public async Task Calibrate_SuppliedFixtureAndPack_UsesSelectedInstrumentAndReportsUnverifiedLabels(string family)
    {
        var (definition, entry) = Case(family);
        definition = definition with { Locale = "sv-SE", SystemText = "Private Swedish fixture rubric", Version = "2.0.0", PassAt = .97 };
        var directory = Path.Combine(_root, "pack");
        await new JudgePromptPackLoader().ExportAsync(new([definition]), directory);
        var dataset = Path.Combine(_root, "cases.jsonl");
        await File.WriteAllTextAsync(dataset, entry.ToJsonString() + "\n");
        var selection = new CalibrationCliOptions.Selection(new(dataset), new(new(Path.Combine(directory, "prompts.json")), "sv-SE"));
        var reply = definition.ResponseContract == JudgeResponseContract.CriteriaV1
            ? JsonSerializer.Serialize(new { overallScore = 100, summary = "Fixture", improvements = Array.Empty<string>(),
                criteriaResults = definition.Criteria.Select(c => new { id = c.Id, met = true, explanation = "Fixture" }) })
            : """{"score":1,"reasoning":"Fixture"}""";
        using var client = new RecordingChatClient(_ => reply);
        var judge = new ChatClientEvaluator(client);
        var report = Path.Combine(_root, "report.md");
        var records = Path.Combine(_root, "records.jsonl");
        var exit = family switch
        {
            "gdpr" => await BenchCalibrateCommand.RunCoreAsync(_root, report, judge, limitPerPillar: 1, selection: selection),
            "eu-ai-act" => await BenchEuAiActCalibrateCommand.RunCoreAsync(_root, report, judge, limitPerPillar: 1, selection: selection),
            _ => await BenchAgenticCalibrateCommand.RunCoreAsync(_root, report, judge, recordsPath: records, limitPerCategory: 1, selection: selection)
        };
        Assert.Equal(0, exit);
        var sent = Assert.Single(client.Calls);
        Assert.Contains(sent, m => m.Text.Contains(definition.SystemText, StringComparison.Ordinal));
        var text = await File.ReadAllTextAsync(report);
        Assert.Contains("Label review: not verified", text);
        Assert.Contains("supplied-jsonl", text);
        Assert.Contains("sha256:", text);
        Assert.Contains("LIMITED RUN", text);
        Assert.DoesNotContain(definition.SystemText, text);
        if (family == "agentic")
        {
            var row = JsonNode.Parse(Assert.Single(await File.ReadAllLinesAsync(records)))!;
            Assert.Equal("unverified", (string?)row["labelReview"]);
            Assert.StartsWith("sha256:", (string?)row["datasetDigest"]);
            Assert.Equal("sv-SE", (string?)row["leaves"]![0]!["judgePrompt"]!["effectiveLocale"]);
            Assert.DoesNotContain(definition.SystemText, row.ToJsonString());
        }
        else
        {
            Assert.Contains("[sv-SE]", text);
            Assert.Contains("| Production scenario attempts | 1 |", text);
            Assert.Contains("| Surrogate rubric attempts | 0 |", text);
        }
    }

    [Fact]
    public async Task Calibrate_InvalidLaterInstrument_PreservesExistingRecordsAndMakesNoCalls()
    {
        var (definition, entry) = Case("agentic");
        var directory = Path.Combine(_root, "pack");
        await new JudgePromptPackLoader().ExportAsync(new([definition]), directory);
        var dataset = Path.Combine(_root, "cases.jsonl");
        await File.WriteAllTextAsync(dataset, entry.ToJsonString());
        var selection = new CalibrationCliOptions.Selection(new(dataset), new(new(Path.Combine(directory, "prompts.json")), "sv-SE"));
        var records = Path.Combine(_root, "records.jsonl");
        await File.WriteAllTextAsync(records, "existing evidence");
        using var client = new FakeChatClient();
        var exit = await BenchAgenticCalibrateCommand.RunCoreAsync(_root, Path.Combine(_root, "report.md"), new ChatClientEvaluator(client),
            recordsPath: records, selection: selection);
        Assert.Equal(2, exit);
        Assert.Equal(0, client.CallCount);
        Assert.Equal("existing evidence", await File.ReadAllTextAsync(records));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{\"scenarioId\":\"one\",\"scenarioId\":\"two\"}")]
    [InlineData("{\"scenarioId\":\"one\",\"evaluatorKey\":\"relevance\",\"input\":\"q\",\"agentResponse\":\"a\",\"expectedVerdict\":\"pass\",\"rationale\":\"fixture\",\"expectedScoreMin\":0.9,\"expectedScoreMax\":0.1}")]
    public async Task Load_InvalidCalibrationDataset_RejectsBeforeTypedLoader(string json)
    {
        var file = new FileInfo(Path.Combine(_root, "bad.jsonl"));
        await File.WriteAllTextAsync(file.FullName, json);
        var called = false;
        await Assert.ThrowsAsync<ArgumentException>(() => CalibrationCliOptions.LoadAsync(file,
            (_, _, _) => { called = true; return Task.FromResult(1); }, default));
        Assert.False(called);
    }

    [Theory]
    [InlineData("agentic")]
    [InlineData("gdpr")]
    [InlineData("eu-ai-act")]
    public void Parse_CalibrationDataAndPromptFlags_AreAvailableOnEachFamily(string family) =>
        Assert.Empty(CliCommandTree.Build().Root.Parse(["bench", family, "calibrate", "--dataset", "cases.jsonl", "--out", "review.md",
            "--judge-prompt-pack", "prompts.json", "--judge-locale", "sv-SE"]).Errors);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_PromptFlagsBeforeOrAfterCalibrate_ReachTheSameSelection(bool before)
    {
        var parent = new System.CommandLine.Command("family");
        var child = new System.CommandLine.Command("calibrate");
        parent.Add(child);
        var prompts = new JudgePromptCliOptions(parent, recursive: true);
        var options = new CalibrationCliOptions(child, prompts, compliance: true);
        string[] flags = ["--judge-prompt-pack", "prompts.json", "--judge-locale", "sv-SE"];
        var parsed = parent.Parse(before ? [.. flags, "calibrate"] : ["calibrate", .. flags]);
        Assert.Empty(parsed.Errors);
        var selection = options.Read(parsed);
        Assert.Equal("sv-SE", selection.Prompts.Locale);
        Assert.Equal("prompts.json", selection.Prompts.Pack!.Name);
    }

    private static (JudgePromptDefinition Definition, JsonObject Entry) Case(string family)
    {
        using var client = new FakeChatClient();
        var judge = new ChatClientEvaluator(client);
        var definition = family == "agentic" ? PromptsCommand.SupportedDefinitions().Single(d => d.EvaluatorKey == "relevance")
            : BenchmarkPromptDefinitions.ForPreset(family, "smoke").First();
        var entry = new JsonObject
        {
            ["scenarioId"] = family == "agentic" ? "fixture-case" : definition.EvaluatorKey,
            ["input"] = "question", ["agentResponse"] = "answer", ["expectedVerdict"] = "pass",
            ["expectedScoreMin"] = .9, ["expectedScoreMax"] = 1, ["rationale"] = "AI-authored fixture, no human review"
        };
        if (family == "agentic") entry["evaluatorKey"] = "relevance";
        else if (family == "gdpr")
        {
            var registry = new Gdpr.ArticlesRegistry(new Gdpr.Loading.ArticleScenarioYamlLoader(), new Gdpr.Building.ArticleCompositeBuilder(new Gdpr.Building.ScenarioToAtomicEval(judge)));
            entry["articleControlId"] = registry.All.Keys.Single(k => registry.GetSpec(k).Scenarios.Any(s => s.Id == definition.EvaluatorKey));
        }
        else
        {
            var registry = new Eu.EuAiActArticlesRegistry(new Eu.Loading.ArticleScenarioYamlLoader(), new Eu.Building.ArticleCompositeBuilder(new Eu.Building.ScenarioToAtomicEval(judge)));
            entry["articleControlId"] = registry.All.Keys.Single(k => registry.GetSpec(k).Scenarios.Any(s => s.Id == definition.EvaluatorKey));
        }
        return (definition, entry);
    }
}
