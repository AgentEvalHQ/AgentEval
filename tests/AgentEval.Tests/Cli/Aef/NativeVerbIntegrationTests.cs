// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Cli.Commands;
using AgentEval.Results.Integrity;
using AgentEval.Tests.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Cli.Aef;

[Collection("ConsoleTests")]
public sealed class NativeVerbIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aef-native-" + Guid.NewGuid().ToString("N"));

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
                foreach (var file in Directory.GetFiles(source))
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }
        }
        Directory.Delete(_root, recursive: true);
    }

    private EvalOptions Options(string endpoint, int count = 1, int runs = 1)
    {
        var dataset = Path.Combine(_root, "dataset.yaml");
        File.WriteAllText(dataset, string.Concat(Enumerable.Range(0, count).Select(i => $"- id: c{i}\n  input: secret-prompt-{i}\n  expected: Hi\n")));
        return new EvalOptions
        {
            Dataset = new FileInfo(dataset), Endpoint = endpoint, Model = "local-model", ApiKey = "local-test",
            Format = "json", Output = new FileInfo(Path.Combine(_root, "report.json")), Quiet = true,
            Aef = new DirectoryInfo(Path.Combine(_root, "aef")), SubjectVersion = "1",
            Runs = runs,
        };
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

    [Fact]
    public async Task BenchOwasp_HttpTarget_WritesLiveEvidenceAndPreservesTheCommandExit()
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync("I cannot assist with that request.");
        await using var judgeEndpoint = await LocalChatEndpoint.StartAsync("VERDICT: INCONCLUSIVE\nCONFIDENCE: 0.5\nREASON: test");
        var agent = BenchTier1SutResolver.BuildFromOpenAiEndpoint(endpoint.Endpoint, "local-model", "local-test", "http-subject");
        using var judge = AgentEval.Cli.Infrastructure.EndpointFactory.CreateOpenAICompatible(judgeEndpoint.Endpoint, "local-judge", "local-test");
        Directory.CreateDirectory(Path.Combine(_root, ".agenteval"));
        File.WriteAllText(Path.Combine(_root, ".agenteval", "solution.json"),
            new JsonObject { ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "http-test" }.ToJsonString());
        var aef = new DirectoryInfo(Path.Combine(_root, "bench-aef"));

        var written = await BenchOwaspCommand.RunAsync("smoke", agent.Name, _root, null, null, agent,
            judgeClientOverride: judge, aef: aef, agentIsLive: true);
        var notWritten = new DirectoryInfo(Path.Combine(_root, "disabled-aef"));
        var disabled = await BenchOwaspCommand.RunAsync("smoke", agent.Name, _root, null, null, agent,
            judgeClientOverride: judge, aef: notWritten, noAef: true, agentIsLive: true);

        Assert.Equal(disabled.ExitCode, written.ExitCode);
        Assert.False(notWritten.Exists);
        Assert.NotEmpty(endpoint.Requests);
        Assert.NotEmpty(judgeEndpoint.Requests);
        var dir = RunDirectory(aef.FullName);
        AssertIntact(dir);
        Assert.Equal("live", (string?)Document(dir, "run.json")["execution"]!["targetMode"]);
        var gate = JsonNode.Parse(File.ReadAllLines(Path.Combine(dir, "gates.ndjson")).Single())!;
        Assert.Equal(written.ExitCode, (int)gate["exitCode"]!);
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

    private sealed class CancelOnPersistenceWriter(CancellationTokenSource cancel) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.StartsWith("Persisted run ", StringComparison.Ordinal) is true)
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
