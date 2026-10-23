// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Writing;

namespace AgentEval.Tests.Cli;

public sealed class ComplianceReportImportTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ae-compliance-import-test-");
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    public void Dispose() => _root.Delete(recursive: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExportComplianceDirectory_ReportOnly_PreservesBytesAndUnknownExecution(bool capture)
    {
        var evidence = ComplianceRenderCommandTests.MakeFixtureEvidence("ImportedAgent");
        evidence = evidence with { Base = evidence.Base with { Subject = evidence.Base.Subject with { Tags = ["evaluated"] } } };
        var original = JsonSerializer.SerializeToUtf8Bytes(evidence, Json);
        var source = Write(original);
        File.WriteAllBytes(Path.Combine(source, "evidence.json"), JsonSerializer.SerializeToUtf8Bytes(evidence.Base, Json));
        var converted = StoreV1Exporter.ExportComplianceDirectory(source, Path.Combine(_root.FullName, "converted"),
            new() { ContentCapture = capture ? AefContentCapture.On : AefContentCapture.Off });
        var result = new AefComplianceArtifactReader().Read(converted.Directory);
        Assert.True(result.Run.Imported);
        Assert.Equal("intact", result.Run.Integrity);
        Assert.Equal("aborted", result.Run.Status);
        Assert.Contains("unknown", result.Run.AbortReason);
        Assert.Contains("runId", result.Run.Asserted);
        Assert.Contains("startedAt", result.Run.Asserted);
        Assert.NotEqual(evidence.Base.SourceRun.RunId, result.Run.RunId);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(source, "gdpr-evidence.json")));
        Assert.Empty(File.ReadAllText(Path.Combine(converted.Directory, "results.ndjson")));
        if (!capture) { Assert.Empty(result.Documents); return; }
        Assert.Equal(2, result.Documents.Count);
        var rich = Assert.Single(result.Documents, d => d.IsRich);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(original), rich.Document.GetRawText());
        Assert.Equal(evidence.Base.SourceRun, rich.Evidence.SourceRun);
        Assert.Contains("ingest", File.ReadAllText(Path.Combine(converted.Directory, "seal.json")));
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("source")]
    [InlineData("timestamp")]
    [InlineData("regulation")]
    [InlineData("summary")]
    public void ExportComplianceDirectory_DisagreeingSibling_RefusesBeforeCreatingOutput(string field)
    {
        var evidence = ComplianceRenderCommandTests.MakeFixtureEvidence("ImportedAgent");
        var source = Write(JsonSerializer.SerializeToUtf8Bytes(evidence, Json));
        var generic = JsonSerializer.SerializeToNode(evidence.Base, Json)!;
        switch (field)
        {
            case "subject": generic["subject"]!["name"] = "OtherAgent"; break;
            case "source": generic["sourceRun"]!["runId"] = "another-run"; break;
            case "timestamp": generic["generatedAt"] = "2026-01-01T00:00:00Z"; break;
            case "regulation": generic["regulation"] = "EU-AI-Act"; break;
            case "summary": generic["summary"]!["overallStatus"] = "FAIL"; break;
        }
        File.WriteAllText(Path.Combine(source, "evidence.json"), generic.ToJsonString());
        var output = Path.Combine(_root.FullName, "converted");
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportComplianceDirectory(source, output));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExportComplianceDirectory_MissingReportsOrNativeMarker_Refuses(bool native)
    {
        var source = Directory.CreateDirectory(Path.Combine(_root.FullName, "source")).FullName;
        if (native) File.WriteAllText(Path.Combine(source, "run.json"), "{}");
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportComplianceDirectory(source, Path.Combine(_root.FullName, "converted")));
    }

    private string Write(byte[] bytes)
    {
        var source = Directory.CreateDirectory(Path.Combine(_root.FullName, "source")).FullName;
        File.WriteAllBytes(Path.Combine(source, "gdpr-evidence.json"), bytes);
        return source;
    }
}
