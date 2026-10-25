// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Evals;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Reading;

public sealed class AefAgenticReportReaderTests : IDisposable
{
    private readonly string _root = AefTestRuns.TempPath("agentic-report");
    private string Runs => Path.Combine(_root, "runs");
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-23T10:00:00Z");
    private static EvalResult Result() => new(new("case", "Case", "test", "1.0"), new(.9, null, "pass", true, .7, "none", null),
        new(null, null, null, null, null), new("code", null, null, null, null, 0, false), Time);
    private static byte[] Document(string id = "source", string hash = "manifest-hash", DateTimeOffset? time = null, string name = "subject") =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            preset = "agentic-execution", subject = new { kind = "agent", name }, sourceRunId = id, sourceManifestHash = hash,
            generatedAt = time ?? Time, compositeTree = Result(),
            summary = new { overallScore = .9, overallStatus = "PASS", perCategory = new { }, perEvaluator = new { } },
            criticalFindings = Array.Empty<EvalResult>(), recommendations = Array.Empty<string>(), disclaimer = "Original report disclaimer.",
            attestation = new { agentEvalVersion = "1.0", judgeMode = "none", promptVersions = new { } },
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } });

    private string Legacy(byte[] bytes, string folder = "legacy")
    {
        var path = Path.Combine(_root, folder);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "agentic-result.json"), bytes);
        return path;
    }
    private string Native(bool capture = true, string id = "source", string hash = "manifest-hash", DateTimeOffset? time = null, byte[]? bytes = null)
    {
        var result = AefCompositeRunWriter.Write(Runs, Result(), AefCompositeRunWriter.Undescribed, new()
        {
            SubjectRef = "agent:subject", Lane = "agentic", SuiteRef = "bench:agentic/test", TargetMode = AefTargetMode.Mocked,
            ContentCapture = capture ? AefContentCapture.On : AefContentCapture.Off, StoreV1 = new() { RunId = id },
            Report = new() { Type = "agentic-result", SourceRunId = id, SourceManifestHash = hash, GeneratedAt = time ?? Time, Bytes = bytes ?? Document(id, hash, time) },
        }, time ?? Time, (time ?? Time).AddSeconds(1));
        return result.Directory;
    }
    public void Dispose() => AefTestRuns.Delete(_root);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Convert_ReportOnly_PreservesExactBytesAndUnknownExecution(bool capture)
    {
        var bytes = Document();
        var source = Legacy(bytes);
        var output = Path.Combine(_root, "import", "run");
        StoreV1Exporter.ExportDirectory(source, output, new() { ContentCapture = capture ? AefContentCapture.On : AefContentCapture.Off });
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(source, "agentic-result.json")));
        Directory.Delete(source, true);
        var run = new AefRunEvidenceReader().Read(output);
        Assert.True(run.Info.Imported);
        Assert.Null(run.StartedAt); Assert.Null(run.EndedAt); Assert.Empty(run.Results);
        Assert.Equal("aborted", run.Info.Status);
        Assert.Equal(capture ? bytes : null, run.CapturedContent(AefReportArtifact.EvidenceId)?.ToArray());
        var report = new AefAgenticReportReader().Read(Path.Combine(_root, "import"), [], "subject");
        Assert.Equal(capture, report is not null);
        if (report is not null) Assert.Equal("2026-10-23_10-00-00", report.Timestamp);
    }

    [Fact]
    public void Read_LatestHasNoCapture_DoesNotSelectOlderCapturedReport()
    {
        Native(); Native(false, "newer", time: Time.AddHours(1));
        var older = Legacy(Document(), "older");
        var newer = Legacy(Document("newer", time: Time.AddHours(1)), "newer");
        var reader = new AefAgenticReportReader();
        Assert.Null(reader.Read(Runs, [older, newer], "subject"));
        Assert.NotNull(reader.Read(Runs, [older, newer], "subject", "2026-10-23_10-00-00"));
        Assert.Null(reader.Read(Runs, [older, newer], "subject", "2026-10-23_11-00-00"));
    }

    [Fact]
    public void Read_DifferentReportsShareTimestamp_RefusesAmbiguousSelection()
    {
        Native(); Native(id: "other");
        Assert.Throws<InvalidDataException>(() => new AefAgenticReportReader().Read(Runs, [], "subject"));
    }

    [Fact]
    public void Read_TamperedHeaderRedirectsSubject_StillRefusesBeforeLegacyFallback()
    {
        var native = Native();
        var header = JsonNode.Parse(File.ReadAllText(Path.Combine(native, "run.json")))!;
        header["subject"]!["ref"] = "agent:other";
        File.WriteAllText(Path.Combine(native, "run.json"), header.ToJsonString());
        var source = Legacy(Document());
        Assert.Throws<InvalidDataException>(() => new AefAgenticReportReader().Read(Runs, [source], "subject"));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("time")]
    [InlineData("bytes")]
    public void Read_LegacyAliasConflictsWithNativeSource_Refuses(string conflict)
    {
        Native();
        var bytes = conflict == "hash" ? Document(hash: "other-hash") : conflict == "time" ? Document(time: Time.AddSeconds(1)) : Document().Concat(new byte[] { 32 }).ToArray();
        var source = Legacy(bytes);
        Assert.Throws<InvalidDataException>(() => new AefAgenticReportReader().Read(Runs, [source], "subject"));
    }

    [Theory]
    [InlineData("captured")]
    [InlineData("version")]
    [InlineData("sourceRunId")]
    [InlineData("sourceManifestHash")]
    [InlineData("generatedAt")]
    [InlineData("evidenceId")]
    public void Read_IntactSealWithInvalidDescriptor_Refuses(string field)
    {
        var native = Native();
        var path = Path.Combine(native, "run.json");
        var header = JsonNode.Parse(File.ReadAllText(path))!;
        var metadata = header["ext"]![AefReportArtifact.Extension]!;
        if (field == "captured") metadata[field] = false;
        else if (field == "version") metadata[field] = 2;
        else metadata[field] = "different";
        File.WriteAllText(path, header.ToJsonString());
        File.Delete(Path.Combine(native, "seal.json")); AefSealer.Seal(native);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(native).Outcome);
        Assert.Throws<InvalidDataException>(() => new AefAgenticReportReader().Read(Runs, [], "subject"));
    }

    [Theory]
    [InlineData("sourceRunId")]
    [InlineData("sourceManifestHash")]
    [InlineData("generatedAt")]
    [InlineData("subject")]
    [InlineData("compositeTree")]
    [InlineData("recommendations")]
    public void Convert_MissingRequiredReportIdentityOrStructure_Refuses(string field)
    {
        var doc = JsonNode.Parse(Document())!.AsObject(); doc.Remove(field);
        var source = Legacy(JsonSerializer.SerializeToUtf8Bytes(doc));
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportAgenticDirectory(source, Path.Combine(_root, "invalid")));
    }

    [Fact]
    public void Read_Cancelled_AndTooManyLegacyFolders_RefuseBeforeWork()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var reader = new AefAgenticReportReader();
        Assert.ThrowsAny<OperationCanceledException>(() => reader.Read(Runs, [], "subject", ct: cancellation.Token));
        Assert.Throws<InvalidDataException>(() => reader.Read(Runs, Enumerable.Repeat("unused", 1001).ToArray(), "subject"));
    }

    [Fact]
    public void Write_MissingCapturedReportBytes_RefusesBeforeCreatingRun()
    {
        Assert.Throws<ArgumentException>(() => Native(bytes: []));
        Assert.False(Directory.Exists(Runs));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Convert_InvalidEncodingOrOversizedReport_RefusesBeforeParsing(bool oversized)
    {
        var source = Legacy([0xff]);
        if (oversized)
            using (var file = File.OpenWrite(Path.Combine(source, "agentic-result.json"))) file.SetLength((64L << 20) + 1);
        if (oversized)
            Assert.ThrowsAny<IOException>(() => StoreV1Exporter.ExportAgenticDirectory(source, Path.Combine(_root, "refused")));
        else
        {
            var error = Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportAgenticDirectory(source, Path.Combine(_root, "refused")));
            Assert.IsType<AgentEval.Results.Json.AefEncodingException>(error.InnerException);
        }
        Assert.False(Directory.Exists(Path.Combine(_root, "refused")));
    }

    [Fact]
    public void Convert_SharedBudgetIncludesConversionWrites_RefusesAndDiscardsPartialOutput()
    {
        var source = Legacy(Document());
        var controlBudget = new AefReadBudget(1 << 20);
        var control = StoreV1Exporter.ExportAgenticDirectoryBounded(source, Path.Combine(_root, "control"), null, default, controlBudget);
        var used = (1 << 20) - controlBudget.Remaining;
        var seal = new FileInfo(Path.Combine(control.Directory, "seal.json")).Length;
        var output = Path.Combine(_root, "limited");
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportAgenticDirectoryBounded(source, output, null, default, new(used - seal / 2)));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Read_OldNativeWithoutReportDescriptor_SuppressesItsLegacyAlias()
    {
        var native = Native();
        var path = Path.Combine(native, "run.json");
        var header = JsonNode.Parse(File.ReadAllText(path))!;
        header["ext"]!.AsObject().Remove(AefReportArtifact.Extension);
        File.WriteAllText(path, header.ToJsonString());
        File.Delete(Path.Combine(native, "seal.json")); AefSealer.Seal(native);
        var source = Legacy(Document());
        Assert.Null(new AefAgenticReportReader().Read(Runs, [source], "subject"));
    }

    [Fact]
    public void Read_ReportBodyNamesAnotherSubject_Refuses()
    {
        Native(bytes: Document(name: "other"));
        Assert.Throws<InvalidDataException>(() => new AefAgenticReportReader().Read(Runs, [], "subject"));
    }
}
