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
    private readonly string _root = Path.Combine(Path.GetTempPath(), "runner-command-" + Guid.NewGuid().ToString("N"));
    public RunnerCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

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
        var inputs = Path.Combine(_root, "inputs"); Directory.CreateDirectory(inputs);
        File.WriteAllText(Path.Combine(inputs, "cases.json"), """[{"id":"original/α","input":"private-input","expected":"Hi"}]""");
        var plan = JsonNode.Parse("""
            {"schemaVersion":"1.0","planId":"cli-test","subject":{"ref":"model:gpt-4o-mini","version":"1"},
             "suites":[{"ref":"dataset:inputs/cases.json","version":"1","lane":"quality"}],
             "limits":{"maxUsd":1},"contentCapture":"off","targetMode":"live","isolation":"process","provider":"local",
             "ext":{"agenteval.run":{"inputCap":8192,"outputCap":100,"caseSeconds":30}}}
            """)!.AsObject();
        plan["subject"]!["endpoint"] = endpoint.Endpoint;
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
