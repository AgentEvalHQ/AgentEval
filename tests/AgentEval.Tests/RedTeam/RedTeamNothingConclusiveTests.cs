// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text;
using System.Text.Json;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Baseline;
using AgentEval.RedTeam.Output;
using AgentEval.RedTeam.Reporting;
using AgentEval.RedTeam.Reporting.Pdf;
using MigraCell = MigraDoc.DocumentObjectModel.Tables.Cell;
using MigraDocument = MigraDoc.DocumentObjectModel.Document;
using MigraObject = MigraDoc.DocumentObjectModel.DocumentObject;
using MigraParagraph = MigraDoc.DocumentObjectModel.Paragraph;
using MigraRow = MigraDoc.DocumentObjectModel.Tables.Row;
using MigraSection = MigraDoc.DocumentObjectModel.Section;
using MigraTable = MigraDoc.DocumentObjectModel.Tables.Table;
using MigraText = MigraDoc.DocumentObjectModel.Text;

namespace AgentEval.Tests.RedTeam;

/// <summary>
/// T81: a red-team scan in which no probe reached a verdict (none ran, or every one was inconclusive) measured nothing.
/// Its scores, attack success rates and risk score are null — not the 0 (or 100, "fully secure") they read — and every
/// report says "n/a" or "not measured" in their place. A scan that did measure keeps its numbers, a measured zero
/// included.
/// </summary>
public class RedTeamNothingConclusiveTests
{
    private static RedTeamResult ZeroProbeScan() => new()
    {
        AgentName = "TestAgent",
        TotalProbes = 0,
        AttackResults = [],
    };

    private static RedTeamResult AllInconclusiveScan() => new()
    {
        AgentName = "TestAgent",
        TotalProbes = 5,
        InconclusiveProbes = 5,
        AttackResults = [Attack(resisted: 0, succeeded: 0, inconclusive: 5)],
    };

    /// <summary>8 resisted, 2 compromised: OverallScore 80, ASR 0.2.</summary>
    private static RedTeamResult MeasuredScan() => new()
    {
        AgentName = "TestAgent",
        TotalProbes = 10,
        ResistedProbes = 8,
        SucceededProbes = 2,
        AttackResults = [Attack(resisted: 8, succeeded: 2, inconclusive: 0)],
    };

    /// <summary>Every probe compromised: a measured zero, which stays a zero.</summary>
    private static RedTeamResult AllCompromisedScan() => new()
    {
        AgentName = "TestAgent",
        TotalProbes = 4,
        SucceededProbes = 4,
        AttackResults = [Attack(resisted: 0, succeeded: 4, inconclusive: 0)],
    };

    /// <summary>8 resisted, 2 inconclusive: conclusive pass rate 100, diluted pass rate 80.</summary>
    private static RedTeamResult ResistedWithInconclusiveScan() => new()
    {
        AgentName = "TestAgent",
        TotalProbes = 10,
        ResistedProbes = 8,
        InconclusiveProbes = 2,
        AttackResults = [Attack(resisted: 8, succeeded: 0, inconclusive: 2)],
    };

    private static RedTeamResult Scan(string kind) => kind switch
    {
        "zero-probe" => ZeroProbeScan(),
        "all-inconclusive" => AllInconclusiveScan(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static AttackResult Attack(int resisted, int succeeded, int inconclusive) => new()
    {
        AttackName = "PromptInjection",
        AttackDisplayName = "Prompt Injection",
        OwaspId = "LLM01",
        MitreAtlasIds = ["AML.T0051"],
        Severity = Severity.Medium,
        ResistedCount = resisted,
        SucceededCount = succeeded,
        InconclusiveCount = inconclusive,
        ProbeResults =
        [
            .. Enumerable.Range(1, resisted).Select(i => Probe($"R-{i:D3}", EvaluationOutcome.Resisted)),
            .. Enumerable.Range(1, succeeded).Select(i => Probe($"S-{i:D3}", EvaluationOutcome.Succeeded)),
            .. Enumerable.Range(1, inconclusive).Select(i => Probe($"I-{i:D3}", EvaluationOutcome.Inconclusive)),
        ],
    };

    private static ProbeResult Probe(string id, EvaluationOutcome outcome) => new()
    {
        ProbeId = id,
        Prompt = "p",
        Response = outcome == EvaluationOutcome.Inconclusive ? "[TIMEOUT]" : "r",
        Outcome = outcome,
        Reason = outcome.ToString(),
        Severity = Severity.Medium,
    };

    // ───────────────────────── the result itself ─────────────────────────

    [Theory]
    [InlineData("zero-probe")]
    [InlineData("all-inconclusive")]
    public void TheFourScores_AreNotMeasured_WhenNoProbeReachedAVerdict(string kind)
    {
        var result = Scan(kind);

        Assert.Null(result.OverallScore);
        Assert.Null(result.ConclusiveScore);
        Assert.Null(result.AttackSuccessRate);
        Assert.Null(result.ConclusiveAttackSuccessRate);
        Assert.NotNull(result.ScoresNotMeasuredReason);
        Assert.Equal(Verdict.Inconclusive, result.Verdict);
    }

    [Fact]
    public void ScoresNotMeasuredReason_SaysWhy()
    {
        Assert.Equal("no probe ran", ZeroProbeScan().ScoresNotMeasuredReason);
        Assert.Equal("none of the 5 probes reached a verdict", AllInconclusiveScan().ScoresNotMeasuredReason);
        Assert.Null(MeasuredScan().ScoresNotMeasuredReason);
    }

    [Fact]
    public void AMeasuredScan_KeepsItsScores()
    {
        var result = MeasuredScan();

        Assert.Equal(80.0, result.OverallScore!.Value, 6);
        Assert.Equal(80.0, result.ConclusiveScore!.Value, 6);
        Assert.Equal(0.2, result.AttackSuccessRate!.Value, 6);
        Assert.Equal(0.2, result.ConclusiveAttackSuccessRate!.Value, 6);
    }

    [Fact]
    public void AMeasuredZero_StaysAZero_NotNull()
    {
        var result = AllCompromisedScan();

        Assert.Equal(0.0, result.OverallScore);
        Assert.Equal(0.0, result.ConclusiveScore);
        Assert.Equal(1.0, result.AttackSuccessRate);
        Assert.Equal(1.0, result.ConclusiveAttackSuccessRate);
    }

    [Fact]
    public void Summary_SaysNotApplicable_AndWhy_NeverZeroPercent()
    {
        var summary = ZeroProbeScan().Summary;

        Assert.Contains("Score: n/a", summary);
        Assert.Contains("ConclusiveScore: n/a", summary);
        Assert.Contains("scores not measured: no probe ran", summary);
        Assert.DoesNotContain("Score: 0.0%", summary);
        Assert.Contains("Score: 80.0%", MeasuredScan().Summary);
    }

    [Fact]
    public void AnAttackWithNoConclusiveProbe_HasNoAttackSuccessRate()
    {
        var attack = Attack(resisted: 0, succeeded: 0, inconclusive: 3);

        Assert.Null(attack.AttackSuccessRate);
        Assert.Null(attack.ConclusiveAttackSuccessRate);
    }

    [Fact]
    public void AnAttackThatResistedEveryProbe_HasAMeasuredZeroRate()
    {
        var resisted = Attack(resisted: 3, succeeded: 0, inconclusive: 1);
        var mixed = Attack(resisted: 1, succeeded: 1, inconclusive: 2);

        Assert.Equal(0.0, resisted.AttackSuccessRate);
        Assert.Equal(0.0, resisted.ConclusiveAttackSuccessRate);
        Assert.Equal(0.25, mixed.AttackSuccessRate!.Value, 6);
        Assert.Equal(0.5, mixed.ConclusiveAttackSuccessRate!.Value, 6);
    }

    // ───────────────────────── baseline and comparison ─────────────────────────

    [Fact]
    public async Task Baseline_OfANothingConclusiveScan_RecordsNoScores_AndSurvivesItsFile()
    {
        var baseline = RedTeamBaseline.FromResult(ZeroProbeScan(), "v0");
        Assert.Null(baseline.OverallScore);
        Assert.Null(baseline.ConclusiveScore);
        Assert.Null(baseline.AttackSuccessRate);

        // The file leaves a null out; a required member would refuse to load it.
        var path = Path.Combine(Path.GetTempPath(), $"agenteval-t81-baseline-{Guid.NewGuid():N}.json");
        try
        {
            await baseline.SaveAsync(path);
            var loaded = await RedTeamBaseline.LoadAsync(path);

            Assert.Null(loaded.OverallScore);
            Assert.Null(loaded.AttackSuccessRate);
            Assert.Equal(0, loaded.ConclusiveProbes);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Baseline_OfAMeasuredScan_KeepsItsScores()
    {
        var baseline = RedTeamBaseline.FromResult(MeasuredScan(), "v1");

        Assert.Equal(80.0, baseline.OverallScore!.Value, 6);
        Assert.Equal(0.2, baseline.AttackSuccessRate!.Value, 6);
    }

    [Fact]
    public void Comparison_AgainstANothingConclusiveBaseline_HasNoDeltas()
    {
        var comparison = new RedTeamBaselineComparer().Compare(
            MeasuredScan(), RedTeamBaseline.FromResult(ZeroProbeScan(), "v0"));

        Assert.Null(comparison.ScoreDelta);
        Assert.Null(comparison.ConclusiveScoreDelta);
        Assert.Null(comparison.AttackSuccessRateDelta);
        Assert.Equal(RegressionStatus.Incomparable, comparison.Status);
    }

    [Fact]
    public void Comparison_AgainstABaselineFileWithNoOverallScore_IsRefused()
    {
        // Probes reached a verdict but the file records no score (hand-edited, or written by another tool): no delta.
        var baseline = RedTeamBaseline.FromResult(MeasuredScan(), "v1") with { OverallScore = null };

        var comparison = new RedTeamBaselineComparer().Compare(MeasuredScan(), baseline);

        Assert.False(comparison.IsComparable);
        Assert.Contains("records no overall score", comparison.NotComparableReason);
        Assert.Equal(RegressionStatus.Incomparable, comparison.Status);
        Assert.False(comparison.IsStable);
    }

    [Fact]
    public void Comparison_BetweenTwoMeasuredScans_HasMeasuredDeltas()
    {
        var comparison = new RedTeamBaselineComparer().Compare(
            MeasuredScan(), RedTeamBaseline.FromResult(MeasuredScan(), "v1"));

        Assert.Equal(0.0, comparison.ScoreDelta);
        Assert.Equal(0.0, comparison.ConclusiveScoreDelta);
        Assert.Equal(0.0, comparison.AttackSuccessRateDelta);
        Assert.Equal(RegressionStatus.Stable, comparison.Status);
    }

    // ───────────────────────── PDF risk score ─────────────────────────

    [Theory]
    [InlineData("zero-probe")]
    [InlineData("all-inconclusive")]
    public void RiskScore_OfANothingConclusiveScan_IsNotMeasured(string kind)
    {
        var calculator = new RiskScoreCalculator();

        var summary = calculator.GetSummary(Scan(kind));

        Assert.Null(calculator.CalculateScore(Scan(kind)));
        Assert.Null(summary.Score);
        Assert.Null(summary.Level);
        Assert.False(summary.IsAssessable);
    }

    [Fact]
    public void RiskScore_OfAMeasuredScan_IsAScore()
    {
        var calculator = new RiskScoreCalculator();

        var summary = calculator.GetSummary(MeasuredScan());

        // 100 - 2 medium findings × 3 = 94, + round(10% OWASP coverage × 5) = 0 under banker's rounding.
        Assert.Equal(94, summary.Score);
        Assert.Equal(AgentEval.RedTeam.Reporting.Compliance.RiskLevel.Low, summary.Level);
        Assert.True(summary.IsAssessable);
    }

    [Fact]
    public void ExecutivePage_OfAZeroProbeScan_SaysNoProbeRan_NotAScore()
    {
        var text = DocumentText(new PdfReportGenerator().CreateDocument(ZeroProbeScan(), new PdfReportOptions()));

        Assert.Contains("NOT ASSESSABLE", text);
        Assert.Contains("No probe ran.", text);
        Assert.DoesNotContain("0 of 0 probes", text);
        Assert.DoesNotContain("/100", text);
    }

    [Fact]
    public void TrendsPage_AgainstANothingConclusiveBaseline_ShowsNoChange_AndNoPreviousScore()
    {
        var options = new PdfReportOptions { IncludeTrends = true, BaselineResults = ZeroProbeScan() };

        var document = new PdfReportGenerator().CreateDocument(ResistedWithInconclusiveScan(), options);

        var text = DocumentText(document);
        Assert.Contains("vs. Previous: not comparable: the previous run has no conclusive probe", text);
        Assert.DoesNotContain("▲", text);
        Assert.DoesNotContain("▼", text);

        var riskScore = Assert.Single(TableRows(document), r => r[0] == "Risk Score");
        Assert.Equal("n/a", riskScore[1]);   // the previous run has no risk score: it read 100
        Assert.Equal("n/a", riskScore[3]);   // so there is no change

        // Pass rate over conclusive probes: 8 of 8 is 100 (not 8 of 10 = 80), and the previous run has none (not 100).
        var passRate = Assert.Single(TableRows(document), r => r[0] == "Pass Rate %");
        Assert.Equal("n/a", passRate[1]);
        Assert.Equal("100", passRate[2]);
        Assert.Equal("n/a", passRate[3]);
    }

    [Fact]
    public void TrendsPage_BetweenTwoMeasuredScans_ShowsTheChange()
    {
        var options = new PdfReportOptions { IncludeTrends = true, BaselineResults = MeasuredScan() };

        var document = new PdfReportGenerator().CreateDocument(ResistedWithInconclusiveScan(), options);

        Assert.DoesNotContain("not comparable", DocumentText(document));
        var riskScore = Assert.Single(TableRows(document), r => r[0] == "Risk Score");
        Assert.Equal("94", riskScore[1]);
        Assert.NotEqual("n/a", riskScore[2]);
    }

    [Theory]
    [InlineData(80, 85, "▲ +5 points")]
    [InlineData(90, 87, "▼ -3 points")]
    [InlineData(70, 70, "▲ +0 points")]
    [InlineData(null, 85, "not comparable: the previous run has no conclusive probe, so it has no risk score")]
    [InlineData(85, null, "not comparable: this run has no conclusive probe, so it has no risk score")]
    [InlineData(null, null, "not comparable: neither run has a conclusive probe, so neither has a risk score")]
    public void ScoreTrend_GivesAChangeOnlyBetweenTwoScores(int? previous, int? current, string expected)
        => Assert.Equal(expected, PdfReportGenerator.ScoreTrend(previous, current));

    [Fact]
    public void ComparisonCells_ShowNotApplicable_ForAValueThatWasNotMeasured()
    {
        Assert.Equal(("n/a", "90", "n/a"), PdfReportGenerator.ComparisonCells(null, 90));
        Assert.Equal(("90", "n/a", "n/a"), PdfReportGenerator.ComparisonCells(90, null));
        Assert.Equal(("80", "90", "+10"), PdfReportGenerator.ComparisonCells(80, 90));
        Assert.Equal(("90", "80", "-10"), PdfReportGenerator.ComparisonCells(90, 80));
    }

    [Fact]
    public void PassRatePercent_OfNothing_IsNotMeasured_NotAHundred()
    {
        Assert.Null(PdfReportGenerator.PassRatePercent(0, 0));
        Assert.Equal(75, PdfReportGenerator.PassRatePercent(3, 4));
        Assert.Equal(0, PdfReportGenerator.PassRatePercent(0, 4));   // a measured zero
    }

    // ───────────────────────── JSON, Markdown and console ─────────────────────────

    [Fact]
    public void JsonReport_WritesNullScores_AndWhy()
    {
        var root = JsonDocument.Parse(new JsonReportExporter().Export(AllInconclusiveScan())).RootElement;
        var summary = root.GetProperty("summary");

        Assert.Equal(JsonValueKind.Null, summary.GetProperty("overall_score").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("conclusive_score").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("attack_success_rate").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("conclusive_attack_success_rate").ValueKind);
        Assert.Equal("none of the 5 probes reached a verdict", summary.GetProperty("scores_not_measured").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("by_attack")[0].GetProperty("asr").ValueKind);
        Assert.Equal("0.4.0", root.GetProperty("schema_version").GetString());
    }

    [Fact]
    public void JsonReport_OfAMeasuredScan_WritesItsScores()
    {
        var root = JsonDocument.Parse(new JsonReportExporter().Export(MeasuredScan())).RootElement;
        var summary = root.GetProperty("summary");

        Assert.Equal(80.0, summary.GetProperty("overall_score").GetDouble(), 6);
        Assert.Equal(0.2, summary.GetProperty("attack_success_rate").GetDouble(), 6);
        Assert.False(summary.TryGetProperty("scores_not_measured", out _));
        Assert.Equal(0.2, root.GetProperty("by_attack")[0].GetProperty("asr").GetDouble(), 6);
    }

    [Fact]
    public void MarkdownReport_SaysNotApplicable_AndWhy()
    {
        var md = new MarkdownReportExporter().Export(ZeroProbeScan());

        Assert.Contains("| **Overall Score** | **n/a** |", md);
        Assert.Contains("| Attack Success Rate | n/a |", md);
        Assert.Contains("| Conclusive Score | n/a |", md);
        Assert.Contains("Scores not measured | no probe ran |", md);
        Assert.DoesNotContain("**0.0%**", md);
        Assert.Contains("| **Overall Score** | **80.0%** |", new MarkdownReportExporter().Export(MeasuredScan()));
    }

    [Fact]
    public void Console_Minimal_SaysNotApplicable_NotZeroPercent()
    {
        var writer = new StringWriter();
        new RedTeamOutputFormatter(new RedTeamOutputOptions { Verbosity = VerbosityLevel.Minimal, UseColors = false }, writer)
            .Print(ZeroProbeScan());

        var output = writer.ToString();
        Assert.Contains("RedTeam: n/a", output);
        Assert.DoesNotContain("0.0%", output);
    }

    [Fact]
    public void Console_Header_SaysWhyTheScoreIsMissing_AndAnUnmeasuredAttackHasNoRate()
    {
        var writer = new StringWriter();
        new RedTeamOutputFormatter(new RedTeamOutputOptions { UseColors = false, UseEmoji = false }, writer)
            .Print(AllInconclusiveScan());

        var output = writer.ToString();
        Assert.Contains("Overall Score: n/a (not measured: none of the 5 probes reached a verdict)", output);
        Assert.Contains("Conclusive Score: n/a", output);
        var attackRow = Assert.Single(output.Split('\n'), l => l.Contains("Prompt Injection") && l.Contains("0/5"));
        Assert.Contains("n/a", attackRow);
        Assert.DoesNotContain("0%", attackRow);
    }

    // ───────────────────────── helpers: read what the PDF document says ─────────────────────────

    private static string DocumentText(MigraDocument document)
    {
        var sb = new StringBuilder();
        foreach (var section in document.Sections.OfType<MigraSection>())
            AppendText(section.Elements, sb);
        return sb.ToString();
    }

    private static void AppendText(IEnumerable<MigraObject> elements, StringBuilder sb)
    {
        foreach (var element in elements)
        {
            switch (element)
            {
                case MigraParagraph paragraph:
                    sb.AppendLine(ParagraphText(paragraph));
                    break;
                case MigraTable table:
                    foreach (var row in table.Rows.OfType<MigraRow>())
                        foreach (var cell in row.Cells.OfType<MigraCell>())
                            AppendText(cell.Elements, sb);
                    break;
            }
        }
    }

    private static string ParagraphText(MigraParagraph paragraph) =>
        string.Concat(paragraph.Elements.OfType<MigraText>().Select(t => t.Content));

    /// <summary>Every table row in the document, one string per cell.</summary>
    private static List<string[]> TableRows(MigraDocument document) =>
        document.Sections.OfType<MigraSection>()
            .SelectMany(s => s.Elements.OfType<MigraTable>())
            .SelectMany(t => t.Rows.OfType<MigraRow>())
            .Select(r => r.Cells.OfType<MigraCell>()
                .Select(c => string.Concat(c.Elements.OfType<MigraParagraph>().Select(ParagraphText)))
                .ToArray())
            .ToList();
}
