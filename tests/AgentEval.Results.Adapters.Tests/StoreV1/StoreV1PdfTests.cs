// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.StoreV1;

public sealed class StoreV1PdfTests : IDisposable
{
    private readonly string _output = AefTestRuns.TempPath("pdf-export");
    private static readonly byte[] Pdf = Encoding.UTF8.GetBytes("%PDF-1.7\nPDF_CAPTURE_POLICY_SENTINEL\n%%EOF");
    public void Dispose() => AefTestRuns.Delete(_output);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Export_SourcePdf_PreservesExactBytesOnlyWhenCaptured(bool workspaceRun, bool capture)
    {
        using var source = await StoreV1Fixture.CreateAsync();
        var report = Assert.Single(Directory.EnumerateFiles(Path.Combine(source.Root, "compliance"), "evidence.json", SearchOption.AllDirectories));
        var directory = Path.GetDirectoryName(report)!;
        File.WriteAllBytes(Path.Combine(directory, "report.pdf"), Pdf);
        var options = new StoreV1ExportOptions { ContentCapture = capture ? AefContentCapture.On : AefContentCapture.Off };
        if (workspaceRun) StoreV1Exporter.ExportWorkspaceRun(source.Root, source.RunDirectory, _output, options);
        else StoreV1Exporter.ExportComplianceDirectory(directory, _output, options);
        Directory.Delete(source.Root, true);
        var run = new AefRunEvidenceReader().Read(_output);
        var pdfs = AefCompliancePdfReader.Read(run, default);
        if (capture)
        {
            var pdf = Assert.Single(pdfs);
            Assert.Equal(Pdf, pdf.Bytes!.Value.ToArray());
            Assert.True(run.Info.Imported);
            Assert.Equal(source.RunId, pdf.Evidence.SourceRun.RunId);
        }
        else
        {
            Assert.Empty(pdfs);
            Assert.DoesNotContain("PDF_CAPTURE_POLICY_SENTINEL", string.Join("\n",
                Directory.EnumerateFiles(_output, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
        }
    }
}
