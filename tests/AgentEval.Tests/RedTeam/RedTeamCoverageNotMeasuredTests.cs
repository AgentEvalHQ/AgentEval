// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Output;
using AgentEval.RedTeam.Reporting;
using AgentEval.RedTeam.Reporting.Compliance;
using AgentEval.Cli.Commands;
using AgentEval.Tests.RedTeam.Baseline;
using Xunit;
using static AgentEval.Tests.RedTeam.Baseline.BaselineNotMeasuredTests;

namespace AgentEval.Tests.RedTeam;

public class RedTeamCoverageNotMeasuredTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BaselineCaptureNote_NothingConclusive_SaysComparisonsAreRefused(bool empty)
    {
        var note = RedTeamCommand.BaselineCaptureNote(empty ? Scan() : Scan(Attack(0, 0, 5)));
        Assert.Contains("every comparison against it is refused (exit 13)", note);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 2)]
    public void ComplianceEvidence_RateUsesVerdictsAndNeverNaN(int resisted, int inconclusive)
    {
        var result = Scan(Attack(resisted, 0, inconclusive));
        var iso = new ISO27001ComplianceReporter().GenerateReport(result);
        var soc = new SOC2ComplianceReporter().GenerateReport(result);
        var expected = resisted == 0 ? "n/a" : "100.0%";
        var isoEvidence = iso.Controls.Select(c => c.EvidenceSummary).Where(e => e.Contains("PromptInjection")).ToArray();
        var socEvidence = soc.Controls.Select(c => c.EvidenceSummary).Where(e => e.Contains("PromptInjection")).ToArray();
        Assert.NotEmpty(isoEvidence);
        Assert.NotEmpty(socEvidence);
        Assert.All(isoEvidence.Concat(socEvidence), evidence =>
        {
            Assert.Contains(expected, evidence);
            Assert.DoesNotContain("NaN", evidence);
        });
    }

    [Fact]
    public void Coverage_NoProbes_HasNoRates()
    {
        var result = Scan();
        Assert.Null(result.Coverage);
        Assert.Null(result.ConclusiveRate);
        Assert.Null(result.InconclusiveRate);
    }

    [Fact]
    public void Coverage_AllInconclusive_IsMeasuredZero()
    {
        var result = Scan(Attack(0, 0, 5));
        Assert.Equal<double?>(0, result.Coverage);
        Assert.Equal<double?>(0, result.ConclusiveRate);
        Assert.Equal<double?>(1, result.InconclusiveRate);
    }

    [Fact]
    public void Reports_NoProbes_ShowAbsentCoverage()
    {
        var result = Scan();
        var output = new StringWriter();
        new RedTeamOutputFormatter(new RedTeamOutputOptions { UseColors = false, UseEmoji = false }, output).Print(result);
        Assert.Contains("Coverage: n/a (conclusive)", output.ToString());
        Assert.Contains("| Coverage (conclusive) | n/a |", new MarkdownReportExporter().Export(result));
        var json = JsonDocument.Parse(new JsonReportExporter().Export(result));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("summary").GetProperty("coverage").ValueKind);
    }

    [Fact]
    public void BeConclusive_NoProbes_ExplainsAbsentCoverage()
    {
        var error = Assert.Throws<RedTeamAssertionException>(() => Scan().Should().BeConclusive());
        Assert.Contains("coverage n/a", error.Message);
    }

    [Fact]
    public void Console_PartlyConclusiveAttack_RateMatchesReports()
    {
        var result = Scan(Attack(8, 0, 2));
        var output = new StringWriter();
        new RedTeamOutputFormatter(new RedTeamOutputOptions { UseColors = false, UseEmoji = false }, output).Print(result);
        var row = Assert.Single(output.ToString().Split('\n'), line => line.Contains("Prompt Injection") && line.Contains("8/10"));
        Assert.Contains("100%", row.Replace(" ", ""));
        Assert.DoesNotContain("80%", row.Replace(" ", ""));
    }

    [Fact]
    public void ScanProgress_NoVerdict_HasNoRate()
    {
        Assert.Null(new ScanProgress(0, 10, "attack", "probe", TimeSpan.Zero).CurrentSuccessRate);
        Assert.Null(new ScanProgress(2, 10, "attack", "probe", TimeSpan.Zero).CurrentSuccessRate);
    }

    [Fact]
    public void ScanProgress_PartlyConclusive_RateUsesVerdicts()
    {
        Assert.Equal<double?>(0.75, new ScanProgress(10, 10, "attack", "probe", TimeSpan.Zero, 6, 2).CurrentSuccessRate);
    }
}
