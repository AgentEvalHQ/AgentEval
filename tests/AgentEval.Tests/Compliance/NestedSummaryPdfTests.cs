// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using Xunit;
using GR = AgentEval.Compliance.Gdpr.Reporting;
using ER = AgentEval.Compliance.EuAiAct.Reporting;
using AR = AgentEval.Evals.Agentic.Reporting;

namespace AgentEval.Tests.Compliance;

[Collection(AgentEval.Tests.Rendering.Pdf.QuestPdfLicenceCollection.Name)]
public sealed class NestedSummaryPdfTests
{
    [Theory]
    [InlineData("gdpr")]
    [InlineData("eu")]
    [InlineData("agentic")]
    public async Task Render_UnmeasuredNestedScores_PrintsAbsenceAtEveryLevel(string kind)
    {
        var report = await SummaryOverallScoreNotMeasuredTests.Report(kind, NestedSummaryNotMeasuredTests.NestedTree(kind, true, "skipped"));
        var path = Path.Combine(Path.GetTempPath(), $"nested-summary-{Guid.NewGuid():N}.pdf");
        try
        {
            await (report switch
            {
                GR.GdprComplianceEvidence g => new GR.Pdf.GDPRPdfRenderer().RenderAsync(g, path),
                ER.EuAiActComplianceEvidence e => new ER.Pdf.EuAiActPdfRenderer().RenderAsync(e, path),
                AR.AgenticBenchmarkResult a => new AR.Pdf.AgenticPdfRenderer().RenderAsync(a, path),
                _ => throw new ArgumentException("Unexpected report"),
            });
            using var document = UglyToad.PdfPig.PdfDocument.Open(File.ReadAllBytes(path));
            var text = new string(string.Concat(document.GetPages().Select(p => p.Text)).Where(c => !char.IsWhiteSpace(c)).ToArray());
            Assert.Contains("n/a", text);
            Assert.DoesNotMatch(@"(?<!\d)0%", text); // Do not confuse methodology thresholds such as 80% with a zero score.
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
