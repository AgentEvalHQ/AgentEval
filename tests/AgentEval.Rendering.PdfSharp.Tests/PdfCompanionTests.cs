// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Cli.Pdf;
using AgentEval.Compliance.Gdpr.Reporting;
using AgentEval.Compliance.EuAiAct.Reporting;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Reporting;
using AgentEval.Output;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AgentEval.Rendering.PdfSharp.Tests;

public sealed class PdfCompanionTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("pdf-e2e-");
    private static readonly DateTimeOffset Generated = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public void Dispose() => directory.Delete(true);

    [Fact]
    public async Task Render_RecordedAgenticRun_RendersRichFactsWithoutChangingSealedSource()
    {
        var source = WriteRun(capture: true);
        var before = Hashes(source);
        var output = Path.Combine(directory.FullName, "agentic.pdf");
        Assert.Equal(0, await PdfCommand.RunAsync(["render", "--run", source, "--output", output]));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(output);
        var text = string.Join("\n", pdf.GetPages().Select(p => ContentOrderTextExtractor.GetText(p)));
        Assert.Contains("RECOMMENDATION-MARKER", text);
        Assert.Contains("EVIDENCE-MARKER", text);
        Assert.Contains("safety-security", text);
        Assert.Contains("0%", text.Replace(" ", ""));
        Assert.Contains("MODEL-VERSION", text);
        Assert.Equal(before, Hashes(source));
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(source).Outcome);
    }

    [Theory]
    [InlineData("gdpr", "GDPR")]
    [InlineData("eu-ai-act", "EU-AI-Act")]
    public async Task Render_RecordedComplianceReports_PreservesFactsAndWithholdsUnknownSensitivity(string family, string regulation)
    {
        var (result, report) = CreateComplianceReport(family, regulation);
        var source = AefCompositeRunWriter.Write(Path.Combine(directory.FullName, family), result, AefCompositeRunWriter.Undescribed, new()
        {
            SubjectRef = "agent:swedish", TargetMode = AefTargetMode.Mocked, Lane = family, SuiteRef = $"bench:{family}/smoke",
            ContentCapture = AefContentCapture.On,
            ComplianceArtifacts = [new() { Bytes = JsonSerializer.SerializeToUtf8Bytes(report, report.GetType(), JsonOptions), CaseIds = ["article"] }]
        }, Generated, Generated.AddSeconds(1)).Directory;
        var before = Hashes(source);
        var output = Path.Combine(directory.FullName, family + ".pdf");
        Assert.Equal(0, await PdfCommand.RunAsync(["render", "--run", source, "--output", output]));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(output);
        var text = string.Join("\n", pdf.GetPages().Select(p => ContentOrderTextExtractor.GetText(p)));
        foreach (var marker in new[] { "RECOMMENDATION-MARKER", "DISCLAIMER-MARKER", "PROMPT-VERSION", "MODEL-VERSION", "scenario", "30%", "FAIL", "metadata unavailable" })
            Assert.Contains(marker, text);
        Assert.DoesNotContain("PRIVATE-SCENARIO-MARKER", text);
        Assert.DoesNotContain("PRIVATE-REFERENCE", text);
        Assert.Equal(before, Hashes(source));
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(source).Outcome);
    }

    [Theory]
    [InlineData("agentic", true)]
    [InlineData("agentic", false)]
    [InlineData("gdpr", true)]
    [InlineData("gdpr", false)]
    [InlineData("eu-ai-act", true)]
    [InlineData("eu-ai-act", false)]
    public async Task Render_ImportedReportWithoutSuite_UsesVerifiedFamilyOrRequiresExplicitGeneric(string family, bool capture)
    {
        var legacy = Directory.CreateDirectory(Path.Combine(directory.FullName, "legacy"));
        object report = family == "agentic" ? CreateAgenticReport()
            : CreateComplianceReport(family, family == "gdpr" ? "GDPR" : "EU-AI-Act").Report;
        var filename = family == "agentic" ? "agentic-result.json" : family + "-evidence.json";
        await File.WriteAllBytesAsync(Path.Combine(legacy.FullName, filename), JsonSerializer.SerializeToUtf8Bytes(report, report.GetType(), JsonOptions));
        var options = new StoreV1ExportOptions { ContentCapture = capture ? AefContentCapture.On : AefContentCapture.Off };
        var destination = Path.Combine(directory.FullName, "imported");
        var source = (family == "agentic"
            ? StoreV1Exporter.ExportAgenticDirectory(legacy.FullName, destination, options)
            : StoreV1Exporter.ExportComplianceDirectory(legacy.FullName, destination, options)).Directory;
        var run = new AefRunEvidenceReader().Read(source);
        Assert.True(run.Info.Imported);
        Assert.False(run.Header.TryGetProperty("suite", out _));
        var before = Hashes(source);
        var output = Path.Combine(directory.FullName, "imported.pdf");

        Assert.Equal(capture ? 0 : 3, await PdfCommand.RunAsync(["render", "--run", source, "--output", output]));
        if (!capture)
        {
            Assert.False(File.Exists(output));
            Assert.Equal(0, await PdfCommand.RunAsync(["render", "--run", source, "--output", output, "--family", "generic"]));
        }
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(output);
        var text = string.Join("\n", pdf.GetPages().Select(p => ContentOrderTextExtractor.GetText(p)));
        if (capture) Assert.Contains("RECOMMENDATION-MARKER", text);
        else Assert.DoesNotContain("RECOMMENDATION-MARKER", text);
        Assert.DoesNotContain("PRIVATE-SCENARIO-MARKER", text);
        Assert.Equal(before, Hashes(source));
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(source).Outcome);
    }

    [Fact]
    public async Task Render_ImportedReportsWithMultipleFamilies_RequiresAnExplicitSelection()
    {
        var source = Path.Combine(directory.FullName, "ambiguous-import");
        var writer = AefRunWriter.Create(source, new()
        {
            RunId = "imported-reports", Producer = new() { Name = "tests", Version = "1.0" },
            Subject = new() { Ref = "agent:swedish", Kind = AefSubjectKind.Agent },
            StartedAt = Generated, ContentCapture = AefContentCapture.On,
            Execution = new() { TargetMode = AefTargetMode.Mocked },
            Imported = new() { From = "test:recorded-reports", Asserted = ["runId", "startedAt", "endedAt", "status", "abortReason", "execution.targetMode", "contentCapture"] }
        });
        foreach (var (family, regulation) in new[] { ("gdpr", "GDPR"), ("eu-ai-act", "EU-AI-Act") })
        {
            var report = CreateComplianceReport(family, regulation).Report;
            writer.AddEvidence(new()
            {
                EvidenceId = "E-" + family, Kind = AefEvidenceKind.ComplianceArtifact,
                Link = AefEvidenceLink.ToBlob(writer.PutBlob(JsonSerializer.SerializeToUtf8Bytes(report, report.GetType(), JsonOptions)))
            });
        }
        writer.Close(AefRunStatus.Aborted, Generated.AddSeconds(1), "Reports imported without original execution.");
        AefSealer.Seal(source);
        var before = Hashes(source);
        var error = Assert.Throws<InvalidDataException>(() => PdfCommand.Project(new AefRunEvidenceReader().Read(source), "auto", CancellationToken.None));
        Assert.Contains("Multiple rich report families", error.Message);
        var output = Path.Combine(directory.FullName, "selected.pdf");
        Assert.Equal(3, await PdfCommand.RunAsync(["render", "--run", source, "--output", output]));
        Assert.False(File.Exists(output));
        Assert.Equal(0, await PdfCommand.RunAsync(["render", "--run", source, "--output", output, "--family", "gdpr"]));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Contains("GDPR Compliance", ContentOrderTextExtractor.GetText(pdf.GetPage(1)));
        Assert.Equal(before, Hashes(source));
    }

    [Fact]
    public async Task Render_UncapturedRichReport_FailsWithoutSilentGenericFallback()
    {
        var source = WriteRun(capture: false);
        var output = Path.Combine(directory.FullName, "missing.pdf");
        Assert.Equal(3, await PdfCommand.RunAsync(["render", "--run", source, "--output", output]));
        Assert.False(File.Exists(output));
        Assert.Equal(0, await PdfCommand.RunAsync(["render", "--run", source, "--output", output, "--family", "generic"]));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.DoesNotContain("EVIDENCE-MARKER", string.Concat(pdf.GetPages().Select(p => p.Text)));
    }

    [Fact]
    public async Task Render_TamperedSource_RefusesOutput()
    {
        var source = WriteRun(true);
        await File.AppendAllTextAsync(Path.Combine(source, "run.json"), " ");
        var output = Path.Combine(directory.FullName, "tampered.pdf");
        Assert.Equal(3, await PdfCommand.RunAsync(["render", "--run", source, "--output", output]));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Render_DestinationInsideSealedSource_IsRejectedBeforeRendering()
    {
        var source = WriteRun(true); var before = Hashes(source);
        Assert.Equal(1, await PdfCommand.RunAsync(["render", "--run", source, "--output", Path.Combine(source, "report.pdf")]));
        Assert.Equal(before, Hashes(source));
    }

    [Fact]
    public async Task Render_ExistingOutput_RequiresExplicitOverwrite()
    {
        var source = WriteRun(true); var output = Path.Combine(directory.FullName, "existing.pdf");
        await File.WriteAllTextAsync(output, "original");
        Assert.Equal(1, await PdfCommand.RunAsync(["render", "--run", source, "--output", output]));
        Assert.Equal("original", await File.ReadAllTextAsync(output));
        Assert.Equal(0, await PdfCommand.RunAsync(["render", "--run", source, "--output", output, "--overwrite"]));
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(output), 0, 4));
    }

    [Fact]
    public async Task Render_CancelledRequest_ProducesNoOutput()
    {
        var source = WriteRun(true); var output = Path.Combine(directory.FullName, "cancelled.pdf");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Equal(130, await PdfCommand.RunAsync(["render", "--run", source, "--output", output], cancellation.Token));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("--family", "invalid")]
    [InlineData("--unknown", "value")]
    public async Task Render_InvalidOptions_FailsAsUsage(string option, string value)
    {
        var source = WriteRun(true);
        Assert.Equal(1, await PdfCommand.RunAsync(["render", "--run", source, "--output", Path.Combine(directory.FullName, "bad.pdf"), option, value]));
    }

    private string WriteRun(bool capture)
    {
        var report = CreateAgenticReport();
        return AefCompositeRunWriter.Write(Path.Combine(directory.FullName, "runs"), report.CompositeTree, AefCompositeRunWriter.Undescribed, new()
        {
            SubjectRef = "agent:swedish", TargetMode = AefTargetMode.Mocked, Lane = "agentic", SuiteRef = "bench:agentic/safety",
            ContentCapture = capture ? AefContentCapture.On : AefContentCapture.Off,
            Report = new() { Type = "agentic-result", GeneratedAt = Generated, SourceRunId = report.SourceRunId,
                SourceManifestHash = report.SourceManifestHash, Bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions) },
        }, Generated, Generated.AddSeconds(1)).Directory;
    }

    private static AgenticBenchmarkResult CreateAgenticReport()
    {
        var result = new EvalResult(new("check", "Check", "safety-security", "1.0"), new(0, null, "fail", false, 0.8, "high", null),
            new(null, [new("test", "reference", "EVIDENCE-MARKER Swedish å ä ö")], ["RECOMMENDATION-MARKER"], null, null),
            new("code", null, null, null, 0, 0, false), Generated);
        return new AgenticBenchmarkResult("safety", new(SubjectKind.Agent, "swedish"), "source-run", "sha256:" + new string('a', 64), Generated,
            result, new(0, "FAIL", new Dictionary<string, AgenticCategorySummary> { ["safety-security"] = new(0, "FAIL", []) },
                new Dictionary<string, AgenticEvaluatorSummary> { ["check"] = new(0, "FAIL", "high", null) }),
            [], ["RECOMMENDATION-MARKER"], "Not a compliance attestation.", new("MODEL-VERSION", "none", new Dictionary<string, string>()));
    }

    private static (EvalResult Result, object Report) CreateComplianceReport(string family, string regulation)
    {
        EvalResult Node(string key, IReadOnlyList<EvalResult>? children, string? text = null) => new(
            new(key, key, "compliance", "1.0"), new(0.3, null, "fail", false, 0.8, "high", null),
            new(null, text is null ? null : [new("test", "PRIVATE-REFERENCE", text)], null, children, "WeightedSum"),
            new("code", null, null, null, null, 0, false), Generated);
        var result = Node("compliance", [Node("article", [Node("scenario", null, "PRIVATE-SCENARIO-MARKER")])]);
        var basis = new ComplianceEvidence("1.0", regulation, new(SubjectKind.Agent, "swedish"), Generated,
            new("source-run", "sha256:" + new string('a', 64)), [], new(1, 0, 1, 0, "FAIL"), new("MODEL-VERSION", null, "code", "none"));
        object report = family == "gdpr"
            ? new GdprComplianceEvidence(basis, "smoke", ["healthcare"], result,
                new(0.3, "FAIL", new Dictionary<string, GdprPillarSummary>(), new Dictionary<string, GdprArticleSummary> { ["article"] = new(0.3, "FAIL", 1, 1, "high") }),
                [], [new("article", "high", "RECOMMENDATION-MARKER")], "DISCLAIMER-MARKER", new("mode-a", new Dictionary<string, string> { ["judge"] = "PROMPT-VERSION" }))
            : new EuAiActComplianceEvidence(basis, "standard", ["high-risk-employment"], result,
                new(0.3, "FAIL", new Dictionary<string, EuAiActPillarSummary>(), new Dictionary<string, EuAiActArticleSummary> { ["article"] = new(0.3, "FAIL", 1, 1, "high") }),
                [], [new("article", "high", "RECOMMENDATION-MARKER")], "DISCLAIMER-MARKER", new("mode-a", new Dictionary<string, string> { ["judge"] = "PROMPT-VERSION" }));
        return (result, report);
    }

    private static string[] Hashes(string source) => Directory.GetFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(file => Path.GetRelativePath(source, file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).ToArray();
}
