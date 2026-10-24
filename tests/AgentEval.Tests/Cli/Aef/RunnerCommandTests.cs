// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Cli.Runner;
using AgentEval.Results;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runner;
using AgentEval.Results.Schemas;
using AgentEval.Tests.TestHelpers;

namespace AgentEval.Tests.Cli.Aef;

public sealed class RunnerCommandTests : IDisposable
{
    [Theory]
    [InlineData("other:subject", "1", 2)]
    [InlineData("model:gpt-4o-mini", "different", 2)]
    [InlineData("model:gpt-4o-mini", "1", 0)]
    public async Task Run_IdentityAssertions_PreservePlanAndRejectMismatchBeforeRequests(string subject, string version, int expected)
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync("Hi");
        var path = Path.Combine(_root, "plan.json");
        var bytes = Encoding.UTF8.GetBytes(CreatePlan(endpoint.Endpoint).ToJsonString());
        File.WriteAllBytes(path, bytes);
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(expected, await RunCommand.ExecuteAsync(path, _root, "test", true, output, error,
            environment: _ => null, dryRun: true, subject: subject, subjectVersion: version));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.Combine(_root, ".agenteval")));
    }
    private readonly string _root = Path.Combine(Path.GetTempPath(), "runner-command-" + Guid.NewGuid().ToString("N"));
    public RunnerCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        var jobs = Path.Combine(_root, ".agenteval", "aef", "jobs");
        if (Directory.Exists(jobs))
            foreach (var job in Directory.GetDirectories(jobs))
                AefRunnerFixtures.Retain(job, Path.GetFileName(_root) + "-" + Path.GetFileName(job));
        Directory.Delete(_root, true);
    }

    [Fact]
    public void Manifest_IdentityPrecedence_UsesOptionThenEnvironmentThenConfiguration()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".agenteval"));
        File.WriteAllText(Path.Combine(_root, ".agenteval", "runner.json"), """{"runnerId":"configured","tags":["team:test"]}""");
        Assert.Equal("explicit", (string?)RunnerManifest.Build("explicit", _ => "environment", _root)["runnerId"]);
        Assert.Equal("environment", (string?)RunnerManifest.Build(null, _ => "environment", _root)["runnerId"]);
        var manifest = RunnerManifest.Build(null, _ => null, _root);
        Assert.Equal("configured", (string?)manifest["runnerId"]);
        Assert.Equal("team:test", (string?)manifest["tags"]![0]);
        Assert.True(AefSchemas.Writer.IsValid("runner", manifest));
        Assert.Equal("local", (string?)manifest["providers"]![0]);
        Assert.Equal("live", (string?)manifest["targetModes"]![0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("secret-invalid id")]
    public void Manifest_MissingOrInvalidIdentity_RefusesWithoutHostnameFallback(string? id)
    {
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        Assert.Equal(2, RunnerManifest.Print(id, true, stdout, stderr, _ => null, _root));
        Assert.Equal("", stdout.ToString());
        Assert.DoesNotContain("secret", stderr.ToString());
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void Manifest_JsonOutput_ContainsOnlyAValidatedManifest()
    {
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        Assert.Equal(0, RunnerManifest.Print("test-runner", true, stdout, stderr, _ => null, _root));
        Assert.True(AefSchemas.Writer.IsValid("runner", JsonNode.Parse(stdout.ToString())));
        Assert.Equal("", stderr.ToString());
    }

    [Theory]
    [InlineData("sealed", 0, "job.sealed", 2)]
    [InlineData("refused", 3, "job.refused", 0)]
    [InlineData("budget", 8, "job.failed", 0)]
    [InlineData("timeout", 11, "job.failed", 0)]
    public async Task Run_PlanOutcome_PreservesInputsAndReturnsDocumentedCode(string scenario, int exit, string terminal, int calls)
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync("Hi");
        var plan = CreatePlan(endpoint.Endpoint);
        if (scenario == "refused") plan["ext"]!["agenteval.run"]!["model"] = "unknown-model";
        if (scenario == "budget") plan["limits"]!["maxUsd"] = 0;
        if (scenario == "timeout")
        {
            plan["limits"]!["timeout"] = "PT1M";
            plan["ext"]!["agenteval.run"]!["preparationSeconds"] = 60;
        }
        var bytes = Encoding.UTF8.GetBytes(plan.ToJsonString() + "\n  ");
        var path = Path.Combine(_root, "plan.json"); File.WriteAllBytes(path, bytes);
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        Assert.Equal(exit, await RunCommand.ExecuteAsync(path, _root, "test-runner", true, stdout, stderr,
            environment: _ => null, home: Path.Combine(_root, "home")));
        Assert.Equal("", stderr.ToString());
        var report = JsonNode.Parse(stdout.ToString())!;
        Assert.Equal(terminal, (string?)report["terminal"]);
        var job = Path.Combine(_root, ".agenteval", "aef", "jobs", (string)report["jobId"]!);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(job, "plan.json")));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(job, "runner.json")));
        Assert.True(AefSchemas.Writer.IsValid("runner", manifest));
        var events = File.ReadAllLines(Path.Combine(job, "events.ndjson")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Empty(RunnerEventStream.Verify(events, plan));
        Assert.Equal(calls, endpoint.Requests.Count);
        foreach (var run in report["runs"]!.AsArray())
            Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(Path.Combine(job, "runs", (string)run!)).Outcome);
        Assert.DoesNotContain("private-input", string.Concat(Directory.GetFiles(job, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
#if NET10_0_OR_GREATER
        if (scenario == "sealed")
        {
            var source = new AgentEval.MissionControl.Services.WorkspaceRunSource(
                new AgentEval.Output.FileSystemOutputStore(Path.Combine(_root, ".agenteval")),
                new AgentEval.Results.Adapters.Reading.AefWorkspaceEvidenceReader());
            var catalog = await source.ReadAsync(default);
            foreach (var saved in report["runs"]!.AsArray())
            {
                var directory = Path.Combine(job, "runs", (string)saved!);
                await MissionControlProducerAssertions.ReadAsync(directory);
                var read = new AgentEval.Results.Adapters.Reading.AefRunEvidenceReader().Read(directory);
                Assert.Contains(catalog, run => run.Evidence.Info.RunId == read.Info.RunId);
            }
        }
#endif
    }

    private JsonObject CreatePlan(string endpoint)
    {
        var inputs = Path.Combine(_root, "inputs"); Directory.CreateDirectory(inputs);
        File.WriteAllText(Path.Combine(inputs, "cases.json"), """[{"id":"original/α","input":"private-input","expected":"Hi"}]""");
        var plan = JsonNode.Parse("""
            {"schemaVersion":"1.0","planId":"cli-test","subject":{"ref":"model:gpt-4o-mini","version":"1"},
             "suites":[{"ref":"dataset:inputs/cases.json","version":"1","lane":"quality"}],
             "limits":{"maxUsd":1},"contentCapture":"off","targetMode":"live","isolation":"process","provider":"local",
             "ext":{"agenteval.run":{"inputCap":8192,"outputCap":100,"caseSeconds":30}}}
            """)!.AsObject();
        plan["subject"]!["endpoint"] = endpoint;
        return plan;
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(true, 0, 3)]
    [InlineData(true, 1, 0)]
    public async Task DryRun_BudgetPolicy_EstimatesWithoutRequestsOrWrites(bool upfront, double cap, int expectedExit)
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync("Hi");
        var path = Path.Combine(_root, "plan.json");
        File.WriteAllText(path, CreatePlan(endpoint.Endpoint).ToJsonString());
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        Assert.Equal(expectedExit, await RunCommand.ExecuteAsync(path, _root, "test-runner", true, stdout, stderr,
            environment: _ => null, home: _root, dryRun: true, maxUsd: cap, refuseOverBudget: upfront));
        var report = JsonNode.Parse(stdout.ToString())!;
        Assert.Equal(expectedExit == 0, (bool)report["accepted"]!);
        if (expectedExit == 0)
        {
            Assert.Equal(1, (int)report["cases"]!);
            Assert.Equal(4, (int)report["calls"]!["target"]!);
            Assert.Equal(0, (int)report["calls"]!["judge"]!);
            Assert.Equal(65, (int)report["durationRange"]!["high"]!);
            Assert.True((double)report["costRange"]!["high"]! > 0);
            Assert.Equal(cap, (double)report["effectiveMaxUsd"]!);
        }
        else Assert.Contains("estimated upper cost", (string?)report["reason"]);
        Assert.Empty(endpoint.Requests);
        Assert.Equal("", stderr.ToString());
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var (file, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.False(Directory.Exists(Path.Combine(_root, ".agenteval")));
    }

    [Fact]
    public async Task Progress_LoweredCap_ExactlyMatchesPersistedEventsAndKeepsPlanBytes()
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync("Hi");
        var path = Path.Combine(_root, "plan.json");
        var bytes = Encoding.UTF8.GetBytes(CreatePlan(endpoint.Endpoint).ToJsonString() + "\n ");
        File.WriteAllBytes(path, bytes);
        using var stdout = new StringWriter(); using var stderr = new StringWriter(); using var progress = new MemoryStream();
        Assert.Equal(0, await RunCommand.ExecuteAsync(path, _root, "test-runner", false, stdout, stderr,
            environment: _ => null, home: _root, progress: "ndjson", maxUsd: 0.01, progressStream: progress));
        Assert.Equal("", stdout.ToString()); Assert.Equal("", stderr.ToString());
        var eventsPath = Assert.Single(Directory.GetFiles(_root, "events.ndjson", SearchOption.AllDirectories));
        Assert.Equal(File.ReadAllBytes(eventsPath), progress.ToArray());
        var job = Path.GetDirectoryName(eventsPath)!;
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(job, "plan.json")));
        Assert.Equal(0.01, (double)JsonNode.Parse(File.ReadAllText(Path.Combine(job, "runner-options.json")))!["maxUsd"]!);
        var events = File.ReadAllLines(eventsPath).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Empty(RunnerEventStream.Verify(events, JsonNode.Parse(bytes)));
        var run = Path.GetDirectoryName(Assert.Single(Directory.GetFiles(job, "run.json", SearchOption.AllDirectories)))!;
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(run).Outcome);
        Assert.Equal(0.01, (double)JsonNode.Parse(File.ReadAllText(Path.Combine(run, "run.json")))!["costPolicy"]!["maxUsd"]!);
    }

    [Fact]
    public async Task DryRun_MultipleSuitesAndJudge_RespectsCaseLimitAndIncludesEveryReadinessRole()
    {
        var plan = CreatePlan("http://127.0.0.1:1/v1"); // No listener: a dry-run must never open it.
        File.WriteAllText(Path.Combine(_root, "inputs", "second.json"),
            """[{"id":"second-a","input":"A"},{"id":"second-b","input":"B"}]""");
        plan["suites"]!.AsArray().Add(new JsonObject { ["ref"] = "dataset:inputs/second.json", ["version"] = "1" });
        plan["limits"]!["cases"] = 2;
        plan["judges"] = new JsonArray(new JsonObject { ["model"] = "gpt-4o" });
        plan["ext"]!["agenteval.run"]!["maxCalls"] = 3;
        plan["ext"]!["agenteval.run"]!["judge"] = new JsonObject { ["maxCalls"] = 4 };
        var path = Path.Combine(_root, "plan.json"); File.WriteAllText(path, plan.ToJsonString());
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        Assert.Equal(0, await RunCommand.ExecuteAsync(path, _root, "test-runner", true, stdout, stderr,
            environment: _ => null, home: _root, dryRun: true));
        var report = JsonNode.Parse(stdout.ToString())!;
        Assert.Equal(2, (int)report["cases"]!);
        Assert.Equal(12, (int)report["calls"]!["target"]!);
        Assert.Equal(14, (int)report["calls"]!["judge"]!);
        Assert.Equal(130, (int)report["durationRange"]!["high"]!);
        Assert.Equal(2, report["models"]!.AsArray().Count);
        Assert.Equal(2, report["suites"]!.AsArray().Count);
        Assert.All(report["suites"]!.AsArray(), suite =>
        {
            Assert.Equal("definition", (string?)suite!["identityScope"]);
            Assert.StartsWith("sha256:", (string?)suite["digest"]);
        });
        Assert.False(Directory.Exists(Path.Combine(_root, ".agenteval")));
    }

    [Theory]
    [InlineData("text", false, false, 1)]
    [InlineData("ndjson", true, false, 1)]
    [InlineData("ndjson", false, true, 1)]
    [InlineData(null, false, true, double.NaN)]
    [InlineData(null, false, true, -1)]
    public async Task Run_InvalidOptions_RefusesBeforeReadingOrWriting(string? progress, bool json, bool dryRun, double cap)
    {
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        Assert.Equal(2, await RunCommand.ExecuteAsync("missing-plan", _root, "test-runner", json, stdout, stderr,
            dryRun: dryRun, progress: progress, maxUsd: cap));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
        Assert.Equal("", stdout.ToString());
        Assert.Contains("--progress", stderr.ToString());
    }

    [Fact]
    public void Parse_DryRun_IsRecognizedBeforeDiagnosticInitialization()
    {
        var command = RunCommand.Create();
        Assert.True(RunCommand.IsDryRun(command.Parse("--plan missing.json --dry-run --json")));
        Assert.False(RunCommand.IsDryRun(command.Parse("--plan missing.json --progress ndjson")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_UnreadablePlan_ReturnsTwoWithoutJobFiles(bool malformed)
    {
        var path = Path.Combine(_root, "plan.json");
        if (malformed) File.WriteAllText(path, "secret-invalid-json");
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        Assert.Equal(2, await RunCommand.ExecuteAsync(path, _root, "test-runner", true, stdout, stderr,
            environment: _ => null, home: _root));
        Assert.Equal("", stdout.ToString());
        Assert.DoesNotContain("secret", stderr.ToString());
        Assert.False(Directory.Exists(Path.Combine(_root, ".agenteval")));
    }

    [Theory]
    [InlineData("job.sealed", null, 0)]
    [InlineData("job.refused", null, 3)]
    [InlineData("job.cancelled", null, 3)]
    [InlineData("job.failed", null, 3)]
    [InlineData("job.failed", "maxUsd", 8)]
    [InlineData("job.failed", "cases", 11)]
    [InlineData("job.failed", "timeout", 11)]
    public void ExitCode_TerminalOutcome_UsesDocumentedMapping(string terminal, string? limit, int expected) =>
        Assert.Equal(expected, RunCommand.ExitCode(new("test", 1, terminal, limit, null, [], DateTimeOffset.UtcNow)));
}
