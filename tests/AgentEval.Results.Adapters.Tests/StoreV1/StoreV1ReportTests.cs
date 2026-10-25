// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.StoreV1;

public sealed class StoreV1ReportTests : IDisposable
{
    private readonly string _output = AefTestRuns.TempPath("aef-reports");
    public void Dispose() => AefTestRuns.Delete(_output);

    [Theory]
    [InlineData(AefContentCapture.On)]
    [InlineData(AefContentCapture.Off)]
    public async Task ExportWorkspace_Reports_RespectsCaptureAndKeepsExactBytes(AefContentCapture capture)
    {
        using var fixture = await StoreV1Fixture.CreateAsync();
        var reports = Directory.CreateDirectory(Path.Combine(fixture.RunDirectory, "reports"));
        string[] formats = ["md", "markdown", "html", "junit", "xml", "sarif"];
        foreach (var format in formats) File.WriteAllBytes(Path.Combine(reports.FullName, "report." + format), Encoding.UTF8.GetBytes("content:" + format));
        File.WriteAllText(Path.Combine(reports.FullName, "report.secret"), "not-an-allowed-report");
        StoreV1Exporter.ExportWorkspaceRun(fixture.Root, fixture.RunDirectory, _output, new() { ContentCapture = capture });
        var read = new AefRunEvidenceReader().Read(_output);
        foreach (var format in formats)
        {
            File.WriteAllText(Path.Combine(reports.FullName, "report." + format), "later-content");
            var content = read.CapturedContent("E-report-" + format);
            if (capture == AefContentCapture.Off) Assert.Null(content);
            else
            {
                Assert.NotNull(content);
                Assert.Equal("content:" + format, Encoding.UTF8.GetString(content.Value.Span));
                var descriptor = Assert.Single(read.Evidence, e => e.GetProperty("evidenceId").GetString() == "E-report-" + format);
                Assert.Equal(format, descriptor.GetProperty("ext").GetProperty("agenteval.store-v1").GetProperty("reportFormat").GetString());
            }
        }
        Assert.Null(read.CapturedContent("E-report-secret"));
        Assert.DoesNotContain(read.Evidence, e => e.ToString().Contains("not-an-allowed-report", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExportWorkspace_ReportExceedsOutputBudget_RefusesAndCleansPartialRun()
    {
        using var fixture = await StoreV1Fixture.CreateAsync();
        var reports = Directory.CreateDirectory(Path.Combine(fixture.RunDirectory, "reports"));
        File.WriteAllBytes(Path.Combine(reports.FullName, "report.md"), new byte[128 * 1024]);
        var inputBytes = Directory.EnumerateFiles(fixture.Root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportWorkspaceRunBounded(fixture.Root, fixture.RunDirectory,
            _output, null, default, new AefReadBudget(inputBytes + 64 * 1024)));
        Assert.False(Directory.Exists(_output));
        Assert.Equal(128 * 1024, new FileInfo(Path.Combine(reports.FullName, "report.md")).Length);
    }
}
