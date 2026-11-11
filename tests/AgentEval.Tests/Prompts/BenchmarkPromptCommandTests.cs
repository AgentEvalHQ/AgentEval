// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Prompts;
using AgentEval.Results.Integrity;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentEval.Tests.Evals.RubricJudgeTests;

namespace AgentEval.Tests.Prompts;

[Collection("ConsoleTests")]
public sealed class BenchmarkPromptCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenteval-bench-prompts-" + Guid.NewGuid().ToString("N"));
    public BenchmarkPromptCommandTests()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_root, ".agenteval"));
        File.WriteAllText(Path.Combine(workspace.FullName, "solution.json"),
            JsonSerializer.Serialize(new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "prompt-tests" }));
    }
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("agentic", "rag-quality")]
    [InlineData("agentic", "adversarial-direct")]
    [InlineData("gdpr", "smoke")]
    [InlineData("gdpr", "standard+healthcare")]
    [InlineData("eu-ai-act", "smoke")]
    [InlineData("eu-ai-act", "standard+high-risk-credit")]
    public async Task ExportInspect_SelectedFamily_ValidatesItsActualGraphWithoutModelCalls(string family, string preset)
    {
        var directory = Path.Combine(_root, "export");
        var errors = new StringWriter();
        Assert.Equal(0, await PromptsCommand.ExportAsync(directory, null, TextWriter.Null, errors, family: family, preset: preset));
        var manifest = Path.Combine(directory, "prompts.json");
        var pack = await new JudgePromptPackLoader().LoadAsync(manifest);
        var definitions = BenchmarkPromptDefinitions.ForPreset(family, preset);
        Assert.Equal(definitions.Select(d => d.EvaluatorKey), pack.Definitions.Select(d => d.EvaluatorKey));
        Assert.Equal(definitions.Select(d => d.SystemText), pack.Definitions.Select(d => d.SystemText));
        Assert.Equal(0, await PromptsCommand.InspectAsync(manifest, true, TextWriter.Null, errors, family: family, preset: preset));
        Assert.Equal(2, await PromptsCommand.InspectAsync(manifest, false, TextWriter.Null, errors));
    }

    [Theory]
    [InlineData("gdpr", "smoke", 1)]
    [InlineData("gdpr", "smoke", 3)]
    [InlineData("eu-ai-act", "smoke", 1)]
    [InlineData("eu-ai-act", "smoke", 3)]
    [InlineData("agentic", "rag-quality", 1)]
    public async Task Benchmark_LocalizedPack_RetainsInstrumentIdentityInSealedOrdinaryAndRepeatedRuns(string family, string preset, int runs)
    {
        var definitions = BenchmarkPromptDefinitions.ForPreset(family, preset).Select(d => d with
        {
            Locale = "sv-SE", SystemText = "Svensk testbedömning: " + d.EvaluatorKey,
            Version = "2.0.0", PassAt = .99
        }).ToArray();
        var directory = Path.Combine(_root, "pack");
        await new JudgePromptPackLoader().ExportAsync(new(definitions), directory);
        var selection = new JudgePromptCliOptions.Selection(new(Path.Combine(directory, "prompts.json")), "sv-SE");
        using var client = new RecordingChatClient(messages => Reply(definitions.Single(d =>
            messages.Any(m => m.Role == ChatRole.System && m.Text.Contains(d.SystemText, StringComparison.Ordinal)))));
        var judge = new ChatClientEvaluator(client);
        var aef = new DirectoryInfo(Path.Combine(_root, "evidence"));
        var exit = await Run(family, preset, judge, selection, aef, runs);
        Assert.Contains(exit, new[] { 0, 1 }); // Some composites require evidence this supplied answer does not contain.
        Assert.NotEmpty(client.Calls);
        Assert.All(client.Calls, messages => Assert.Contains(messages, m => m.Role == ChatRole.System && m.Text.Contains("Svensk testbedömning:")));
        var run = Assert.Single(Directory.GetFiles(aef.FullName, "run.json", SearchOption.AllDirectories));
        var runDirectory = Path.GetDirectoryName(run)!;
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(runDirectory).Outcome);
        var prompts = File.ReadLines(Path.Combine(runDirectory, "results.ndjson"))
            .Select(l => JsonNode.Parse(l)!["ext"]?["agenteval.judgePrompt"]).Where(p => p is not null).ToArray();
        Assert.NotEmpty(prompts);
        var options = (await selection.LoadAsync(default))!;
        var digests = definitions.Select(d => new JudgePromptResolver().Resolve(d, options).Provenance.Digest).ToHashSet();
        Assert.All(prompts, p =>
        {
            Assert.Equal("sv-SE", (string?)p!["effectiveLocale"]);
            Assert.Contains((string)p["digest"]!, digests);
        });
        if (family != "agentic") Assert.Equal(definitions.Length * runs, client.Calls.Count);
        Assert.DoesNotContain("Svensk testbedömning:", string.Concat(Directory.GetFiles(runDirectory, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
    }

    [Theory]
    [InlineData("gdpr")]
    [InlineData("eu-ai-act")]
    [InlineData("agentic")]
    public async Task Benchmark_MissingSelectedLocale_FailsBeforeJudgeCallsOrEvidenceWrites(string family)
    {
        var preset = family == "agentic" ? "rag-quality" : "smoke";
        var directory = Path.Combine(_root, "pack");
        await new JudgePromptPackLoader().ExportAsync(new(BenchmarkPromptDefinitions.ForPreset(family, preset)), directory);
        using var client = new FakeChatClient();
        var aef = new DirectoryInfo(Path.Combine(_root, "evidence"));
        var selection = new JudgePromptCliOptions.Selection(new(Path.Combine(directory, "prompts.json")), "sv-SE");
        Assert.Equal(2, await Run(family, preset, new ChatClientEvaluator(client), selection, aef));
        Assert.Equal(0, client.CallCount);
        Assert.False(aef.Exists);
    }

    [Fact]
    public async Task Load_LocaleWithoutPack_RejectsUnusedSelection() =>
        await Assert.ThrowsAsync<ArgumentException>(() => JudgePromptCliOptions.LoadAsync(null, "sv-SE", null, default));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compliance_IncompatibleCriterionRoster_FailsBeforeDrivingLiveTarget(bool eu)
    {
        var family = eu ? "eu-ai-act" : "gdpr";
        var definitions = BenchmarkPromptDefinitions.ForPreset(family, "smoke").Select(d => d with
        {
            Locale = "sv-SE", Criteria = [new("wrong-criterion-id", "Incorrect fixture roster")]
        }).ToArray();
        var directory = Path.Combine(_root, "invalid");
        await new JudgePromptPackLoader().ExportAsync(new(definitions), directory);
        var selection = new JudgePromptCliOptions.Selection(new(Path.Combine(directory, "prompts.json")), "sv-SE");
        using var client = new FakeChatClient();
        var agent = new CountingAgent();
        var judge = new ChatClientEvaluator(client);
        var exit = eu
            ? await BenchEuAiActCommand.RunAsync("smoke", "subject", _root, "question", judge, agentOverride: agent, promptSelection: selection)
            : await BenchCommand.RunGdprAsync("smoke", "subject", _root, "question", judge, agentOverride: agent, promptSelection: selection);
        Assert.Equal(2, exit);
        Assert.Equal(0, agent.Calls);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public void Preflight_CodeOnlyPreset_RejectsUnusedPromptPack()
    {
        using var client = new FakeChatClient();
        var judge = new ChatClientEvaluator(client);
        Assert.Throws<ArgumentException>(() => JudgePromptCliOptions.Preflight(
            BenchAgenticCommand.ResolvePreset("telemetry", judge), judge, new() { Locale = "en" }));
        Assert.Equal(0, client.CallCount);
    }

    [Theory]
    [InlineData("gdpr")]
    [InlineData("eu-ai-act")]
    [InlineData("agentic")]
    public void Parse_BenchmarkPromptFlags_AreRecognized(string family)
    {
        var parsed = CliCommandTree.Build().Root.Parse(["bench", family, "--subject", "test", "--input", "question", "--response", "answer",
            "--judge-prompt-pack", "prompts.yaml", "--judge-locale", "sv-SE", "--judge-fallback-locale", "en"]);
        Assert.Empty(parsed.Errors);
        Assert.Empty(PromptsCommand.Create().Parse(["export", "directory", "--family", family, "--preset", "smoke"]).Errors);
    }

    private Task<int> Run(string family, string preset, IEvaluator judge, JudgePromptCliOptions.Selection selection, DirectoryInfo aef, int runs = 1) => family switch
    {
        "gdpr" => BenchCommand.RunGdprAsync(preset, "test-subject", _root, "question", evaluatorOverride: judge,
            responseText: "answer", runs: runs, aef: aef, promptSelection: selection),
        "eu-ai-act" => BenchEuAiActCommand.RunAsync(preset, "test-subject", _root, "question", evaluatorOverride: judge,
            responseText: "answer", runs: runs, aef: aef, promptSelection: selection),
        _ => BenchAgenticCommand.RunAsync(preset, "test-subject", _root, "question", "answer", evaluatorOverride: judge,
            context: "context", reference: "answer", aef: aef, promptSelection: selection)
    };

    private static string Reply(JudgePromptDefinition definition) => definition.ResponseContract == JudgeResponseContract.CriteriaV1
        ? JsonSerializer.Serialize(new { overallScore = 100, summary = "Fixture", improvements = Array.Empty<string>(),
            criteriaResults = definition.Criteria.Select(c => new { id = c.Id, met = true, explanation = "Fixture" }) })
        : JsonSerializer.Serialize(new { score = definition.Scale == RubricScoreScale.Unit ? 1 : 100, reasoning = "Fixture" });

    private sealed class CountingAgent : IEvaluableAgent
    {
        public string Name => "target-fixture";
        public int Calls { get; private set; }
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AgentResponse { Text = "answer" });
        }
    }
}
