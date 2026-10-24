// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#if NET10_0_OR_GREATER
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.MissionControl.GraphQL;
using AgentEval.Output;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Writing;
using AgentEval.Tests.Cli;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentEval.Tests.MissionControl;

public sealed class AefCompliancePdfTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ae-mc-pdf-");
    private string Workspace => Path.Combine(_root.FullName, ".agenteval");
    private static readonly byte[] Pdf = Encoding.UTF8.GetBytes("%PDF-1.7\nexact captured source bytes\n%%EOF");
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private static ComplianceEvidence Report => ComplianceRenderCommandTests.MakeFixtureEvidence("PdfAgent").Base with
        { GeneratedAt = DateTimeOffset.Parse("2026-10-23T10:00:00Z") };
    private const string Timestamp = "2026-10-23_10-00-00";
    private const string Url = "/api/v1/compliance/GDPR/PdfAgent/" + Timestamp + "/report.pdf";
    public void Dispose() => _root.Delete(true);
    private WebApplicationFactory<Query> Factory() => new WebApplicationFactory<Query>().WithWebHostBuilder(builder =>
    {
        LoopbackTestConnection.Configure(builder);
        builder.UseSetting("AgentEval:Root", _root.FullName);
        builder.UseSetting("AgentEval:Queries:Enabled", "false");
    });

    private string LegacyReport()
    {
        var directory = Path.Combine(Workspace, "compliance", "GDPR", "PdfAgent", Timestamp);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "evidence.json"), JsonSerializer.SerializeToUtf8Bytes(Report, Json));
        File.WriteAllBytes(Path.Combine(directory, "report.pdf"), Pdf);
        return directory;
    }

    private string NativeCaptureOffAlias()
    {
        var directory = Path.Combine(Workspace, "aef", "runs", "native");
        var writer = AefRunWriter.Create(directory, new()
        {
            RunId = "run:native", Producer = new() { Name = "tests", Version = "1.0" },
            Subject = new() { Ref = "agent:PdfAgent", Kind = AefSubjectKind.Agent },
            Suite = new() { Ref = "bench:GDPR/smoke", Version = "1" }, StartedAt = Report.GeneratedAt,
            ContentCapture = AefContentCapture.Off, Execution = new() { TargetMode = AefTargetMode.Mocked },
            Ext = new() { [StoreV1Exporter.ExtName] = new JsonObject { ["runId"] = Report.SourceRun.RunId } },
        });
        writer.AddResult(new() { CaseId = "case", Path = "check", Evaluator = new("code:check"), State = AefState.Passed });
        writer.Close(AefRunStatus.Completed, Report.GeneratedAt);
        AefSealer.Seal(directory);
        return directory;
    }

    private void NativeReport(ComplianceEvidence report, string directoryName = "native", bool pdf = true,
        string pdfFor = "E-report", bool external = false, bool duplicate = false, JsonNode? pdfMetadata = null, bool wrongKind = false)
    {
        var directory = Path.Combine(Workspace, "aef", "runs", directoryName);
        var workflow = report.Subject.Kind == SubjectKind.Workflow;
        var writer = AefRunWriter.Create(directory, new()
        {
            RunId = "run:" + directoryName, Producer = new() { Name = "tests", Version = "1.0" },
            Subject = new() { Ref = (workflow ? "workflow:" : "agent:") + report.Subject.Name,
                Kind = workflow ? AefSubjectKind.Workflow : AefSubjectKind.Agent },
            StartedAt = report.GeneratedAt, ContentCapture = AefContentCapture.On,
            Execution = new() { TargetMode = AefTargetMode.Mocked },
        });
        writer.AddEvidence(new() { EvidenceId = "E-report", Kind = AefEvidenceKind.ComplianceArtifact,
            Link = AefEvidenceLink.ToBlob(writer.PutBlob(JsonSerializer.SerializeToUtf8Bytes(report, Json))) });
        if (pdf) for (var i = 0; i < (duplicate ? 2 : 1); i++) writer.AddEvidence(new()
        {
            EvidenceId = "E-pdf" + (i == 0 ? "" : "-duplicate"), Kind = wrongKind ? AefEvidenceKind.Transcript : AefEvidenceKind.Document,
            Link = external ? AefEvidenceLink.ToUri("https://example.invalid/source.pdf") : AefEvidenceLink.ToBlob(writer.PutBlob(Pdf)),
            Ext = new() { ["agenteval.compliance"] = pdfMetadata?.DeepClone() ?? new JsonObject { ["pdfFor"] = pdfFor } },
        });
        writer.Close(AefRunStatus.Completed, report.GeneratedAt);
        AefSealer.Seal(directory);
    }

    [Fact]
    public async Task Pdf_DetachedConvertedReport_ReturnsExactCapturedBytes()
    {
        var directory = LegacyReport();
        StoreV1Exporter.ExportComplianceDirectory(directory, Path.Combine(Workspace, "aef", "runs", "captured"));
        Directory.Delete(directory, true);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Pdf, await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Pdf_CaptureOffAlias_DoesNotRecoverUncapturedLegacyContent()
    {
        LegacyReport(); NativeCaptureOffAlias();
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("exact captured source bytes", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Pdf_TamperedNativeEvidence_DoesNotFallbackToLegacyFile()
    {
        LegacyReport();
        var directory = NativeCaptureOffAlias();
        await File.AppendAllTextAsync(Path.Combine(directory, "results.ndjson"), " ");
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Pdf_LegacyOrphan_ImportsAndReturnsCapturedSource()
    {
        LegacyReport();
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Pdf, await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Pdf_NativeCapturedReport_DownloadsWithoutLegacyWorkspace()
    {
        NativeReport(Report);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url + "?kind=agent");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Pdf, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pdf_MissingOrExternalContent_DoesNotRecoverOrFetch(bool external)
    {
        NativeReport(Report, pdf: external, external: external);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("pdf_not_captured", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("E-missing")]
    [InlineData("E-pdf")]
    [InlineData("")]
    public async Task Pdf_InvalidDocumentLink_RefusesUnverifiableAssociation(string pdfFor)
    {
        NativeReport(Report, pdfFor: pdfFor);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Pdf_DuplicateArtifacts_RefusesArbitrarySelection()
    {
        NativeReport(Report, duplicate: true);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Pdf_SameNameKinds_RequiresKindSelection()
    {
        NativeReport(Report, "agent");
        NativeReport(Report with { Subject = Report.Subject with { Kind = SubjectKind.Workflow } }, "workflow");
        using var factory = Factory(); using var client = factory.CreateClient();
        using var ambiguous = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.Conflict, ambiguous.StatusCode);
        using var selected = await client.GetAsync(Url + "?kind=workflow");
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        Assert.Equal(Pdf, await selected.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Pdf_IndependentSources_RequiresExactSourceSelection()
    {
        NativeReport(Report, "first"); NativeReport(Report, "second");
        var sourceId = new AefWorkspaceEvidenceReader().Read(Workspace).Runs.Single(r => r.Evidence.Info.RunId == "run:second").SourceId;
        using var factory = Factory(); using var client = factory.CreateClient();
        using var ambiguous = await client.GetAsync(Url + "?kind=agent");
        Assert.Equal(HttpStatusCode.Conflict, ambiguous.StatusCode);
        using var selected = await client.GetAsync(Url + "?kind=agent&sourceId=" + sourceId);
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        Assert.Equal(Pdf, await selected.Content.ReadAsByteArrayAsync());
        using var absent = await client.GetAsync(Url + "?sourceId=unknown");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
    }

    [Fact]
    public async Task Pdf_WithoutRecordedReportIdentity_DoesNotInventProvenance()
    {
        var directory = LegacyReport();
        File.Delete(Path.Combine(directory, "evidence.json"));
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("namespace")]
    [InlineData("reference")]
    [InlineData("kind")]
    public async Task Pdf_InvalidAssociationShape_RefusesDeclaredPdfMetadata(string mutation)
    {
        JsonNode? metadata = mutation switch
        {
            "namespace" => JsonValue.Create(7), "reference" => new JsonObject { ["pdfFor"] = 7 }, _ => null,
        };
        NativeReport(Report, pdfMetadata: metadata, wrongKind: mutation == "kind");
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}
#endif
