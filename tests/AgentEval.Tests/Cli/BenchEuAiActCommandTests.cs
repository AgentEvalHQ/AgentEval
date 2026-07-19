// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Tests for the <c>agenteval bench eu-ai-act</c> subcommand
/// (<see cref="BenchEuAiActCommand"/>). Phase-2 / Task 4.6 — closes the
/// coverage gap for the EU AI Act bench path. Mirrors
/// <see cref="BenchCommandTests"/> for the GDPR side; the composite-preset
/// E2E test (Task 2.1 / 2.2) and the judge-model attestation test (Task 2.3)
/// are required regression nets per the Phase-2 Opus gate review.
/// </summary>
[Collection("EnvVarTests")]
public class BenchEuAiActCommandTests : IDisposable
{
    private readonly string _root;
    private readonly (string? Endpoint, string? Key, string? Deployment, string? Stub) _envSnapshot;

    public BenchEuAiActCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-bench-euai-test-" + Guid.NewGuid().ToString("N"));
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
        var solutionDoc = new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "BenchEuAiActTestSolution" };
        File.WriteAllText(
            Path.Combine(dir, "solution.json"),
            JsonSerializer.Serialize(solutionDoc,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private sealed class PassingStubEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default)
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

    /// <summary>Stub evaluator that records the most recent graded output (the agent response) — mirrors BenchCommandTests' GDPR-side helper.</summary>
    private sealed class CapturingStubEvaluator : IEvaluator
    {
        public string? LastOutput { get; private set; }

        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default)
        {
            LastOutput = output;
            var list = criteria.ToList();
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 100,
                Summary = "stub-capture",
                CriteriaResults = list.Select(c => new CriterionResult { Criterion = c, Met = true, Explanation = "stub" }).ToList()
            });
        }
    }

    /// <summary>Fixed-answer fake agent — simulates what a resolved --sut target would hand to RunAsync's agentOverride parameter.</summary>
    private sealed class FixedAnswerAgent : IEvaluableAgent
    {
        private readonly string _answer;
        public string Name { get; }
        public FixedAnswerAgent(string name, string answer) { Name = name; _answer = answer; }
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse { Text = _answer });
    }

    // ── Env-gate trio (Phase-4 gate-review follow-up) ────────────────────

    [Fact]
    public async Task BenchEuAiAct_NoEnvVars_NoStubOptIn_ReturnsExitCode3()
    {
        // env already scrubbed by ctor — no override path, no stub opt-in.
        InitWorkspace();

        var exit = await BenchEuAiActCommand.RunAsync(
            preset: "smoke",
            subject: "EuAiActNoEnvAgent",
            rootOverride: _root,
            inputText: null);

        Assert.Equal(3, exit);
    }

    [Fact]
    public async Task BenchEuAiAct_PartialAzureConfig_ReturnsExitCode3()
    {
        InitWorkspace();
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/");
        // Missing key + deployment — JudgeFactory should refuse to construct an Azure client.

        var exit = await BenchEuAiActCommand.RunAsync(
            preset: "smoke",
            subject: "EuAiActPartialEnvAgent",
            rootOverride: _root,
            inputText: null);

        Assert.Equal(3, exit);
    }

    // ── Smoke tests ──────────────────────────────────────────────────────

    [Fact]
    public async Task BenchEuAiAct_MissingWorkspace_ReturnsExitCode1()
    {
        // Arrange — no .agenteval/ directory initialised
        var noWorkspaceRoot = Path.Combine(_root, "no-workspace");
        Directory.CreateDirectory(noWorkspaceRoot);

        // Act
        var exitCode = await BenchEuAiActCommand.RunAsync(
            preset: "smoke",
            subject: "MissingWorkspaceAgent",
            rootOverride: noWorkspaceRoot,
            inputText: null,
            evaluatorOverride: new PassingStubEvaluator());

        // Assert
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task BenchEuAiAct_SmokePreset_PassingStub_WritesReportFiles()
    {
        // Arrange
        InitWorkspace();

        // Act
        var exitCode = await BenchEuAiActCommand.RunAsync(
            preset: "smoke",
            subject: "EuAiActSmokeAgent",
            rootOverride: _root,
            // Phase-7 Task 7.22: --input is now required; provide a fixture string.
            inputText: "Sample EU AI Act bench probe for the smoke preset.",
            evaluatorOverride: new PassingStubEvaluator());

        // Assert — exit cleanly + evidence files present
        Assert.Equal(0, exitCode);
        var complianceRoot = Path.Combine(_root, ".agenteval", "compliance", "EU-AI-Act", "EuAiActSmokeAgent");
        var tsDir = Directory.GetDirectories(complianceRoot).OrderByDescending(d => d).First();

        Assert.True(File.Exists(Path.Combine(tsDir, "report.md")), "report.md should be generated");
        Assert.True(File.Exists(Path.Combine(tsDir, "report.pdf")), "report.pdf should be generated");
        Assert.True(new FileInfo(Path.Combine(tsDir, "report.pdf")).Length > 0,
            "report.pdf should be non-empty");
    }

    [Fact]
    public async Task BenchEuAiAct_WithAgentOverride_DrivesTheAgentPerScenario_NotAStaticResponse()
    {
        // Bench Tier 2 (§2.2): mirrors BenchCommandTests' GDPR-side equivalent — proves the CLI wiring
        // actually reaches ScenarioToAtomicEval's pre-existing agent parameter for eu-ai-act too, and that
        // agentOverride wins even when --response text is also supplied.
        InitWorkspace();
        var capturing = new CapturingStubEvaluator();
        const string agentAnswer = "EU-AI-ACT-AGENT-OVERRIDE-DROVE-THIS unique marker";
        var agent = new FixedAnswerAgent("EuAiActSutAgent", agentAnswer);

        var exitCode = await BenchEuAiActCommand.RunAsync(
            preset: "smoke",
            subject: "EuAiActSutAgent",
            rootOverride: _root,
            inputText: "Sample EU AI Act bench probe for the smoke preset.",
            evaluatorOverride: capturing,
            agentOverride: agent,
            responseText: "this static text should be ignored — agentOverride takes priority");

        Assert.True(exitCode == 0 || exitCode == 9 || exitCode == 10 || exitCode == 11); // gate PASS, or FAIL/WARN/indeterminate (BUG-22 split)
        Assert.Equal(agentAnswer, capturing.LastOutput);
    }

    // ── Phase-2 / Task 2.2 regression — composite preset E2E ─────────────

    /// <summary>
    /// `bench eu-ai-act --preset standard+high-risk-employment` previously crashed at
    /// SaveReportAsync because the schema enum only listed the 6 base names. The fix
    /// splits the preset into a base + `domainPacks[]` array; verify the full pipeline
    /// writes schema-valid evidence end-to-end and records the pack.
    /// </summary>
    [Fact]
    public async Task BenchEuAiAct_StandardPlusHighRiskEmployment_E2E_WritesValidEvidence()
    {
        // Arrange
        InitWorkspace();

        // Act
        var exitCode = await BenchEuAiActCommand.RunAsync(
            preset: "standard+high-risk-employment",
            subject: "EuAiActComposedAgent",
            rootOverride: _root,
            // Phase-7 Task 7.22: --input is now required; provide a fixture string.
            inputText: "Sample EU AI Act bench probe for the composite preset.",
            evaluatorOverride: new PassingStubEvaluator());

        // Assert — exit cleanly + evidence files present
        Assert.Equal(0, exitCode);
        var complianceRoot = Path.Combine(_root, ".agenteval", "compliance", "EU-AI-Act", "EuAiActComposedAgent");
        var tsDir = Directory.GetDirectories(complianceRoot).OrderByDescending(d => d).First();

        // eu-ai-act-evidence.json was the file that crashed pre-fix; its mere
        // presence proves the schema accepted the composite preset.
        var euAiActEvidencePath = Path.Combine(tsDir, "eu-ai-act-evidence.json");
        Assert.True(File.Exists(euAiActEvidencePath),
            $"eu-ai-act-evidence.json should be present at {euAiActEvidencePath} — if missing, SaveReportAsync's schema validation rejected the composite preset.");

        // The wrapper should record the base preset cleanly + the pack in domainPacks.
        var euJson = JsonDocument.Parse(File.ReadAllText(euAiActEvidencePath));
        Assert.Equal("standard", euJson.RootElement.GetProperty("preset").GetString());
        var packs = euJson.RootElement.GetProperty("domainPacks").EnumerateArray()
            .Select(p => p.GetString()).ToArray();
        Assert.Contains("high-risk-employment", packs);
    }

    // ── Phase-2 / Task 2.3 regression — judge model in Attestation ───────

    /// <summary>
    /// Pre-pass-3, <c>Attestation.EvaluatorModel</c> was hard-coded to
    /// <c>"internal"</c> on every EU AI Act write. With the JudgeFactory + reporter
    /// wiring landed in pass-3, the test-override path records <c>"override"</c>;
    /// an Azure path would record the deployment name.
    /// </summary>
    [Fact]
    public async Task BenchEuAiAct_RecordsJudgeModelInAttestation()
    {
        // Arrange
        InitWorkspace();

        // Act
        await BenchEuAiActCommand.RunAsync(
            preset: "smoke",
            subject: "EuAiActAttestationAgent",
            rootOverride: _root,
            // Phase-7 Task 7.22: --input is now required; provide a fixture string.
            inputText: "Sample EU AI Act attestation probe.",
            evaluatorOverride: new PassingStubEvaluator());

        // Assert — base evidence.json's attestation.evaluatorModel reflects the resolved judge.
        var complianceRoot = Path.Combine(_root, ".agenteval", "compliance", "EU-AI-Act", "EuAiActAttestationAgent");
        var tsDir = Directory.GetDirectories(complianceRoot).OrderByDescending(d => d).First();
        var baseEvidencePath = Path.Combine(tsDir, "evidence.json");
        Assert.True(File.Exists(baseEvidencePath));

        var baseJson = JsonDocument.Parse(File.ReadAllText(baseEvidencePath));
        var attestation = baseJson.RootElement.GetProperty("attestation");
        // JudgeFactory.Resolve returns "override" as the judge model when evaluatorOverride is supplied.
        Assert.Equal("override", attestation.GetProperty("evaluatorModel").GetString());
        Assert.Equal("AgentEval.Compliance.EuAiAct", attestation.GetProperty("evaluator").GetString());
    }
}
