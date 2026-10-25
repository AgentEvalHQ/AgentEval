// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#if NET10_0_OR_GREATER
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.MissionControl.Services;
using AgentEval.Output;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;
using AgentEval.Tests.Cli;

namespace AgentEval.Tests.MissionControl;

public sealed class ComplianceSourceSelectionTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ae-compliance-selection-");
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-23T10:00:00Z");
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public void Dispose() => _root.Delete(true);
    private ComplianceMatrixService Service() => new(new FileSystemOutputStore(_root.FullName));
    private static ComplianceEvidence Report(string id, DateTimeOffset? time = null, SubjectKind kind = SubjectKind.Agent) =>
        ComplianceRenderCommandTests.MakeFixtureEvidence("Same").Base with
        {
            Regulation = "gdpr", GeneratedAt = time ?? Time, Subject = new(kind, "Same"),
            SourceRun = new(id, "sha256:" + new string('b', 64)),
            Controls = [new("C1", "Control", "pass", null, [], null)]
        };
    private void Native(ComplianceEvidence report, bool capture, string subject = "agent:Same", string? alias = null)
    {
        var tree = ComplianceRenderCommandTests.MakeFixtureEvidence("Same").CompositeTree;
        AefCompositeRunWriter.Write(Path.Combine(_root.FullName, "aef", "runs"), tree, AefCompositeRunWriter.Undescribed,
            new()
            {
                SubjectRef = subject, TargetMode = AefTargetMode.Mocked, SuiteRef = "bench:gdpr/smoke", Lane = "gdpr",
                ContentCapture = capture ? AefContentCapture.On : AefContentCapture.Off,
                StoreV1 = alias is null ? null : new() { RunId = alias },
                ComplianceArtifacts = [new() { Bytes = JsonSerializer.SerializeToUtf8Bytes(report, Json), CaseIds = ["gdpr.art17.erasure"] }]
            }, report.GeneratedAt, report.GeneratedAt);
    }
    private void Legacy(ComplianceEvidence report)
    {
        var directory = Path.Combine(_root.FullName, "compliance", "gdpr", "Same", report.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "evidence.json"), JsonSerializer.SerializeToUtf8Bytes(report, Json));
    }

    [Theory]
    [InlineData("agent:Same", "legacy-1")]
    [InlineData("model:Same", "legacy-1")]
    [InlineData("agent:Same", null)]
    public async Task Compliance_NativeCaptureOff_DoesNotRecoverLegacyContent(string subject, string? alias)
    {
        var report = Report("legacy-1");
        Native(report, false, subject, alias);
        Legacy(report);
        Assert.Empty(await Service().ListRegulationsAsync());
    }

    [Fact]
    public async Task Compliance_DualWrittenCapturedReport_CountsOnce()
    {
        var report = Report("legacy-1");
        Native(report, true, alias: "legacy-1");
        Legacy(report);
        Assert.Equal(1, Assert.Single(await Service().ListRegulationsAsync()).EvidenceCount);
    }

    [Fact]
    public async Task Compliance_NewerCaptureOff_DoesNotPresentOlderReportAsLatest()
    {
        Native(Report("older"), true);
        Native(Report("newer", Time.AddHours(1)), false);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service().BuildMatrixAsync("gdpr"));
        Assert.Contains("unavailable", error.Message);
        Assert.NotNull(await Service().ReadEvidenceAsync("gdpr", SubjectKind.Agent, "Same", Time.ToString("yyyy-MM-dd_HH-mm-ss")));
    }

    [Fact]
    public async Task Compliance_SubjectKindCollision_RetainsBothRowsAndCells()
    {
        Native(Report("agent-run"), true);
        Native(Report("workflow-run", kind: SubjectKind.Workflow), true, "workflow:Same");
        var matrix = await Service().BuildMatrixAsync("gdpr");
        Assert.Equal(2, matrix.Subjects.Count);
        Assert.Equal(2, matrix.Cells.Count);
        Assert.Equal(2, matrix.Cells.Select(c => c.SubjectKind).Distinct().Count());
        Assert.All(matrix.Cells, c => Assert.Null(c.PassRate));
    }

    [Fact]
    public async Task Compliance_SubjectVersionChanges_StillUsesOneLatestKindNameRow()
    {
        var older = Report("older");
        var newer = Report("newer", Time.AddHours(1));
        Native(older with { Subject = older.Subject with { Version = "1" } }, true);
        Native(newer with { Subject = newer.Subject with { Version = "2" } }, true);
        var matrix = await Service().BuildMatrixAsync("gdpr");
        Assert.Single(matrix.Subjects);
        Assert.Equal("newer", Assert.Single(matrix.Cells).LastEvidenceRunId);
        Assert.Equal(1, Assert.Single(await Service().ListRegulationsAsync()).SubjectCount);
    }

    [Fact]
    public async Task Compliance_AmbiguousTimestamp_RefusesArbitraryDetailSelection()
    {
        Native(Report("one"), true);
        Native(Report("two"), true);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service().ReadEvidenceAsync("gdpr", SubjectKind.Agent, "Same", Time.ToString("yyyy-MM-dd_HH-mm-ss")));
    }
}
#endif
