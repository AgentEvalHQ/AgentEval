// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Prompts;
using AgentEval.Results.Integrity;
using AgentEval.Testing;

namespace AgentEval.Tests.Prompts;

[Collection("ConsoleTests")]
public sealed class RedTeamPromptCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenteval-redteam-pack-cli-" + Guid.NewGuid().ToString("N"));
    public RedTeamPromptCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private const string Success = """{"verdict":"SUCCEEDED","confidence":0.9,"reason":"Underlag"}""";

    [Theory]
    [InlineData("SupplyChain")]
    [InlineData("InsecureOutput")]
    [InlineData("InferenceAPIAbuse")]
    [InlineData("DataPoisoning")]
    [InlineData("Misinformation")]
    [InlineData("ExcessiveAgency")]
    [InlineData("PromptInjection")]
    [InlineData("IndirectInjection")]
    [InlineData("SkillInjection")]
    [InlineData("Crescendo")]
    [InlineData("PAIR")]
    [InlineData("TAP")]
    [InlineData("Jailbreak")]
    [InlineData("PIILeakage")]
    [InlineData("EncodingEvasion")]
    [InlineData("VectorEmbedding")]
    [InlineData("ToolEscalation")]
    [InlineData("supported")]
    [InlineData("benign-controls")]
    public async Task ExportInspect_RedTeamPreset_UsesActualGraphDefinitionsWithoutModelCalls(string preset)
    {
        var directory = Path.Combine(_root, "export");
        Assert.Equal(0, await PromptsCommand.ExportAsync(directory, null, TextWriter.Null, TextWriter.Null, family: "redteam", preset: preset));
        var manifest = Path.Combine(directory, "prompts.json");
        Assert.Equal(0, await PromptsCommand.InspectAsync(manifest, true, TextWriter.Null, TextWriter.Null, family: "redteam", preset: preset));
        var pack = await new JudgePromptPackLoader().LoadAsync(manifest);
        Assert.Equal(BenchmarkPromptDefinitions.ForPreset("redteam", preset).Select(d => d.SystemText), pack.Definitions.Select(d => d.SystemText));
        Assert.All(pack.Definitions, d =>
        {
            if (d.EvaluatorKey == "gate.over-refusal")
            {
                Assert.Contains(preset, new[] { "supported", "benign-controls" });
                Assert.Equal(JudgeResponseContract.GateDecisionV1, d.ResponseContract);
            }
            else Assert.StartsWith("redteam.", d.EvaluatorKey);
        });
    }

    [Fact]
    public async Task Execute_LocalizedGraph_UsesCliWiringAndSealsDeclaredAndObservedIdentity()
    {
        var digests = new List<string>();
        for (var variation = 0; variation < 2; variation++)
        {
            var manifest = await PackAsync("SupplyChain", variation);
            var target = new Subject();
            using var judge = new FakeChatClient(Success, Success);
            var runs = Path.Combine(_root, "runs-" + variation);
            var exit = await RedTeamCommand.ExecuteAsync(Options(manifest, runs), default, target, judgeClientOverride: judge);
            Assert.Equal(0, exit);
            Assert.Equal(1, target.Calls);
            Assert.Equal(2, judge.CallCount);
            Assert.All(judge.ReceivedMessages, messages => Assert.StartsWith("PRIVATE-SWEDISH-GRAPH", messages.First().Text));
            var runFile = Assert.Single(Directory.GetFiles(runs, "run.json", SearchOption.AllDirectories));
            var directory = Path.GetDirectoryName(runFile)!;
            Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
            var run = JsonNode.Parse(await File.ReadAllTextAsync(runFile))!;
            digests.Add((string)run["suite"]!["digest"]!);
            var line = JsonNode.Parse(File.ReadAllLines(Path.Combine(directory, "results.ndjson")).Single())!;
            Assert.NotNull(line["ext"]!["agenteval.probeJudge"]!["graphDigest"]);
            Assert.Equal(2, line["ext"]!["agenteval.probeJudge"]!["instruments"]!.AsArray().Count);
            Assert.DoesNotContain("PRIVATE-SWEDISH-GRAPH", string.Concat(Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
        }
        Assert.NotEqual(digests[0], digests[1]); // Changing selected text invalidates the full pre-execution suite identity.
    }

    [Theory]
    [InlineData("missing-leaf")]
    [InlineData("unsupported-explanation")]
    public async Task Execute_InvalidSelection_FailsBeforeTargetConstructionOrCalls(string problem)
    {
        var manifest = await PackAsync(problem == "missing-leaf" ? "Misinformation" : "SupplyChain", 0);
        var target = new Subject();
        using var judge = new FakeChatClient();
        var options = Options(manifest, Path.Combine(_root, "runs"), explain: problem == "unsupported-explanation");
        if (problem == "missing-leaf")
            await Assert.ThrowsAsync<ArgumentException>(() => RedTeamCommand.ExecuteAsync(options, default, target, judgeClientOverride: judge));
        else
            await Assert.ThrowsAsync<NotSupportedException>(() => RedTeamCommand.ExecuteAsync(options, default, target, judgeClientOverride: judge));
        Assert.Equal(0, target.Calls);
        Assert.Equal(0, judge.CallCount);
        Assert.False(Directory.Exists(Path.Combine(_root, "runs")));
    }

    [Fact]
    public async Task Execute_MarkerGraph_UsesSelectedJudgeAndSealsItsObservedIdentity()
    {
        var manifest = await PackAsync("PromptInjection", 0);
        var target = new Subject("I cannot comply. PWNED 41414141 INJECTED");
        using var judge = new FakeChatClient(Success);
        var runs = Path.Combine(_root, "marker-runs");
        Assert.Equal(0, await RedTeamCommand.ExecuteAsync(Options(manifest, runs, "PromptInjection"), default, target, judgeClientOverride: judge));
        Assert.Equal(1, target.Calls);
        Assert.Equal(1, judge.CallCount);
        var directory = Path.GetDirectoryName(Assert.Single(Directory.GetFiles(runs, "run.json", SearchOption.AllDirectories)))!;
        var verification = AefRunVerifier.Verify(directory);
        Assert.True(verification.Outcome == AefOutcome.Intact, System.Text.Json.JsonSerializer.Serialize(verification.Problems));
        var line = JsonNode.Parse(File.ReadAllLines(Path.Combine(directory, "results.ndjson")).Single())!;
        Assert.NotNull(line["ext"]!["agenteval.probeJudge"]!["graphDigest"]);
        Assert.Single(line["ext"]!["agenteval.probeJudge"]!["instruments"]!.AsArray());
        Assert.DoesNotContain("PRIVATE-SWEDISH-GRAPH", string.Concat(Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
    }

    [Fact]
    public async Task Execute_DefaultRoster_UsesExportedUnionAndProducesIntactNativeEvidence()
    {
        var manifest = await PackAsync("supported", 0);
        var target = new Subject();
        using var judge = new FakeChatClient(Enumerable.Repeat(Success.Replace("SUCCEEDED", "INCONCLUSIVE"), 100).ToArray());
        var runs = Path.Combine(_root, "default-roster");
        var exit = await RedTeamCommand.ExecuteAsync(Options(manifest, runs, ""), default, target, judgeClientOverride: judge);
        Assert.Equal(0, exit); // --fail-on never still writes all inconclusive results honestly.
        Assert.True(target.Calls > 0);
        Assert.True(judge.CallCount > 0);
        var runFile = Assert.Single(Directory.GetFiles(runs, "run.json", SearchOption.AllDirectories));
        var directory = Path.GetDirectoryName(runFile)!;
        var verified = AefRunVerifier.Verify(directory);
        Assert.True(verified.Outcome == AefOutcome.Intact, System.Text.Json.JsonSerializer.Serialize(verified.Problems));
        Assert.Equal(AgentEval.RedTeam.Attack.All.Count, File.ReadAllLines(Path.Combine(directory, "results.ndjson")).Length);
        Assert.All(judge.ReceivedMessages, m => Assert.StartsWith("PRIVATE-SWEDISH-GRAPH", m.First().Text));
    }

    [Fact]
    public async Task Export_CodeOnlySystemPromptOracle_ExplainsWhyNoTranslationExists()
    {
        using var error = new StringWriter();
        Assert.Equal(AgentEval.Cli.ExitCodes.UsageError, await PromptsCommand.ExportAsync(Path.Combine(_root, "code-only"), null, TextWriter.Null, error,
            family: "redteam", preset: "SystemPromptExtraction"));
        Assert.Contains("code-only", error.ToString());
        Assert.Contains("no model instructions", error.ToString());
    }

    [Fact]
    public void Parse_JudgeSelectionFlags_AreAvailableOnRedTeam()
    {
        var parse = RedTeamCommand.Create().Parse(["--endpoint", "http://127.0.0.1:1/v1", "--model", "fixture",
            "--judge-prompt-pack", "prompts.json", "--judge-locale", "sv-SE", "--judge-fallback-locale", "sv"]);
        Assert.Empty(parse.Errors);
    }

    private async Task<string> PackAsync(string preset, int variation)
    {
        var path = Path.Combine(_root, "pack-" + preset + "-" + variation);
        var definitions = BenchmarkPromptDefinitions.ForPreset("redteam", preset).Select(d => d with
        { Locale = "sv-SE", SystemText = "PRIVATE-SWEDISH-GRAPH " + variation + " " + d.EvaluatorKey });
        await new JudgePromptPackLoader().ExportAsync(new(definitions), path);
        return Path.Combine(path, "prompts.json");
    }

    private static RedTeamOptions Options(string manifest, string runs, string attack = "SupplyChain", bool explain = false) => new()
    {
        Endpoint = "http://127.0.0.1:1/v1", Model = "fixture", JudgeEndpoint = "http://127.0.0.1:1/v1", JudgeModel = "fixture-judge",
        Attacks = attack, MaxProbes = 1, Intensity = "quick", Format = "json", Quiet = true, FailOn = "never",
        Explain = explain,
        Aef = new(runs), PromptSelection = new(new(manifest), "sv-SE")
    };

    private sealed class Subject(string response = "Svar") : IEvaluableAgent
    {
        public string Name => "cli-graph-fixture";
        public int Calls { get; private set; }
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AgentResponse { Text = response });
        }
    }
}
