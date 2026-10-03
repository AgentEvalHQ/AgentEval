// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Tests for the <c>agenteval bench owasp</c> subcommand
/// (<see cref="BenchOwaspCommand"/>) introduced in Phase 5 of v0.10.0-beta.
/// Mirrors the env-gate + workspace + happy-path coverage applied to
/// <see cref="BenchAgenticCommand"/> and <see cref="BenchEuAiActCommand"/>.
/// </summary>
[Collection("EnvVarTests")]
public class BenchOwaspCommandTests : IDisposable
{
    private readonly string _root;
    private readonly (string? Endpoint, string? Key, string? Deployment, string? Stub) _envSnapshot;

    public BenchOwaspCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-bench-owasp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _envSnapshot = (
            Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"),
            Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),
            Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT"),
            Environment.GetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE"));
        ScrubEnv();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT",      _envSnapshot.Endpoint);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY",       _envSnapshot.Key);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT",    _envSnapshot.Deployment);
        Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", _envSnapshot.Stub);
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void ScrubEnv()
    {
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", null);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", null);
        Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", null);
    }

    private void InitWorkspace()
    {
        var dir = Path.Combine(_root, ".agenteval");
        Directory.CreateDirectory(dir);
        var solutionDoc = new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "OwaspBenchTestSolution" };
        File.WriteAllText(
            Path.Combine(dir, "solution.json"),
            JsonSerializer.Serialize(solutionDoc,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    // ── Env-gate parity with the other bench commands ─────────────────────────

    private string[] WorkspaceFiles() =>
        Directory.GetFileSystemEntries(Path.Combine(_root, ".agenteval"), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    private sealed class CountingJudge : Microsoft.Extensions.AI.IChatClient
    {
        private int _calls;
        public int Calls => _calls;

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant, "VERDICT: INCONCLUSIVE\nCONFIDENCE: 0.5\nREASON: test")));
        }

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>Answers the preflight call, then fails every grading call, as an expired key or a quota would.</summary>
    private sealed class FailsAfterPreflightJudge : Microsoft.Extensions.AI.IChatClient
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

    private sealed class DeadJudge : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new HttpRequestException("401 invalid api key");

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public async Task BenchOwasp_AJudgeThatCannotAnswer_StopsTheRunBeforeTheScan()
    {
        InitWorkspace();
        var before = WorkspaceFiles();

        var result = await BenchOwaspCommand.RunAsync(
            preset: "top10", subject: "OwaspDeadJudgeAgent", rootOverride: _root, inputText: null,
            evaluatorOverride: null, agentOverride: new SafeRefusalAgent("OwaspDeadJudgeAgent"), judgeClientOverride: new DeadJudge());

        Assert.Equal(AgentEval.Cli.ExitCodes.RuntimeError, result.ExitCode);
        Assert.Equal(before, WorkspaceFiles());
    }

    [Fact]
    public async Task BenchOwasp_AJudgeThatFailsMidScan_IsIncomplete_NotAPass()
    {
        // Through 0.42 a failing judge turned the semantic categories into skipped leaves and the composite read PASS.
        InitWorkspace();

        var result = await BenchOwaspCommand.RunAsync(
            preset: "top10", subject: "OwaspFlakyJudgeAgent", rootOverride: _root, inputText: null,
            evaluatorOverride: null, agentOverride: new SafeRefusalAgent("OwaspFlakyJudgeAgent"),
            judgeClientOverride: new FailsAfterPreflightJudge());

        Assert.Equal(AgentEval.Cli.ExitCodes.GateIndeterminate, result.ExitCode);
        var summary = Directory.GetFiles(Path.Combine(_root, ".agenteval"), "summary.json", SearchOption.AllDirectories).Single();
        Assert.Contains("\"WARN\"", File.ReadAllText(summary));
    }

    [Fact]
    public async Task BenchOwasp_GradesTheAttacksWithTheJudge()
    {
        // Through 0.42 bench owasp resolved a judge and never called it.
        InitWorkspace();
        var judge = new CountingJudge();

        var result = await BenchOwaspCommand.RunAsync(
            preset: "top10", subject: "OwaspJudgedAgent", rootOverride: _root, inputText: null,
            evaluatorOverride: null, agentOverride: new SafeRefusalAgent("OwaspJudgedAgent"), judgeClientOverride: judge);

        Assert.True(result.ExitCode is 0 or 9 or 10 or 11, $"Expected a gate verdict; got {result.ExitCode}.");
        Assert.True(judge.Calls > 0, "The judge was never called.");
    }

    [Fact]
    public async Task BenchOwasp_NoTarget_Refuses_AndStoresNothing()
    {
        // Through 0.42 a run with no target scanned a built-in agent that refuses everything (a red-team PASS)
        // and stored it as the subject's result.
        InitWorkspace();
        var before = WorkspaceFiles();

        var result = await BenchOwaspCommand.RunAsync(
            preset: "smoke", subject: "OwaspNoTargetAgent", rootOverride: _root, inputText: null,
            evaluatorOverride: new PassingStubEvaluator(), agentOverride: null);

        Assert.Equal(AgentEval.Cli.ExitCodes.UsageError, result.ExitCode);
        Assert.Null(result.ReportDir);
        Assert.Equal(before, WorkspaceFiles());
    }

    [Fact]
    public async Task BenchOwasp_Mock_NeedsNoJudgeOrProvider()
    {
        // A selector naming a provider with no variables makes any real judge resolution fail closed (exit 3).
        // A mock run must not reach it.
        InitWorkspace();
        using var env = new ProviderEnvironmentScope(("AI_INFERENCE_PROVIDER", "foundry"));

        var result = await BenchOwaspCommand.RunAsync(
            preset: "smoke", subject: "OwaspMockNoProviderAgent", rootOverride: _root, inputText: null,
            evaluatorOverride: null, agentOverride: null, mock: true);

        Assert.Equal(AgentEval.Cli.ExitCodes.GateIndeterminate, result.ExitCode);
    }

    [Fact]
    public async Task BenchOwasp_MockWithARealTarget_IsRefusedByTheCommandItself()
    {
        InitWorkspace();
        var before = WorkspaceFiles();

        var result = await BenchOwaspCommand.RunAsync(
            preset: "smoke", subject: "OwaspMockPlusTargetAgent", rootOverride: _root, inputText: null,
            evaluatorOverride: new PassingStubEvaluator(), agentOverride: new SafeRefusalAgent("OwaspReal"), mock: true);

        Assert.Equal(AgentEval.Cli.ExitCodes.UsageError, result.ExitCode);
        Assert.Equal(before, WorkspaceFiles());
    }

    [Fact]
    public async Task BenchOwasp_Mock_ExitsIndeterminate_AndStoresNothing()
    {
        InitWorkspace();
        var before = WorkspaceFiles();

        var result = await BenchOwaspCommand.RunAsync(
            preset: "smoke", subject: "OwaspMockAgent", rootOverride: _root, inputText: null,
            evaluatorOverride: new PassingStubEvaluator(), agentOverride: null, mock: true);

        Assert.Equal(AgentEval.Cli.ExitCodes.GateIndeterminate, result.ExitCode);
        Assert.Null(result.ReportDir);
        Assert.Equal(before, WorkspaceFiles());
    }

    [Fact]
    public async Task BenchOwasp_NoProvider_ReturnsExitCode3()
    {
        InitWorkspace();
        var result = await BenchOwaspCommand.RunAsync(
            preset: "smoke",
            subject: "OwaspGateTestAgent",
            rootOverride: _root,
            inputText: null,
            evaluatorOverride: null,
            agentOverride: new SafeRefusalAgent("OwaspTargetAgent"));
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task BenchOwasp_PartialAzureConfig_ReturnsExitCode3()
    {
        InitWorkspace();
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/");
        // Missing key + deployment → partial config → exit 2

        var result = await BenchOwaspCommand.RunAsync(
            preset: "smoke",
            subject: "OwaspPartialAgent",
            rootOverride: _root,
            inputText: null,
            evaluatorOverride: null,
            agentOverride: new SafeRefusalAgent("OwaspTargetAgent"));
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task BenchOwasp_MissingWorkspace_ReturnsExitCode1()
    {
        var noWorkspaceRoot = Path.Combine(_root, "no-workspace");
        Directory.CreateDirectory(noWorkspaceRoot);

        var result = await BenchOwaspCommand.RunAsync(
            preset: "smoke",
            subject: "OwaspMissingAgent",
            rootOverride: noWorkspaceRoot,
            inputText: null,
            evaluatorOverride: new PassingStubEvaluator(),
            agentOverride: new SafeRefusalAgent("OwaspTargetAgent"));

        Assert.Equal(1, result.ExitCode);
    }

    // ── Preset arg parsing ────────────────────────────────────────────────────

    [Theory]
    [InlineData("top10",       "Top10")]
    [InlineData("Smoke",       "Smoke")]
    [InlineData("SMOKE",       "Smoke")]
    [InlineData("audit",       "AuditGrade")]
    [InlineData("auditgrade",  "AuditGrade")]
    [InlineData("top10-rag",   "Top10ForRag")]
    [InlineData("top10forrag", "Top10ForRag")]
    public void ResolvePreset_MapsKnownPresets(string spec, string expectedPresetName)
    {
        var run = BenchOwaspCommand.ResolvePreset(spec, new PassingStubEvaluator());
        Assert.Equal(expectedPresetName, run.PresetName);
    }

    [Fact]
    public void ResolvePreset_UnknownPreset_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            BenchOwaspCommand.ResolvePreset("not-a-preset", new PassingStubEvaluator()));
        Assert.Contains("Unknown OWASP preset", ex.Message);
    }

    // ── Happy-path end-to-end: pipeline runs and writes artefacts ─────────────

    [Fact]
    public async Task BenchOwasp_SmokePreset_PassingAgent_RunsToCompletion_AndWritesArtefacts()
    {
        InitWorkspace();

        var (exit, reportDir) = await BenchOwaspCommand.RunAsync(
            preset: "smoke",
            subject: "OwaspSmokeAgent",
            rootOverride: _root,
            inputText: "Hello, what can you do?",
            evaluatorOverride: new PassingStubEvaluator(),
            agentOverride: new SafeRefusalAgent("OwaspSmokeAgent"));

        // 0 (PASS) or 2 (WARN/FAIL) both indicate the pipeline executed cleanly;
        // 1 would indicate a workspace / preset / config error.
        Assert.True(exit is 0 or 9 or 10 or 11,
            $"Expected exit 0 (PASS), 9 (FAIL), 10 (WARN) or 11 (indeterminate); got {exit}.");

        // The command returns the absolute path of the timestamped report
        // directory; using it directly avoids the second-precision-timestamp
        // race that "OrderByDescending(d => d).First()" would otherwise have on
        // directory-name strings of shape `yyyy-MM-dd_HH-mm-ss`.
        Assert.NotNull(reportDir);
        Assert.True(Directory.Exists(reportDir),
            $"Report directory {reportDir} should exist after a completed bench run.");
        Assert.True(File.Exists(Path.Combine(reportDir!, "report.md")),
            "report.md should be generated alongside the run.");
        Assert.True(File.Exists(Path.Combine(reportDir!, "report.json")),
            "report.json should be generated alongside the run.");

        // Sanity-check that the directory lives under the expected compliance
        // tree (the path-shape part of the original assertion).
        var reportsRoot = Path.Combine(_root, ".agenteval", "compliance", "OWASP-LLM-Top10", "OwaspSmokeAgent");
        Assert.StartsWith(reportsRoot, reportDir);

        // The unified output-store should have a run manifest written by the
        // command's StartRunAsync/CompleteRunAsync flow. FileSystemLayout writes
        // runs under .agenteval/subjects/<kind>/<name>/runs/<runId>.
        var subjectRunsDir = Path.Combine(_root, ".agenteval", "subjects", "agents", "OwaspSmokeAgent", "runs");
        Assert.True(Directory.Exists(subjectRunsDir),
            $"Run directory {subjectRunsDir} should exist after persisting the OWASP run.");
        Assert.NotEmpty(Directory.GetDirectories(subjectRunsDir));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Always-safe-refusal agent for happy-path testing. Mirrors the CLI's
    /// internal stub but lives here so the test does not depend on that
    /// internal symbol.
    /// </summary>
    private sealed class SafeRefusalAgent : IEvaluableAgent
    {
        public string Name { get; }
        public SafeRefusalAgent(string name) { Name = name; }
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse
            {
                Text = "I cannot help with that request."
            });
    }

    private sealed class PassingStubEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            var list = criteria.ToList();
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 100,
                Summary = "stub-pass",
                CriteriaResults = list
                    .Select(c => new CriterionResult { Criterion = c, Met = true, Explanation = "stub" })
                    .ToList()
            });
        }
    }
}
