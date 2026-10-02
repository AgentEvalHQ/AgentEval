// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Direct unit tests for <see cref="JudgeFactory.Resolve"/> — Phase-4 Task 4.5.
/// Covers each of the 5 documented resolution branches:
/// <list type="number">
///   <item>Test override supplied → passthrough with model name "override".</item>
///   <item>All three AZURE_OPENAI_* set → real Azure judge constructed.</item>
///   <item>Partial Azure config → exit code 2 with diagnostic.</item>
///   <item>No config → exit code 3 with help message.</item>
///   <item>No config + the retired <c>AGENTEVAL_ALLOW_STUB_JUDGE</c> → still exit code 3: there is no stand-in judge.</item>
/// </list>
/// </summary>
[Collection("EnvVarTests")]
public class JudgeFactoryTests : IDisposable
{
    // Clears EVERY provider variable for the duration of each test and restores them afterwards. Scrubbing
    // the three AZURE_OPENAI_* names was enough while Azure was the only path the CLI knew; it is not enough
    // now that the CLI resolves AI_INFERENCE_PROVIDER, because an ambient OPENAI_API_KEY or BITDEER_API_KEY
    // would configure a judge in a test whose whole point is that no judge is configured.
    private readonly ProviderEnvironmentScope _env = new();

    public void Dispose() => _env.Dispose();

    private sealed class FakeEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
            => Task.FromResult(new EvaluationResult { OverallScore = 100, Summary = "fake" });
    }

    // ── Branch 1: override ───────────────────────────────────────────────

    [Fact]
    public void Resolve_OverridePassed_ReturnsOverrideWithModelOverride()
    {
        var fake = new FakeEvaluator();

        var (judge, model, exit) = JudgeFactory.Resolve(fake, "test-kind");

        Assert.Same(fake, judge);
        Assert.Equal("override", model);
        Assert.Equal(0, exit);
    }

    // ── Branch 2: all-Azure-config → real ChatClientEvaluator ────────────

    [Fact]
    public void Resolve_AllAzureVarsSet_ReturnsChatClientEvaluator()
    {
        // The URI need not point at a real endpoint — JudgeFactory only
        // constructs the client; it doesn't probe the server until a real
        // EvaluateAsync call happens (none here).
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/");
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "test-key-not-real");
        Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", "gpt-4o-test");

        var (judge, model, exit) = JudgeFactory.Resolve(evaluatorOverride: null, judgeKind: "branch-2-test");

        Assert.NotNull(judge);
        Assert.IsType<ChatClientEvaluator>(judge);
        Assert.Equal("gpt-4o-test", model);
        Assert.Equal(0, exit);
    }

    // ── Branch 3: partial Azure config → exit 3 (RuntimeError, BUG-22) ──────────────────────────

    [Theory]
    [InlineData("endpoint-only", true,  false, false)]
    [InlineData("key-only",      false, true,  false)]
    [InlineData("deployment-only",false, false, true)]
    [InlineData("endpoint+key",  true,  true,  false)]
    [InlineData("endpoint+deployment", true, false, true)]
    [InlineData("key+deployment", false, true, true)]
    public void Resolve_PartialAzureConfig_ReturnsExitCode3(string scenario, bool setEndpoint, bool setKey, bool setDeployment)
    {
        if (setEndpoint)   Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT",   "https://example.openai.azure.com/");
        if (setKey)        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY",    "test-key");
        if (setDeployment) Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", "gpt-4o");

        var (judge, model, exit) = JudgeFactory.Resolve(evaluatorOverride: null, judgeKind: scenario);

        Assert.Null(judge);
        Assert.Equal(3, exit);
        Assert.Equal("", model);
    }

    // ── Branch 4: no config → exit 3 (RuntimeError, BUG-22) ─────────────────────────

    [Fact]
    public void Resolve_NoConfig_ReturnsExitCode3()
    {
        // env already scrubbed by ctor

        var (judge, model, exit) = JudgeFactory.Resolve(evaluatorOverride: null, judgeKind: "no-config-test");

        Assert.Null(judge);
        Assert.Equal(3, exit);
        Assert.Equal("", model);
    }

    // ── Branch 5: the retired stub opt-in ────────────────────────────────

    /// <summary>
    /// Through 0.42, <c>AGENTEVAL_ALLOW_STUB_JUDGE=1</c> on a machine with no provider returned a judge that scored
    /// 75 with every criterion met, for benchmarks and for calibration. There is no stand-in judge now: whatever the
    /// variable says, a machine with no provider gets exit 3 and no judge.
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("0")]
    [InlineData("")]
    public void Resolve_NoConfig_TheRetiredStubOptIn_ChangesNothing(string optInValue)
    {
        Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", optInValue);

        var (judge, model, exit) = JudgeFactory.Resolve(evaluatorOverride: null, judgeKind: "retired-opt-in");

        Assert.Null(judge);
        Assert.Equal("", model);
        Assert.Equal(3, exit);
    }
}

/// <summary>
/// The compliance families resolve ONE judge for the benchmark and its calibration. Before this, <c>bench gdpr</c>
/// sent <c>gdpr-judge-system.v1.md</c> while <c>bench gdpr calibrate</c> sent the generic default prompt, so every
/// published GDPR (and EU AI Act) calibration figure described a different judge from the one the benchmark ran.
/// </summary>
[Collection("EnvVarTests")]
public class JudgeFactoryFamilyPromptTests : IDisposable
{
    private readonly ProviderEnvironmentScope _env = new();

    public void Dispose() => _env.Dispose();

    private static void ConfigureAzureJudge()
    {
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/");
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "test-key-not-real");
        Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", "gpt-4o-test");
    }

    [Fact]
    public void ResolveGdpr_SendsAndNamesTheGdprSystemPrompt()
    {
        ConfigureAzureJudge();

        var (judge, _, exit) = JudgeFactory.ResolveGdpr(evaluatorOverride: null, judgeKind: "test");

        Assert.Equal(0, exit);
        var chat = Assert.IsType<ChatClientEvaluator>(judge);
        Assert.Equal(JudgeFactory.GdprJudgeSystemPromptFile, chat.SystemPromptId);
        Assert.DoesNotContain("You are a Test Evaluator Agent", chat.PromptMaterial, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveEuAiAct_SendsAndNamesTheEuAiActSystemPrompt()
    {
        ConfigureAzureJudge();

        var (judge, _, exit) = JudgeFactory.ResolveEuAiAct(evaluatorOverride: null, judgeKind: "test");

        Assert.Equal(0, exit);
        var chat = Assert.IsType<ChatClientEvaluator>(judge);
        Assert.Equal(JudgeFactory.EuAiActJudgeSystemPromptFile, chat.SystemPromptId);
        Assert.DoesNotContain("You are a Test Evaluator Agent", chat.PromptMaterial, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BenchCommand.cs", "JudgeFactory.ResolveGdpr(")]
    [InlineData("BenchCalibrateCommand.cs", "JudgeFactory.ResolveGdpr(")]
    [InlineData("BenchEuAiActCommand.cs", "JudgeFactory.ResolveEuAiAct(")]
    [InlineData("BenchEuAiActCalibrateCommand.cs", "JudgeFactory.ResolveEuAiAct(")]
    public void BenchAndCalibrate_GoThroughTheSameFamilyResolver(string file, string resolver)
    {
        // A guard on the source, because the defect was a CALL SITE that bypassed the resolver: the benchmark
        // and its calibration must not be able to drift onto two different judges again.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "AgentEval.Cli", "Commands", file));

        Assert.Contains(resolver, source, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCommand_LoadsAFamilyJudgePrompt_OutsideTheResolver()
    {
        var commands = Path.Combine(RepoRoot(), "src", "AgentEval.Cli", "Commands");
        var offenders = Directory.EnumerateFiles(commands, "*.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) is not ("JudgeFactory.cs" or "EmbeddedPromptLoader.cs"))
            .Where(f => File.ReadAllText(f).Contains("EmbeddedPromptLoader.Load(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find repo root (AgentEval.sln).");
    }
}

