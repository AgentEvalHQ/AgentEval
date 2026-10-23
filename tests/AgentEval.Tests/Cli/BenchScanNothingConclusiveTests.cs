// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// T81 (and T13's run-summary rule): <c>bench owasp</c>, <c>bench mitre</c> and <c>bench nist</c> against a target none
/// of whose probes reached a verdict persist a run summary with no <c>overallScore</c> and no <c>overallPassRate</c>.
/// Both read 0 there, beside a verdict that already said the run measured nothing.
/// </summary>
[Collection("EnvVarTests")]
public class BenchScanNothingConclusiveTests : IDisposable
{
    private readonly string _root;

    public BenchScanNothingConclusiveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-bench-nothing-conclusive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void InitWorkspace()
    {
        var dir = Path.Combine(_root, ".agenteval");
        Directory.CreateDirectory(dir);
        var solutionDoc = new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "NothingConclusiveTestSolution" };
        File.WriteAllText(
            Path.Combine(dir, "solution.json"),
            JsonSerializer.Serialize(solutionDoc,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    /// <summary>A target that is down: every call fails, so every probe is inconclusive (a transport error).</summary>
    private sealed class UnreachableAgent : IEvaluableAgent
    {
        public string Name => "unreachable-agent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("503 Service Unavailable");
    }

    /// <summary>Answers the readiness check, then fails any further call: it never grades anything.</summary>
    private sealed class AnswersPreflightOnlyJudge : Microsoft.Extensions.AI.IChatClient
    {
        private int _calls;

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.Assistant, "OK")));
            throw new HttpRequestException("429 quota exceeded");
        }

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Theory]
    [InlineData("owasp")]
    [InlineData("mitre")]
    [InlineData("nist")]
    public async Task ARunWithNoConclusiveProbe_PersistsNoOverallScore_AndNoOverallPassRate(string family)
    {
        InitWorkspace();
        var agent = new UnreachableAgent();
        var judge = new AnswersPreflightOnlyJudge();

        var result = await Aef.NativeVerbIntegrationTests.WithLegacyOutput(async () => family switch
        {
            "owasp" => await BenchOwaspCommand.RunAsync("smoke", agent.Name, _root, null, null, agent, judgeClientOverride: judge, noAef: true),
            "mitre" => await BenchMitreCommand.RunAsync("smoke", agent.Name, _root, null, null, agent, judgeClientOverride: judge, noAef: true),
            _ => await BenchNistCommand.RunAsync("smoke", agent.Name, _root, null, null, agent, judgeClientOverride: judge, noAef: true),
        });

        Assert.NotEqual(0, result.ExitCode);   // nothing measured is not a pass
        var aef = Path.Combine(_root, ".agenteval", "aef") + Path.DirectorySeparatorChar;
        var summaryPath = Directory.GetFiles(Path.Combine(_root, ".agenteval"), "summary.json", SearchOption.AllDirectories)
            .Single(f => !f.StartsWith(aef, StringComparison.Ordinal));
        using var summary = JsonDocument.Parse(File.ReadAllText(summaryPath));
        var metrics = summary.RootElement.GetProperty("metrics");

        Assert.False(metrics.TryGetProperty("overallScore", out _), "a composite that measured nothing has no overall score");
        Assert.False(metrics.TryGetProperty("overallPassRate", out _), "a scan with no conclusive probe has no pass rate");
        Assert.NotEqual("PASS", summary.RootElement.GetProperty("verdict").GetString());
    }
}
