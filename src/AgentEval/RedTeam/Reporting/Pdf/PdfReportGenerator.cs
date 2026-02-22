// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
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
    }

    private static void AddRiskScoreSection(Section section, RiskSummary summary, PdfReportOptions options)
    {
        section.AddParagraph("RISK SCORE").Style = "Heading1";

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
        AddStatCell(row.Cells[2], "Skipped", (summary.TotalProbes - summary.PassedProbes - summary.FailedProbes).ToString(), Colors.Orange);

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

        // Strengths
        var passedAttacks = result.AttackResults.Count(a => a.Passed);
        if (passedAttacks > 0)
        {
            var para = section.AddParagraph($"🟢 STRONG: {passedAttacks} attack categories fully resisted");
            para.Format.Font.Color = Colors.DarkGreen;
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
            var passedProbes = group.Sum(a => a.ResistedCount);
            var passRate = totalProbes > 0 ? (passedProbes * 100.0 / totalProbes) : 100;
            
            var statusPara = row.Cells[2].AddParagraph(passRate >= 100 ? "✓ Passed" : passRate >= 80 ? "⚠ Partial" : "✗ Failed");
            statusPara.Format.Font.Color = passRate >= 100 ? Colors.DarkGreen : passRate >= 80 ? Colors.Orange : Colors.DarkRed;
            
            row.Cells[3].AddParagraph($"{passRate:F0}%");
            row.Cells[4].AddParagraph($"{passedProbes}/{totalProbes}");
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
            
            var resistRate = attack.TotalCount > 0 ? (attack.ResistedCount * 100.0 / attack.TotalCount) : 100;
            var ratePara = row.Cells[4].AddParagraph($"{resistRate:F0}%");
            ratePara.Format.Font.Color = resistRate >= 100 ? Colors.DarkGreen : resistRate >= 80 ? Colors.Orange : Colors.DarkRed;
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
            baselineSummary.TotalProbes > 0 ? baselineSummary.PassedProbes * 100 / baselineSummary.TotalProbes : 100,
            currentSummary.TotalProbes > 0 ? currentSummary.PassedProbes * 100 / currentSummary.TotalProbes : 100);
    }

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
                var para = section.AddParagraph($"• {probe.ProbeId}");
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
