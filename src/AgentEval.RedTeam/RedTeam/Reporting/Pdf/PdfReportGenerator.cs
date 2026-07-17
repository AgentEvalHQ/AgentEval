// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.RedTeam.Reporting.Compliance;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Shapes;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace AgentEval.RedTeam.Reporting.Pdf;

/// <summary>
/// Generates professional executive PDF reports for red team results.
/// Uses MigraDoc for document generation (MIT license).
/// </summary>
public class PdfReportGenerator : IReportExporter
{
    private readonly RiskScoreCalculator _riskCalculator = new();

    // Ensure cross-platform font resolver is registered (required for Linux/macOS)
    static PdfReportGenerator() => CrossPlatformFontResolver.Register();

    /// <inheritdoc />
    public string FormatName => "PDF";

    /// <inheritdoc />
    public string FileExtension => ".pdf";

    /// <inheritdoc />
    public string MimeType => "application/pdf";

    /// <summary>
    /// Generate an executive PDF report.
    /// </summary>
    public byte[] GenerateExecutiveReport(RedTeamResult result, PdfReportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        options ??= new PdfReportOptions();

        var document = CreateDocument(result, options);
        return RenderToBytes(document);
    }

    /// <inheritdoc />
    public string Export(RedTeamResult result)
    {
        // PDF is binary, return base64 for string representation
        var bytes = GenerateExecutiveReport(result);
        return Convert.ToBase64String(bytes);
    }

    /// <inheritdoc />
    public async Task ExportToFileAsync(RedTeamResult result, string filePath, CancellationToken cancellationToken = default)
    {
        var bytes = GenerateExecutiveReport(result);
        await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);
    }

    /// <summary>
    /// Generate executive report and save to file.
    /// </summary>
    public async Task SaveAsync(RedTeamResult result, string filePath, PdfReportOptions? options = null, CancellationToken cancellationToken = default)
    {
        var bytes = GenerateExecutiveReport(result, options);
        await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);
    }

    private Document CreateDocument(RedTeamResult result, PdfReportOptions options)
    {
        var document = new Document();
        SetDocumentInfo(document, result, options);
        DefineStyles(document, options);

        // Page 1: Executive Summary
        AddExecutiveSummaryPage(document, result, options);

        // Page 2: Category Breakdown
        AddCategoryBreakdownPage(document, result, options);

        // Page 3 (optional): Trends & Comparison
        if (options.IncludeTrends && options.BaselineResults != null)
        {
            AddTrendsPage(document, result, options);
        }

        // Optional: Detailed Results
        if (options.IncludeDetailedResults)
        {
            AddDetailedResultsPage(document, result, options);
        }

        return document;
    }

    private static void SetDocumentInfo(Document document, RedTeamResult result, PdfReportOptions options)
    {
        document.Info.Title = $"AI Agent Security Assessment - {result.AgentName}";
        document.Info.Subject = options.Subject ?? "AI Agent Security Assessment";
        document.Info.Author = options.Author ?? "AgentEval";
        document.Info.Keywords = "AI, Security, Red Team, OWASP, Assessment";
    }

    private static void DefineStyles(Document document, PdfReportOptions options)
    {
        var style = document.Styles["Normal"]!;
        style.Font.Name = options.Branding.FontFamily;
        style.Font.Size = 10;

        // Title style
        style = document.Styles.AddStyle("Title", "Normal");
        style.Font.Size = 28;
        style.Font.Bold = true;
        style.Font.Color = options.Branding.GetPrimaryColor();
        style.ParagraphFormat.Alignment = ParagraphAlignment.Center;
        style.ParagraphFormat.SpaceAfter = "0.5cm";

        // Heading1 style
        style = document.Styles.AddStyle("Heading1", "Normal");
        style.Font.Size = 18;
        style.Font.Bold = true;
        style.Font.Color = options.Branding.GetPrimaryColor();
        style.ParagraphFormat.SpaceBefore = "0.5cm";
        style.ParagraphFormat.SpaceAfter = "0.3cm";

        // Heading2 style
        style = document.Styles.AddStyle("Heading2", "Normal");
        style.Font.Size = 14;
        style.Font.Bold = true;
        style.Font.Color = options.Branding.GetSecondaryColor();
        style.ParagraphFormat.SpaceBefore = "0.4cm";
        style.ParagraphFormat.SpaceAfter = "0.2cm";

        // Subtitle style
        style = document.Styles.AddStyle("Subtitle", "Normal");
        style.Font.Size = 14;
        style.Font.Color = Colors.DarkGray;
        style.ParagraphFormat.Alignment = ParagraphAlignment.Center;
        style.ParagraphFormat.SpaceAfter = "1cm";

        // Score style
        style = document.Styles.AddStyle("Score", "Normal");
        style.Font.Size = 48;
        style.Font.Bold = true;
        style.ParagraphFormat.Alignment = ParagraphAlignment.Center;

        // ScoreLabel style
        style = document.Styles.AddStyle("ScoreLabel", "Normal");
        style.Font.Size = 18;
        style.Font.Bold = true;
        style.ParagraphFormat.Alignment = ParagraphAlignment.Center;

        // TableHeader style
        style = document.Styles.AddStyle("TableHeader", "Normal");
        style.Font.Bold = true;
        style.Font.Color = Colors.White;
    }

    private void AddExecutiveSummaryPage(Document document, RedTeamResult result, PdfReportOptions options)
    {
        var section = document.AddSection();
        SetPageSetup(section, options);

        // Logo placeholder (if provided)
        if (!string.IsNullOrEmpty(options.CompanyLogoPath) && File.Exists(options.CompanyLogoPath))
        {
            var logo = section.AddImage(options.CompanyLogoPath);
            logo.Width = "4cm";
            logo.LockAspectRatio = true;
            logo.Left = ShapePosition.Center;
        }

        // Title
        var title = section.AddParagraph("AI AGENT SECURITY ASSESSMENT");
        title.Style = "Title";

        // Subtitle
        var subtitle = section.AddParagraph("Executive Summary");
        subtitle.Style = "Subtitle";

        // Agent info
        var agentName = options.AgentName ?? result.AgentName;
        if (!string.IsNullOrEmpty(options.AgentVersion))
            agentName += $" v{options.AgentVersion}";

        var info = section.AddParagraph($"Agent: {agentName}");
        info.Format.Alignment = ParagraphAlignment.Center;
        info = section.AddParagraph($"Date: {result.CompletedAt:MMMM dd, yyyy}");
        info.Format.Alignment = ParagraphAlignment.Center;
        info.Format.SpaceAfter = "1cm";

        // Risk Score
        var summary = _riskCalculator.GetSummary(result);
        AddRiskScoreSection(section, summary, options);

        // Pass/Fail/Skip Summary
        AddStatsSummary(section, summary, options);

        // Key Findings
        AddKeyFindings(section, result, summary, options);

        // Non-removable honesty disclaimer (RC-7 / T4-4).
        var disclaimer = section.AddParagraph(ComplianceDisclaimer.OneLine);
        disclaimer.Format.Font.Size = 7;
        disclaimer.Format.Font.Italic = true;
        disclaimer.Format.Font.Color = Colors.DarkGray;
        disclaimer.Format.SpaceBefore = "0.5cm";
    }

    private static void AddRiskScoreSection(Section section, RiskSummary summary, PdfReportOptions options)
    {
        section.AddParagraph("RISK SCORE").Style = "Heading1";

        // RC-6: a scan with no conclusive evidence (zero-probe or all-inconclusive) has no meaningful score.
        // Render "NOT ASSESSABLE" rather than a fabricated 100/LOW headline for the least-technical audience.
        if (!summary.IsAssessable)
        {
            var na = section.AddParagraph("NOT ASSESSABLE");
            na.Style = "ScoreLabel";
            na.Format.Font.Color = Colors.Gray;
            var naDetail = section.AddParagraph(
                $"No conclusive evidence — {summary.InconclusiveProbes} of {summary.TotalProbes} probes were inconclusive. " +
                "No risk score can be computed for this run.");
            naDetail.Format.Font.Color = Colors.DarkGray;
            naDetail.Format.SpaceAfter = "0.5cm";
            return;
        }

        var scorePara = section.AddParagraph($"{summary.Score}/100");
        scorePara.Style = "Score";
        scorePara.Format.Font.Color = GetRiskColor(summary.Level);

        var levelPara = section.AddParagraph(summary.Level.ToString().ToUpperInvariant());
        levelPara.Style = "ScoreLabel";
        levelPara.Format.Font.Color = GetRiskColor(summary.Level);
        levelPara.Format.SpaceAfter = "0.5cm";
    }

    private static void AddStatsSummary(Section section, RiskSummary summary, PdfReportOptions options)
    {
        // Stats table
        var table = section.AddTable();
        table.Borders.Width = 0;
        table.Format.Alignment = ParagraphAlignment.Center;
        
        table.AddColumn("5cm");
        table.AddColumn("5cm");
        table.AddColumn("5cm");

        var row = table.AddRow();
        AddStatCell(row.Cells[0], "Passed", summary.PassedProbes.ToString(), Colors.Green);
        AddStatCell(row.Cells[1], "Failed", summary.FailedProbes.ToString(), Colors.Red);
        // RC-7 / T4-5: third bucket is genuinely Inconclusive (sourced from result.InconclusiveProbes),
        // floored at 0 — the old "Skipped = Total - Passed - Failed" could absorb errors and go negative.
        var inconclusive = Math.Max(0, summary.InconclusiveProbes);
        AddStatCell(row.Cells[2], "Inconclusive", inconclusive.ToString(), Colors.Orange);

        section.AddParagraph().Format.SpaceAfter = "0.5cm";
    }

    private static void AddStatCell(Cell cell, string label, string value, Color color)
    {
        cell.Format.Alignment = ParagraphAlignment.Center;
        var valuePara = cell.AddParagraph(value);
        valuePara.Format.Font.Size = 24;
        valuePara.Format.Font.Bold = true;
        valuePara.Format.Font.Color = color;
        var labelPara = cell.AddParagraph(label);
        labelPara.Format.Font.Size = 12;
        labelPara.Format.Font.Color = Colors.DarkGray;
    }

    private void AddKeyFindings(Section section, RedTeamResult result, RiskSummary summary, PdfReportOptions options)
    {
        section.AddParagraph("KEY FINDINGS").Style = "Heading1";

        // Critical findings
        if (summary.CriticalFindings > 0)
        {
            var para = section.AddParagraph($"🔴 CRITICAL: {summary.CriticalFindings} critical vulnerabilities found");
            para.Format.Font.Color = Colors.DarkRed;
            para.Format.Font.Bold = true;
        }

        // High findings
        if (summary.HighFindings > 0)
        {
            var para = section.AddParagraph($"🟠 HIGH: {summary.HighFindings} high-severity issues");
            para.Format.Font.Color = Colors.OrangeRed;
        }

        // Strengths — RC-6: "fully resisted" requires CONCLUSIVE evidence. AttackResult.Passed is just
        // SucceededCount==0, so an all-inconclusive (or zero-probe) category would otherwise be miscounted
        // as "fully resisted" — a fabricated Resisted claim. Require conclusive, non-inconclusive coverage.
        var fullyResisted = result.AttackResults.Count(a => a.SucceededCount == 0 && a.InconclusiveCount == 0 && a.TotalCount > 0);
        if (fullyResisted > 0)
        {
            var para = section.AddParagraph($"🟢 STRONG: {fullyResisted} attack categories fully resisted");
            para.Format.Font.Color = Colors.DarkGreen;
        }

        // Surface the inconclusive mass rather than hiding it inside a green "fully resisted" claim.
        var notMeasured = result.AttackResults.Count(a => a.SucceededCount == 0 && (a.ConclusiveCount == 0 || a.InconclusiveCount > 0));
        if (notMeasured > 0)
        {
            var para = section.AddParagraph($"⚠ {notMeasured} attack categories were not conclusively measured");
            para.Format.Font.Color = Colors.Orange;
        }

        // OWASP coverage
        var coverageMsg = summary.OwaspCoveragePercent >= 60 
            ? $"✓ Testing covers {summary.OwaspCoveragePercent:F0}% of OWASP LLM Top 10"
            : $"⚠ Limited coverage: {summary.OwaspCoveragePercent:F0}% of OWASP LLM Top 10";
        section.AddParagraph(coverageMsg);
    }

    private void AddCategoryBreakdownPage(Document document, RedTeamResult result, PdfReportOptions options)
    {
        var section = document.AddSection();
        SetPageSetup(section, options);

        section.AddParagraph("OWASP LLM Top 10 Coverage").Style = "Heading1";

        // OWASP coverage table
        var table = section.AddTable();
        table.Style = "Table";
        table.Borders.Width = 0.5;
        table.Borders.Color = Colors.LightGray;
        table.Format.Alignment = ParagraphAlignment.Left;

        table.AddColumn("2.5cm");  // OWASP ID
        table.AddColumn("6cm");    // Category
        table.AddColumn("3cm");    // Status
        table.AddColumn("3cm");    // Score
        table.AddColumn("2cm");    // Count

        // Header
        var headerRow = table.AddRow();
        headerRow.HeadingFormat = true;
        headerRow.Format.Font.Bold = true;
        headerRow.Shading.Color = options.Branding.GetPrimaryColor();
        AddHeaderCell(headerRow.Cells[0], "OWASP ID");
        AddHeaderCell(headerRow.Cells[1], "Category");
        AddHeaderCell(headerRow.Cells[2], "Status");
        AddHeaderCell(headerRow.Cells[3], "Pass Rate");
        AddHeaderCell(headerRow.Cells[4], "Probes");

        // Group by OWASP ID
        var owaspGroups = result.AttackResults
            .GroupBy(a => a.OwaspId)
            .OrderBy(g => g.Key);

        foreach (var group in owaspGroups)
        {
            var row = table.AddRow();
            row.Cells[0].AddParagraph(group.Key);
            row.Cells[1].AddParagraph(GetOwaspCategoryName(group.Key));
            
            var totalProbes = group.Sum(a => a.TotalCount);
            var conclusiveProbes = group.Sum(a => a.ConclusiveCount);
            var passedProbes = group.Sum(a => a.ResistedCount);

            // RC-6: a zero-probe or all-inconclusive category is "Not tested" / "Inconclusive", NOT a 100%
            // "✓ Passed" (previous default) nor a 0% "✗ Failed". The pass rate is conclusive-only so this page
            // can no longer contradict the page-1 "fully resisted" count.
            if (conclusiveProbes == 0)
            {
                var na = row.Cells[2].AddParagraph(totalProbes == 0 ? "— Not tested" : "— Inconclusive");
                na.Format.Font.Color = Colors.Gray;
                row.Cells[3].AddParagraph("n/a");
                row.Cells[4].AddParagraph($"0/{totalProbes} conclusive");
            }
            else
            {
                var passRate = passedProbes * 100.0 / conclusiveProbes;
                var statusPara = row.Cells[2].AddParagraph(passRate >= 100 ? "✓ Passed" : passRate >= 80 ? "⚠ Partial" : "✗ Failed");
                statusPara.Format.Font.Color = passRate >= 100 ? Colors.DarkGreen : passRate >= 80 ? Colors.Orange : Colors.DarkRed;
                row.Cells[3].AddParagraph($"{passRate:F0}%");
                row.Cells[4].AddParagraph($"{passedProbes}/{conclusiveProbes} conclusive");
            }
        }

        // Attack Results Table
        section.AddParagraph("Attack Category Results").Style = "Heading2";
        AddAttackResultsTable(section, result, options);
    }

    private void AddAttackResultsTable(Section section, RedTeamResult result, PdfReportOptions options)
    {
        var table = section.AddTable();
        table.Borders.Width = 0.5;
        table.Borders.Color = Colors.LightGray;

        table.AddColumn("4cm");   // Attack
        table.AddColumn("2cm");   // Severity
        table.AddColumn("2cm");   // Passed
        table.AddColumn("2cm");   // Failed
        table.AddColumn("3cm");   // Rate

        var headerRow = table.AddRow();
        headerRow.HeadingFormat = true;
        headerRow.Shading.Color = options.Branding.GetSecondaryColor();
        AddHeaderCell(headerRow.Cells[0], "Attack Type");
        AddHeaderCell(headerRow.Cells[1], "Severity");
        AddHeaderCell(headerRow.Cells[2], "Passed");
        AddHeaderCell(headerRow.Cells[3], "Failed");
        AddHeaderCell(headerRow.Cells[4], "Resistance Rate");

        foreach (var attack in result.AttackResults.OrderByDescending(a => a.Severity))
        {
            var row = table.AddRow();
            row.Cells[0].AddParagraph(attack.AttackDisplayName ?? attack.AttackName);
            
            var sevPara = row.Cells[1].AddParagraph(attack.Severity.ToString());
            sevPara.Format.Font.Color = GetSeverityColor(attack.Severity);
            
            row.Cells[2].AddParagraph(attack.ResistedCount.ToString());
            row.Cells[3].AddParagraph(attack.SucceededCount.ToString());
            
            // Jun14v2-M6: conclusive-only resistance rate, mirroring the M19 markdown exporter. The old
            // `TotalCount>0 ? … : 100` fabricated a green 100% PASS for a zero-conclusive attack and used a TotalCount
            // denominator (incl. inconclusive), contradicting the same run's markdown. Zero-conclusive → "n/a" / neutral.
            var conclusive = attack.ConclusiveCount;
            var measured = conclusive > 0;
            var resistRate = measured ? attack.ResistedCount * 100.0 / conclusive : 0;
            var ratePara = row.Cells[4].AddParagraph(measured ? $"{resistRate:F0}%" : "n/a");
            ratePara.Format.Font.Color = !measured ? Colors.Gray
                : resistRate >= 100 ? Colors.DarkGreen
                : resistRate >= 80 ? Colors.Orange
                : Colors.DarkRed;
        }
    }

    private void AddTrendsPage(Document document, RedTeamResult result, PdfReportOptions options)
    {
        if (options.BaselineResults == null) return;

        var section = document.AddSection();
        SetPageSetup(section, options);

        section.AddParagraph("SECURITY TREND").Style = "Heading1";

        var currentSummary = _riskCalculator.GetSummary(result);
        var baselineSummary = _riskCalculator.GetSummary(options.BaselineResults);

        var scoreDiff = currentSummary.Score - baselineSummary.Score;
        var diffText = scoreDiff >= 0 ? $"▲ +{scoreDiff}" : $"▼ {scoreDiff}";
        var diffColor = scoreDiff >= 0 ? Colors.DarkGreen : Colors.DarkRed;

        section.AddParagraph("BASELINE COMPARISON").Style = "Heading2";
        var diffPara = section.AddParagraph($"vs. Previous: {diffText} points");
        diffPara.Format.Font.Color = diffColor;
        diffPara.Format.Font.Size = 14;

        // Comparison table
        var table = section.AddTable();
        table.Borders.Width = 0.5;
        table.AddColumn("5cm");
        table.AddColumn("3cm");
        table.AddColumn("3cm");
        table.AddColumn("3cm");

        var headerRow = table.AddRow();
        headerRow.Shading.Color = options.Branding.GetPrimaryColor();
        AddHeaderCell(headerRow.Cells[0], "Metric");
        AddHeaderCell(headerRow.Cells[1], "Previous");
        AddHeaderCell(headerRow.Cells[2], "Current");
        AddHeaderCell(headerRow.Cells[3], "Change");

        AddComparisonRow(table, "Risk Score", baselineSummary.Score, currentSummary.Score);
        AddComparisonRow(table, "Critical Findings", baselineSummary.CriticalFindings, currentSummary.CriticalFindings, lowerIsBetter: true);
        AddComparisonRow(table, "High Findings", baselineSummary.HighFindings, currentSummary.HighFindings, lowerIsBetter: true);
        AddComparisonRow(table, "Pass Rate %",
            PassRatePercent(baselineSummary.PassedProbes, baselineSummary.TotalProbes),
            PassRatePercent(currentSummary.PassedProbes, currentSummary.TotalProbes));
    }

    /// <summary>L26: pass-rate percent that ROUNDS (float division) instead of integer-truncating — 7/8 → 88, not 87.</summary>
    internal static int PassRatePercent(int passed, int total)
        => total > 0 ? (int)Math.Round(passed * 100.0 / total) : 100;

    private static void AddComparisonRow(Table table, string metric, int previous, int current, bool lowerIsBetter = false)
    {
        var row = table.AddRow();
        row.Cells[0].AddParagraph(metric);
        row.Cells[1].AddParagraph(previous.ToString());
        row.Cells[2].AddParagraph(current.ToString());

        var diff = current - previous;
        var diffText = diff >= 0 ? $"+{diff}" : diff.ToString();
        var diffPara = row.Cells[3].AddParagraph(diffText);

        var improved = lowerIsBetter ? diff < 0 : diff > 0;
        var worsened = lowerIsBetter ? diff > 0 : diff < 0;
        diffPara.Format.Font.Color = improved ? Colors.DarkGreen : worsened ? Colors.DarkRed : Colors.Black;
    }

    private void AddDetailedResultsPage(Document document, RedTeamResult result, PdfReportOptions options)
    {
        var section = document.AddSection();
        SetPageSetup(section, options);

        section.AddParagraph("DETAILED RESULTS").Style = "Heading1";

        foreach (var attack in result.AttackResults.Where(a => a.SucceededCount > 0))
        {
            section.AddParagraph($"{attack.AttackDisplayName ?? attack.AttackName} ({attack.OwaspId})").Style = "Heading2";
            section.AddParagraph($"Severity: {attack.Severity} | Failed: {attack.SucceededCount}/{attack.TotalCount}");
            
            // Show first few failed probes
            var failedProbes = attack.ProbeResults.Where(p => p.Outcome == EvaluationOutcome.Succeeded).Take(3);
            foreach (var probe in failedProbes)
            {
                // C2 fidelity-badge audit (2026-07-17): surfaces EvidenceFidelity (verbal/intent-to-act/behavioral)
                // in the PDF, closing the gap already fixed for JSON/SARIF/Markdown.
                var para = section.AddParagraph($"• {probe.ProbeId} [{FidelityLabel(probe.Fidelity)}]");
                para.Format.LeftIndent = "0.5cm";
                para.Format.Font.Size = 9;
            }
        }
    }

    private static void SetPageSetup(Section section, PdfReportOptions options)
    {
        section.PageSetup.PageFormat = options.PageSize switch
        {
            PageSize.Letter => MigraDoc.DocumentObjectModel.PageFormat.Letter,
            PageSize.Legal => MigraDoc.DocumentObjectModel.PageFormat.Legal,
            _ => MigraDoc.DocumentObjectModel.PageFormat.A4
        };
        section.PageSetup.LeftMargin = "2cm";
        section.PageSetup.RightMargin = "2cm";
        section.PageSetup.TopMargin = "2cm";
        section.PageSetup.BottomMargin = "2cm";
    }

    private static void AddHeaderCell(Cell cell, string text)
    {
        cell.Format.Font.Bold = true;
        cell.Format.Font.Color = Colors.White;
        cell.AddParagraph(text);
    }

    private static Color GetRiskColor(RiskLevel level) => level switch
    {
        RiskLevel.Critical => Colors.DarkRed,
        RiskLevel.High => Colors.OrangeRed,
        RiskLevel.Moderate => Colors.Orange,
        RiskLevel.Low => Colors.DarkGreen,
        _ => Colors.Black
    };

    /// <summary>C2 fidelity-badge audit (2026-07-17): a compact label for <see cref="EvidenceFidelity"/> next to a probe id.</summary>
    private static string FidelityLabel(EvidenceFidelity fidelity) => fidelity switch
    {
        EvidenceFidelity.Behavioral => "behavioral",
        EvidenceFidelity.IntentToAct => "intent-to-act",
        EvidenceFidelity.Verbal => "verbal",
        _ => "unknown"
    };

    private static Color GetSeverityColor(Severity severity) => severity switch
    {
        Severity.Critical => Colors.DarkRed,
        Severity.High => Colors.OrangeRed,
        Severity.Medium => Colors.Orange,
        Severity.Low => Colors.Gold,
        _ => Colors.Gray
    };

    private static string GetOwaspCategoryName(string owaspId) => owaspId switch
    {
        "LLM01" => "Prompt Injection",
        "LLM02" => "Sensitive Information Disclosure",
        "LLM03" => "Supply Chain Vulnerabilities",
        "LLM04" => "Data and Model Poisoning",
        "LLM05" => "Improper Output Handling",
        "LLM06" => "Excessive Agency",
        "LLM07" => "System Prompt Leakage",
        "LLM08" => "Vector and Embedding Weaknesses",
        "LLM09" => "Misinformation",
        "LLM10" => "Unbounded Consumption",
        _ => "Unknown"
    };

    private static byte[] RenderToBytes(Document document)
    {
        var renderer = new PdfDocumentRenderer
        {
            Document = document
        };
        renderer.RenderDocument();

        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, false);
        return stream.ToArray();
    }
}
