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

    private EvalOptions Options(string endpoint, int count = 1, int runs = 1, string? metrics = null, float? temperature = null, int? maxTokens = null, bool azure = false, string? judgeEndpoint = null, bool quiet = true, string? subjectVersion = "1", bool noAef = false)
    {
        var dataset = Path.Combine(_root, "dataset.yaml");
        File.WriteAllText(dataset, string.Concat(Enumerable.Range(0, count).Select(i => $"- id: c{i}\n  input: secret-prompt-{i}\n  expected: Hi\n")));
        return new EvalOptions
        {
            Dataset = new FileInfo(dataset), Endpoint = azure ? new Uri(endpoint).GetLeftPart(UriPartial.Authority) : endpoint,
            Azure = azure, DeploymentName = azure ? "local-model" : null, Model = "local-model", ApiKey = "local-test",
            Format = "json", Output = new FileInfo(Path.Combine(_root, "report.json")), Quiet = quiet, NoAef = noAef,
            Aef = new DirectoryInfo(Path.Combine(_root, "aef")), SubjectVersion = subjectVersion,
            Runs = runs, JudgeEndpoint = judgeEndpoint, JudgeModel = judgeEndpoint is null ? null : "local-judge",
            Metrics = metrics, Temperature = temperature, MaxTokens = maxTokens,
        };
    }

    [Theory]
    [InlineData(null, false, false, true)]
    [InlineData("1", false, false, false)]
    [InlineData(null, true, false, false)]
    [InlineData(null, false, true, false)]
    public async Task Eval_SubjectVersionNote_OnlyForWrittenUnversionedEvidence(string? version, bool quiet, bool noAef, bool expectNote)
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync();
        var options = Options(endpoint.Endpoint, quiet: quiet, subjectVersion: version, noAef: noAef);
        var previous = Console.Error;
        using var stderr = new StringWriter();
        try
        {
            Console.SetError(stderr);
            Assert.Equal(0, await EvalCommand.ExecuteAsync(options, CancellationToken.None));
        }
        finally { Console.SetError(previous); }
        Assert.Equal(expectNote, stderr.ToString().Contains("cannot satisfy a checkpoint lane", StringComparison.Ordinal));
        Assert.Single(endpoint.Requests);
        if (!noAef)
        {
            var dir = RunDirectory(options.Aef!.FullName);
            AssertIntact(dir);
            Assert.Equal(version, (string?)Document(dir, "run.json")["subject"]!["version"]);
            Assert.Equal("eval", (string?)Document(dir, "run.json")["ext"]!["agenteval.cli"]!["verb"]);
        }
        else Assert.False(options.Aef!.Exists);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(true, 3)]
    public async Task Eval_DatasetThresholds_RecordsAppliedPerCasePolicy(bool mixed, int runs)
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync();
        var options = Options(endpoint.Endpoint, count: 2, runs: runs, metrics: "code_toxicity");
        File.WriteAllText(options.Dataset.FullName,
            "- id: low\n  input: secret-one\n  expected: Hi\n  passingScore: 60\n" +
            $"- id: high\n  input: secret-two\n  expected: Hi\n  passingScore: {(mixed ? 90 : 60)}\n");
        var exit = await EvalCommand.ExecuteAsync(options, CancellationToken.None);
        Assert.NotEqual(2, exit);
        var dir = RunDirectory(options.Aef!.FullName);
        AssertIntact(dir);
        var config = Document(dir, "run.json")["config"]!;
        if (mixed)
        {
            Assert.Null(config["thresholds"]);
            Assert.Equal(60, (int)config["caseThresholds"]!["low"]!["value"]!);
            Assert.Equal(90, (int)config["caseThresholds"]!["high"]!["value"]!);
        }
        else
        {
            Assert.Equal(">=", (string?)config["thresholds"]!["case"]!["op"]);
            Assert.Equal(60, (int)config["thresholds"]!["case"]!["value"]!);
            Assert.Null(config["caseThresholds"]);
        }
        if (runs > 1) Assert.Null(config["metrics"]);
        else Assert.Equal("code_toxicity", (string?)Assert.Single(config["metrics"]!.AsArray()));
        Assert.DoesNotContain("secret-one", File.ReadAllText(Path.Combine(dir, "run.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BenchEvidence_Endpoint_RemovesSecretsAndMatchesRunner(bool scan)
    {
        const string endpoint = "https://user:private@example.test/a%20b?api-key=secret#token";
        const string sanitized = "https://example.test/a%20b";
        var aef = new DirectoryInfo(Path.Combine(_root, "aef"));
        var started = DateTimeOffset.UtcNow;
        var result = await new MockProgressEval("case").EvaluateAsync(new EvalInput("q", "r"));
        var failed = scan
            ? BenchAef.WriteScan(aef, _root, "test", "test", "subject", null,
                AgentEval.Results.Writing.AefTargetMode.Live, null, new RedTeamAef.PartialScan("subject").Snapshot(),
                null, 1, null, abortReason: "cancelled", endpoint: endpoint)
            : BenchAef.WriteResult(aef, _root, "test", "test", "subject", null,
                AgentEval.Results.Writing.AefTargetMode.Live, result, started, DateTimeOffset.UtcNow, endpoint: endpoint);
        Assert.False(failed);
        var dir = RunDirectory(aef.FullName);
        AssertIntact(dir);
        var deployment = Document(dir, "run.json")["deployment"]!;
        Assert.Equal(sanitized, (string?)deployment["endpoint"]);
        var plan = new JsonObject { ["subject"] = new JsonObject { ["endpoint"] = sanitized } };
        Assert.Equal(AgentEval.Results.Runner.AefPlanDefaults.DeploymentRef(plan), (string?)deployment["ref"]);
        var persisted = string.Concat(Directory.GetFiles(dir).Select(File.ReadAllText));
        Assert.DoesNotContain("private", persisted);
        Assert.DoesNotContain("secret", persisted);
        Assert.DoesNotContain("api-key", persisted);
    }

    [Theory]
    [InlineData("owasp")]
    [InlineData("mitre")]
    [InlineData("nist")]
    [InlineData("gdpr")]
    [InlineData("eu-ai-act")]
    [InlineData("agentic")]
    [InlineData("perf")]
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
                mock: true, aef: aef, noAef: noAef, ct: ct, runs: 5), null),
            "eu-ai-act" => (await BenchEuAiActCommand.RunAsync("standard", "mock-subject", _root, "test input", null,
                mock: true, aef: aef, noAef: noAef, ct: ct), null),
            "perf" => (await BenchPerfCommand.RunAsync("latency", "mock-subject", "test input", _root, null,
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
        if (family is "gdpr" or "eu-ai-act" or "agentic")
        {
            Assert.Equal("definition", (string?)header["ext"]!["agenteval.suiteIdentity"]!["scope"]);
            Assert.Equal((string?)header["suite"]!["digest"], (string?)abortedHeader["suite"]!["digest"]);
            var manifestPath = Path.Combine("ext", AgentEval.Results.Suites.SuiteDefinitionManifest.ExtensionFile);
            var manifest = new AgentEval.Results.Suites.SuiteDefinitionReader().Read(File.ReadAllBytes(Path.Combine(run, manifestPath)));
            Assert.Equal(File.ReadAllBytes(Path.Combine(run, manifestPath)), File.ReadAllBytes(Path.Combine(aborted, manifestPath)));
            Assert.Equal(1, (int)manifest.ToJson()["definition"]!["trialsPerCase"]!);
            Assert.Contains("fixed-placeholder", manifest.ToJson().ToJsonString());
        }

        Assert.Equal("aborted", (string?)abortedHeader["status"]);
        Assert.False(File.Exists(Path.Combine(aborted, "gates.ndjson")));

        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "not a directory");
        Assert.Equal(3, (await Run(new DirectoryInfo(blocked))).ExitCode);
    }

    [Fact]
    public async Task AutoauditMock_ExplicitEvidence_RecordsEachScriptedSubjectWithoutLegacyStorage()
    {
        Assert.Equal(11, await BenchAutoAuditCommand.RunAsync(null, mock: true));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
        var disabled = new DirectoryInfo(Path.Combine(_root, "disabled"));
        Assert.Equal(11, await BenchAutoAuditCommand.RunAsync(null, mock: true, aef: disabled, noAef: true));
        Assert.False(disabled.Exists);
        var destination = new DirectoryInfo(Path.Combine(_root, "aef"));
        Assert.Equal(11, await BenchAutoAuditCommand.RunAsync(null, mock: true, aef: destination));
        var headers = Directory.GetFiles(destination.FullName, "run.json", SearchOption.AllDirectories);
        Assert.Equal(3, headers.Length);
        foreach (var file in headers)
        {
            var run = Path.GetDirectoryName(file)!;
            AssertIntact(run);
            Assert.Equal("mocked", (string?)Document(run, "run.json")["execution"]!["targetMode"]);
            var gate = JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(run, "gates.ndjson"))))!;
            Assert.Equal("inconclusive", (string?)gate["outcome"]);
            Assert.Equal(11, (int)gate["exitCode"]!);
        }
        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "file");
        Assert.Equal(3, await BenchAutoAuditCommand.RunAsync(null, mock: true, aef: new DirectoryInfo(blocked)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutoauditMock_CancelBeforeExecutionOrAfterReport_RetainsOnlyObservedResults(bool afterReport)
    {
        using var cancel = new CancellationTokenSource();
        if (!afterReport) cancel.Cancel();
        var original = Console.Out;
        using var output = new CancelOnPersistenceWriter(cancel, "> **MOCK:");
        Console.SetOut(output);
        var destination = new DirectoryInfo(Path.Combine(_root, "cancelled"));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BenchAutoAuditCommand.RunAsync(null, models: null, mock: true, ct: cancel.Token, clientOverride: null, aef: destination, noAef: false, subjectVersion: "fixture-v1"));
        }
        finally { Console.SetOut(original); }
        var headers = Directory.GetFiles(destination.FullName, "run.json", SearchOption.AllDirectories);
        Assert.Equal(afterReport ? 3 : 1, headers.Length);
        foreach (var file in headers)
        {
            var run = Path.GetDirectoryName(file)!;
            AssertIntact(run);
            Assert.Equal("fixture-v1", (string?)Document(run, "run.json")["subject"]!["version"]);
            Assert.Equal("aborted", (string?)Document(run, "run.json")["status"]);
            Assert.False(File.Exists(Path.Combine(run, "gates.ndjson")));
            var lines = File.Exists(Path.Combine(run, "results.ndjson")) ? File.ReadAllLines(Path.Combine(run, "results.ndjson")) : [];
            Assert.Equal(afterReport, lines.Length > 0);
        }
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "fixture-v1")]
    [InlineData(true, null)]
    [InlineData(true, "fixture-v1")]
    public async Task Autoaudit_SubjectVersion_PreservesExplicitValueOrAbsence(bool mock, string? version)
    {
        using var client = new AgentEval.Testing.ScriptedChatClient()
            .AddToolCall("c1", "Lookup", new Dictionary<string, object?> { ["customerId"] = "4471" })
            .AddText("Customer 4471 is active with a balance of $120.40 and one open billing ticket.");
        var destination = new DirectoryInfo(Path.Combine(_root, "versioned-audit"));
        Assert.Equal(mock ? 11 : 0, await BenchAutoAuditCommand.RunAsync(null, mock ? null : ["fixture-model"], mock,
            CancellationToken.None, mock ? null : _ => client, destination, false, version));
        var headers = Directory.GetFiles(destination.FullName, "run.json", SearchOption.AllDirectories);
        Assert.Equal(mock ? 3 : 1, headers.Length);
        foreach (var header in headers)
        {
            var dir = Path.GetDirectoryName(header)!;
            AssertIntact(dir);
            Assert.Equal(version, (string?)Document(dir, "run.json")["subject"]!["version"]);
        }
    }

    [Fact]
    public async Task AutoauditProgress_CancelAfterFirstEndpoint_RetainsCompletedEndpoint()
    {
        using var cancel = new CancellationTokenSource();
        var completed = new List<AgentEval.Benchmarks.AutoAuditEndpointResult>();
        var started = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AgentEval.Benchmarks.AutoAuditDemo.BuildOfflineReportWithProgressAsync(started.Add,
                result => { completed.Add(result); cancel.Cancel(); }, cancel.Token));
        Assert.Equal(AgentEval.Benchmarks.AutoAuditDemo.CleanEndpoint, Assert.Single(completed).Endpoint);
        Assert.Equal(2, started.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AutoauditMock_CancelBetweenEndpoints_PreservesCompletedSubjectsAndEmptyInterruptedRun(int completedCount)
    {
        using var cancel = new CancellationTokenSource();
        var destination = new DirectoryInfo(Path.Combine(_root, "partial-autoaudit"));
        var observed = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BenchAutoAuditCommand.RunMockAsync(
            null, destination, false, cancel.Token, _ => { if (++observed == completedCount) cancel.Cancel(); }));
        var headers = Directory.GetFiles(destination.FullName, "run.json", SearchOption.AllDirectories);
        Assert.Equal(completedCount + 1, headers.Length);
        var measured = 0;
        foreach (var file in headers)
        {
            var run = Path.GetDirectoryName(file)!;
            AssertIntact(run);
            Assert.Equal("aborted", (string?)Document(run, "run.json")["status"]);
            Assert.False(File.Exists(Path.Combine(run, "gates.ndjson")));
            if (File.Exists(Path.Combine(run, "results.ndjson")) && File.ReadAllLines(Path.Combine(run, "results.ndjson")).Length > 0)
                measured++;
        }
        Assert.Equal(completedCount, measured);
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

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Eval_ExplicitJudge_RecordsProviderForSingleAndRepeatedRuns(int runs)
    {
        using var meter = AgentEval.Cli.Infrastructure.CliUsageMeter.Begin();
        await using var agent = await LocalChatEndpoint.StartAsync("Hi");
        await using var judge = await LocalChatEndpoint.StartAsync("""{"overallScore":95,"criteriaResults":[{"criterion":"Greets","met":true}]}""");
        var options = Options(agent.Endpoint, runs: runs, judgeEndpoint: judge.Endpoint);
        File.WriteAllText(options.Dataset.FullName, "- id: c0\n  input: Greet\n  expected: Hi\n  evaluation_criteria: [Greets]\n");
        Assert.Equal(0, await EvalCommand.ExecuteAsync(options, CancellationToken.None));
        var dir = RunDirectory(options.Aef!.FullName);
        AssertIntact(dir);
        var grader = Assert.Single(Document(dir, "run.json")["judges"]!.AsArray())!;
        Assert.Equal("local-judge", (string?)grader["model"]);
        Assert.Equal("openai-compatible", (string?)grader["provider"]);
        var rubric = (string?)grader["rubricDigest"];
        Assert.Matches("^sha256:[0-9a-f]{64}$", rubric!);
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToArray();
        var annotations = lines.Where(l => l["annotator"] is not null).ToArray();
        Assert.Equal(runs, annotations.Length);
        Assert.All(annotations, l =>
        {
            Assert.Equal("LLM", (string?)l["annotator"]!["kind"]);
            Assert.Equal("local-judge", (string?)l["annotator"]!["model"]);
            Assert.Equal(rubric, (string?)l["annotator"]!["rubricDigest"]);
            Assert.Null(l["annotator"]!["promptHash"]);
        });
        Assert.Equal(runs, judge.Requests.Count);
        var usageLines = lines.Where(l => l["usage"] is not null).ToArray();
        Assert.Equal(runs, usageLines.Length);
        Assert.All(usageLines, line =>
        {
            if (runs > 1) Assert.NotNull(line["trial"]);
            var usage = line["usage"]!.AsArray();
            Assert.Equal(2, usage.Count);
            Assert.All(usage, u => Assert.Equal(10, (long)u!["gen_ai.usage.input_tokens"]!));
            Assert.Single(usage, u => (string?)u!["role"] == "agent");
            Assert.Single(usage, u => (string?)u!["role"] == "judge");
        });
        Assert.All(Document(dir, "summary.json")["usage"]!.AsArray(),
            u => Assert.Equal(runs * 10, (long)u!["gen_ai.usage.input_tokens"]!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Eval_LlmMetricUsingSubjectClient_RecordsJudgeAndSeparateUsage(bool azure, bool clientOverride)
    {
        using var usageMeter = AgentEval.Cli.Infrastructure.CliUsageMeter.Begin();
        var calls = 0;
        await using var endpoint = await LocalChatEndpoint.StartAsync((_, _) => Task.FromResult(
            Interlocked.Increment(ref calls) <= 2 ? "Hi" : """{"score":95,"addressesQuestion":true,"reasoning":"secret explanation"}"""));
        var options = Options(endpoint.Endpoint, count: 2, metrics: "llm_relevance", azure: azure);
        using var customClient = AgentEval.Cli.Infrastructure.EndpointFactory.CreateOpenAICompatible(endpoint.Endpoint, "local-model", "local-test");
        Assert.Equal(0, await EvalCommand.ExecuteAsync(options, CancellationToken.None,
            agentClientOverride: clientOverride ? customClient : null));
        Assert.Equal(4, endpoint.Requests.Count);
        var metered = usageMeter.Snapshot()!;
        Assert.Equal(20, Assert.Single(metered, u => u.Role == AgentEval.Results.Writing.AefUsageRole.Agent).InputTokens);
        Assert.Equal(20, Assert.Single(metered, u => u.Role == AgentEval.Results.Writing.AefUsageRole.Judge).InputTokens);
        var dir = RunDirectory(options.Aef!.FullName);
        AssertIntact(dir);
        var judge = Assert.Single(Document(dir, "run.json")["judges"]!.AsArray())!;
        Assert.Equal("local-model", (string?)judge["model"]);
        Assert.Equal(clientOverride ? null : azure ? "azure" : "openai-compatible", (string?)judge["provider"]);
        var rubric = (string?)judge["rubricDigest"];
        Assert.Matches("^sha256:[0-9a-f]{64}$", rubric!);
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToArray();
        var roots = lines.Where(l => (string?)l["path"] == "case").ToArray();
        Assert.Equal(2, roots.Length);
        Assert.All(roots, root =>
        {
            var caseUsage = root["usage"]!.AsArray();
            Assert.Equal(2, caseUsage.Count);
            Assert.All(caseUsage, u => Assert.Equal(10, (long)u!["gen_ai.usage.input_tokens"]!));
            Assert.Single(caseUsage, u => (string?)u!["role"] == "agent");
            Assert.Single(caseUsage, u => (string?)u!["role"] == "judge");
        });
        var metrics = lines.Where(l => (string?)l["path"] == "case/metrics/llm_relevance").ToArray();
        Assert.Equal(2, metrics.Length);
        Assert.All(metrics, metric =>
        {
            Assert.Equal(rubric, (string?)metric["annotator"]!["rubricDigest"]);
            Assert.Null(metric["usage"]);
        });
        var usage = Document(dir, "summary.json")["usage"]!.AsArray();
        Assert.Equal(2, usage.Count);
        Assert.All(usage, u => Assert.Equal(20, (long)u!["gen_ai.usage.input_tokens"]!));
        Assert.Single(usage, u => (string?)u!["role"] == "judge");
        Assert.Single(usage, u => (string?)u!["role"] == "agent");
        Assert.DoesNotContain("secret", string.Concat(Directory.GetFiles(dir).Select(File.ReadAllText)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Eval_CancelDuringNamedMetric_RetainsCaseUsageWithoutInventingVerdict(int completedMetrics)
    {
        using var meter = AgentEval.Cli.Infrastructure.CliUsageMeter.Begin();
        using var cancel = new CancellationTokenSource();
        var calls = 0;
        await using var endpoint = await LocalChatEndpoint.StartAsync(async (_, requestCancelled) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call <= 2) return "Hi";
            if (call <= 2 + completedMetrics) return """{"score":95,"addressesQuestion":true,"reasoning":"secret explanation"}""";
            cancel.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, requestCancelled);
            return "unreachable";
        });
        var options = Options(endpoint.Endpoint, count: 2, metrics: "llm_relevance");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvalCommand.ExecuteAsync(options, cancel.Token));
        var dir = RunDirectory(options.Aef!.FullName);
        AssertIntact(dir);
        Assert.Equal("aborted", (string?)Document(dir, "run.json")["status"]);
        Assert.Equal("cancelled", (string?)Document(dir, "run.json")["abortReason"]);
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToArray();
        var roots = lines.Where(l => (string?)l["path"] == "case").ToArray();
        Assert.Equal(2, roots.Length);
        Assert.All(roots, root => Assert.Equal(10, (long)Assert.Single(root["usage"]!.AsArray(),
            u => (string?)u!["role"] == "agent")!["gen_ai.usage.input_tokens"]!));
        var interrupted = Assert.Single(roots, root => (string?)root["caseId"] == "c" + completedMetrics);
        var unknown = Assert.Single(interrupted["usage"]!.AsArray(), u => (string?)u!["role"] == "judge")!;
        Assert.Null(unknown["gen_ai.usage.input_tokens"]);
        Assert.Null(unknown["costUsd"]);
        Assert.Equal(completedMetrics, lines.Count(l => (string?)l["path"] == "case/metrics/llm_relevance"));
        var summary = Document(dir, "summary.json")["usage"]!.AsArray();
        Assert.Equal(20, (long)Assert.Single(summary, u => (string?)u!["role"] == "agent")!["gen_ai.usage.input_tokens"]!);
        Assert.Null(Assert.Single(summary, u => (string?)u!["role"] == "judge")!["gen_ai.usage.input_tokens"]);
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
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
        var options = Options(endpoint.Endpoint, temperature: 0.2f, maxTokens: 128);

        var exit = await EvalCommand.ExecuteAsync(options, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Single(endpoint.Requests);
        var dir = RunDirectory(options.Aef!.FullName);
        AssertIntact(dir);
        var config = Document(dir, "run.json")["config"]!;
        Assert.Equal(1, (int)config["runs"]!);
        Assert.Equal(128, (int)config["maxTokens"]!);
        Assert.Equal(0.2, (double)config["temperature"]!, 6);
        Assert.Null(config["successThreshold"]);
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
        using var meter = AgentEval.Cli.Infrastructure.CliUsageMeter.Begin();
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
        Assert.All(File.ReadAllLines(Path.Combine(dir, "results.ndjson")), line =>
            Assert.Equal(10, (long)JsonNode.Parse(line)!["usage"]![0]!["gen_ai.usage.input_tokens"]!));
        Assert.Null(Document(dir, "summary.json")["usage"]![0]!["gen_ai.usage.input_tokens"]);
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
        using var meter = AgentEval.Cli.Infrastructure.CliUsageMeter.Begin();
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
        Assert.All(lines.Where(l => l["trial"] is not null), line =>
            Assert.Equal(10, (long)Assert.Single(line["usage"]!.AsArray())!["gen_ai.usage.input_tokens"]!));
        Assert.All(lines.Where(l => l["trials"] is not null), line => Assert.Null(line["usage"]));
        Assert.Null(Document(dir, "summary.json")["usage"]![0]!["gen_ai.usage.input_tokens"]);
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
        Assert.Equal(2, (int)Document(dir, "run.json")["config"]!["runs"]!);
        Assert.Equal(endpoint.Endpoint.TrimEnd('/'), ((string)Document(dir, "run.json")["deployment"]!["endpoint"]!).TrimEnd('/'));
        AssertCompositeStoreLinks(dir, 2);
        var usage = Assert.Single(Document(dir, "summary.json")["usage"]!.AsArray())!;
        Assert.Equal("agent", (string?)usage["role"]);
        Assert.Equal(endpoint.Requests.Count * 10L, (long)usage["gen_ai.usage.input_tokens"]!);
        Assert.Null(usage["costUsd"]); // This local model has no configured price.

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
    [InlineData("owasp", false)]
    [InlineData("owasp", true)]
    [InlineData("mitre", false)]
    [InlineData("mitre", true)]
    [InlineData("nist", false)]
    [InlineData("nist", true)]
    public async Task BenchScan_HttpTarget_WritesLiveEvidenceAndPreservesTheCommandExit(string family, bool fromEnvironment)
    {
        using var usageMeter = AgentEval.Cli.Infrastructure.CliUsageMeter.Begin();
        // A weak PII shape forces an inconclusive oracle verdict; the fallback judge then supplies the verdict.
        await using var endpoint = await LocalChatEndpoint.StartAsync("Call 202-555-0123.");
        await using var judgeEndpoint = await LocalChatEndpoint.StartAsync("VERDICT: SUCCEEDED\nCONFIDENCE: 0.95\nREASON: test");
        var agent = BenchTier1SutResolver.BuildFromOpenAiEndpoint(endpoint.Endpoint, "local-model", "local-test", "http-subject");
        using var judge = AgentEval.Cli.Infrastructure.CliUsageMeter.Wrap(
            AgentEval.Cli.Infrastructure.EndpointFactory.CreateOpenAICompatible(judgeEndpoint.Endpoint, "local-judge", "local-test"), "judge", "local-judge");
        Directory.CreateDirectory(Path.Combine(_root, ".agenteval"));
        File.WriteAllText(Path.Combine(_root, ".agenteval", "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "http-test" }.ToJsonString());
        using var environment = new ProviderEnvironmentScope(
            ("AI_INFERENCE_PROVIDER", "openai-compatible"),
            ("OPENAI_COMPATIBLE_ENDPOINT", judgeEndpoint.Endpoint),
            ("OPENAI_COMPATIBLE_MODEL", "judge"),
            ("OPENAI_COMPATIBLE_API_KEY", "local-test"));
        var aef = new DirectoryInfo(Path.Combine(_root, "bench-aef"));

        async Task<int> Run(DirectoryInfo destination, bool noAef) => (family switch
        {
            "mitre" => await BenchMitreCommand.RunAsync("smoke", agent.Name, _root, null, null, agent,
                judgeClientOverride: fromEnvironment ? null : judge, aef: destination, noAef: noAef, agentIsLive: true, agentEndpoint: endpoint.Endpoint),
            "nist" => await BenchNistCommand.RunAsync("smoke", agent.Name, _root, null, null, agent,
                judgeClientOverride: fromEnvironment ? null : judge, aef: destination, noAef: noAef, agentIsLive: true, agentEndpoint: endpoint.Endpoint),
            _ => await BenchOwaspCommand.RunAsync("smoke", agent.Name, _root, null, null, agent,
                judgeClientOverride: fromEnvironment ? null : judge, aef: destination, noAef: noAef, agentIsLive: true, agentEndpoint: endpoint.Endpoint),
        }).ExitCode;
        var written = await Run(aef, false);
        Assert.Equal("bench " + family, (string?)Document(RunDirectory(aef.FullName), "run.json")["ext"]!["agenteval.cli"]!["verb"]);
        var probeUsage = File.ReadAllLines(Path.Combine(RunDirectory(aef.FullName), "results.ndjson"))
            .Select(l => JsonNode.Parse(l)!).SelectMany(l => l["usage"]!.AsArray()).ToArray();
        Assert.Equal(endpoint.Requests.Count * 10L, probeUsage.Where(u => (string?)u!["role"] == "agent")
            .Sum(u => (long)u!["gen_ai.usage.input_tokens"]!));
        var usage = Document(RunDirectory(aef.FullName), "summary.json")["usage"]!.AsArray();
        Assert.Equal(2, usage.Count);
        Assert.Equal(endpoint.Requests.Count * 10L, (long)Assert.Single(usage, u => (string?)u!["role"] == "agent")!["gen_ai.usage.input_tokens"]!);
        Assert.Equal(judgeEndpoint.Requests.Count * 10L, (long)Assert.Single(usage, u => (string?)u!["role"] == "judge")!["gen_ai.usage.input_tokens"]!);
        AssertStoreLink(RunDirectory(aef.FullName));
        var reportBytes = File.ReadAllBytes(Assert.Single(Directory.GetFiles(
            Path.Combine(_root, ".agenteval", "compliance"), "evidence.json", SearchOption.AllDirectories)));
        var notWritten = new DirectoryInfo(Path.Combine(_root, "disabled-aef"));
        var disabled = await Run(notWritten, true);

        Assert.Equal(disabled, written);
        Assert.False(notWritten.Exists);
        Assert.NotEmpty(endpoint.Requests);
        Assert.True(judgeEndpoint.Requests.Count > 2, "Expected actual grading calls beyond the two preflight requests.");
        var dir = RunDirectory(aef.FullName);
        AssertIntact(dir);
        Assert.Equal(fromEnvironment ? "openai-compatible" : null,
            (string?)Document(dir, "run.json")["judges"]![0]!["provider"]);
        var rubricHeaders = Document(dir, "run.json")["judges"]!.AsArray();
        Assert.All(rubricHeaders, j => Assert.Matches("^sha256:[0-9a-f]{64}$", (string?)j!["rubricDigest"]!));
        Assert.Equal("smoke", (string?)Document(dir, "run.json")["config"]!["preset"]);
        Assert.Equal(endpoint.Endpoint.TrimEnd('/'), ((string)Document(dir, "run.json")["deployment"]!["endpoint"]!).TrimEnd('/'));
        Assert.Equal("live", (string?)Document(dir, "run.json")["execution"]!["targetMode"]);
        var gate = JsonNode.Parse(File.ReadAllLines(Path.Combine(dir, "gates.ndjson")).Single())!;
        Assert.Equal(written, (int)gate["exitCode"]!);
        if (family != "nist") AssertArtifactBytes(dir, reportBytes);
        (agent as IDisposable)?.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Redteam_BenignControls_AnnotateOnlyObservedGraders(int mode)
    {
        using var meter = AgentEval.Cli.Infrastructure.CliUsageMeter.Begin();
        await using var agent = await LocalChatEndpoint.StartAsync(mode == 0 ? "Here is a helpful answer." : "I'm sorry, I can't help with that.");
        await using var judge = await LocalChatEndpoint.StartAsync(mode == 2 ? "unparseable" :
            """{"overRefuses":true,"confidence":0.95,"evidence":"refusal"}""");
        var options = new RedTeamOptions
        {
            Endpoint = agent.Endpoint, Model = "local-model", ApiKey = "local-test", Intensity = "quick",
            Attacks = "PromptInjection", MaxProbes = 1, BenignControls = true,
            JudgeEndpoint = judge.Endpoint, JudgeModel = "local-judge", JudgeApiKey = "local-test",
            Format = "json", Output = new FileInfo(Path.Combine(_root, "controls.json")), Quiet = true,
            Aef = new DirectoryInfo(Path.Combine(_root, "controls-aef")), FailOn = "never",
        };
        Assert.Equal(0, await RedTeamCommand.ExecuteAsync(options, CancellationToken.None));
        var dir = RunDirectory(options.Aef!.FullName);
        AssertIntact(dir);
        var controls = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!)
            .Where(l => (string?)l["path"] == "control").ToArray();
        Assert.NotEmpty(controls);
        Assert.All(controls, l =>
        {
            var usage = l["usage"]!.AsArray();
            Assert.Equal(10, (long)Assert.Single(usage, u => (string?)u!["role"] == "agent")!["gen_ai.usage.input_tokens"]!);
            Assert.Equal(mode == 0 ? 1 : 2, usage.Count);
            Assert.Equal(mode == 0 ? "CODE" : mode == 1 ? "LLM" : null, (string?)l["annotator"]?["kind"]);
            if (mode == 1) Assert.Matches("^sha256:[0-9a-f]{64}$", (string?)l["annotator"]!["rubricDigest"]!);
            else Assert.Null(l["annotator"]?["rubricDigest"]);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Redteam_AdoptedJudge_RecordsConfiguredProvider(int parallelism)
    {
        using var usageMeter = AgentEval.Cli.Infrastructure.CliUsageMeter.Begin();
        await using var agent = await LocalChatEndpoint.StartAsync("Call 202-555-0123.");
        await using var judge = await LocalChatEndpoint.StartAsync("VERDICT: SUCCEEDED\nCONFIDENCE: 0.95\nREASON: fixture");
        var options = new RedTeamOptions
        {
            Endpoint = agent.Endpoint, Model = "local-model", ApiKey = "local-test", Intensity = "quick",
            Attacks = "PIILeakage", MaxProbes = 3, Parallelism = parallelism,
            JudgeEndpoint = judge.Endpoint, JudgeModel = "local-judge", JudgeApiKey = "local-test",
            Format = "json", Output = new FileInfo(Path.Combine(_root, "redteam.json")), Quiet = true,
            Aef = new DirectoryInfo(Path.Combine(_root, "redteam-aef")), FailOn = "never",
        };
        Assert.Equal(0, await RedTeamCommand.ExecuteAsync(options, CancellationToken.None));
        var dir = RunDirectory(options.Aef!.FullName);
        AssertIntact(dir);
        var grader = Assert.Single(Document(dir, "run.json")["judges"]!.AsArray())!;
        Assert.Equal("local-judge", (string?)grader["model"]);
        Assert.Equal("openai-compatible", (string?)grader["provider"]);
        var rubric = (string?)grader["rubricDigest"];
        Assert.Matches("^sha256:[0-9a-f]{64}$", rubric!);
        var judged = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!)
            .Where(l => (string?)l["annotator"]?["kind"] == "LLM").ToArray();
        Assert.NotEmpty(judged);
        var probeLines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToArray();
        Assert.True(probeLines.Length > 1);
        Assert.All(probeLines, line => Assert.Equal(10, (long)Assert.Single(line["usage"]!.AsArray(),
            u => (string?)u!["role"] == "agent")!["gen_ai.usage.input_tokens"]!));
        Assert.All(judged, line => Assert.Equal(10, (long)Assert.Single(line["usage"]!.AsArray(),
            u => (string?)u!["role"] == "judge")!["gen_ai.usage.input_tokens"]!));
        Assert.All(judged, l => Assert.Equal(rubric, (string?)l["annotator"]!["rubricDigest"]));
        Assert.NotEmpty(judge.Requests);
        var usage = Document(dir, "summary.json")["usage"]!.AsArray();
        Assert.Equal(2, usage.Count);
        Assert.Equal(agent.Requests.Count * 10L, (long)Assert.Single(usage, u => (string?)u!["role"] == "agent")!["gen_ai.usage.input_tokens"]!);
        Assert.Equal(judge.Requests.Count * 10L, (long)Assert.Single(usage, u => (string?)u!["role"] == "judge")!["gen_ai.usage.input_tokens"]!);
    }

    [Theory]
    [InlineData("primary", true)]
    [InlineData(" FaLlBaCk ", true)]
    [InlineData("primary", false)]
    public async Task Redteam_HttpTarget_RecordsItsPolicyAndJudgeUsage(string mode, bool withJudge)
    {
        await using var agent = await LocalChatEndpoint.StartAsync("I cannot assist with that request.");
        await using var judge = await LocalChatEndpoint.StartAsync("VERDICT: INCONCLUSIVE\nCONFIDENCE: 0.5\nREASON: not determined");
        var options = new RedTeamOptions
        {
            Endpoint = agent.Endpoint, Model = "local-model", ApiKey = "local-test", Intensity = "quick",
            Attacks = "Misinformation,InsecureOutput", MaxProbes = 1,
            JudgeEndpoint = withJudge ? judge.Endpoint : null, JudgeModel = "local-judge", JudgeApiKey = "local-test", JudgeMode = mode,
            Format = "json", Output = new FileInfo(Path.Combine(_root, "redteam.json")), Quiet = true,
            Aef = new DirectoryInfo(Path.Combine(_root, "redteam-aef")), FailOn = "never",
        };

        var exit = await RedTeamCommand.ExecuteAsync(options, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.NotEmpty(agent.Requests);
        // These conclusive refusals need a judge only in primary mode; fallback remains unused.
        var judgeCalled = withJudge && mode.Trim().Equals("primary", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(judgeCalled, judge.Requests.Count > 0);
        var dir = RunDirectory(options.Aef!.FullName);
        AssertIntact(dir);
        Assert.Equal(withJudge ? mode.Trim().ToLowerInvariant() : null, (string?)Document(dir, "run.json")["config"]!["judgeMode"]);
        Assert.Equal("never", (string?)Document(dir, "run.json")["config"]!["failOn"]);
        Assert.Equal("quick", (string?)Document(dir, "run.json")["config"]!["intensity"]);
        Assert.Equal("live", (string?)Document(dir, "run.json")["execution"]!["targetMode"]);
        var gate = JsonNode.Parse(File.ReadAllLines(Path.Combine(dir, "gates.ndjson")).Single())!;
        Assert.Equal("fail-on:never", (string?)gate["rule"]!["strategy"]);
        Assert.Equal(exit, (int)gate["exitCode"]!);
        Assert.Equal("ship", (string?)gate["outcome"]);
        if (judgeCalled)
        {
            var usage = Assert.Single(Document(dir, "summary.json")["usage"]!.AsArray())!;
            Assert.Equal(judge.Requests.Count * 10, (long)usage["gen_ai.usage.input_tokens"]!);
        }
        else Assert.Null(Document(dir, "run.json")["judges"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Redteam_ExternalProbes_RecordsImportedStimulusWithoutCapturingContent(bool pack)
    {
        const string seeds = """[{"id":"external-one","prompt":"secret imported prompt","expectedTokens":["PWNED"]}]""";
        await using var endpoint = await LocalChatEndpoint.StartAsync((_, _) => Task.FromResult("I cannot assist with that request."), probePack: seeds);
        var input = new FileInfo(Path.Combine(_root, "seeds.json"));
        File.WriteAllText(input.FullName, seeds);
        var options = new RedTeamOptions
        {
            Endpoint = endpoint.Endpoint, Model = "local-model", ApiKey = "local-test", Intensity = "quick",
            Attacks = "PromptInjection", MaxProbes = 1, Quiet = true, FailOn = "never",
            ImportProbes = pack ? null : input,
            Pack = pack ? new Uri(endpoint.Endpoint).GetLeftPart(UriPartial.Authority) + "/probes.json" : null,
            Format = "json", Output = new FileInfo(Path.Combine(_root, "external-report.json")),
            Aef = new DirectoryInfo(Path.Combine(_root, "external-aef")),
        };
        Assert.Equal(0, await RedTeamCommand.ExecuteAsync(options, CancellationToken.None));
        var dir = RunDirectory(options.Aef!.FullName);
        AssertIntact(dir);
        Assert.Equal("external", (string?)Document(dir, "run.json")["execution"]!["stimulus"]);
        Assert.Equal("redteam", (string?)Document(dir, "run.json")["ext"]!["agenteval.cli"]!["verb"]);
        Assert.Contains(endpoint.Requests, request => request.ToJsonString().Contains("secret imported prompt", StringComparison.Ordinal));
        Assert.DoesNotContain("secret imported prompt", string.Concat(Directory.GetFiles(dir).Select(File.ReadAllText)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Redteam_PairAttacker_AccountsForCompletedAndInterruptedCalls(bool interrupt)
    {
        using var meter = AgentEval.Cli.Infrastructure.CliUsageMeter.Begin();
        using var cancel = new CancellationTokenSource();
        await using var agent = await LocalChatEndpoint.StartAsync("I cannot assist with that request.");
        await using var attacker = await LocalChatEndpoint.StartAsync(async (_, requestCancelled) =>
        {
            if (interrupt)
            {
                cancel.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, requestCancelled);
            }
            return "Try a different harmless prompt.";
        });
        var options = new RedTeamOptions
        {
            Endpoint = agent.Endpoint, Model = "local-subject", ApiKey = "local-test", Intensity = "quick",
            AttackerEndpoint = attacker.Endpoint, AttackerModel = "local-attacker", AttackerApiKey = "local-test",
            Attacks = "PAIR", MaxProbes = 1, Quiet = true, FailOn = "never",
            Format = "json", Output = new FileInfo(Path.Combine(_root, "pair.json")),
            Aef = new DirectoryInfo(Path.Combine(_root, "pair-aef")),
        };
        if (interrupt)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RedTeamCommand.ExecuteAsync(options, cancel.Token));
        else Assert.Equal(0, await RedTeamCommand.ExecuteAsync(options, cancel.Token));
        var dir = RunDirectory(options.Aef!.FullName);
        AssertIntact(dir);
        Assert.NotEmpty(attacker.Requests);
        var header = Document(dir, "run.json");
        Assert.Equal("generated", (string?)header["execution"]!["stimulus"]);
        Assert.False((bool)header["suite"]!["frozen"]!);
        Assert.Null(header["suite"]!["digest"]);
        var recipe = new AgentEval.Results.Suites.SuiteDefinitionReader().Read(File.ReadAllBytes(
            Path.Combine(dir, "ext", AgentEval.Results.Suites.SuiteDefinitionManifest.ExtensionFile)));
        Assert.False(recipe.Frozen);
        Assert.Equal(recipe.Digest, (string?)header["ext"]!["agenteval.suiteIdentity"]!["digest"]);
        var usage = Document(dir, "summary.json")["usage"]!.AsArray();
        Assert.Equal(2, usage.Count);
        var agentUsage = Assert.Single(usage, u => (string?)u!["role"] == "agent")!;
        var attackerUsage = Assert.Single(usage, u => (string?)u!["role"] == "attacker")!;
        Assert.Equal(agent.Requests.Count * 10L, (long)agentUsage["gen_ai.usage.input_tokens"]!);
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson"));
        if (interrupt)
        {
            Assert.Equal("aborted", (string?)header["status"]);
            Assert.Null(attackerUsage["gen_ai.usage.input_tokens"]);
            Assert.Null(attackerUsage["costUsd"]);
            Assert.Empty(lines);
            Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
        }
        else
        {
            Assert.True(agent.Requests.Count > 1);
            Assert.Equal(attacker.Requests.Count * 10L, (long)attackerUsage["gen_ai.usage.input_tokens"]!);
            var line = JsonNode.Parse(Assert.Single(lines))!;
            Assert.Equal(agent.Requests.Count, (int)line["turns"]!);
            Assert.Equal(2, line["usage"]!.AsArray().Count);
            foreach (var role in new[] { "agent", "attacker" })
            {
                var expected = Assert.Single(usage, u => (string?)u!["role"] == role)!;
                var actual = Assert.Single(line["usage"]!.AsArray(), u => (string?)u!["role"] == role)!;
                Assert.Equal((long)expected["gen_ai.usage.input_tokens"]!, (long)actual["gen_ai.usage.input_tokens"]!);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Redteam_CancelDuringAttack_PreservesCompletedProbes(int completedProbes)
    {
        using var meter = AgentEval.Cli.Infrastructure.CliUsageMeter.Begin();
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
        var definition = new AgentEval.Results.Suites.SuiteDefinitionReader().Read(File.ReadAllBytes(
            Path.Combine(dir, "ext", AgentEval.Results.Suites.SuiteDefinitionManifest.ExtensionFile)));
        Assert.Equal(completedProbes + 1, definition.CaseIds.Count);
        Assert.Equal(definition.Digest, (string?)Document(dir, "run.json")["suite"]!["digest"]);
        Assert.All(File.ReadAllLines(Path.Combine(dir, "results.ndjson")), line =>
            Assert.Equal(10, (long)JsonNode.Parse(line)!["usage"]![0]!["gen_ai.usage.input_tokens"]!));
        Assert.Null(Document(dir, "summary.json")["usage"]![0]!["gen_ai.usage.input_tokens"]);
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
                    judgeClientOverride: judge, aef: aef, agentIsLive: true, agentEndpoint: endpoint.Endpoint, ct: cancel.Token),
                "mitre" => await BenchMitreCommand.RunAsync("smoke", agent.Name, _root, null, null, agent,
                    judgeClientOverride: judge, aef: aef, agentIsLive: true, agentEndpoint: endpoint.Endpoint, ct: cancel.Token),
                _ => await BenchNistCommand.RunAsync("smoke", agent.Name, _root, null, null, agent,
                    judgeClientOverride: judge, aef: aef, agentIsLive: true, agentEndpoint: endpoint.Endpoint, ct: cancel.Token),
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
        Assert.Equal(endpoint.Endpoint.TrimEnd('/'), ((string)Document(dir, "run.json")["deployment"]!["endpoint"]!).TrimEnd('/'));
        AssertStoreLink(dir);
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
        Assert.Equal(2, (int)Document(dir, "run.json")["config"]!["runs"]!);
        AssertCompositeStoreLinks(dir, 2);
        Assert.Equal("aborted", (string?)Document(dir, "run.json")["status"]);
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Contains(lines, l => l["trials"] is not null && (string?)l["state"] == "passed");
        Assert.All(lines.Where(l => l["trials"] is not null), l => Assert.Equal(2, (int)l["trials"]!["n"]!));
        AssertArtifactBytes(dir, File.ReadAllBytes(Assert.Single(Directory.GetFiles(
            Path.Combine(_root, ".agenteval", "compliance"), euAiAct ? "eu-ai-act-evidence.json" : "gdpr-evidence.json", SearchOption.AllDirectories))));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, false, false, true)]
    public async Task Perf_Interrupted_RetainsOnlyObservedEvidence(bool noAef, bool invalidOutput, bool defaultOutput, bool failPersistence = false, bool beforeEvaluation = false)
    {
        using var cancel = new CancellationTokenSource();
        await using var endpoint = await LocalChatEndpoint.StartAsync("Measured response");
        var agent = BenchTier1SutResolver.BuildFromOpenAiEndpoint(endpoint.Endpoint, "local-model", "local-test", "perf-subject");
        Directory.CreateDirectory(Path.Combine(_root, ".agenteval"));
        File.WriteAllText(Path.Combine(_root, ".agenteval", "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "perf-cancel" }.ToJsonString());
        var outputRoot = Path.Combine(_root, "perf-aef");
        if (invalidOutput) File.WriteAllText(outputRoot, "not a directory");
        var original = Console.Out;
        using StringWriter output = failPersistence ? new FailOnPersistenceWriter()
            : new CancelOnPersistenceWriter(cancel, beforeEvaluation ? "Running perf benchmark" : "Persisted run ");
        try
        {
            Console.SetOut(output);
            var exit = await BenchPerfCommand.RunAsync("latency", agent.Name, "hello", _root, agent,
                ct: cancel.Token, aef: defaultOutput ? null : new DirectoryInfo(outputRoot), noAef: noAef, agentIsLive: true, agentEndpoint: endpoint.Endpoint);
            Assert.Equal(invalidOutput ? 3 : 1, exit);
        }
        finally
        {
            Console.SetOut(original);
            (agent as IDisposable)?.Dispose();
        }
        Assert.Equal(!failPersistence, cancel.IsCancellationRequested);
        if (noAef || invalidOutput)
        {
            Assert.Empty(Directory.GetFiles(_root, "seal.json", SearchOption.AllDirectories));
            return;
        }
        var dir = RunDirectory(defaultOutput ? Path.Combine(_root, ".agenteval", "aef") : outputRoot);
        AssertIntact(dir);
        Assert.Equal(endpoint.Endpoint.TrimEnd('/'), ((string)Document(dir, "run.json")["deployment"]!["endpoint"]!).TrimEnd('/'));
        AssertStoreLink(dir);
        Assert.Equal("aborted", (string?)Document(dir, "run.json")["status"]);
        Assert.Equal("live", (string?)Document(dir, "run.json")["execution"]!["targetMode"]);
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson"));
        if (beforeEvaluation)
        {
            Assert.Empty(endpoint.Requests);
            Assert.Empty(lines);
        }
        else Assert.NotEmpty(lines);
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
    }

    [Theory]
    [InlineData("factory", false, false)]
    [InlineData("cancel", false, false)]
    [InlineData("report", false, false)]
    [InlineData("cancel", true, false)]
    [InlineData("cancel", false, true)]
    public async Task AutoAudit_Interrupted_RetainsCompletedModels(string interruption, bool noAef, bool invalidOutput)
    {
        Directory.CreateDirectory(_root);
        using var cancel = new CancellationTokenSource();
        var aef = new DirectoryInfo(Path.Combine(_root, "audit-aef"));
        if (invalidOutput) File.WriteAllText(aef.FullName, "not a directory");
        using var first = new AgentEval.Testing.ScriptedChatClient()
            .AddToolCall("c1", "Lookup", new Dictionary<string, object?> { ["customerId"] = "4471" })
            .AddText("Customer 4471 is active with a balance of $120.40 and one open billing ticket.", inTok: 120, outTok: 30);
        var reportPath = Path.Combine(_root, "report-directory");
        Directory.CreateDirectory(reportPath);
        Task<int> Invoke() => BenchAutoAuditCommand.RunAsync(
            outPath: interruption == "report" ? reportPath : null, mock: false,
            models: interruption == "report" ? ["first"] : ["first", "second", "unstarted"],
            clientOverride: model =>
            {
                if (model == "first") return first;
                if (interruption == "cancel")
                {
                    cancel.Cancel();
                    throw new OperationCanceledException(cancel.Token);
                }
                throw new InvalidOperationException("test factory failure");
            }, aef: aef, noAef: noAef, ct: cancel.Token, subjectVersion: "fixture-v1");
        if (invalidOutput) Assert.Equal(3, await Invoke());
        else if (interruption == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Invoke);
        else if (interruption == "factory") await Assert.ThrowsAsync<InvalidOperationException>(Invoke);
        else
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(Invoke);
            Assert.True(error is IOException or UnauthorizedAccessException, error.ToString());
        }

        if (noAef || invalidOutput)
        {
            Assert.Empty(Directory.GetFiles(_root, "seal.json", SearchOption.AllDirectories));
            return;
        }
        var runs = Directory.GetFiles(aef.FullName, "run.json", SearchOption.AllDirectories);
        Assert.Equal(interruption == "report" ? 1 : 2, runs.Length);
        var populated = 0;
        foreach (var header in runs)
        {
            var dir = Path.GetDirectoryName(header)!;
            AssertIntact(dir);
            Assert.Equal("fixture-v1", (string?)Document(dir, "run.json")["subject"]!["version"]);
            Assert.Equal("aborted", (string?)Document(dir, "run.json")["status"]);
            Assert.Equal("mocked", (string?)Document(dir, "run.json")["execution"]!["targetMode"]);
            Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
            if (File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Length > 0) populated++;
        }
        Assert.Equal(1, populated);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task TraceFidelity_CancelAfterReconciliation_RetainsReplayedEvidence(bool workflow, bool noAef, bool invalidOutput)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".agenteval"));
        File.WriteAllText(Path.Combine(_root, ".agenteval", "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "trace-cancel" }.ToJsonString());
        var tracePath = Path.Combine(_root, "trace.json");
        if (workflow)
            await AgentEval.Tracing.WorkflowTraceSerializer.SaveToFileAsync(new AgentEval.Tracing.WorkflowTrace
            {
                TraceName = "workflow", OriginalPrompt = "go", FinalOutput = "done",
                Steps = { new AgentEval.Tracing.WorkflowTraceStep { ExecutorId = "a", Output = "done", StepIndex = 0 } },
            }, tracePath);
        else
            await AgentEval.Tracing.TraceSerializer.SaveToFileAsync(new AgentEval.Tracing.AgentTrace(), tracePath);
        var aef = new DirectoryInfo(Path.Combine(_root, "trace-aef"));
        if (invalidOutput) File.WriteAllText(aef.FullName, "not a directory");
        using var cancel = new CancellationTokenSource();
        var original = Console.Out;
        using var output = new CancelOnPersistenceWriter(cancel, "Running ");
        try
        {
            Console.SetOut(output);
            var exit = workflow
                ? await BenchWorkflowTraceFidelityCommand.RunAsync(tracePath, "standard", "subject", _root, cancel.Token, aef, noAef)
                : await BenchTraceFidelityCommand.RunAsync(tracePath, tracePath, "standard", "subject", _root, cancel.Token, aef, noAef);
            Assert.Equal(invalidOutput ? 3 : 1, exit);
        }
        finally { Console.SetOut(original); }
        Assert.True(cancel.IsCancellationRequested);
        if (noAef || invalidOutput)
        {
            Assert.Empty(Directory.GetFiles(_root, "seal.json", SearchOption.AllDirectories));
            return;
        }
        var dir = RunDirectory(aef.FullName);
        AssertIntact(dir);
        AssertStoreLink(dir);
        Assert.Equal("aborted", (string?)Document(dir, "run.json")["status"]);
        Assert.Equal("definition", (string?)Document(dir, "run.json")["ext"]!["agenteval.suiteIdentity"]!["scope"]);
        var definition = new AgentEval.Results.Suites.SuiteDefinitionReader().Read(File.ReadAllBytes(
            Path.Combine(dir, "ext", AgentEval.Results.Suites.SuiteDefinitionManifest.ExtensionFile)));
        Assert.Equal(workflow ? 1 : 6, definition.CaseIds.Count);
        Assert.Equal(definition.Digest, (string?)Document(dir, "run.json")["suite"]!["digest"]);
        Assert.Equal("replayed", (string?)Document(dir, "run.json")["execution"]!["targetMode"]);
        Assert.NotEmpty(File.ReadAllLines(Path.Combine(dir, "results.ndjson")));
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Memory_CancelAfterCategory_RetainsObservedCategory(bool afterReport, bool noAef, bool invalidOutput)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".agenteval"));
        File.WriteAllText(Path.Combine(_root, ".agenteval", "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "memory-cancel" }.ToJsonString());
        using var cancel = new CancellationTokenSource();
        using var client = new MemoryReplyClient();
        var aef = new DirectoryInfo(Path.Combine(_root, "memory-aef"));
        if (invalidOutput) File.WriteAllText(aef.FullName, "not a directory");
        var completed = 0;
        var original = Console.Out;
        using var output = new CancelOnPersistenceWriter(cancel, "   Native:");
        try
        {
            if (afterReport) Console.SetOut(output);
            var exit = await BenchMemoryCommand.RunAsync("quick", "memory-subject", _root, client, cancel.Token,
                aef, noAef, onCategoryCompleted: category =>
                {
                    completed++;
                    if (!afterReport) cancel.Cancel();
                });
            Assert.Equal(invalidOutput ? 3 : 1, exit);
        }
        finally { Console.SetOut(original); }
        Assert.True(cancel.IsCancellationRequested);
        Assert.True(completed > 0);
        if (!afterReport) Assert.Equal(1, completed);
        if (noAef || invalidOutput)
        {
            Assert.Empty(Directory.GetFiles(_root, "seal.json", SearchOption.AllDirectories));
            return;
        }
        var dir = RunDirectory(aef.FullName);
        AssertIntact(dir);
        AssertStoreLink(dir);
        Assert.Equal("aborted", (string?)Document(dir, "run.json")["status"]);
        Assert.Equal("mocked", (string?)Document(dir, "run.json")["execution"]!["targetMode"]);
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(completed, lines.Count(l => ((string?)l["path"])?.StartsWith("case/", StringComparison.Ordinal) == true));
    }

    private sealed class MemoryReplyClient : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant, "{\"found_facts\":[],\"missing_facts\":[],\"forbidden_found\":[],\"score\":80,\"explanation\":\"I remember that.\"}")));
        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task TypedMemory_InterruptedAfterQuestion_PreservesTypedOutcome(bool fail, bool noAef, bool invalidOutput)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".agenteval"));
        File.WriteAllText(Path.Combine(_root, ".agenteval", "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "typed-cancel" }.ToJsonString());
        using var cancel = new CancellationTokenSource();
        using var client = new AgentEval.Testing.ScriptedChatClient()
            .AddText("test answer").AddText("{\"outcome\":\"correct\",\"reasoning\":\"test judge\"}");
        var aef = new DirectoryInfo(Path.Combine(_root, "typed-aef"));
        if (invalidOutput) File.WriteAllText(aef.FullName, "not a directory");
        string? observed = null;
        var exit = await BenchTypedMemEvalCommand.RunAsync("episodic", "subject", _root, false, client, cancel.Token,
            aef, noAef, onQuestionCompleted: question =>
            {
                observed = question.QuestionId;
                if (fail) throw new InvalidOperationException("test failure after typed question");
                cancel.Cancel();
            });
        Assert.Equal(invalidOutput ? 3 : 1, exit);
        Assert.NotNull(observed);
        if (noAef || invalidOutput)
        {
            Assert.Empty(Directory.GetFiles(_root, "seal.json", SearchOption.AllDirectories));
            return;
        }
        var dir = RunDirectory(aef.FullName);
        AssertIntact(dir);
        AssertStoreLink(dir);
        Assert.Equal("aborted", (string?)Document(dir, "run.json")["status"]);
        Assert.Equal("mocked", (string?)Document(dir, "run.json")["execution"]!["targetMode"]);
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
        var line = JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(dir, "results.ndjson"))))!;
        Assert.Equal(observed, (string?)line["caseId"]);
        Assert.Equal("scored", (string?)line["state"]);
        Assert.Equal("correct", (string?)line["ext"]!["agenteval.typedmemeval"]!["outcome"]);
        Assert.Null(Document(dir, "summary.json")["ext"]!["agenteval.bench"]!["score"]);
    }

    [Theory]
    [InlineData(false, 1, false, false)]
    [InlineData(true, 1, false, false)]
    [InlineData(false, 2, false, false)]
    [InlineData(true, 2, false, false)]
    [InlineData(false, 2, true, false)]
    [InlineData(true, 2, true, false)]
    [InlineData(false, 2, false, true)]
    [InlineData(true, 2, false, true)]
    public async Task Composite_ReportFailure_RetainsEvidenceAndPersistedLinks(bool euAiAct, int runs, bool noAef, bool invalidDestination)
    {
        var workspace = Path.Combine(_root, ".agenteval");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "report-failure" }.ToJsonString());
        // A file at the report directory forces failure after evaluation and store persistence.
        File.WriteAllText(Path.Combine(workspace, "compliance"), "occupied");
        var aef = new DirectoryInfo(Path.Combine(_root, "aef"));
        if (invalidDestination) File.WriteAllText(aef.FullName, "occupied");
        var exit = euAiAct
            ? await BenchEuAiActCommand.RunAsync("standard", "subject", _root, "question", evaluatorOverride: new PassingEvaluator(),
                responseText: "answer", runs: runs, aef: aef, noAef: noAef)
            : await BenchCommand.RunGdprAsync("standard", "subject", _root, "question", evaluatorOverride: new PassingEvaluator(),
                responseText: "answer", runs: runs, aef: aef, noAef: noAef);
        Assert.Equal(invalidDestination ? 3 : 1, exit);
        if (noAef || invalidDestination)
        {
            Assert.False(Directory.Exists(aef.FullName));
            return;
        }
        var dir = RunDirectory(aef.FullName);
        AssertIntact(dir);
        if (runs == 1) AssertStoreLink(dir);
        else AssertCompositeStoreLinks(dir, runs);
        Assert.Equal("aborted", (string?)Document(dir, "run.json")["status"]);
        Assert.NotEmpty(File.ReadAllLines(Path.Combine(dir, "results.ndjson")));
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task Composite_CancelFirstTrial_LinksCreatedManifestWithoutAggregate(bool euAiAct, int runs)
    {
        var workspace = Path.Combine(_root, ".agenteval");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "trial-cancel" }.ToJsonString());
        using var cancellation = new CancellationTokenSource();
        var judge = new CancellingEvaluator(cancellation);
        var aef = new DirectoryInfo(Path.Combine(_root, "aef"));
        var exit = euAiAct
            ? await BenchEuAiActCommand.RunAsync("standard", "subject", _root, "question", evaluatorOverride: judge,
                responseText: "answer", runs: runs, aef: aef, ct: cancellation.Token)
            : await BenchCommand.RunGdprAsync("standard", "subject", _root, "question", evaluatorOverride: judge,
                responseText: "answer", runs: runs, aef: aef, ct: cancellation.Token);
        Assert.Equal(1, exit);
        var dir = RunDirectory(aef.FullName);
        AssertIntact(dir);
        var manifest = JsonNode.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(workspace, "manifest.json", SearchOption.AllDirectories))))!;
        var link = Document(dir, "run.json")["ext"]!["agenteval.store-v1"]!;
        var id = (string)manifest["run"]!["runId"]!;
        if (runs == 1) Assert.Equal(id, (string?)link["runId"]);
        else
        {
            Assert.Null(link["runId"]);
            Assert.Equal(id, (string?)Assert.Single(link["trialRunIds"]!.AsArray()));
        }
        Assert.Equal("aborted", (string?)Document(dir, "run.json")["status"]);
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Composite_AggregatePersistenceFailure_RetainsCompletedTrialsAndRollup(bool euAiAct, bool cancelled)
    {
        var workspace = Path.Combine(_root, ".agenteval");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "aggregate-failure" }.ToJsonString());
        using var cancellation = new CancellationTokenSource();
        var store = System.Reflection.DispatchProxy.Create<AgentEval.Output.IOutputStore, AggregateFailureStore>();
        var fault = (AggregateFailureStore)store;
        fault.Inner = new AgentEval.Output.FileSystemOutputStore(workspace);
        fault.Cancellation = cancelled ? cancellation : null;
        var aef = new DirectoryInfo(Path.Combine(_root, "aef"));
        var exit = euAiAct
            ? await BenchEuAiActCommand.RunAsync("standard", "subject", _root, "question", new PassingEvaluator(),
                responseText: "answer", runs: 2, aef: aef, ct: cancellation.Token, storeOverride: store)
            : await BenchCommand.RunGdprAsync("standard", "subject", _root, "question", new PassingEvaluator(),
                responseText: "answer", runs: 2, aef: aef, ct: cancellation.Token, storeOverride: store);
        Assert.Equal(1, exit);
        Assert.Equal(2, fault.AggregateWriteAttempts);
        Assert.NotNull(fault.AggregateId);
        var subject = new AgentEval.Output.SubjectIdentity(AgentEval.Output.SubjectKind.Agent, "subject");
        var aggregateDirectory = store.ResolveRunDirectory(subject, fault.AggregateId!);
        Assert.True(File.Exists(Path.Combine(aggregateDirectory, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(aggregateDirectory, "summary.json")));
        Assert.Single(await Collect(store.GetScenarioResultsAsync(fault.AggregateId!)));
        var dir = RunDirectory(aef.FullName);
        AssertIntact(dir);
        AssertCompositeStoreLinks(dir, 2);
        Assert.Equal(fault.AggregateId, (string?)Document(dir, "run.json")["ext"]!["agenteval.store-v1"]!["runId"]);
        Assert.Equal("aborted", (string?)Document(dir, "run.json")["status"]);
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Contains(lines, l => l["trials"] is not null && (string?)l["state"] == "passed");
        Assert.All(lines.Where(l => l["trials"] is not null), l => Assert.Equal(2, (int)l["trials"]!["n"]!));
        Assert.Contains(lines, l => (int?)l["trial"] == 0);
        Assert.Contains(lines, l => (int?)l["trial"] == 1);
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> items)
    {
        var result = new List<T>();
        await foreach (var item in items) result.Add(item);
        return result;
    }

    // Intercept only aggregate persistence, forwarding the full store interface to real files.
    public class AggregateFailureStore : System.Reflection.DispatchProxy
    {
        public AgentEval.Output.IOutputStore Inner { get; set; } = null!;
        public CancellationTokenSource? Cancellation { get; set; }
        public string? AggregateId { get; private set; }
        public int AggregateWriteAttempts { get; private set; }

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(AgentEval.Output.IOutputStore.StartRunAsync))
                return Start((AgentEval.Output.SubjectIdentity)args![0]!, (AgentEval.Output.RunContext)args[1]!, (CancellationToken)args[2]!);
            if (method.Name == nameof(AgentEval.Output.IOutputStore.WriteScenarioResultAsync)
                && (string)args![0]! == AggregateId && ++AggregateWriteAttempts == 2)
            {
                if (Cancellation is not null)
                {
                    Cancellation.Cancel();
                    return Task.FromCanceled(Cancellation.Token);
                }
                return Task.FromException(new IOException("Aggregate persistence fixture failure"));
            }
            return method.Invoke(Inner, args);
        }

        private async Task<AgentEval.Output.RunManifest> Start(AgentEval.Output.SubjectIdentity subject,
            AgentEval.Output.RunContext context, CancellationToken token)
        {
            var manifest = await Inner.StartRunAsync(subject, context, token);
            if (context.Harness == "StochasticBenchRunner") AggregateId = manifest.Run.RunId;
            return manifest;
        }
    }

    [Theory]
    [InlineData("owasp", 0, false)]
    [InlineData("owasp", 1, false)]
    [InlineData("mitre", 0, false)]
    [InlineData("mitre", 1, false)]
    [InlineData("nist", 0, false)]
    [InlineData("nist", 1, false)]
    [InlineData("owasp", 0, true)]
    [InlineData("owasp", 1, true)]
    [InlineData("mitre", 0, true)]
    [InlineData("mitre", 1, true)]
    [InlineData("nist", 0, true)]
    [InlineData("nist", 1, true)]
    public async Task BenchScan_InterruptedDuringExecution_RetainsOnlyCompletedProbes(string family, int completed, bool cancelled)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".agenteval"));
        File.WriteAllText(Path.Combine(_root, ".agenteval", "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "scan-failure" }.ToJsonString());
        using var cancellation = new CancellationTokenSource();
        var agent = new InterruptedScanAgent(completed, cancelled ? cancellation : null);
        await using var endpoint = await LocalChatEndpoint.StartAsync("VERDICT: RESISTED\nCONFIDENCE: 0.95\nREASON: test");
        using var judge = AgentEval.Cli.Infrastructure.EndpointFactory.CreateOpenAICompatible(endpoint.Endpoint, "judge", "local-test");
        var aef = new DirectoryInfo(Path.Combine(_root, "aef"));
        var result = family switch
        {
            "mitre" => await BenchMitreCommand.RunAsync("smoke", "subject", _root, null, null, agent,
                judgeClientOverride: judge, aef: aef, ct: cancellation.Token),
            "nist" => await BenchNistCommand.RunAsync("smoke", "subject", _root, null, null, agent,
                judgeClientOverride: judge, aef: aef, ct: cancellation.Token),
            _ => await BenchOwaspCommand.RunAsync("smoke", "subject", _root, null, null, agent,
                judgeClientOverride: judge, aef: aef, ct: cancellation.Token),
        };
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(completed + 1, agent.Calls);
        var dir = RunDirectory(aef.FullName);
        AssertIntact(dir);
        Assert.Equal("aborted", (string?)Document(dir, "run.json")["status"]);
        Assert.Equal("mocked", (string?)Document(dir, "run.json")["execution"]!["targetMode"]);
        Assert.Null(Document(dir, "run.json")["ext"]?["agenteval.store-v1"]);
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
        Assert.Equal(completed, File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Length);
    }

    private sealed class InterruptedScanAgent(int completed, CancellationTokenSource? cancellation) : AgentEval.Core.IEvaluableAgent
    {
        public string Name => "subject";
        public int Calls { get; private set; }
        public Task<AgentEval.Core.AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            if (++Calls > completed)
            {
                if (cancellation is not null)
                {
                    cancellation.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                throw new ScanFixtureFatalException();
            }
            return Task.FromResult(new AgentEval.Core.AgentResponse { Text = "I cannot help with that." });
        }
    }

    private sealed class ScanFixtureFatalException() : AgentEval.Core.FatalEvaluationException("Structural fixture failure");

    [Theory]
    [InlineData("gdpr", false)]
    [InlineData("eu-ai-act", false)]
    [InlineData("agentic", false)]
    [InlineData("gdpr", true)]
    [InlineData("eu-ai-act", true)]
    [InlineData("agentic", true)]
    public async Task Composite_InterruptedDuringEvaluation_RetainsCompletedLeaves(string family, bool cancelled)
    {
        var workspace = Path.Combine(_root, ".agenteval");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "partial-evaluation" }.ToJsonString());
        using var cancellation = new CancellationTokenSource();
        var judge = new StopAfterOneEvaluator(cancelled ? cancellation : null);
        var aef = new DirectoryInfo(Path.Combine(_root, "aef"));
        var exit = family switch
        {
            "gdpr" => await BenchCommand.RunGdprAsync("standard", "subject", _root, "question", judge,
                responseText: "answer", aef: aef, ct: cancellation.Token),
            "eu-ai-act" => await BenchEuAiActCommand.RunAsync("standard", "subject", _root, "question", judge,
                responseText: "answer", aef: aef, ct: cancellation.Token),
            _ => await BenchAgenticCommand.RunAsync("agentic-execution", "subject", _root, "question", "answer", judge,
                budgetTier: "HIGH", aef: aef, ct: cancellation.Token),
        };
        Assert.Equal(1, exit);
        Assert.True(judge.Calls >= 2);
        var dir = RunDirectory(aef.FullName);
        AssertIntact(dir);
        AssertStoreLink(dir);
        Assert.Equal("aborted", (string?)Document(dir, "run.json")["status"]);
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToArray();
        Assert.Contains(lines, l => (string?)l["state"] == "passed" && (string?)l["annotator"]?["kind"] == "LLM");
        Assert.Contains(lines, l => (string?)l["state"] is "error" or "skipped" or "not_measured");
    }

    private sealed class StopAfterOneEvaluator(CancellationTokenSource? cancellation) : AgentEval.Core.IEvaluator
    {
        public int Calls { get; private set; }
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria,
            CancellationToken ct = default)
        {
            if (++Calls == 1) return new PassingEvaluator().EvaluateAsync(input, output, criteria, ct);
            if (cancellation is not null)
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            throw new ScanFixtureFatalException();
        }
    }

    private sealed class CancellingEvaluator(CancellationTokenSource cancellation) : AgentEval.Core.IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not forwarded to the judge.");
        }
    }

    [Theory]
    [InlineData("success", false, false)]
    [InlineData("mock", false, false)]
    [InlineData("report-error", false, false)]
    [InlineData("report-error", true, false)]
    [InlineData("report-error", false, true)]
    [InlineData("cancel", false, false)]
    public async Task Agentic_StoreAndInterruption_RetainsObservedEvidence(string failure, bool noAef, bool invalidDestination)
    {
        var workspace = Path.Combine(_root, ".agenteval");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "agentic-recovery" }.ToJsonString());
        if (failure == "report-error") File.WriteAllText(Path.Combine(workspace, "benchmarks"), "occupied");
        using var cancellation = new CancellationTokenSource();
        AgentEval.Core.IEvaluator judge = failure == "cancel" ? new CancellingEvaluator(cancellation) : new PassingEvaluator();
        var aef = new DirectoryInfo(Path.Combine(_root, "aef"));
        if (invalidDestination) File.WriteAllText(aef.FullName, "occupied");
        var exit = await BenchAgenticCommand.RunAsync("agentic-execution", "subject", _root, "question", failure == "mock" ? null : "answer", judge,
            mock: failure == "mock", budgetTier: "HIGH",
            aef: aef, noAef: noAef, ct: cancellation.Token);
        Assert.Equal(invalidDestination ? 3 : failure == "success" ? 10 : failure == "mock" ? 11 : 1, exit);
        if (noAef || invalidDestination)
        {
            Assert.False(Directory.Exists(aef.FullName));
            return;
        }
        var dir = RunDirectory(aef.FullName);
        AssertIntact(dir);
        if (failure != "mock") AssertStoreLink(dir);
        else Assert.Null(Document(dir, "run.json")["ext"]?["agenteval.store-v1"]);
        Assert.Equal("high", (string?)Document(dir, "run.json")["config"]!["budgetTier"]);
        Assert.Equal(failure == "mock" ? "mocked" : "replayed", (string?)Document(dir, "run.json")["execution"]!["targetMode"]);
        Assert.Equal(failure is "success" or "mock" ? "completed" : "aborted", (string?)Document(dir, "run.json")["status"]);
        Assert.Equal(failure is "success" or "mock", File.Exists(Path.Combine(dir, "gates.ndjson")));
        if (failure is "success" or "mock")
            Assert.Equal(exit, (int)JsonNode.Parse(File.ReadAllLines(Path.Combine(dir, "gates.ndjson")).Single())!["exitCode"]!);
        if (failure != "cancel") Assert.NotEmpty(File.ReadAllLines(Path.Combine(dir, "results.ndjson")));
    }

    [Theory]
    [InlineData("owasp", false, false)]
    [InlineData("mitre", false, false)]
    [InlineData("nist", false, false)]
    [InlineData("owasp", true, false)]
    [InlineData("mitre", true, false)]
    [InlineData("nist", true, false)]
    [InlineData("owasp", false, true)]
    [InlineData("mitre", false, true)]
    [InlineData("nist", false, true)]
    public async Task BenchScan_PersistenceError_RetainsMeasuredScan(string family, bool noAef, bool invalidDestination)
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync("I cannot assist with that request.");
        await using var judgeEndpoint = await LocalChatEndpoint.StartAsync("VERDICT: INCONCLUSIVE\nCONFIDENCE: 0.5\nREASON: test");
        var agent = BenchTier1SutResolver.BuildFromOpenAiEndpoint(endpoint.Endpoint, "local-model", "local-test", "http-subject");
        using var judge = AgentEval.Cli.Infrastructure.EndpointFactory.CreateOpenAICompatible(judgeEndpoint.Endpoint, "local-judge", "local-test");
        Directory.CreateDirectory(Path.Combine(_root, ".agenteval"));
        File.WriteAllText(Path.Combine(_root, ".agenteval", "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "scan-error" }.ToJsonString());
        var aef = new DirectoryInfo(Path.Combine(_root, "aef"));
        if (invalidDestination) File.WriteAllText(aef.FullName, "occupied");
        var original = Console.Out;
        using var output = new FailOnPersistenceWriter();
        try
        {
            Console.SetOut(output);
            var result = family switch
            {
                "owasp" => await BenchOwaspCommand.RunAsync("smoke", agent.Name, _root, null, null, agent,
                    judgeClientOverride: judge, aef: aef, noAef: noAef, agentIsLive: true, agentEndpoint: endpoint.Endpoint),
                "mitre" => await BenchMitreCommand.RunAsync("smoke", agent.Name, _root, null, null, agent,
                    judgeClientOverride: judge, aef: aef, noAef: noAef, agentIsLive: true, agentEndpoint: endpoint.Endpoint),
                _ => await BenchNistCommand.RunAsync("smoke", agent.Name, _root, null, null, agent,
                    judgeClientOverride: judge, aef: aef, noAef: noAef, agentIsLive: true, agentEndpoint: endpoint.Endpoint),
            };
            Assert.Equal(invalidDestination ? 3 : 1, result.ExitCode);
        }
        finally { Console.SetOut(original); (agent as IDisposable)?.Dispose(); }
        if (noAef || invalidDestination)
        {
            Assert.False(Directory.Exists(aef.FullName));
            return;
        }
        var dir = RunDirectory(aef.FullName);
        AssertIntact(dir);
        Assert.Equal(endpoint.Endpoint.TrimEnd('/'), ((string)Document(dir, "run.json")["deployment"]!["endpoint"]!).TrimEnd('/'));
        AssertStoreLink(dir);
        Assert.Equal("aborted", (string?)Document(dir, "run.json")["status"]);
        Assert.NotEmpty(File.ReadAllLines(Path.Combine(dir, "results.ndjson")));
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
    }

    private void AssertCompositeStoreLinks(string directory, int trials)
    {
        var manifests = Directory.GetFiles(Path.Combine(_root, ".agenteval"), "manifest.json", SearchOption.AllDirectories)
            .Select(path => JsonNode.Parse(File.ReadAllText(path))!).ToList();
        var link = Document(directory, "run.json")["ext"]!["agenteval.store-v1"]!;
        var trialIds = link["trialRunIds"]!.AsArray().Select(id => (string)id!).ToArray();
        Assert.Equal(trials, trialIds.Length);
        Assert.Equal(trials, trialIds.Distinct().Count());
        var aggregate = (string)link["runId"]!;
        Assert.DoesNotContain(aggregate, trialIds);
        Assert.Equal(manifests.Select(m => (string)m["run"]!["runId"]!).Order(),
            trialIds.Append(aggregate).Order());
    }

    private void AssertStoreLink(string directory)
    {
        var manifests = Directory.GetFiles(Path.Combine(_root, ".agenteval"), "manifest.json", SearchOption.AllDirectories);
        var link = Document(directory, "run.json")["ext"]?["agenteval.store-v1"];
        if (manifests.Length == 0) Assert.Null(link);
        else
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Assert.Single(manifests)))!;
            Assert.Equal((string?)manifest["run"]!["runId"], (string?)link?["runId"]);
            Assert.NotNull(link);
        }
    }

    [Theory]
    [InlineData("gdpr", false)]
    [InlineData("eu-ai-act", false)]
    [InlineData("agentic", false)]
    [InlineData("owasp", false)]
    [InlineData("mitre", false)]
    [InlineData("nist", false)]
    [InlineData("gdpr", true)]
    [InlineData("eu-ai-act", true)]
    [InlineData("agentic", true)]
    [InlineData("owasp", true)]
    [InlineData("mitre", true)]
    [InlineData("nist", true)]
    public async Task Bench_OutputFailureAfterPublication_KeepsOneCompletedRun(string family, bool diagnostic)
    {
        var workspace = Path.Combine(_root, ".agenteval");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "output-failure" }.ToJsonString());
        await using var endpoint = await LocalChatEndpoint.StartAsync("Call 202-555-0123.");
        await using var judgeEndpoint = await LocalChatEndpoint.StartAsync("VERDICT: SUCCEEDED\nCONFIDENCE: 0.95\nREASON: test");
        var agent = BenchTier1SutResolver.BuildFromOpenAiEndpoint(endpoint.Endpoint, "local-model", "local-test", "subject");
        using var judge = AgentEval.Cli.Infrastructure.EndpointFactory.CreateOpenAICompatible(judgeEndpoint.Endpoint, "local-judge", "local-test");
        var aef = new DirectoryInfo(Path.Combine(_root, "aef"));
        var original = diagnostic ? Console.Error : Console.Out;
        using var output = new FailOnOutputWriter(diagnostic ? "AEF run:" : "Overall result:");
        int exit;
        try
        {
            if (diagnostic) Console.SetError(output); else Console.SetOut(output);
            exit = family switch
            {
                "gdpr" => await BenchCommand.RunGdprAsync("standard", "subject", _root, "question",
                    evaluatorOverride: new PassingEvaluator(), responseText: "answer", aef: aef),
                "eu-ai-act" => await BenchEuAiActCommand.RunAsync("standard", "subject", _root, "question",
                    evaluatorOverride: new PassingEvaluator(), responseText: "answer", aef: aef),
                "agentic" => await BenchAgenticCommand.RunAsync("agentic-execution", "subject", _root, "question", "answer",
                    new PassingEvaluator(), budgetTier: "HIGH", aef: aef),
                "mitre" => (await BenchMitreCommand.RunAsync("smoke", "subject", _root, null, null, agent,
                    judgeClientOverride: judge, aef: aef, agentIsLive: true)).ExitCode,
                "nist" => (await BenchNistCommand.RunAsync("smoke", "subject", _root, null, null, agent,
                    judgeClientOverride: judge, aef: aef, agentIsLive: true)).ExitCode,
                _ => (await BenchOwaspCommand.RunAsync("smoke", "subject", _root, null, null, agent,
                    judgeClientOverride: judge, aef: aef, agentIsLive: true)).ExitCode,
            };
        }
        finally
        {
            if (diagnostic) Console.SetError(original); else Console.SetOut(original);
            (agent as IDisposable)?.Dispose();
        }
        Assert.True(output.Failed, "The fixture must reach the post-publication output.");
        var dir = RunDirectory(aef.FullName); // Exactly one run, never a completed + aborted pair.
        AssertIntact(dir);
        Assert.Equal("completed", (string?)Document(dir, "run.json")["status"]);
        var gate = JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(dir, "gates.ndjson"))))!;
        Assert.Equal(diagnostic ? (int)gate["exitCode"]! : 1, exit);
        Assert.DoesNotContain("not written", output.ToString());
    }

    [Theory]
    [InlineData(false, "AEF run:")]
    [InlineData(false, "AEF note:")]
    [InlineData(true, "AEF run:")]
    [InlineData(true, "AEF note:")]
    public async Task Native_SavedDiagnosticFailure_PreservesEvidenceAndExit(bool redteam, string prefix)
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync(redteam ? "I cannot help with that." : "Hi");
        using var output = new FailOnOutputWriter(prefix);
        var original = Console.Error;
        string root;
        try
        {
            Console.SetError(output);
            if (redteam)
            {
                var options = new RedTeamOptions
                {
                    Endpoint = endpoint.Endpoint, Model = "local-model", ApiKey = "local-test",
                    Attacks = "PromptInjection", Intensity = "quick", MaxProbes = 1,
                    Format = "json", Output = new FileInfo(Path.Combine(_root, "report.json")),
                    Aef = new DirectoryInfo(Path.Combine(_root, "aef")), Quiet = false, FailOn = "never",
                };
                Assert.Equal(0, await RedTeamCommand.ExecuteAsync(options, CancellationToken.None));
                root = options.Aef.FullName;
            }
            else
            {
                var options = Options(endpoint.Endpoint, quiet: false, subjectVersion: null);
                Assert.Equal(0, await EvalCommand.ExecuteAsync(options, CancellationToken.None));
                root = options.Aef!.FullName;
            }
        }
        finally { Console.SetError(original); }
        Assert.True(output.Failed);
        var dir = RunDirectory(root);
        AssertIntact(dir);
        Assert.Equal("completed", (string?)Document(dir, "run.json")["status"]);
        Assert.DoesNotContain("not written", output.ToString());
        Assert.DoesNotContain("could not be completed", output.ToString());
    }

    private sealed class FailOnOutputWriter(string prefix) : StringWriter
    {
        public bool Failed { get; private set; }
        public override void WriteLine(string? value)
        {
            if (value?.TrimStart().StartsWith(prefix, StringComparison.Ordinal) == true)
            {
                Failed = true;
                throw new IOException("Test output failure after evidence publication");
            }
            base.WriteLine(value);
        }
    }

    private sealed class FailOnPersistenceWriter : StringWriter
    {
        public override void WriteLine(string? value)
        {
            if (value?.StartsWith("Persisted run ", StringComparison.Ordinal) == true)
                throw new IOException("Test persistence failure after measurement");
            base.WriteLine(value);
        }
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
        Assert.Equal(AgentEval.Models.ModelPricing.TableName, (string?)Document(dir, "run.json")["costPolicy"]!["priceTable"]);
        Assert.Null(Document(dir, "run.json")["costPolicy"]!["maxUsd"]);
        var report = AefRunVerifier.Verify(dir);
        Assert.Equal(AefOutcome.Intact, report.Outcome);
        Assert.Empty(report.Problems);
    }
}
