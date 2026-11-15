// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using Xunit;
using GR = AgentEval.Compliance.Gdpr.Reporting;
using ER = AgentEval.Compliance.EuAiAct.Reporting;
using AR = AgentEval.Evals.Agentic.Reporting;

namespace AgentEval.Tests.Compliance;

public sealed class NestedSummaryPdfTests
{
    [Theory]
    [InlineData("gdpr")]
    [InlineData("eu")]
    [InlineData("agentic")]
    public async Task Render_UnmeasuredNestedScores_PrintsAbsenceAtEveryLevel(string kind)
    {
        var report = await SummaryOverallScoreNotMeasuredTests.Report(kind, NestedSummaryNotMeasuredTests.NestedTree(kind, true, "skipped"));
        var document = report switch
        {
            GR.GdprComplianceEvidence g => new GR.Pdf.GDPRPdfRenderer().CreateDocument(g),
            ER.EuAiActComplianceEvidence e => new ER.Pdf.EuAiActPdfRenderer().CreateDocument(e),
            AR.AgenticBenchmarkResult a => new AR.Pdf.AgenticPdfRenderer().CreateDocument(a),
            _ => throw new ArgumentException("Unexpected report"),
        };
        var text = new string(string.Concat(document.Sections.SelectMany(s => s.Paragraphs.Concat(s.Tables.SelectMany(t => t.Rows.SelectMany(r => r)))))
            .Where(c => !char.IsWhiteSpace(c)).ToArray());
            Assert.Contains("n/a", text);
            Assert.DoesNotMatch(@"(?<!\d)0%", text); // Do not confuse methodology thresholds such as 80% with a zero score.
    }
}
