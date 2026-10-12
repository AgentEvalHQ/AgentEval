// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Tests for the <c>agenteval bench gdpr</c> subcommand (<see cref="BenchCommand"/>).
/// </summary>
[Collection("EnvVarTests")]
public class BenchCommandTests : IDisposable
{
    private readonly string _root;

    public BenchCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-bench-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("gdpr", 0)]
    [InlineData("gdpr", 1)]
    [InlineData("eu-ai-act", 0)]
    [InlineData("eu-ai-act", 1)]
    public async Task CancelledComposite_PreservesFinishedLeavesAndWithholdsGate(string family, int completed)
    {
        InitWorkspace();
        using var cancel = new CancellationTokenSource();
        var evaluator = new CancellingEvaluator(cancel, completed);
        var root = new DirectoryInfo(Path.Combine(_root, "cancel-aef"));
        var exit = family == "gdpr"
            ? await BenchCommand.RunGdprAsync("standard", "subject", _root, "question",
                evaluatorOverride: evaluator, responseText: "secret answer", aef: root, ct: cancel.Token)
            : await BenchEuAiActCommand.RunAsync("standard", "subject", _root, "question",
                evaluatorOverride: evaluator, responseText: "secret answer", aef: root, ct: cancel.Token);

        Assert.Equal(1, exit);
        var dir = Path.GetDirectoryName(Assert.Single(Directory.GetFiles(root.FullName, "run.json", SearchOption.AllDirectories)))!;
        var verified = AgentEval.Results.Integrity.AefRunVerifier.Verify(dir);
        Assert.Equal(AgentEval.Results.Integrity.AefOutcome.Intact, verified.Outcome);
        Assert.Empty(verified.Problems);
        var header = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "run.json")))!;
        Assert.Equal("aborted", (string?)header["status"]);
        Assert.Equal("replayed", (string?)header["execution"]!["targetMode"]);
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => System.Text.Json.Nodes.JsonNode.Parse(l)!).ToList();
        Assert.Contains(lines, l => (string?)l["state"] == "error");
        if (completed > 0) Assert.Contains(lines, l => (string?)l["state"] == "passed");
        Assert.DoesNotContain("secret answer", string.Concat(Directory.GetFiles(dir).Select(File.ReadAllText)), StringComparison.Ordinal);
    }

    private sealed class CancellingEvaluator(CancellationTokenSource cancel, int completed) : IEvaluator
    {
        private int _calls;
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) > completed)
            {
                cancel.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return new PassingStubEvaluator().EvaluateAsync(input, output, criteria, cancellationToken);
        }
    }

    [Fact]
    public async Task RepeatedRuns_WriteSealedTrialTrees()
    {
        InitWorkspace();
        var root = new DirectoryInfo(Path.Combine(_root, "trial-aef"));
        var exit = await BenchCommand.RunGdprAsync("standard", "subject", _root, "question",
            evaluatorOverride: new PassingStubEvaluator(), responseText: "answer", runs: 2, aef: root);

        var headerPath = Assert.Single(Directory.GetFiles(root.FullName, "run.json", SearchOption.AllDirectories));
        var dir = Path.GetDirectoryName(headerPath)!;
        var verification = AgentEval.Results.Integrity.AefRunVerifier.Verify(dir);
        Assert.Equal(AgentEval.Results.Integrity.AefOutcome.Intact, verification.Outcome);
        Assert.Empty(verification.Problems);
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson"))
            .Select(l => System.Text.Json.Nodes.JsonNode.Parse(l)!).ToList();
        Assert.Contains(lines, l => (int?)l["trial"] == 1);
        Assert.Contains(lines, l => (int?)l["trial"] == 0);
        Assert.All(lines.Where(l => l["trial"] is null), l => Assert.Equal(2, (int)l["trials"]!["n"]!));
        var gate = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllLines(Path.Combine(dir, "gates.ndjson")).Single())!;
        Assert.Equal(exit, (int)gate["exitCode"]!);
    }

    private void InitWorkspace()
    {
        var dir = Path.Combine(_root, ".agenteval");
        Directory.CreateDirectory(dir);
        var solutionDoc = new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "BenchTestSolution" };
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
            var criteriaList = criteria.ToList();
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 100,
                Summary = "stub-pass",
                CriteriaResults = criteriaList
                    .Select(c => new CriterionResult { Criterion = c, Met = true, Explanation = "stub" })
                    .ToList()
            });
        }
    }

    private sealed class FailingStubEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default)
        {
            var criteriaList = criteria.ToList();
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 10,
                Summary = "stub-fail",
                CriteriaResults = criteriaList
                    .Select(c => new CriterionResult { Criterion = c, Met = false, Explanation = "stub-fail" })
                    .ToList()
            });
        }
    }

    /// <summary>Fixed-answer fake agent — simulates what a resolved --sut target would hand to RunGdprAsync's agentOverride parameter.</summary>
    private sealed class FixedAnswerAgent : IEvaluableAgent
    {
        private readonly string _answer;
        public string Name { get; }
        public FixedAnswerAgent(string name, string answer) { Name = name; _answer = answer; }
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse { Text = _answer });
    }

    /// <summary>Stub evaluator that records the most recent graded output (the agent response).</summary>
    private sealed class CapturingStubEvaluator : IEvaluator
    {
        public string? LastOutput { get; private set; }

        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default)
        {
            LastOutput = output;
            var criteriaList = criteria.ToList();
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 100,
                Summary = "stub-capture",
                CriteriaResults = criteriaList
                    .Select(c => new CriterionResult { Criterion = c, Met = true, Explanation = "stub" })
                    .ToList()
            });
        }
    }

    private const string SuppliedQuestion = "What personal data do you store?";
    private const string SuppliedAnswer = "We keep your name and email for your account. Ask us at any time and we delete them.";

    private string[] WorkspaceFiles() =>
        Directory.GetFileSystemEntries(Path.Combine(_root, ".agenteval"), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    // ── Tests ─────────────────────────────────────────────────────────────

    private static System.Text.Json.Nodes.JsonNode AefRun(string aefRoot, out string dir)
    {
        var run = Assert.Single(Directory.GetFiles(aefRoot, "run.json", SearchOption.AllDirectories));
        dir = Path.GetDirectoryName(run)!;
        var verification = AgentEval.Results.Integrity.AefRunVerifier.Verify(dir);
        Assert.Equal(AgentEval.Results.Integrity.AefOutcome.Intact, verification.Outcome);
        Assert.Empty(verification.Problems);
        return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(run))!;
    }

    [Fact]
    public async Task BenchGdpr_GradingASuppliedResponse_WritesASealedReplayedRun_IntoTheWorkspace()
    {
        InitWorkspace();

        await BenchCommand.RunGdprAsync(preset: "smoke", subject: "ResponseAgent", rootOverride: _root, inputText: SuppliedQuestion,
            evaluatorOverride: new CapturingStubEvaluator(), responseText: SuppliedAnswer, subjectVersion: "2.1.0");

        var header = AefRun(Path.Combine(_root, ".agenteval", "aef"), out var dir);
        Assert.Equal("replayed", (string?)header["execution"]!["targetMode"]);   // the subject was not driven
        Assert.Equal("agent:ResponseAgent", (string?)header["subject"]!["ref"]);
        Assert.Equal("2.1.0", (string?)header["subject"]!["version"]);
        Assert.StartsWith("bench:gdpr", (string?)header["suite"]!["ref"]);
        Assert.NotEmpty(File.ReadAllLines(Path.Combine(dir, "results.ndjson")));
        var all = string.Concat(Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("delete them", all, StringComparison.Ordinal);   // the graded answer is content
    }

    [Fact]
    public async Task BenchGdpr_WithAStandInAgent_IsMocked_AndNoAefWritesNothing()
    {
        InitWorkspace();
        var aef = Path.Combine(_root, "aef-out");

        await BenchCommand.RunGdprAsync(preset: "smoke", subject: "SutAgent", rootOverride: _root, inputText: null,
            evaluatorOverride: new CapturingStubEvaluator(), agentOverride: new FixedAnswerAgent("SutAgent", "an answer"),
            aef: new DirectoryInfo(aef));
        Assert.Equal("mocked", (string?)AefRun(aef, out _)["execution"]!["targetMode"]);   // not resolved from --sut

        var none = Path.Combine(_root, "aef-none");
        await BenchCommand.RunGdprAsync(preset: "smoke", subject: "SutAgent", rootOverride: _root, inputText: null,
            evaluatorOverride: new CapturingStubEvaluator(), agentOverride: new FixedAnswerAgent("SutAgent", "an answer"),
            aef: new DirectoryInfo(none), noAef: true);
        Assert.False(Directory.Exists(none));
    }

    [Fact]
    public async Task BenchGdpr_WithResponseText_GradesSuppliedResponse_NotFixture()
    {
        // BUG-18: the graded response must be the operator-supplied one, not the canned fixture.
        InitWorkspace();
        var capturing = new CapturingStubEvaluator();
        const string realResponse = "MY-REAL-AGENT-RESPONSE-XYZ unique marker";

        var exitCode = await BenchCommand.RunGdprAsync(
            preset: "smoke",
            subject: "ResponseAgent",
            rootOverride: _root,
            inputText: "What personal data do you store?",
            evaluatorOverride: capturing,
            responseText: realResponse);

        Assert.True(exitCode == 0 || exitCode == 9 || exitCode == 10 || exitCode == 11); // gate PASS, or FAIL/WARN/indeterminate (BUG-22 split)
        Assert.Equal(realResponse, capturing.LastOutput);
        Assert.DoesNotContain("privacy@example.com", capturing.LastOutput ?? ""); // not the fixture
    }

    [Fact]
    public async Task BenchGdpr_WithAgentOverride_DrivesTheAgentPerScenario_NotAStaticResponse()
    {
        // Bench Tier 2 (§2.2): agentOverride is what a resolved --sut target's IEvaluableAgent flows in as.
        // ScenarioToAtomicEval already accepted this parameter (pre-existing, generic) — this proves the CLI
        // wiring actually reaches it, and that agentOverride wins even when --response text is also supplied.
        InitWorkspace();
        var capturing = new CapturingStubEvaluator();
        const string agentAnswer = "AGENT-OVERRIDE-DROVE-THIS unique marker";
        var agent = new FixedAnswerAgent("SutAgent", agentAnswer);

        var exitCode = await BenchCommand.RunGdprAsync(
            preset: "smoke",
            subject: "SutAgent",
            rootOverride: _root,
            inputText: "What personal data do you store?",
            evaluatorOverride: capturing,
            agentOverride: agent,
            responseText: "this static text should be ignored — agentOverride takes priority");

        Assert.True(exitCode == 0 || exitCode == 9 || exitCode == 10 || exitCode == 11); // gate PASS, or FAIL/WARN/indeterminate (BUG-22 split)
        Assert.Equal(agentAnswer, capturing.LastOutput);
    }

    [Fact]
    public async Task BenchGdpr_NoTarget_Refuses_AndGradesNothing()
    {
        // Through 0.42 a run with no target graded a built-in answer and stored it as the subject's evidence.
        InitWorkspace();
        var capturing = new CapturingStubEvaluator();
        var before = WorkspaceFiles();

        var exitCode = await BenchCommand.RunGdprAsync(
            preset: "smoke",
            subject: "NoTargetAgent",
            rootOverride: _root,
            inputText: SuppliedQuestion,
            evaluatorOverride: capturing,
            responseText: null);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Null(capturing.LastOutput);
        Assert.Equal(before, WorkspaceFiles());
    }

    [Fact]
    public async Task BenchGdpr_Mock_UsesTheMockJudge_AndNeverReadsTheProvider()
    {
        // A selector naming a provider with no variables makes any real judge resolution fail closed (exit 3)
        // without a network call. A mock run must not reach it: it neither needs nor bills a judge.
        InitWorkspace();
        using var env = new ProviderEnvironmentScope(("AI_INFERENCE_PROVIDER", "foundry"));

        var exitCode = await BenchCommand.RunGdprAsync(
            preset: "smoke",
            subject: "MockNoProviderAgent",
            rootOverride: _root,
            inputText: null,
            evaluatorOverride: null,
            mock: true);

        Assert.Equal(ExitCodes.GateIndeterminate, exitCode);
    }

    [Fact]
    public async Task BenchGdpr_MockWithARealTarget_IsRefusedByTheCommandItself()
    {
        InitWorkspace();
        var capturing = new CapturingStubEvaluator();

        var exitCode = await BenchCommand.RunGdprAsync(
            preset: "smoke",
            subject: "MockPlusAnswerAgent",
            rootOverride: _root,
            inputText: SuppliedQuestion,
            evaluatorOverride: capturing,
            responseText: SuppliedAnswer,
            mock: true);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Null(capturing.LastOutput);
    }

    [Fact]
    public async Task BenchGdpr_SuppliedAnswerWithoutItsQuestion_Refuses()
    {
        // The answer is graded against the question it answered, never a built-in one.
        InitWorkspace();
        var capturing = new CapturingStubEvaluator();

        var exitCode = await BenchCommand.RunGdprAsync(
            preset: "smoke",
            subject: "NoQuestionAgent",
            rootOverride: _root,
            inputText: null,
            evaluatorOverride: capturing,
            responseText: SuppliedAnswer);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Null(capturing.LastOutput);
    }

    [Fact]
    public async Task BenchGdpr_Mock_GradesTheCannedAnswer_ExitsIndeterminate_AndStoresNothing()
    {
        InitWorkspace();
        var capturing = new CapturingStubEvaluator();
        var before = WorkspaceFiles();

        var exitCode = await BenchCommand.RunGdprAsync(
            preset: "smoke",
            subject: "MockAgent",
            rootOverride: _root,
            inputText: null,
            evaluatorOverride: capturing,
            responseText: null,
            mock: true);

        Assert.Equal(ExitCodes.GateIndeterminate, exitCode);
        Assert.Contains("privacy@example.com", capturing.LastOutput ?? "");
        Assert.Equal(before, WorkspaceFiles());
    }

    [Fact]
    public async Task BenchGdpr_MissingWorkspace_ReturnsExitCode1()
    {
        // Arrange — no .agenteval/ directory initialised
        var noWorkspaceRoot = Path.Combine(_root, "no-workspace");
        Directory.CreateDirectory(noWorkspaceRoot);

        // Act
        var exitCode = await BenchCommand.RunGdprAsync(
            preset: "smoke",
            subject: "TestAgent",
            rootOverride: noWorkspaceRoot,
            inputText: SuppliedQuestion,
            evaluatorOverride: new PassingStubEvaluator(),
            responseText: SuppliedAnswer);

        // Assert
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task BenchGdpr_SmokePreset_PassingStub_ReturnsExitCode0()
    {
        // Arrange
        InitWorkspace();

        // Act
        var exitCode = await BenchCommand.RunGdprAsync(
            preset: "smoke",
            subject: "SmokeTestAgent",
            rootOverride: _root,
            inputText: SuppliedQuestion,
            evaluatorOverride: new PassingStubEvaluator(),
            responseText: SuppliedAnswer);

        // Assert — passing benchmark exits 0
        Assert.Equal(0, exitCode);

        // Verify that the compliance directory was created with artifacts
        var agentEvalDir = Path.Combine(_root, ".agenteval");
        var complianceRoot = Path.Combine(agentEvalDir, "compliance", "GDPR", "SmokeTestAgent");
        Assert.True(Directory.Exists(complianceRoot),
            $"Expected compliance directory at {complianceRoot}");

        // At least one timestamp directory should exist
        var tsDirs = Directory.GetDirectories(complianceRoot);
        Assert.NotEmpty(tsDirs);
    }

    [Fact]
    public async Task BenchGdpr_SmokePreset_FailingStub_ReturnsNonZeroExitCode()
    {
        // Arrange
        InitWorkspace();

        // Act
        var exitCode = await BenchCommand.RunGdprAsync(
            preset: "smoke",
            subject: "FailAgent",
            rootOverride: _root,
            inputText: SuppliedQuestion,
            evaluatorOverride: new FailingStubEvaluator(),
            responseText: SuppliedAnswer);

        // Assert — a failing grade (9). Through 0.42 this test accepted any non-zero exit, so a refusal (2), a crash
        // (1) or a judge configuration error (3) would have satisfied it without grading anything.
        Assert.Equal(ExitCodes.GateFailed, exitCode);
    }

    [Fact]
    public async Task BenchGdpr_StandardPreset_PassingStub_WritesReportFiles()
    {
        // Arrange
        InitWorkspace();

        // Act
        var exitCode = await BenchCommand.RunGdprAsync(
            preset: "standard",
            subject: "StandardTestAgent",
            rootOverride: _root,
            inputText: SuppliedQuestion,
            evaluatorOverride: new PassingStubEvaluator(),
            responseText: SuppliedAnswer);

        // Assert — exit cleanly + evidence directory contains report.md and report.pdf
        Assert.Equal(0, exitCode);
        var agentEvalDir = Path.Combine(_root, ".agenteval");
        var complianceRoot = Path.Combine(agentEvalDir, "compliance", "GDPR", "StandardTestAgent");
        var tsDir = Directory.GetDirectories(complianceRoot).OrderByDescending(d => d).First();

        Assert.True(File.Exists(Path.Combine(tsDir, "report.md")), "report.md should be generated");
        Assert.True(File.Exists(Path.Combine(tsDir, "report.pdf")), "report.pdf should be generated");
        Assert.True(new FileInfo(Path.Combine(tsDir, "report.pdf")).Length > 0,
            "report.pdf should be non-empty");
    }

    /// <summary>
    /// Phase 2 / Task 2.1 regression — composite preset (`standard+healthcare`) previously
    /// crashed at SaveReportAsync because the schema enum only listed the six base names.
    /// The fix splits the preset into a base + `domainPacks[]` array; verify the full
    /// pipeline writes schema-valid evidence end-to-end and records the pack.
    /// </summary>
    [Fact]
    public async Task BenchGdpr_StandardPlusHealthcare_E2E_WritesValidEvidence()
    {
        // Arrange
        InitWorkspace();

        // Act
        var exitCode = await BenchCommand.RunGdprAsync(
            preset: "standard+healthcare",
            subject: "ComposedPresetAgent",
            rootOverride: _root,
            inputText: SuppliedQuestion,
            evaluatorOverride: new PassingStubEvaluator(),
            responseText: SuppliedAnswer);

        // Assert — exit cleanly + evidence files present
        Assert.Equal(0, exitCode);
        var complianceRoot = Path.Combine(_root, ".agenteval", "compliance", "GDPR", "ComposedPresetAgent");
        var tsDir = Directory.GetDirectories(complianceRoot).OrderByDescending(d => d).First();

        // gdpr-evidence.json was the file that crashed pre-fix; its mere presence proves the schema accepted the composite preset.
        var gdprEvidencePath = Path.Combine(tsDir, "gdpr-evidence.json");
        Assert.True(File.Exists(gdprEvidencePath),
            $"gdpr-evidence.json should be present at {gdprEvidencePath} — if missing, SaveReportAsync's schema validation rejected the composite preset.");

        // The wrapper should record the base preset cleanly + the pack in domainPacks.
        var gdprJson = JsonDocument.Parse(File.ReadAllText(gdprEvidencePath));
        Assert.Equal("standard", gdprJson.RootElement.GetProperty("preset").GetString());
        var packs = gdprJson.RootElement.GetProperty("domainPacks").EnumerateArray()
            .Select(p => p.GetString()).ToArray();
        Assert.Contains("healthcare", packs);
    }

    /// <summary>
    /// Phase 2 / Task 2.3 regression — Attestation.EvaluatorModel previously hard-coded
    /// "internal" regardless of which judge actually ran. With pass-3's JudgeFactory wiring,
    /// the test-override path records "override"; an Azure path would record the deployment.
    /// </summary>
    [Fact]
    public async Task BenchGdpr_RecordsJudgeModelInAttestation()
    {
        // Arrange
        InitWorkspace();

        // Act
        await BenchCommand.RunGdprAsync(
            preset: "smoke",
            subject: "AttestationTestAgent",
            rootOverride: _root,
            inputText: SuppliedQuestion,
            evaluatorOverride: new PassingStubEvaluator(),
            responseText: SuppliedAnswer);

        // Assert — base evidence.json's attestation.evaluatorModel reflects the resolved judge.
        var complianceRoot = Path.Combine(_root, ".agenteval", "compliance", "GDPR", "AttestationTestAgent");
        var tsDir = Directory.GetDirectories(complianceRoot).OrderByDescending(d => d).First();
        var baseEvidencePath = Path.Combine(tsDir, "evidence.json");
        Assert.True(File.Exists(baseEvidencePath));

        var baseJson = JsonDocument.Parse(File.ReadAllText(baseEvidencePath));
        var attestation = baseJson.RootElement.GetProperty("attestation");
        // JudgeFactory.Resolve returns "override" as the judge model when evaluatorOverride is supplied.
        Assert.Equal("override", attestation.GetProperty("evaluatorModel").GetString());
        Assert.Equal("AgentEval.Compliance.Gdpr", attestation.GetProperty("evaluator").GetString());
    }

    // ── No judge configured ──────────────────────────────────────────────────
    //
    // These tests scrub the AZURE_OPENAI_* env vars to simulate a CI run that
    // forgot to wire the secrets. With evaluatorOverride=null the resolver
    // falls into the gate path. Marked [Collection("EnvVarTests")] so they
    // don't race with parallel tests that also touch env vars.

    [Fact]
    public async Task RunGdprAsync_NoProvider_ReturnsExitCode3()
    {
        InitWorkspace();

        // Snapshot + scrub env vars so the resolver hits the no-config branch.
        var envAzureEndpoint   = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var envAzureKey        = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var envAzureDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
        var envAllowStub       = Environment.GetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", null);
            Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", null);

            var exit = await BenchCommand.RunGdprAsync(
                preset: "smoke",
                subject: "EnvGateTest",
                rootOverride: _root,
                inputText: SuppliedQuestion,
                evaluatorOverride: null,
                responseText: SuppliedAnswer);

            Assert.Equal(3, exit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT",     envAzureEndpoint);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY",      envAzureKey);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT",   envAzureDeployment);
            Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", envAllowStub);
        }
    }

    [Fact]
    public async Task RunGdprAsync_PartialAzureConfig_ReturnsExitCode3()
    {
        // Endpoint set but API key + deployment missing — must NOT fall through
        // to the stub silently. The resolver flags partial config explicitly.
        InitWorkspace();
        var snap = (
            Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"),
            Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),
            Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT"),
            Environment.GetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE"));
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com");
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY",  null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", null);
            Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", null);

            var exit = await BenchCommand.RunGdprAsync(
                preset: "smoke",
                subject: "PartialConfigTest",
                rootOverride: _root,
                inputText: SuppliedQuestion,
                evaluatorOverride: null,
                responseText: SuppliedAnswer);

            Assert.Equal(3, exit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT",     snap.Item1);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY",      snap.Item2);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT",   snap.Item3);
            Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", snap.Item4);
        }
    }
}
