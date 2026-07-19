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
///   <item>No config + no opt-in → exit code 2 with help message.</item>
///   <item>No config + AGENTEVAL_ALLOW_STUB_JUDGE=1 → stub judge with warning.</item>
/// </list>
/// </summary>
[Collection("EnvVarTests")]
public class JudgeFactoryTests : IDisposable
{
    private readonly (string? Endpoint, string? Key, string? Deployment, string? Stub) _snapshot;

    public JudgeFactoryTests()
    {
        // Snapshot the 4 env vars before each test so the .Dispose restore is reliable.
        _snapshot = (
            Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"),
            Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),
            Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT"),
            Environment.GetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE"));
        ScrubEnv();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT",     _snapshot.Endpoint);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY",      _snapshot.Key);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT",   _snapshot.Deployment);
        Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", _snapshot.Stub);
    }

    private static void ScrubEnv()
    {
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", null);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", null);
        Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", null);
    }

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

    // ── Branch 4: no config + no opt-in → exit 3 (RuntimeError, BUG-22) ─────────────────────────

    [Fact]
    public void Resolve_NoConfig_NoStubOptIn_ReturnsExitCode3()
    {
        // env already scrubbed by ctor

        var (judge, model, exit) = JudgeFactory.Resolve(evaluatorOverride: null, judgeKind: "no-config-test");

        Assert.Null(judge);
        Assert.Equal(3, exit);
        Assert.Equal("", model);
    }

    // ── Branch 5: opt-in stub ────────────────────────────────────────────

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    public void Resolve_NoConfig_StubOptIn_ReturnsStubEvaluator(string optInValue)
    {
        Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", optInValue);

        var (judge, model, exit) = JudgeFactory.Resolve(evaluatorOverride: null, judgeKind: "stub-opt-in");

        Assert.NotNull(judge);
        Assert.IsType<JudgeFactory.StubEvaluator>(judge);
        Assert.Equal("stub", model);
        Assert.Equal(0, exit);
    }

    /// <summary>
    /// Negative test for the stub opt-in: only "1" and "true" (case-insensitive)
    /// should engage the stub. Other values (including "yes", "0", "false", empty)
    /// must continue to gate.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData("anything-else")]
    public void Resolve_NoConfig_InvalidStubOptInValue_ReturnsExitCode3(string optInValue)
    {
        Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", optInValue);

        var (judge, _, exit) = JudgeFactory.Resolve(evaluatorOverride: null, judgeKind: "invalid-opt-in");

        Assert.Null(judge);
        Assert.Equal(3, exit);
    }

    // ── Stub evaluator behaviour ─────────────────────────────────────────

    /// <summary>
    /// Sanity-check that the stub evaluator returned by branch 5 actually
    /// produces deterministic placeholder output. Documents the contract so
    /// downstream consumers expecting score=75 / criteria-met don't get
    /// surprised by a future stub-shape change.
    /// </summary>
    [Fact]
    public async Task StubEvaluator_ReturnsDeterministic75WithAllCriteriaMet()
    {
        Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", "1");
        var (judge, _, _) = JudgeFactory.Resolve(evaluatorOverride: null);
        Assert.NotNull(judge);

        var result = await judge!.EvaluateAsync("input", "output", new[] { "criterion-a", "criterion-b" });

        Assert.Equal(75, result.OverallScore);
        Assert.Equal(2, result.CriteriaResults.Count);
        Assert.All(result.CriteriaResults, c => Assert.True(c.Met));
    }
}
