// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using System.CommandLine;
using System.Security.Cryptography;
using AgentEval.Cli.Commands;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Results.Integrity;
using AgentEval.Tests.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Cli.Aef;

[Collection("ConsoleTests")]
public sealed class NativeVerbIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aef-native-" + Guid.NewGuid().ToString("N"));

    private static Task<int> WithCapture(bool enabled, Func<Task<int>> invoke)
    {
        var command = new Command("test");
        command.Options.Add(new Option<DirectoryInfo?>("--aef"));
        var controls = new AefCliOptions(command);
        return controls.InvokeAsync(command.Parse(["--aef-content", enabled ? "on" : "off"]), invoke);
    }

    private static List<JsonNode> ArtifactLines(string directory) => File.Exists(Path.Combine(directory, "evidence.ndjson"))
        ? File.ReadAllLines(Path.Combine(directory, "evidence.ndjson")).Select(l => JsonNode.Parse(l)!)
            .Where(l => (string?)l["kind"] == "compliance_artifact").ToList() : [];

    private static void AssertArtifactBytes(string directory, byte[] expected)
    {
        var artifact = Assert.Single(ArtifactLines(directory));
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant();
        Assert.Equal(digest, (string?)artifact["link"]!["blob"]);
        Assert.Contains(Directory.GetFiles(Path.Combine(directory, "blobs"), "*", SearchOption.AllDirectories),
            path => File.ReadAllBytes(path).SequenceEqual(expected));
        var id = (string)artifact["evidenceId"]!;
        var references = File.ReadAllLines(Path.Combine(directory, "results.ndjson")).Select(l => JsonNode.Parse(l)!)
            .Where(l => l["evidence"]?.AsArray().Any(e => (string?)e == id) == true).ToList();
        Assert.NotEmpty(references);
        Assert.All(references, line => Assert.Null(line["trial"]));
    }

    public NativeVerbIntegrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // The local cross-verification script retains only AEF artifacts, never datasets or command reports.
        if (Environment.GetEnvironmentVariable("AEF_VERB_FIXTURES") is { Length: > 0 } fixtures)
        {
            foreach (var header in Directory.GetFiles(_root, "run.json", SearchOption.AllDirectories))
            {
                var source = Path.GetDirectoryName(header)!;
                if (!File.Exists(Path.Combine(source, "seal.json"))) continue;
                var target = Path.Combine(fixtures, Path.GetFileName(source));
                Directory.CreateDirectory(target);
                foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    var copy = Path.Combine(target, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(file, copy);
                }
            }
        }
        Directory.Delete(_root, recursive: true);
    }

    private EvalOptions Options(string endpoint, int count = 1, int runs = 1, string? metrics = null)
    {
        var dataset = Path.Combine(_root, "dataset.yaml");
        File.WriteAllText(dataset, string.Concat(Enumerable.Range(0, count).Select(i => $"- id: c{i}\n  input: secret-prompt-{i}\n  expected: Hi\n")));
        return new EvalOptions
        {
            Dataset = new FileInfo(dataset), Endpoint = endpoint, Model = "local-model", ApiKey = "local-test",
            Format = "json", Output = new FileInfo(Path.Combine(_root, "report.json")), Quiet = true,
            Aef = new DirectoryInfo(Path.Combine(_root, "aef")), SubjectVersion = "1",
            Runs = runs,
            Metrics = metrics,
        };
    }

    [Theory]
    [InlineData("owasp")]
    [InlineData("mitre")]
    [InlineData("nist")]
    [InlineData("gdpr")]
    [InlineData("eu-ai-act")]
    [InlineData("agentic")]
    public async Task BenchMock_ExplicitEvidenceIsMockedAndDefaultStoresNothing(string family)
    {
        var workspace = Path.Combine(_root, ".agenteval");
        Directory.CreateDirectory(workspace);
        async Task<(int ExitCode, string? ReportDir)> Run(DirectoryInfo? aef, bool noAef = false, CancellationToken ct = default) => family switch
        {
            "owasp" => await BenchOwaspCommand.RunAsync("smoke", "mock-subject", _root, null, null, null,
                mock: true, aef: aef, noAef: noAef, ct: ct),
            "mitre" => await BenchMitreCommand.RunAsync("smoke", "mock-subject", _root, null, null, null,
                mock: true, aef: aef, noAef: noAef, ct: ct),
            "nist" => await BenchNistCommand.RunAsync("smoke", "mock-subject", _root, null, null, null,
                mock: true, aef: aef, noAef: noAef, ct: ct),
            "gdpr" => (await BenchCommand.RunGdprAsync("standard", "mock-subject", _root, "test input", null,
                mock: true, aef: aef, noAef: noAef, ct: ct), null),
            "eu-ai-act" => (await BenchEuAiActCommand.RunAsync("standard", "mock-subject", _root, "test input", null,
                mock: true, aef: aef, noAef: noAef, ct: ct), null),
            _ => (await BenchAgenticCommand.RunAsync("agentic-execution", "mock-subject", _root, "test input", null, null,
                mock: true, aef: aef, noAef: noAef, ct: ct), null),
        };
        Assert.Equal(11, (await Run(null)).ExitCode);
        Assert.Empty(Directory.GetFileSystemEntries(workspace));
        var disabled = new DirectoryInfo(Path.Combine(_root, "disabled"));
        Assert.Equal(11, (await Run(disabled, noAef: true)).ExitCode);
        Assert.False(disabled.Exists);

        var destination = new DirectoryInfo(Path.Combine(_root, "explicit"));
        var result = await Run(destination);
        Assert.Equal(11, result.ExitCode);
        Assert.Null(result.ReportDir);
        Assert.Empty(Directory.GetFileSystemEntries(workspace));
        var run = RunDirectory(destination.FullName);
        AssertIntact(run);
        var header = Document(run, "run.json");
        Assert.Equal("mocked", (string?)header["execution"]!["targetMode"]);
        Assert.True(header["judges"] is null || header["judges"]!.AsArray().Count == 0);
        var lines = File.ReadAllLines(Path.Combine(run, "results.ndjson")).Select(l => JsonNode.Parse(l)!);
        Assert.All(lines, line => Assert.NotEqual("LLM", (string?)line["annotator"]?["kind"]));
        var gate = JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(run, "gates.ndjson"))))!;
        Assert.Equal(11, (int)gate["exitCode"]!);
        Assert.Equal("inconclusive", (string?)gate["outcome"]);

        var cancelled = new DirectoryInfo(Path.Combine(_root, "cancelled"));
        Assert.Equal(1, (await Run(cancelled, ct: new CancellationToken(canceled: true))).ExitCode);
        var aborted = RunDirectory(cancelled.FullName);
        AssertIntact(aborted);
        var abortedHeader = Document(aborted, "run.json");
        Assert.Equal("mocked", (string?)abortedHeader["execution"]!["targetMode"]);
        Assert.Equal("aborted", (string?)abortedHeader["status"]);
        Assert.False(File.Exists(Path.Combine(aborted, "gates.ndjson")));

        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "not a directory");
        Assert.Equal(3, (await Run(new DirectoryInfo(blocked))).ExitCode);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task MockComposite_Interrupted_PreservesCompletedJudgments(bool cancel, bool placeholder)
    {
        using var cancellation = new CancellationTokenSource();
        var benchmark = new CompositeEval("test", "Test", "test", "1",
            [new(new MockProgressEval("completed"), 1),
             new(new MockProgressEval("interrupted", () =>
             {
                 if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                 throw new InvalidOperationException("fixture failure");
             }), 1)], WeightedSumAggregation.Instance);
        var destination = new DirectoryInfo(Path.Combine(_root, "partial"));
        var exit = await MockTarget.RunCompositeAsync("agentic", "test", "mock-subject", null, _root,
            benchmark, new EvalInput("q", "r"), placeholder, destination, false, cancellation.Token);
        Assert.Equal(1, exit);
        var run = RunDirectory(destination.FullName);
        AssertIntact(run);
        Assert.Equal("aborted", (string?)Document(run, "run.json")["status"]);
        var lines = File.ReadAllLines(Path.Combine(run, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToList();
        var completed = Assert.Single(lines, l => (string?)l["caseId"] == "completed");
        Assert.Equal("passed", (string?)completed["state"]);
        Assert.Equal(placeholder ? "CODE" : "LLM", (string?)completed["annotator"]!["kind"]);
        Assert.Equal(placeholder ? null : "fixture-judge", (string?)completed["annotator"]?["model"]);
        Assert.DoesNotContain(lines, l => (string?)l["caseId"] == "interrupted" && (string?)l["state"] == "passed");
        Assert.False(File.Exists(Path.Combine(run, "gates.ndjson")));
    }

    private sealed class MockProgressEval(string key, Action? interrupt = null) : IEval
    {
        public string Key => key;
        public string Name => key;
        public string Category => "test";
        public string Version => "1";
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
        {
            interrupt?.Invoke();
            return Task.FromResult(new EvalResult(new(Key, Name, Category, Version),
                new(1, null, "pass", true, null, "none", null), new(null, null, null, null, null),
                new("atomic-llm", "fixture-judge", null, null, null, 0, false), DateTimeOffset.UtcNow));
        }
    }

    [Fact]
    public async Task Eval_SharedUnsealedControl_LeavesClosedEvidenceWithoutSeal()
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync((_, _) => Task.FromResult("Hi"));
        var command = EvalCommand.Create();
        var controls = new AefCliOptions(command);
        var options = Options(endpoint.Endpoint);
        var parsed = command.Parse(["--aef-unsealed", "--dataset", options.Dataset.FullName]);
        var code = await controls.InvokeAsync(parsed, () => EvalCommand.ExecuteAsync(options, CancellationToken.None));
        Assert.Equal(0, code);
        var directory = RunDirectory(options.Aef!.FullName);
        var verified = AefRunVerifier.Verify(directory);
        Assert.Equal(AefOutcome.Unsealed, verified.Outcome);
        Assert.Empty(verified.Problems);
        Assert.Equal("completed", (string?)Document(directory, "run.json")["status"]);
        Assert.False(File.Exists(Path.Combine(directory, "seal.json")));
    }

    [Fact]
    public async Task Eval_SharedSigningControl_ProducesSignedSeal()
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync((_, _) => Task.FromResult("Hi"));
        var command = EvalCommand.Create();
        var controls = new AefCliOptions(command);
        var options = Options(endpoint.Endpoint);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyPath = Path.Combine(_root, "key.pem");
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        var parsed = command.Parse(["--aef-key", keyPath, "--dataset", options.Dataset.FullName]);
        var code = await controls.InvokeAsync(parsed, () => EvalCommand.ExecuteAsync(options, CancellationToken.None));
        Assert.Equal(0, code);
        var directory = RunDirectory(options.Aef!.FullName);
        AssertIntact(directory);
        using var expectedSigner = new AgentEval.Results.Signatures.EcdsaP256Signer(key);
        var envelope = Document(directory, "attestation.dsse.json");
        Assert.Equal(expectedSigner.KeyId, (string?)envelope["signatures"]![0]!["keyid"]);
        Assert.Equal(File.ReadAllBytes(Path.Combine(directory, "seal.json")), Convert.FromBase64String((string)envelope["payload"]!));
        Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(Path.Combine(directory, "seal.json")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Eval_BlockedSecondCall_FirstResultAlreadyExistsInRunningFolder(int runs)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var endpoint = await LocalChatEndpoint.StartAsync(async (_, ct) =>
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
            }
            return "Hi";
        });
        var options = Options(endpoint.Endpoint, 2, runs);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var evaluation = EvalCommand.ExecuteAsync(options, cancel.Token);
        try
        {
            await entered.Task.WaitAsync(cancel.Token);
            var dir = RunDirectory(options.Aef!.FullName);
            Assert.Equal("running", (string?)Document(dir, "run.json")["status"]);
            Assert.Single(File.ReadAllLines(Path.Combine(dir, "results.ndjson")));
            Assert.False(File.Exists(Path.Combine(dir, "seal.json")));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(0, await evaluation);
        AssertIntact(RunDirectory(options.Aef!.FullName));
    }

    [Fact]
    public async Task Eval_LlmMetricUsingSubjectClient_RecordsJudgeAndSeparateUsage()
    {
        var calls = 0;
        await using var endpoint = await LocalChatEndpoint.StartAsync((_, _) => Task.FromResult(
            Interlocked.Increment(ref calls) == 1 ? "Hi" : """{"score":95,"addressesQuestion":true,"reasoning":"secret explanation"}"""));
        var options = Options(endpoint.Endpoint, metrics: "llm_relevance");
        Assert.Equal(0, await EvalCommand.ExecuteAsync(options, CancellationToken.None));
        Assert.Equal(2, endpoint.Requests.Count);
        var dir = RunDirectory(options.Aef!.FullName);
        AssertIntact(dir);
        var judge = Assert.Single(Document(dir, "run.json")["judges"]!.AsArray())!;
        Assert.Equal("local-model", (string?)judge["model"]);
        var usage = Document(dir, "summary.json")["usage"]!.AsArray();
        Assert.Equal(2, usage.Count);
        Assert.All(usage, u => Assert.Equal(10, (long)u!["gen_ai.usage.input_tokens"]!));
        Assert.Single(usage, u => (string?)u!["role"] == "judge");
        Assert.Single(usage, u => (string?)u!["role"] == "agent");
        Assert.DoesNotContain("secret", string.Concat(Directory.GetFiles(dir).Select(File.ReadAllText)));
    }

    [Fact]
    public async Task Eval_NamedMetric_WritesChildWithoutChangingHarnessGate()
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync();
        var options = Options(endpoint.Endpoint, metrics: "code_toxicity");
        Assert.Equal(0, await EvalCommand.ExecuteAsync(options, CancellationToken.None));
        Assert.Single(endpoint.Requests);
        var dir = RunDirectory(options.Aef!.FullName);
        AssertIntact(dir);
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal("passed", (string?)lines[0]["state"]);
        var metric = Assert.Single(lines, l => (string?)l["path"] == "case/metrics/code_toxicity");
        Assert.Equal((string?)lines[0]["resultId"], (string?)metric["parentResultId"]);
        Assert.Equal("code_toxicity", (string?)metric["scores"]![0]!["metric"]);
        Assert.DoesNotContain("secret-prompt", string.Concat(Directory.GetFiles(dir).Select(File.ReadAllText)));
    }

    [Fact]
    public async Task Eval_HttpTarget_WritesLiveSealedEvidenceWithoutContent()
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync();
        var options = Options(endpoint.Endpoint);

        var exit = await EvalCommand.ExecuteAsync(options, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Single(endpoint.Requests);
        var dir = RunDirectory(options.Aef!.FullName);
        AssertIntact(dir);
        Assert.Equal("live", (string?)Document(dir, "run.json")["execution"]!["targetMode"]);
        var gate = JsonNode.Parse(File.ReadAllLines(Path.Combine(dir, "gates.ndjson")).Single())!;
        Assert.Equal(exit, (int)gate["exitCode"]!);
        Assert.DoesNotContain("secret-prompt", string.Concat(Directory.GetFiles(dir).Select(File.ReadAllText)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Eval_CancelDuringHttpCall_SealsAbortedCompletedPrefix(int completedCases)
    {
        using var cancel = new CancellationTokenSource();
        var count = 0;
        await using var endpoint = await LocalChatEndpoint.StartAsync(async (_, requestCancelled) =>
        {
            if (Interlocked.Increment(ref count) > completedCases)
            {
                cancel.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, requestCancelled);
            }
            return "Hi";
        });
        var options = Options(endpoint.Endpoint, completedCases + 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvalCommand.ExecuteAsync(options, cancel.Token));

        var dir = RunDirectory(options.Aef!.FullName);
        AssertIntact(dir);
        var header = Document(dir, "run.json");
        Assert.Equal("aborted", (string?)header["status"]);
        Assert.Equal("cancelled", (string?)header["abortReason"]);
        Assert.Equal(completedCases, File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Length);
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
    }

    private static string RunDirectory(string root) => Path.GetDirectoryName(Assert.Single(Directory.GetFiles(root, "run.json", SearchOption.AllDirectories)))!;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Eval_CancelDuringStochasticTrial_PreservesCompletedTrialsAndOriginalPolicy(int completedTrials)
    {
        using var cancel = new CancellationTokenSource();
        var count = 0;
        await using var endpoint = await LocalChatEndpoint.StartAsync(async (_, requestCancelled) =>
        {
            if (Interlocked.Increment(ref count) > completedTrials)
            {
                cancel.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, requestCancelled);
            }
            return "Hi";
        });
        var options = Options(endpoint.Endpoint, 2, runs: 3);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvalCommand.ExecuteAsync(options, cancel.Token));

        var dir = RunDirectory(options.Aef!.FullName);
        AssertIntact(dir);
        var header = Document(dir, "run.json");
        Assert.Equal("aborted", (string?)header["status"]);
        Assert.Equal(3, (int)header["suite"]!["executionPolicy"]!["trialsPerCase"]!);
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(completedTrials, lines.Count(l => l["trial"] is not null));
        if (completedTrials % 3 != 0)
        {
            var partial = Assert.Single(lines, l => l["trials"] is { } t && (int)t["n"]! == completedTrials % 3);
            Assert.Equal("error", (string?)partial["state"]);
        }
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BenchGdpr_FromEnvironment_UsesHttpTargetAndSealsLiveTrials(bool capture)
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync("I respect privacy and minimize personal data.");
        using var environment = new ProviderEnvironmentScope(
            ("AI_INFERENCE_PROVIDER", "openai-compatible"),
            ("OPENAI_COMPATIBLE_ENDPOINT", endpoint.Endpoint),
            ("OPENAI_COMPATIBLE_MODEL", "local-model"),
            ("OPENAI_COMPATIBLE_API_KEY", "local-test"));
        Directory.CreateDirectory(Path.Combine(_root, ".agenteval"));
        File.WriteAllText(Path.Combine(_root, ".agenteval", "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "http-test" }.ToJsonString());
        var aef = new DirectoryInfo(Path.Combine(_root, "gdpr-aef"));

        var exit = await WithCapture(capture, () => BenchCommand.RunGdprAsync("smoke", "http-subject", _root, "privacy question",
            evaluatorOverride: new PassingEvaluator(), azureFromEnv: true, runs: 2, aef: aef, subjectVersion: "1"));

        Assert.Equal(0, exit);
        Assert.NotEmpty(endpoint.Requests);
        var dir = RunDirectory(aef.FullName);
        AssertIntact(dir);
        var header = Document(dir, "run.json");
        Assert.Equal("live", (string?)header["execution"]!["targetMode"]);
        Assert.Equal("1", (string?)header["subject"]!["version"]);
        Assert.Equal(2, (int)header["suite"]!["executionPolicy"]!["trialsPerCase"]!);
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Contains(lines, l => l["trial"] is not null);
        Assert.Contains(lines, l => l["trials"] is not null);
        Assert.DoesNotContain("I respect privacy", string.Concat(Directory.GetFiles(dir).Select(File.ReadAllText)));
        var artifacts = ArtifactLines(dir);
        Assert.Equal(capture ? 1 : 0, artifacts.Count);
        if (capture) AssertArtifactBytes(dir, File.ReadAllBytes(Assert.Single(Directory.GetFiles(
            Path.Combine(_root, ".agenteval", "compliance"), "gdpr-evidence.json", SearchOption.AllDirectories))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BenchScan_HttpTarget_WritesLiveEvidenceAndPreservesTheCommandExit(bool mitre)
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync("I cannot assist with that request.");
        await using var judgeEndpoint = await LocalChatEndpoint.StartAsync("VERDICT: INCONCLUSIVE\nCONFIDENCE: 0.5\nREASON: test");
        var agent = BenchTier1SutResolver.BuildFromOpenAiEndpoint(endpoint.Endpoint, "local-model", "local-test", "http-subject");
        using var judge = AgentEval.Cli.Infrastructure.EndpointFactory.CreateOpenAICompatible(judgeEndpoint.Endpoint, "local-judge", "local-test");
        Directory.CreateDirectory(Path.Combine(_root, ".agenteval"));
        File.WriteAllText(Path.Combine(_root, ".agenteval", "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "http-test" }.ToJsonString());
        var aef = new DirectoryInfo(Path.Combine(_root, "bench-aef"));

        async Task<int> Run(DirectoryInfo destination, bool noAef) => mitre
            ? (await BenchMitreCommand.RunAsync("smoke", agent.Name, _root, null, null, agent,
                judgeClientOverride: judge, aef: destination, noAef: noAef, agentIsLive: true)).ExitCode
            : (await BenchOwaspCommand.RunAsync("smoke", agent.Name, _root, null, null, agent,
                judgeClientOverride: judge, aef: destination, noAef: noAef, agentIsLive: true)).ExitCode;
        var written = await Run(aef, false);
        var reportBytes = File.ReadAllBytes(Assert.Single(Directory.GetFiles(
            Path.Combine(_root, ".agenteval", "compliance"), "evidence.json", SearchOption.AllDirectories)));
        var notWritten = new DirectoryInfo(Path.Combine(_root, "disabled-aef"));
        var disabled = await Run(notWritten, true);

        Assert.Equal(disabled, written);
        Assert.False(notWritten.Exists);
        Assert.NotEmpty(endpoint.Requests);
        Assert.NotEmpty(judgeEndpoint.Requests);
        var dir = RunDirectory(aef.FullName);
        AssertIntact(dir);
        Assert.Equal("live", (string?)Document(dir, "run.json")["execution"]!["targetMode"]);
        var gate = JsonNode.Parse(File.ReadAllLines(Path.Combine(dir, "gates.ndjson")).Single())!;
        Assert.Equal(written, (int)gate["exitCode"]!);
        AssertArtifactBytes(dir, reportBytes);
        (agent as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task Redteam_HttpTarget_RecordsItsPolicyAndJudgeUsage()
    {
        await using var agent = await LocalChatEndpoint.StartAsync("I cannot assist with that request.");
        await using var judge = await LocalChatEndpoint.StartAsync("VERDICT: INCONCLUSIVE\nCONFIDENCE: 0.5\nREASON: not determined");
        var options = new RedTeamOptions
        {
            Endpoint = agent.Endpoint, Model = "local-model", ApiKey = "local-test", Intensity = "quick",
            Attacks = "Misinformation,InsecureOutput", MaxProbes = 1,
            JudgeEndpoint = judge.Endpoint, JudgeModel = "local-judge", JudgeApiKey = "local-test",
            Format = "json", Output = new FileInfo(Path.Combine(_root, "redteam.json")), Quiet = true,
            Aef = new DirectoryInfo(Path.Combine(_root, "redteam-aef")), FailOn = "never",
        };

        var exit = await RedTeamCommand.ExecuteAsync(options, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.NotEmpty(agent.Requests);
        Assert.NotEmpty(judge.Requests);
        var dir = RunDirectory(options.Aef!.FullName);
        AssertIntact(dir);
        Assert.Equal("live", (string?)Document(dir, "run.json")["execution"]!["targetMode"]);
        var gate = JsonNode.Parse(File.ReadAllLines(Path.Combine(dir, "gates.ndjson")).Single())!;
        Assert.Equal("fail-on:never", (string?)gate["rule"]!["strategy"]);
        Assert.Equal(exit, (int)gate["exitCode"]!);
        Assert.Equal("ship", (string?)gate["outcome"]);
        var usage = Assert.Single(Document(dir, "summary.json")["usage"]!.AsArray())!;
        Assert.Equal(judge.Requests.Count * 10, (long)usage["gen_ai.usage.input_tokens"]!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Redteam_CancelDuringAttack_PreservesCompletedProbes(int completedProbes)
    {
        using var cancel = new CancellationTokenSource();
        var count = 0;
        await using var endpoint = await LocalChatEndpoint.StartAsync(async (_, requestCancelled) =>
        {
            if (Interlocked.Increment(ref count) > completedProbes)
            {
                cancel.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, requestCancelled);
            }
            return "I cannot assist with that request.";
        });
        var options = new RedTeamOptions
        {
            Endpoint = endpoint.Endpoint, Model = "local-model", ApiKey = "local-test", Intensity = "quick",
            Attacks = "PromptInjection", MaxProbes = completedProbes + 1, Quiet = true,
            Format = "json", Aef = new DirectoryInfo(Path.Combine(_root, "redteam-aef")),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RedTeamCommand.ExecuteAsync(options, cancel.Token));

        var dir = RunDirectory(options.Aef!.FullName);
        AssertIntact(dir);
        Assert.Equal("aborted", (string?)Document(dir, "run.json")["status"]);
        Assert.Equal(completedProbes, File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Length);
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Redteam_CancelAfterReport_PreservesCompletedScanWithoutGate(bool noAef)
    {
        using var cancel = new CancellationTokenSource();
        await using var endpoint = await LocalChatEndpoint.StartAsync("I cannot assist with that request.");
        var options = new RedTeamOptions
        {
            Endpoint = endpoint.Endpoint, Model = "local-model", ApiKey = "local-test", Intensity = "quick",
            Attacks = "PromptInjection", MaxProbes = 1, Quiet = true,
            Format = "json", Aef = new DirectoryInfo(Path.Combine(_root, "redteam-aef")), NoAef = noAef,
            SaveBaseline = new FileInfo(Path.Combine(_root, "baseline.json")),
        };
        var original = Console.Out;
        using var output = new CancelOnReportWriter(cancel);
        try
        {
            Console.SetOut(output);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RedTeamCommand.ExecuteAsync(options, cancel.Token));
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.True(output.ReportWritten);
        Assert.Single(endpoint.Requests);
        if (noAef)
        {
            Assert.False(Directory.Exists(options.Aef.FullName));
            return;
        }
        var dir = RunDirectory(options.Aef.FullName);
        AssertIntact(dir);
        Assert.Equal("aborted", (string?)Document(dir, "run.json")["status"]);
        Assert.Single(File.ReadAllLines(Path.Combine(dir, "results.ndjson")));
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
    }

    [Theory]
    [InlineData("owasp")]
    [InlineData("mitre")]
    [InlineData("nist")]
    public async Task Bench_CancelAfterPersistence_SealsCompletedScanWithoutGate(string family)
    {
        using var cancel = new CancellationTokenSource();
        await using var endpoint = await LocalChatEndpoint.StartAsync("I cannot assist with that request.");
        await using var judgeEndpoint = await LocalChatEndpoint.StartAsync("VERDICT: INCONCLUSIVE\nCONFIDENCE: 0.5\nREASON: test");
        var agent = BenchTier1SutResolver.BuildFromOpenAiEndpoint(endpoint.Endpoint, "local-model", "local-test", "http-subject");
        using var judge = AgentEval.Cli.Infrastructure.EndpointFactory.CreateOpenAICompatible(judgeEndpoint.Endpoint, "local-judge", "local-test");
        Directory.CreateDirectory(Path.Combine(_root, ".agenteval"));
        File.WriteAllText(Path.Combine(_root, ".agenteval", "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "http-test" }.ToJsonString());
        var aef = new DirectoryInfo(Path.Combine(_root, "bench-aef"));
        var original = Console.Out;
        using var output = new CancelOnPersistenceWriter(cancel);
        try
        {
            Console.SetOut(output);
            var result = family switch
            {
                "owasp" => await BenchOwaspCommand.RunAsync("smoke", agent.Name, _root, null, null, agent,
                    judgeClientOverride: judge, aef: aef, agentIsLive: true, ct: cancel.Token),
                "mitre" => await BenchMitreCommand.RunAsync("smoke", agent.Name, _root, null, null, agent,
                    judgeClientOverride: judge, aef: aef, agentIsLive: true, ct: cancel.Token),
                _ => await BenchNistCommand.RunAsync("smoke", agent.Name, _root, null, null, agent,
                    judgeClientOverride: judge, aef: aef, agentIsLive: true, ct: cancel.Token),
            };
            Assert.Equal(1, result.ExitCode);
        }
        finally
        {
            Console.SetOut(original);
            (agent as IDisposable)?.Dispose();
        }

        Assert.True(cancel.IsCancellationRequested);
        var dir = RunDirectory(aef.FullName);
        AssertIntact(dir);
        Assert.Equal("aborted", (string?)Document(dir, "run.json")["status"]);
        Assert.NotEmpty(File.ReadAllLines(Path.Combine(dir, "results.ndjson")));
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedComposite_CancelDuringReports_PreservesAlreadyComputedRollups(bool euAiAct)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".agenteval"));
        File.WriteAllText(Path.Combine(_root, ".agenteval", "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "report-cancel" }.ToJsonString());
        using var cancel = new CancellationTokenSource();
        var aef = new DirectoryInfo(Path.Combine(_root, "aef"));
        var original = Console.Out;
        using var output = new CancelOnPersistenceWriter(cancel, "Markdown report:");
        try
        {
            Console.SetOut(output);
            var exit = await WithCapture(true, () => euAiAct
                ? BenchEuAiActCommand.RunAsync("standard", "subject", _root, "question", evaluatorOverride: new PassingEvaluator(),
                    responseText: "answer", runs: 2, ct: cancel.Token, aef: aef)
                : BenchCommand.RunGdprAsync("standard", "subject", _root, "question", evaluatorOverride: new PassingEvaluator(),
                    responseText: "answer", runs: 2, ct: cancel.Token, aef: aef));
            Assert.Equal(1, exit);
        }
        finally { Console.SetOut(original); }

        Assert.True(cancel.IsCancellationRequested);
        var dir = RunDirectory(aef.FullName);
        AssertIntact(dir);
        Assert.Equal("aborted", (string?)Document(dir, "run.json")["status"]);
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Contains(lines, l => l["trials"] is not null && (string?)l["state"] == "passed");
        Assert.All(lines.Where(l => l["trials"] is not null), l => Assert.Equal(2, (int)l["trials"]!["n"]!));
        AssertArtifactBytes(dir, File.ReadAllBytes(Assert.Single(Directory.GetFiles(
            Path.Combine(_root, ".agenteval", "compliance"), euAiAct ? "eu-ai-act-evidence.json" : "gdpr-evidence.json", SearchOption.AllDirectories))));
    }

    private sealed class PassingEvaluator : AgentEval.Core.IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default) =>
            Task.FromResult(new AgentEval.Core.EvaluationResult
            {
                OverallScore = 100, Summary = "test",
                CriteriaResults = criteria.Select(c => new AgentEval.Core.CriterionResult { Criterion = c, Met = true, Explanation = "test" }).ToList(),
            });
    }

    private sealed class CancelOnPersistenceWriter(CancellationTokenSource cancel, string prefix = "Persisted run ") : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.StartsWith(prefix, StringComparison.Ordinal) is true)
                cancel.Cancel();
        }
    }

    private sealed class CancelOnReportWriter(CancellationTokenSource cancel) : StringWriter
    {
        public bool ReportWritten { get; private set; }

        public override void Write(string? value)
        {
            base.Write(value);
            if (value?.TrimStart().StartsWith('{') is true)
            {
                ReportWritten = true;
                cancel.Cancel();
            }
        }
    }

    private static JsonObject Document(string dir, string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(dir, name)))!.AsObject();

    private static void AssertIntact(string dir)
    {
        var report = AefRunVerifier.Verify(dir);
        Assert.Equal(AefOutcome.Intact, report.Outcome);
        Assert.Empty(report.Problems);
    }
}
