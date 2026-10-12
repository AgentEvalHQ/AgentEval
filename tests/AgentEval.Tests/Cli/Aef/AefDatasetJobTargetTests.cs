// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Cli.Runner;
using AgentEval.Results;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runner;
using AgentEval.Tests.TestHelpers;

namespace AgentEval.Tests.Cli.Aef;

public sealed class AefDatasetJobTargetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aef-live-job-" + Guid.NewGuid().ToString("N"));
    private string PlanDirectory => Path.Combine(_root, "plans");
    private string Dataset => Path.Combine(_root, "inputs", "cases.json");
    private string Output => Path.Combine(_root, "job");
    private static readonly AefTime Start = AefTime.Parse("2026-10-12T12:00:00Z");
    private JsonObject? _plan;
    private bool _expectValidStream = true;
    public AefDatasetJobTargetTests()
    {
        Directory.CreateDirectory(PlanDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(Dataset)!);
        File.WriteAllText(Dataset, """[{"id":"original/α","input":"secret-prompt","expected":"Hi"}]""");
    }
    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("AEF_RUNNER_FIXTURES") is { Length: > 0 } fixtures
            && _plan is not null && Directory.Exists(Path.Combine(Output, "runs")))
        {
            var destination = Path.Combine(fixtures, Path.GetFileName(_root));
            foreach (var file in Directory.GetFiles(Output, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(Output, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            File.WriteAllBytes(Path.Combine(destination, "plan.json"), Bytes(_plan));
            File.WriteAllBytes(Path.Combine(destination, "runner.json"), Bytes(Manifest()));
            File.WriteAllText(Path.Combine(destination, "expected-stream.txt"), _expectValidStream ? "valid" : "budget-violation");
        }
        Directory.Delete(_root, true);
    }
    private static byte[] Bytes(JsonObject plan) => Encoding.UTF8.GetBytes(plan.ToJsonString());
    private static JsonObject Manifest() => JsonNode.Parse("""
        {"schemaVersion":"1.0","runnerId":"local-test","kind":"local","os":"linux",
         "runtime":{"name":"dotnet","version":"10"},"providers":["local"],"tags":[],"targetModes":["live"],"version":"1.0.0"}
        """)!.AsObject();
    private JsonObject Plan(string endpoint)
    {
        var plan = JsonNode.Parse("""
            {"schemaVersion":"1.0","planId":"dataset-test","subject":{"ref":"model:gpt-4o-mini","version":"1"},
             "suites":[{"ref":"dataset:../inputs/cases.json","version":"1","lane":"quality"}],
             "limits":{"maxUsd":1},"contentCapture":"off","targetMode":"live","isolation":"process","provider":"local",
             "ext":{"agenteval.run":{"inputCap":8192,"outputCap":100,"caseSeconds":30}}}
            """)!.AsObject();
        plan["subject"]!["endpoint"] = endpoint;
        plan["suites"]![0]!["digest"] = AefEvalWriter.DatasetDigest(File.ReadAllBytes(Dataset));
        return _plan = plan;
    }
    private List<JsonNode> Events() => File.ReadAllLines(Path.Combine(Output, "events.ndjson")).Select(l => JsonNode.Parse(l)!).ToList();
    private string RunDirectory(AefJobResult result) => Path.Combine(Output, "runs", Assert.Single(result.Runs));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_DatasetSnapshot_UsesOriginalIdsAndSealsLiveEvidence(bool azure)
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync("Hi secret-response");
        var plan = Plan(azure ? endpoint.Endpoint[..^3] : endpoint.Endpoint);
        if (azure)
        {
            plan["ext"]!["agenteval.run"]!["azure"] = true;
            plan["ext"]!["agenteval.run"]!["deployment"] = "test-deployment";
            plan["credentialRefs"] = new JsonArray(new JsonObject
            { ["name"] = "SUBJECT_KEY", ["scheme"] = "env", ["path"] = "runner-key", ["purpose"] = "subject" });
        }
        var bytes = Bytes(plan);
        var target = await AefDatasetJobTarget.LoadAsync(bytes, PlanDirectory);
        Assert.Null(target.RefusalReason);
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        var options = new AefJobOptions { At = Start, Environment = _ => "secret-scoped" };
        var analysis = AefJobRunner.Analyze(bytes, Manifest(), target, options);
        Assert.True(analysis.Accepted, analysis.Reason);
        Assert.Equal(1, analysis.Estimate!.Cases);
        Assert.Empty(endpoint.Requests);
        Assert.Equal(before, Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        // The file may change after resolution; the job must execute the bytes whose digest it accepted.
        File.WriteAllText(Dataset, "changed after loading");
        var result = await AefJobRunner.RunAsync(bytes, Manifest(), target, Output, options);

        Assert.Equal("job.sealed", result.Terminal);
        Assert.Equal(2, endpoint.Requests.Count); // readiness + case
        var directory = RunDirectory(result);
        var root = JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(directory, "results.ndjson"))))!;
        Assert.Equal("original/α", (string?)root["caseId"]);
        Assert.Equal("passed", (string?)root["state"]);
        var header = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "run.json")))!;
        Assert.Equal("live", (string?)header["execution"]!["targetMode"]);
        Assert.Equal((string?)plan["suites"]![0]!["digest"], (string?)header["suite"]!["digest"]);
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "summary.json")))!;
        Assert.Equal(20, (int)summary["usage"]![0]!["gen_ai.usage.input_tokens"]!);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
        Assert.Empty(RunnerEventStream.Verify(Events(), plan));
        Assert.DoesNotContain("secret", string.Concat(Directory.GetFiles(Output, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
    }

    [Fact]
    public async Task RunAsync_JudgeAndSubject_ReceiveDistinctScopedKeysAndRecordActualJudge()
    {
        File.WriteAllText(Dataset, """[{"id":"c1","input":"secret-prompt","evaluationCriteria":["correct"]}]""");
        await using var subject = await LocalChatEndpoint.StartAsync("Hi");
        await using var judge = await LocalChatEndpoint.StartAsync("""{"overallScore":90,"summary":"good","criteriaResults":[{"criterion":"1. correct","met":true,"explanation":"secret-reason"}]}""");
        var plan = Plan(subject.Endpoint);
        plan["judges"] = new JsonArray(new JsonObject { ["model"] = "gpt-4o-mini" });
        plan["ext"]!["agenteval.run"]!["judge"] = new JsonObject { ["endpoint"] = judge.Endpoint };
        plan["credentialRefs"] = new JsonArray(
            new JsonObject { ["name"] = "SUBJECT_API_KEY", ["scheme"] = "env", ["path"] = "subject-ref", ["purpose"] = "subject" },
            new JsonObject { ["name"] = "JUDGE_API_KEY", ["scheme"] = "env", ["path"] = "judge-ref", ["purpose"] = "judge" });
        var bytes = Bytes(plan);
        var target = await AefDatasetJobTarget.LoadAsync(bytes, PlanDirectory);
        var result = await AefJobRunner.RunAsync(bytes, Manifest(), target, Output,
            new AefJobOptions { At = Start, Environment = name => name == "subject-ref" ? "secret-subject-key" : "secret-judge-key" });
        Assert.Equal("job.sealed", result.Terminal);
        Assert.Equal(2, subject.Requests.Count);
        Assert.Equal(2, judge.Requests.Count);
        Assert.All(subject.AuthorizationHeaders, h => Assert.Equal("Bearer secret-subject-key", h));
        Assert.All(judge.AuthorizationHeaders, h => Assert.Equal("Bearer secret-judge-key", h));
        var directory = RunDirectory(result);
        var header = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "run.json")))!;
        Assert.Equal("gpt-4o-mini", (string?)header["judges"]![0]!["model"]);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(directory, "results.ndjson")).Length);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
        Assert.Empty(RunnerEventStream.Verify(Events(), plan));
        Assert.DoesNotContain("secret", string.Concat(Directory.GetFiles(Output, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
    }

    [Theory]
    [InlineData("unknown-price")]
    [InlineData("missing-id")]
    [InlineData("digest-mismatch")]
    [InlineData("tool-expectations")]
    [InlineData("criteria-without-judge")]
    [InlineData("unknown-field")]
    public async Task RunAsync_UnresolvablePlan_RefusesBeforeAnyHttpCall(string issue)
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync();
        if (issue == "missing-id") File.WriteAllText(Dataset, """[{"input":"private-input"}]""");
        if (issue == "tool-expectations") File.WriteAllText(Dataset, """[{"id":"c1","input":"private-input","expectedTools":["Buy"]}]""");
        if (issue == "criteria-without-judge") File.WriteAllText(Dataset, """[{"id":"c1","input":"private-input","evaluationCriteria":["correct"]}]""");
        var plan = Plan(endpoint.Endpoint);
        if (issue == "unknown-price") plan["ext"]!["agenteval.run"]!["model"] = "unknown-gpt-4o-mini-alias";
        if (issue == "unknown-field") plan["ext"]!["agenteval.run"]!["apiKey"] = "secret-key";
        if (issue == "digest-mismatch") plan["suites"]![0]!["digest"] = "sha256:" + new string('0', 64);
        var bytes = Bytes(plan);
        var target = await AefDatasetJobTarget.LoadAsync(bytes, PlanDirectory);
        Assert.False(AefJobRunner.Analyze(bytes, Manifest(), target, new AefJobOptions { At = Start }).Accepted);
        var result = await AefJobRunner.RunAsync(bytes, Manifest(), target, Output, new AefJobOptions { At = Start });
        Assert.Equal("job.refused", result.Terminal);
        Assert.Empty(endpoint.Requests);
        Assert.Single(Events());
        Assert.Empty(result.Runs);
        Assert.DoesNotContain("secret", result.Reason ?? "");
        Assert.DoesNotContain("private-input", result.Reason ?? "");
    }

    [Fact]
    public async Task RunAsync_InsufficientBudget_StopsBeforeReadiness()
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync();
        var plan = Plan(endpoint.Endpoint);
        plan["limits"]!["maxUsd"] = 0;
        var bytes = Bytes(plan);
        var target = await AefDatasetJobTarget.LoadAsync(bytes, PlanDirectory);
        var result = await AefJobRunner.RunAsync(bytes, Manifest(), target, Output, new AefJobOptions { At = Start });
        Assert.Equal("job.failed", result.Terminal);
        Assert.Equal("maxUsd", result.Limit);
        Assert.Empty(endpoint.Requests);
        Assert.Empty(result.Runs);
    }

    [Fact]
    public async Task RunAsync_ProviderOmitsUsage_ChargesBoundsWithoutInventingTokenCounts()
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync((_, _) => Task.FromResult("Hi"), usage: _ => null);
        var plan = Plan(endpoint.Endpoint);
        var bytes = Bytes(plan);
        var target = await AefDatasetJobTarget.LoadAsync(bytes, PlanDirectory);
        var estimate = AefJobRunner.Analyze(bytes, Manifest(), target, new AefJobOptions { At = Start }).Estimate!;
        var result = await AefJobRunner.RunAsync(bytes, Manifest(), target, Output, new AefJobOptions { At = Start });
        Assert.Equal("job.sealed", result.Terminal);
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(RunDirectory(result), "summary.json")))!;
        Assert.Equal(estimate.UsdHigh, (double)summary["cost"]!["totalUsd"]!, precision: 12);
        Assert.Null(summary["usage"]![0]!["gen_ai.usage.input_tokens"]);
        Assert.Null(summary["usage"]![0]!["gen_ai.usage.output_tokens"]);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory(result)).Outcome);
        Assert.Empty(RunnerEventStream.Verify(Events(), plan));
    }

    [Fact]
    public async Task RunAsync_ReadinessFails_RetainsPreparationSpendAndDoesNotRunCases()
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync((_, _) => Task.FromResult("unused"), statusCode: 503);
        var plan = Plan(endpoint.Endpoint);
        var bytes = Bytes(plan);
        var target = await AefDatasetJobTarget.LoadAsync(bytes, PlanDirectory);
        var result = await AefJobRunner.RunAsync(bytes, Manifest(), target, Output, new AefJobOptions { At = Start });
        Assert.Equal("job.failed", result.Terminal);
        Assert.Single(endpoint.Requests);
        Assert.Empty(File.ReadAllLines(Path.Combine(RunDirectory(result), "results.ndjson")));
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(RunDirectory(result), "summary.json")))!;
        Assert.Equal(target.Suites[0].PreparationUsdBound, (double)summary["cost"]!["totalUsd"]!, precision: 12);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory(result)).Outcome);
        Assert.Empty(RunnerEventStream.Verify(Events(), plan));
    }

    [Fact]
    public async Task RunAsync_ProviderExceedsBudget_StopsAfterCurrentCallAndReportsActualLimit()
    {
        _expectValidStream = false; // Honest evidence of a provider violating its bound must fail the budget verifier.
        File.WriteAllText(Dataset, """[{"id":"first","input":"first"},{"id":"second","input":"second"}]""");
        var count = 0;
        await using var endpoint = await LocalChatEndpoint.StartAsync((_, _) => Task.FromResult("Hi"),
            usage: _ => Interlocked.Increment(ref count) == 1 ? (10L, 5L) : (10L, 1_000_000L));
        var plan = Plan(endpoint.Endpoint);
        plan["limits"]!["maxUsd"] = 0.01;
        var bytes = Bytes(plan);
        var target = await AefDatasetJobTarget.LoadAsync(bytes, PlanDirectory);
        var result = await AefJobRunner.RunAsync(bytes, Manifest(), target, Output, new AefJobOptions { At = Start });
        Assert.Equal("job.failed", result.Terminal);
        Assert.Equal("maxUsd", result.Limit);
        Assert.Equal(2, endpoint.Requests.Count);
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(RunDirectory(result), "summary.json")))!;
        Assert.True((double)summary["cost"]!["totalUsd"]! > 0.01);
        Assert.Equal(1_000_005, (long)summary["usage"]![0]!["gen_ai.usage.output_tokens"]!);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory(result)).Outcome);
    }

    [Fact]
    public async Task RunAsync_CaseDeadlineExpires_SealsAnErrorAndStops()
    {
        var calls = 0;
        await using var endpoint = await LocalChatEndpoint.StartAsync(async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) > 1) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "Hi";
        });
        var plan = Plan(endpoint.Endpoint);
        plan["ext"]!["agenteval.run"]!["caseSeconds"] = 1;
        var bytes = Bytes(plan);
        var target = await AefDatasetJobTarget.LoadAsync(bytes, PlanDirectory);
        var result = await AefJobRunner.RunAsync(bytes, Manifest(), target, Output,
            new AefJobOptions { At = Start }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("job.failed", result.Terminal);
        var root = JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(RunDirectory(result), "results.ndjson"))))!;
        Assert.Equal("error", (string?)root["state"]);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory(result)).Outcome);
        Assert.Empty(RunnerEventStream.Verify(Events(), plan));
    }

    [Fact]
    public async Task RunAsync_CancelDuringCase_RetainsReservedCostAndAbortedEvidence()
    {
        using var cancelled = new CancellationTokenSource();
        var calls = 0;
        await using var endpoint = await LocalChatEndpoint.StartAsync(async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                cancelled.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return "Hi";
        });
        var plan = Plan(endpoint.Endpoint);
        var bytes = Bytes(plan);
        var target = await AefDatasetJobTarget.LoadAsync(bytes, PlanDirectory);
        var result = await AefJobRunner.RunAsync(bytes, Manifest(), target, Output, new AefJobOptions { At = Start }, cancelled.Token);
        Assert.Equal("job.cancelled", result.Terminal);
        var directory = RunDirectory(result);
        var header = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "run.json")))!;
        Assert.Equal("aborted", (string?)header["status"]);
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "summary.json")))!;
        Assert.True((double)summary["cost"]!["totalUsd"]! > 0);
        Assert.Null(summary["usage"]![0]!["gen_ai.usage.input_tokens"]);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
        Assert.Empty(RunnerEventStream.Verify(Events(), plan));
    }
}
