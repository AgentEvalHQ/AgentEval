// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Models;
using AgentEval.Prompts;
using AgentEval.Results.Integrity;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests.Prompts;

[Collection("ConsoleTests")]
public sealed class PromptsCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenteval-prompt-cli-" + Guid.NewGuid().ToString("N"));
    public PromptsCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task ExportValidateInspect_SupportedDefinitions_WorkWithoutProviderConfiguration()
    {
        var directory = Path.Combine(_root, "export");
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, await PromptsCommand.ExportAsync(directory, null, output, error));
        var path = Path.Combine(directory, "prompts.json");
        var pack = await new JudgePromptPackLoader().LoadAsync(path);
        Assert.Equal(15, pack.Definitions.Count);
        Assert.All(pack.Definitions, d => Assert.Equal("draft", d.Status));
        Assert.Contains(pack.Definitions, d => d.EvaluatorKey == "relevance" && d.ResponseContract == JudgeResponseContract.RubricV1);
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await PromptsCommand.InspectAsync(path, true, output, error));
        Assert.True((bool)JsonNode.Parse(output.ToString())!["valid"]!);
        Assert.DoesNotContain("systemText", output.ToString());
        Assert.Equal("", error.ToString());
        Assert.Equal(2, await PromptsCommand.ExportAsync(directory, null, TextWriter.Null, TextWriter.Null));
    }

    [Fact]
    public void Parse_PromptAuthoringAndJudgeFlags_AreRecognizedSeparatelyFromTargetPrompt()
    {
        Assert.Empty(PromptsCommand.Create().Parse(["inspect", "prompts.yaml", "--json"]).Errors);
        Assert.Empty(EvalCommand.Create().Parse(["--dataset", "cases.jsonl", "--endpoint", "http://localhost:1234/v1", "--model", "fixture",
            "--judge-prompt-pack", "prompts.json", "--judge-locale", "sv-SE", "--judge-fallback-locale", "sv", "--system-prompt", "target instructions"]).Errors);
    }

    [Fact]
    public async Task Eval_LoadedSwedishPack_GradesRetrievedEvidenceAndSealsActualInstrument()
    {
        var definition = JudgePromptTests.Swedish();
        var packDirectory = Path.Combine(_root, "pack");
        await new JudgePromptPackLoader().ExportAsync(new([definition]), packDirectory);
        var dataset = Path.Combine(_root, "cases.jsonl");
        await File.WriteAllTextAsync(dataset, """{"id":"swedish","input":"Fråga","context":["Underlag"]}""" + "\n");
        var client = new FakeChatClient("Svar", JudgePromptTests.FaithfulReply);
        var runs = Path.Combine(_root, "runs");
        var exit = await EvalCommand.ExecuteAsync(new EvalOptions
        {
            Dataset = new(dataset), Endpoint = "http://localhost:1234/v1", Model = "scripted",
            Format = "json", Quiet = true, Metrics = "llm_faithfulness", Aef = new(runs),
            JudgePromptPack = new(Path.Combine(packDirectory, "prompts.json")), JudgeLocale = "sv-SE"
        }, default, agentClientOverride: client);
        Assert.Equal(0, exit);
        Assert.Equal(2, client.CallCount);
        Assert.Equal(definition.SystemText, client.ReceivedMessages.Last().First().Text);
        var runPath = Assert.Single(Directory.GetFiles(runs, "run.json", SearchOption.AllDirectories));
        var directory = Path.GetDirectoryName(runPath)!;
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
        var run = JsonNode.Parse(await File.ReadAllTextAsync(runPath))!;
        Assert.Contains("sv-SE", run.ToJsonString());
        var lines = File.ReadLines(Path.Combine(directory, "results.ndjson")).Select(x => JsonNode.Parse(x)!).ToArray();
        Assert.Contains(lines, l => (string?)l["ext"]?["agenteval.judgePrompt"]?["effectiveLocale"] == "sv-SE");
        Assert.DoesNotContain(definition.SystemText, string.Concat(Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
    }

    [Theory]
    [InlineData("llm_toxicity")] // Supported metric, but this pack omits its Swedish definition.
    [InlineData("llm_faithfulness,code_toxicity")]
    public async Task Eval_UnsupportedJudgeOrLanguagePrefilter_FailsBeforeCallingTarget(string metrics)
    {
        var directory = Path.Combine(_root, "pack");
        await new JudgePromptPackLoader().ExportAsync(new([JudgePromptTests.Swedish()]), directory);
        var client = new FakeChatClient();
        await Assert.ThrowsAsync<ArgumentException>(() => EvalCommand.ExecuteAsync(new EvalOptions
        {
            Dataset = new(Path.Combine(_root, "not-needed.jsonl")), Endpoint = "http://localhost:1234/v1", Model = "scripted",
            Format = "json", Quiet = true, Metrics = metrics, NoAef = true,
            JudgePromptPack = new(Path.Combine(directory, "prompts.json")), JudgeLocale = "sv-SE"
        }, default, agentClientOverride: client));
        Assert.Empty(client.ReceivedMessages);
    }

    [Theory]
    [MemberData(nameof(LocalizedMetricTests.Metrics), MemberType = typeof(LocalizedMetricTests))]
    public async Task Eval_AdditionalLocalizedMetric_UsesSelectedPackAndSealsContentFreeEvidence(string key)
    {
        var definition = BuiltInJudgePrompts.GetMetric(key) with { Locale = "sv-SE", SystemText = "Privata svenska bedömningsinstruktioner." };
        var packDirectory = Path.Combine(_root, "pack");
        await new JudgePromptPackLoader().ExportAsync(new([definition]), packDirectory);
        var dataset = Path.Combine(_root, "cases.jsonl");
        await File.WriteAllTextAsync(dataset, """{"id":"one","input":"Fråga","context":["Underlag"]}""");
        using var client = new FakeChatClient("Svar", LocalizedMetricTests.Reply(key));
        var runs = Path.Combine(_root, "runs");
        var exit = await EvalCommand.ExecuteAsync(new EvalOptions
        {
            Dataset = new(dataset), Endpoint = "http://localhost:1234/v1", Model = "scripted",
            Format = "json", Quiet = true, Metrics = key, Aef = new(runs),
            JudgePromptPack = new(Path.Combine(packDirectory, "prompts.json")), JudgeLocale = "sv-SE"
        }, default, agentClientOverride: client);
        Assert.Equal(0, exit);
        Assert.Equal(2, client.CallCount);
        Assert.Equal(definition.SystemText, client.ReceivedMessages.Last().First().Text);
        var run = Assert.Single(Directory.GetFiles(runs, "run.json", SearchOption.AllDirectories));
        var directory = Path.GetDirectoryName(run)!;
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
        Assert.Contains("sv-SE", await File.ReadAllTextAsync(run));
        var results = File.ReadLines(Path.Combine(directory, "results.ndjson")).Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Contains(results, line => (string?)line["ext"]?["agenteval.judgePrompt"]?["effectiveLocale"] == "sv-SE");
        Assert.DoesNotContain(definition.SystemText, string.Concat(Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToMetricsContext_RuntimeRetrievalBeatsFixture_IncludingCapturedEmpty(bool empty)
    {
        var context = EvalCommand.ToMetricsContext(new DatasetTestCase { Input = "q", Context = ["fixture"] },
            new TestResult { TestName = "case", ActualOutput = "a", JudgeContext = empty ? [] : ["retrieved"] });
        Assert.Equal(empty ? "" : "retrieved", context.Context);
    }

    [Fact]
    public async Task Eval_EnglishPackTargetsCodePrefilter_RejectsBeforeCallingTarget()
    {
        var directory = Path.Combine(_root, "prefilter-pack");
        var definition = BuiltInJudgePrompts.GetRag("llm_faithfulness");
        await new JudgePromptPackLoader().ExportAsync(new([definition,
            definition with { EvaluatorKey = "code_toxicity" }]), directory);
        using var client = new FakeChatClient();
        var error = await Assert.ThrowsAsync<ArgumentException>(() => EvalCommand.ExecuteAsync(new EvalOptions
        {
            Dataset = new(Path.Combine(_root, "not-needed.jsonl")), Endpoint = "http://localhost:1234/v1", Model = "scripted",
            Format = "json", Quiet = true, Metrics = "llm_faithfulness,code_toxicity", NoAef = true,
            JudgePromptPack = new(Path.Combine(directory, "prompts.json")), JudgeLocale = "en"
        }, default, agentClientOverride: client));
        Assert.Contains("code_toxicity", error.Message);
        Assert.Empty(client.ReceivedMessages);
    }

    [Fact]
    public async Task Eval_DifferentCriterionTextWithSameOrdinalIds_RejectsBeforeReplacingCaseInstructions()
    {
        var definition = BuiltInJudgePrompts.ForEvaluator("judge.criteria", ["Be helpful"]) with { Locale = "sv-SE" };
        var directory = Path.Combine(_root, "criteria-pack");
        await new JudgePromptPackLoader().ExportAsync(new([definition]), directory);
        var dataset = Path.Combine(_root, "criteria.jsonl");
        await File.WriteAllTextAsync(dataset, """
            {"id":"one","input":"Q1","evaluation_criteria":["Be helpful"]}
            {"id":"two","input":"Q2","evaluation_criteria":["Never disclose secrets"]}
            """);
        // Fail every attempted target call locally, so an incorrect preflight cannot call a real judge.
        using var client = new AgentEval.Tests.Evals.RubricJudgeTests.RecordingChatClient(_ =>
            throw new InvalidOperationException("The target must not run before criterion-roster validation."));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => EvalCommand.ExecuteAsync(new EvalOptions
        {
            Dataset = new(dataset), Endpoint = "http://localhost:1234/v1", Model = "scripted",
            JudgeEndpoint = "http://localhost:1234/v1", Format = "json", Quiet = true, NoAef = true,
            JudgePromptPack = new(Path.Combine(directory, "prompts.json")), JudgeLocale = "sv-SE"
        }, default, agentClientOverride: client));
        Assert.Contains("criterion roster", error.Message);
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Eval_ConfiguredCriteriaOverHttp_SupportsOrdinaryAndStochasticRuns(int runs)
    {
        var definition = BuiltInJudgePrompts.ForEvaluator("judge.criteria", ["Supported"]) with
        {
            Locale = "sv-SE", SystemText = "Kundens svenska bedömning. Returnera JSON enligt svarskontraktet.",
            Criteria = [new("criterion-1", "Svaret stöds av underlaget.")]
        };
        var directory = Path.Combine(_root, "http-pack");
        await new JudgePromptPackLoader().ExportAsync(new([definition]), directory);
        var dataset = Path.Combine(_root, "http.jsonl");
        await File.WriteAllTextAsync(dataset, """{"id":"one","input":"Fråga","context":["Underlag"],"evaluation_criteria":["Supported"]}""");
        const string reply = """{"overallScore":90,"summary":"Bra","improvements":[],"criteriaResults":[{"id":"criterion-1","met":true,"explanation":"Ja"}]}""";
        await using var endpoint = await AgentEval.Tests.TestHelpers.LocalChatEndpoint.StartAsync(reply);
        using var target = new AgentEval.Tests.Evals.RubricJudgeTests.RecordingChatClient(_ => "Svar");
        var artifactRoot = Path.Combine(_root, "runs");
        var exit = await EvalCommand.ExecuteAsync(new EvalOptions
        {
            Dataset = new(dataset), Endpoint = "http://localhost:1234/v1", Model = "scripted", Runs = runs,
            JudgeEndpoint = endpoint.Endpoint, JudgeModel = "scripted-judge", ApiKey = "loopback-test-key", Format = "json", Quiet = true,
            Aef = new(artifactRoot), JudgePromptPack = new(Path.Combine(directory, "prompts.json")), JudgeLocale = "sv-SE"
        }, default, agentClientOverride: target);
        Assert.Equal(0, exit);
        Assert.Equal(runs, target.Calls.Count);
        Assert.Equal(runs, endpoint.Requests.Count);
        var unescapedJson = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        Assert.All(endpoint.Requests, request => Assert.Contains(definition.SystemText, request.ToJsonString(unescapedJson)));
        var runPath = Assert.Single(Directory.GetFiles(artifactRoot, "run.json", SearchOption.AllDirectories));
        var runDirectory = Path.GetDirectoryName(runPath)!;
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(runDirectory).Outcome);
        var results = File.ReadAllLines(Path.Combine(runDirectory, "results.ndjson")).Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Contains(results, result => (string?)result["ext"]?["agenteval.judgePrompt"]?["effectiveLocale"] == "sv-SE");
        Assert.DoesNotContain(definition.SystemText, string.Concat(Directory.GetFiles(runDirectory, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
    }
}
