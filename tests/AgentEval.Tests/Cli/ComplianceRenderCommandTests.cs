// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using AgentEval.Cli.Commands;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Loading;
using AgentEval.Compliance.Gdpr.Reporting;
using AgentEval.Output;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;
using Xunit;
using AgentEval.Compliance.Core;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Tests for the <c>agenteval compliance render</c> subcommand (<see cref="ComplianceRenderCommand"/>).
/// </summary>
public class ComplianceRenderCommandTests : IDisposable
{
    private readonly string _root;

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ComplianceRenderCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-render-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task<string> WriteFixtureEvidenceAsync(string subjectName)
    {
        var ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        var sanitizedSubject = subjectName.Replace(' ', '-');
        var evidenceDir = Path.Combine(
            _root, ".agenteval", "compliance", "GDPR", sanitizedSubject, ts);
        Directory.CreateDirectory(evidenceDir);

        var evidence = MakeFixtureEvidence(subjectName);
        var evidenceFile = Path.Combine(evidenceDir, "gdpr-evidence.json");
        await using var stream = File.Create(evidenceFile);
        await JsonSerializer.SerializeAsync(stream, evidence, s_jsonOpts);

        return ts;
    }

    internal static GdprComplianceEvidence MakeFixtureEvidence(string subjectName)
    {
        var subject = new SubjectIdentity(SubjectKind.Agent, subjectName);
        var baseEvidence = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: "GDPR",
            Subject: subject,
            GeneratedAt: DateTimeOffset.UtcNow,
            SourceRun: new SourceRunRef("run-render-001", "sha256:" + new string('b', 64)),
            Controls: [],
            Summary: new EvidenceSummary(1, 1, 0, 0, "PASS"),
            Attestation: new Attestation("0.0.0", null, "AgentEval.Compliance.Gdpr", "stub"));

        var articleLeaf = new AgentEval.Evals.EvalResult(
            Metric: new("gdpr.art17.erasure-001", "scenario", "compliance", "1.0"),
            Score: new(0.90, null, "pass", true, 0.75, "high", null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic", "stub", null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        var articleComposite = new AgentEval.Evals.EvalResult(
            Metric: new("gdpr.art17.erasure", "Erasure", "compliance", "1.0"),
            Score: new(0.90, null, "pass", true, 0.75, "high", null),
            Details: new(null, null, null, [articleLeaf], "WeightedSum"),
            Provenance: new("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        var compositeTree = new AgentEval.Evals.EvalResult(
            Metric: new("gdpr.compliance.smoke", "GDPR Compliance Smoke", "compliance.gdpr", "1.0"),
            Score: new(0.90, null, "pass", true, 0.80, "none", null),
            Details: new(null, null, null, [articleComposite], "WeightedSum"),
            Provenance: new("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        return new GdprComplianceEvidence(
            Base: baseEvidence,
            Preset: "smoke",
            DomainPacks: Array.Empty<string>(),
            CompositeTree: compositeTree,
            Summary: new GdprSummary(
                OverallScore: 0.90,
                OverallStatus: "PASS",
                PerPillar: new Dictionary<string, GdprPillarSummary>(),
                PerArticle: new Dictionary<string, GdprArticleSummary>
                {
                    ["gdpr.art17.erasure"] = new(0.90, "PASS", 1, 0, "high")
                }),
            CriticalFindings: [],
            Recommendations: Array.Empty<AgentEval.Compliance.Core.Recommendation>(),
            Disclaimer: GDPRComplianceReporter.Disclaimer,
            GdprAttestation: new GdprAttestation(
                "mode-a",
                new Dictionary<string, string> { ["gdpr-judge-system"] = "v1" }));
    }

    private static ArticlesRegistry BuildRegistry()
    {
        var loader = new ArticleScenarioYamlLoader();
        var stub = new StubEval();
        var scenarioBuilder = new ScenarioToAtomicEval(stub, judgeModel: "stub");
        var builder = new ArticleCompositeBuilder(scenarioBuilder);
        return new ArticlesRegistry(loader, builder);
    }

    private sealed class StubEval : AgentEval.Core.IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentEval.Core.EvaluationResult { OverallScore = 100, Summary = "stub" });
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ComplianceRender_NativeOnlyEvidence_UsesCapturedArtifactOrReportsItsAbsence(bool capture)
    {
        const string subjectName = "NativeRenderAgent";
        var evidence = MakeFixtureEvidence(subjectName);
        var timestamp = evidence.Base.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        var directory = WriteNativeEvidence(evidence, capture);
        var seal = File.ReadAllBytes(Path.Combine(directory, "seal.json"));
        Assert.False(Directory.Exists(Path.Combine(_root, ".agenteval", "compliance")));

        var exit = await ComplianceRenderCommand.RunAsync("gdpr", subjectName, timestamp, _root, BuildRegistry());

        Assert.Equal(capture ? 0 : 1, exit);
        var pdf = Path.Combine(_root, ".agenteval", "compliance", "GDPR", subjectName, timestamp, "report.pdf");
        if (capture)
        {
            Assert.True(File.Exists(pdf));
            Assert.True(new FileInfo(pdf).Length > 1_000);
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(pdf), 0, 4));
        }
        else Assert.False(File.Exists(pdf));
        Assert.Equal(seal, File.ReadAllBytes(Path.Combine(directory, "seal.json")));
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
    }

    [Fact]
    public async Task ComplianceRender_LatestNativeReportUncaptured_DoesNotSubstituteOlderCapturedReport()
    {
        const string subjectName = "LatestUncapturedAgent";
        var fixture = MakeFixtureEvidence(subjectName);
        var earlier = fixture with { Base = fixture.Base with { GeneratedAt = fixture.Base.GeneratedAt.AddHours(-2) } };
        WriteNativeEvidence(earlier, true);
        var later = earlier with { Base = earlier.Base with { GeneratedAt = earlier.Base.GeneratedAt.AddHours(1) } };
        WriteNativeEvidence(later, false);
        Assert.Equal(1, await ComplianceRenderCommand.RunAsync("gdpr", subjectName, null, _root, BuildRegistry()));
        Assert.Equal(0, await ComplianceRenderCommand.RunAsync("gdpr", subjectName,
            earlier.Base.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss"), _root, BuildRegistry()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComplianceRender_AgreeingNativeAndLegacyReports_RendersOnce(bool tags)
    {
        const string subjectName = "DualReportsAgent";
        var evidence = MakeFixtureEvidence(subjectName);
        if (tags) evidence = evidence with { Base = evidence.Base with { Subject = evidence.Base.Subject with { Tags = ["evaluated"] } } };
        WriteNativeEvidence(evidence, true);
        var timestamp = evidence.Base.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        var legacy = Path.Combine(_root, ".agenteval", "compliance", "GDPR", subjectName, timestamp);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "gdpr-evidence.json"), JsonSerializer.Serialize(evidence, s_jsonOpts));
        Assert.Equal(0, await ComplianceRenderCommand.RunAsync("gdpr", subjectName, timestamp, _root, BuildRegistry()));
        Assert.True(File.Exists(Path.Combine(legacy, "report.pdf")));
    }

    [Fact]
    public async Task ComplianceRender_ReusedRunIdWithDifferentSourceHash_RefusesAmbiguousIdentity()
    {
        const string subjectName = "ReusedIdAgent";
        var evidence = MakeFixtureEvidence(subjectName);
        WriteNativeEvidence(evidence, true);
        var timestamp = evidence.Base.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        var legacy = Path.Combine(_root, ".agenteval", "compliance", "GDPR", subjectName, timestamp);
        Directory.CreateDirectory(legacy);
        var other = evidence with { Base = evidence.Base with { SourceRun = evidence.Base.SourceRun with { ManifestHash = "sha256:" + new string('c', 64) } } };
        File.WriteAllText(Path.Combine(legacy, "gdpr-evidence.json"), JsonSerializer.Serialize(other, s_jsonOpts));
        Assert.Equal(1, await ComplianceRenderCommand.RunAsync("gdpr", subjectName, timestamp, _root, BuildRegistry()));
        Assert.False(File.Exists(Path.Combine(legacy, "report.pdf")));
    }

    [Fact]
    public async Task ComplianceRender_DistinctNativeReportsShareTimestamp_RefusesArbitrarySelection()
    {
        const string subjectName = "CollisionAgent";
        var evidence = MakeFixtureEvidence(subjectName);
        WriteNativeEvidence(evidence, true);
        var other = evidence with { Base = evidence.Base with { SourceRun = evidence.Base.SourceRun with { RunId = "another-run" } } };
        WriteNativeEvidence(other, true);
        Assert.Equal(1, await ComplianceRenderCommand.RunAsync("gdpr", subjectName,
            evidence.Base.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss"), _root, BuildRegistry()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComplianceRender_EuAiActNativeOrLegacy_ProducesPdf(bool native)
    {
        const string subjectName = "EuAiActAgent";
        var gdpr = MakeFixtureEvidence(subjectName);
        // The two report families share the structural summary contract, with distinct attestation names.
        var json = JsonSerializer.SerializeToNode(gdpr, s_jsonOpts)!;
        json["base"]!["regulation"] = "EU-AI-Act";
        json["euAiActAttestation"] = json["gdprAttestation"]!.DeepClone();
        json.AsObject().Remove("gdprAttestation");
        var euJson = json.ToJsonString().Replace("gdpr.art17.erasure", "eu_ai.art13.transparency", StringComparison.Ordinal)
            .Replace("gdpr.compliance.smoke", "eu_ai.compliance.smoke", StringComparison.Ordinal)
            .Replace("compliance.gdpr", "compliance.eu-ai-act", StringComparison.Ordinal);
        var bytes = System.Text.Encoding.UTF8.GetBytes(euJson);
        var euEvidence = JsonSerializer.Deserialize<AgentEval.Compliance.EuAiAct.Reporting.EuAiActComplianceEvidence>(bytes, s_jsonOpts)!;
        var timestamp = gdpr.Base.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        var target = Path.Combine(_root, ".agenteval", "compliance", "EU-AI-Act", subjectName, timestamp);
        if (native)
        {
            AefCompositeRunWriter.Write(Path.Combine(_root, ".agenteval", "aef", "runs"), euEvidence.CompositeTree,
                AefCompositeRunWriter.Undescribed, new()
                {
                    SubjectRef = "agent:" + subjectName, TargetMode = AefTargetMode.Mocked,
                    Lane = "eu-ai-act", SuiteRef = "bench:eu-ai-act/smoke", ContentCapture = AefContentCapture.On,
                    ComplianceArtifacts = [new() { Bytes = bytes, CaseIds = ["eu_ai.art13.transparency"] }],
                }, gdpr.Base.GeneratedAt, gdpr.Base.GeneratedAt);
        }
        else
        {
            Directory.CreateDirectory(target);
            File.WriteAllBytes(Path.Combine(target, "eu-ai-act-evidence.json"), bytes);
        }
        Assert.Equal(0, await ComplianceRenderCommand.RunAsync("eu-ai-act", subjectName, timestamp, _root));
        var pdf = Path.Combine(target, "report.pdf");
        Assert.True(new FileInfo(pdf).Length > 1000);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(pdf), 0, 4));
    }

    [Fact]
    public async Task ComplianceRender_ConflictingNativeAndLegacyReports_RefusesInsteadOfDroppingLegacy()
    {
        const string subjectName = "ConflictingReportsAgent";
        var evidence = MakeFixtureEvidence(subjectName);
        WriteNativeEvidence(evidence, true);
        var timestamp = evidence.Base.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        var legacy = Path.Combine(_root, ".agenteval", "compliance", "GDPR", subjectName, timestamp);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "gdpr-evidence.json"), JsonSerializer.Serialize(evidence with { Disclaimer = "Different claim" }, s_jsonOpts));
        Assert.Equal(1, await ComplianceRenderCommand.RunAsync("gdpr", subjectName, timestamp, _root, BuildRegistry()));
        Assert.False(File.Exists(Path.Combine(legacy, "report.pdf")));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("score")]
    [InlineData("tree-score")]
    public async Task ComplianceRender_MalformedRecordedReport_DoesNotInventDefaults(string damage)
    {
        const string subjectName = "MissingFactsAgent";
        var evidence = MakeFixtureEvidence(subjectName);
        var json = JsonSerializer.SerializeToNode(evidence, s_jsonOpts)!;
        if (damage == "count") json["summary"]!["perArticle"]!["gdpr.art17.erasure"]!.AsObject().Remove("scenariosFailed");
        if (damage == "score") json["summary"]!["overallScore"] = 4.2;
        if (damage == "tree-score") json["compositeTree"]!["score"]!.AsObject().Remove("value");
        WriteNativeEvidence(evidence, true, System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        var timestamp = evidence.Base.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        Assert.Equal(1, await ComplianceRenderCommand.RunAsync("gdpr", subjectName, timestamp, _root, BuildRegistry()));
        Assert.False(File.Exists(Path.Combine(_root, ".agenteval", "compliance", "GDPR", subjectName, timestamp, "report.pdf")));
    }

    [Fact]
    public async Task ComplianceRender_NativeReportAlreadyRendered_RendersAgain()
    {
        const string subjectName = "RepeatNativeAgent";
        var evidence = MakeFixtureEvidence(subjectName);
        WriteNativeEvidence(evidence, true);
        var timestamp = evidence.Base.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        Assert.Equal(0, await ComplianceRenderCommand.RunAsync("gdpr", subjectName, timestamp, _root, BuildRegistry()));
        Assert.Equal(0, await ComplianceRenderCommand.RunAsync("gdpr", subjectName, timestamp, _root, BuildRegistry()));
    }

    [Fact]
    public async Task ComplianceRender_CaptureOffWithLegacySibling_DoesNotRecoverUncapturedContent()
    {
        const string subjectName = "PrivateNativeAgent";
        var evidence = MakeFixtureEvidence(subjectName);
        WriteNativeEvidence(evidence, false);
        var timestamp = evidence.Base.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        var legacy = Path.Combine(_root, ".agenteval", "compliance", "GDPR", subjectName, timestamp);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "gdpr-evidence.json"), JsonSerializer.Serialize(evidence, s_jsonOpts));
        Assert.Equal(1, await ComplianceRenderCommand.RunAsync("gdpr", subjectName, timestamp, _root, BuildRegistry()));
        Assert.False(File.Exists(Path.Combine(legacy, "report.pdf")));
    }

    [Fact]
    public async Task ComplianceRender_NativeHeaderRemovedWithLegacySibling_RefusesFallback()
    {
        const string subjectName = "MissingHeaderAgent";
        var evidence = MakeFixtureEvidence(subjectName);
        var directory = WriteNativeEvidence(evidence, true);
        File.Delete(Path.Combine(directory, "run.json"));
        var timestamp = evidence.Base.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        var legacy = Path.Combine(_root, ".agenteval", "compliance", "GDPR", subjectName, timestamp);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "gdpr-evidence.json"), JsonSerializer.Serialize(evidence, s_jsonOpts));
        Assert.Equal(1, await ComplianceRenderCommand.RunAsync("gdpr", subjectName, timestamp, _root, BuildRegistry()));
        Assert.False(File.Exists(Path.Combine(legacy, "report.pdf")));
    }

    [Fact]
    public async Task ComplianceRender_TamperedNativeEvidenceWithLegacySibling_RefusesFallback()
    {
        const string subjectName = "TamperedNativeAgent";
        var evidence = MakeFixtureEvidence(subjectName);
        var timestamp = evidence.Base.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        var directory = WriteNativeEvidence(evidence, capture: true);
        File.AppendAllText(Path.Combine(directory, "results.ndjson"), " ");
        Assert.Equal(AefOutcome.Invalid, AefRunVerifier.Verify(directory).Outcome);
        var legacy = Path.Combine(_root, ".agenteval", "compliance", "GDPR", subjectName, timestamp);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "gdpr-evidence.json"), JsonSerializer.Serialize(evidence, s_jsonOpts));

        var exit = await ComplianceRenderCommand.RunAsync("gdpr", subjectName, timestamp, _root, BuildRegistry());

        Assert.Equal(1, exit);
        Assert.False(File.Exists(Path.Combine(legacy, "report.pdf")));
    }

    internal string WriteNativeEvidence(GdprComplianceEvidence evidence, bool capture, params byte[][] artifacts)
    {
        if (artifacts.Length == 0) artifacts = [JsonSerializer.SerializeToUtf8Bytes(evidence, s_jsonOpts)];
        var directory = AefCompositeRunWriter.Write(Path.Combine(_root, ".agenteval", "aef", "runs"),
            evidence.CompositeTree, AefCompositeRunWriter.Undescribed, new()
            {
                SubjectRef = "agent:" + evidence.Base.Subject.Name,
                TargetMode = AefTargetMode.Mocked,
                Lane = "gdpr",
                SuiteRef = "bench:gdpr/smoke",
                ContentCapture = capture ? AefContentCapture.On : AefContentCapture.Off,
                ComplianceArtifacts = artifacts.Select(bytes => new AefComplianceArtifact
                {
                    Bytes = bytes,
                    CaseIds = ["gdpr.art17.erasure"],
                }).ToArray(),
            }, evidence.Base.GeneratedAt, evidence.Base.GeneratedAt).Directory;
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
        return directory;
    }

    [Fact]
    public async Task ComplianceRender_UnsupportedRegulation_ReturnsExitCode1()
    {
        // Arrange / Act
        var exitCode = await ComplianceRenderCommand.RunAsync(
            regulation: "sox",
            subject: "AnyAgent",
            ts: null,
            rootOverride: _root,
            registryOverride: null);

        // Assert
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task ComplianceRender_MissingEvidenceDirectory_ReturnsExitCode1()
    {
        // Arrange — .agenteval/compliance/GDPR/<subject> does not exist

        // Act
        var exitCode = await ComplianceRenderCommand.RunAsync(
            regulation: "gdpr",
            subject: "NonExistentAgent",
            ts: null,
            rootOverride: _root,
            registryOverride: null);

        // Assert
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task ComplianceRender_ValidEvidence_ProducesPdfFile()
    {
        // Arrange
        const string subjectName = "RenderTestAgent";
        var ts = await WriteFixtureEvidenceAsync(subjectName);
        var registry = BuildRegistry();

        // Act
        var exitCode = await ComplianceRenderCommand.RunAsync(
            regulation: "gdpr",
            subject: subjectName,
            ts: ts,
            rootOverride: _root,
            registryOverride: registry);

        // Assert
        Assert.Equal(0, exitCode);

        var evidenceDir = Path.Combine(
            _root, ".agenteval", "compliance", "GDPR", subjectName, ts);
        var pdfPath = Path.Combine(evidenceDir, "report.pdf");
        Assert.True(File.Exists(pdfPath), $"Expected PDF at {pdfPath}");
        Assert.True(new FileInfo(pdfPath).Length > 0, "PDF file should be non-empty");
    }

    [Fact]
    public async Task ComplianceRender_OmittedTs_UsesLatestDirectory()
    {
        // Arrange — write two evidence snapshots; command should find the latest
        const string subjectName = "AutoLatestAgent";
        await WriteFixtureEvidenceAsync(subjectName);
        await Task.Delay(1100); // ensure different second-resolution timestamps
        var latestTs = await WriteFixtureEvidenceAsync(subjectName);
        var registry = BuildRegistry();

        // Act — ts is null; should auto-select the most recent
        var exitCode = await ComplianceRenderCommand.RunAsync(
            regulation: "gdpr",
            subject: subjectName,
            ts: null,
            rootOverride: _root,
            registryOverride: registry);

        // Assert
        Assert.Equal(0, exitCode);

        // Verify the PDF was written in the latest ts directory
        var pdfPath = Path.Combine(
            _root, ".agenteval", "compliance", "GDPR", subjectName, latestTs, "report.pdf");
        Assert.True(File.Exists(pdfPath), $"Expected PDF at {pdfPath}");
    }
}
