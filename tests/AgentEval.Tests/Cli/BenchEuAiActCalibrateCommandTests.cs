// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Smoke tests for the <c>agenteval bench eu-ai-act calibrate</c> subcommand
/// (<see cref="BenchEuAiActCalibrateCommand"/>). Phase-4 Task 4.6 — closes the
/// coverage gap for the EU AI Act calibrate path. Detailed env-var-gate paths
/// are exercised at the unit level in <see cref="JudgeFactoryTests"/>; these
/// tests verify the end-to-end command shape.
/// </summary>
[Collection("EnvVarTests")]
public class BenchEuAiActCalibrateCommandTests : IDisposable
{
    private readonly string _root;
    private readonly (string? Endpoint, string? Key, string? Deployment, string? Stub) _envSnapshot;

    public BenchEuAiActCalibrateCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-euai-cal-test-" + Guid.NewGuid().ToString("N"));
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
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT",     _envSnapshot.Endpoint);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY",      _envSnapshot.Key);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT",   _envSnapshot.Deployment);
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

    private sealed class AlwaysPassEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            var list = criteria.ToList();
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 100,
                Summary = "stub-pass",
                CriteriaResults = list.Select(c => new CriterionResult { Criterion = c, Met = true, Explanation = "stub" }).ToList()
            });
        }
    }

    [Fact]
    public async Task BenchEuAiActCalibrate_NoEnvVars_NoStubOptIn_ReturnsExitCode3()
    {
        // env already scrubbed by ctor
        var exit = await BenchEuAiActCalibrateCommand.RunAsync(_root, outPathOverride: null);
        Assert.Equal(3, exit);
    }

    [Fact]
    public async Task BenchEuAiActCalibrate_PartialAzureConfig_ReturnsExitCode3()
    {
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/");
        // Missing key + deployment

        var exit = await BenchEuAiActCalibrateCommand.RunAsync(_root, outPathOverride: null);
        Assert.Equal(3, exit);
    }

    [Fact]
    public async Task BenchEuAiActCalibrate_PassingStubEvaluatorOverride_CompletesWithExitCode()
    {
        // evaluatorOverride bypasses env-var resolution entirely — the command
        // runs the calibration pipeline against the embedded golden datasets
        // and returns the gate exit code. We don't pin a specific kappa value
        // (it depends on dataset / stub interaction); just verify a clean
        // numeric exit and no thrown exception.
        var exit = await BenchEuAiActCalibrateCommand.RunCoreAsync(
            rootOverride: _root,
            outPathOverride: null,
            evaluatorOverride: new AlwaysPassEvaluator());

        Assert.True(exit is 0 or 9,
            $"Expected exit 0 (calibration passed) or 9 (GateFailed); got {exit}.");
    }
}
