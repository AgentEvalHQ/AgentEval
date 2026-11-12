// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.RedTeam.Reporting.Compliance;
using AgentEval.Reporting;
using AgentEval.Rendering.Pdf;

namespace AgentEval.RedTeam.Reporting.Pdf;

/// <summary>Projects red-team risk facts and renders via an explicitly registered optional PDF provider.</summary>
public class PdfReportGenerator(IPdfRendererRegistry? pdfRenderers = null, string? providerId = null) : IReportExporter
{
    /// <summary>Preserves the original construction signature. Rendering requires an explicitly supplied registry.</summary>
    public PdfReportGenerator() : this(null, null) { }

    private readonly RiskScoreCalculator _riskCalculator = new();
    /// <inheritdoc />
    public string FormatName => "PDF";
    /// <inheritdoc />
    public string FileExtension => ".pdf";
    /// <inheritdoc />
    public string MimeType => "application/pdf";

    /// <summary>Renders the explicitly requested report using the registered provider.</summary>
    public byte[] GenerateExecutiveReport(RedTeamResult result, PdfReportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var provider = PdfReportFiles.RequireProvider(pdfRenderers, providerId);
        options ??= new();
        return provider.RenderAsync(CreateDocument(result, options), RenderOptions(options)).GetAwaiter().GetResult();
    }
    /// <inheritdoc />
    public string Export(RedTeamResult result) => Convert.ToBase64String(GenerateExecutiveReport(result));
    /// <inheritdoc />
    public Task ExportToFileAsync(RedTeamResult result, string filePath, CancellationToken cancellationToken = default)
        => SaveAsync(result, filePath, cancellationToken: cancellationToken);
    /// <summary>Renders the explicitly requested report using the registered provider.</summary>
    public Task SaveAsync(RedTeamResult result, string filePath, PdfReportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        var provider = PdfReportFiles.RequireProvider(pdfRenderers, providerId);
        options ??= new();
        return PdfReportFiles.RenderAsync(provider, CreateDocument(result, options), filePath, RenderOptions(options), cancellationToken);
    }

    private static PdfRenderOptions RenderOptions(PdfReportOptions options)
    {
        ReadOnlyMemory<byte> logo = default;
        if (!string.IsNullOrEmpty(options.CompanyLogoPath))
        {
            var file = new FileInfo(options.CompanyLogoPath);
            if (file.Exists)
            {
                if (file.Length > 8 << 20) throw new ArgumentException("Expected: logo smaller than 8 MiB. Actual: oversized logo. Suggestions: resize the local image. Because: report images are bounded.");
                logo = File.ReadAllBytes(file.FullName);
            }
        }
        return new((PdfPageSize)options.PageSize, options.Branding.FontFamily, options.Branding.PrimaryColor, options.Branding.SecondaryColor, logo);
    }

    /// <summary>Projects risk and comparison semantics without loading a PDF engine.</summary>
    public ReportDocument CreateDocument(RedTeamResult result, PdfReportOptions options)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        var summary = _riskCalculator.GetSummary(result);
        var lines = new List<string>
        {
            $"Agent: {options.AgentName ?? result.AgentName}" + (string.IsNullOrEmpty(options.AgentVersion) ? "" : " v" + options.AgentVersion),
            $"Date: {result.CompletedAt:O}", $"Company: {options.CompanyName}", "RISK SCORE",
        };
        if (!summary.IsAssessable || summary.Score is not { } score || summary.Level is not { } level)
        {
            lines.Add("NOT ASSESSABLE");
            lines.Add(summary.TotalProbes == 0 ? "No probe ran. No risk score can be computed for this run."
                : $"No conclusive evidence — {summary.InconclusiveProbes} of {summary.TotalProbes} probes were inconclusive. No risk score can be computed for this run.");
        }
        else { lines.Add($"{score}/100"); lines.Add(level.ToString().ToUpperInvariant()); }
        lines.AddRange([$"Passed: {summary.PassedProbes}", $"Failed: {summary.FailedProbes}", $"Inconclusive: {Math.Max(0, summary.InconclusiveProbes)}", "KEY FINDINGS"]);
        if (summary.CriticalFindings > 0) lines.Add($"CRITICAL: {summary.CriticalFindings} critical vulnerabilities found");
        if (summary.HighFindings > 0) lines.Add($"HIGH: {summary.HighFindings} high-severity issues");
        var resisted = result.AttackResults.Count(a => a.SucceededCount == 0 && a.InconclusiveCount == 0 && a.TotalCount > 0);
        if (resisted > 0) lines.Add($"STRONG: {resisted} attack categories fully resisted");
        var unmeasured = result.AttackResults.Count(a => a.SucceededCount == 0 && (a.ConclusiveCount == 0 || a.InconclusiveCount > 0));
        if (unmeasured > 0) lines.Add($"{unmeasured} attack categories were not conclusively measured");
        lines.Add($"Testing covers {summary.OwaspCoveragePercent:F0}% of OWASP LLM Top 10");
        lines.Add(ComplianceDisclaimer.OneLine);
        var sections = new List<ReportSection> { new("AI AGENT SECURITY ASSESSMENT — Executive Summary", lines) };
        var categories = result.AttackResults.GroupBy(a => a.OwaspId).OrderBy(g => g.Key).Select(g =>
        {
            var total = g.Sum(a => a.TotalCount); var conclusive = g.Sum(a => a.ConclusiveCount); var passed = g.Sum(a => a.ResistedCount);
            var rate = conclusive > 0 ? passed * 100.0 / conclusive : (double?)null;
            return new[] { g.Key, GetOwaspCategoryName(g.Key), rate is null ? (total == 0 ? "Not tested" : "Inconclusive") : rate >= 100 ? "Passed" : rate >= 80 ? "Partial" : "Failed",
                rate?.ToString("F0") + (rate is null ? "n/a" : "%"), $"{passed}/{conclusive} conclusive ({total} total)" };
        });
        sections.Add(new("OWASP LLM Top 10 Coverage", tables: [new(["OWASP ID", "Category", "Status", "Pass Rate", "Probes"], categories)]));
        sections.Add(new("Attack Category Results", tables: [new(["Attack Type", "Severity", "Passed", "Failed", "Resistance Rate"],
            result.AttackResults.OrderByDescending(a => a.Severity).Select(a => new[] { a.AttackDisplayName ?? a.AttackName, a.Severity.ToString(), a.ResistedCount.ToString(), a.SucceededCount.ToString(),
                a.ConclusiveCount > 0 ? $"{a.ResistedCount * 100.0 / a.ConclusiveCount:F0}%" : "n/a" }))]));
        if (options.IncludeTrends && options.BaselineResults is { } baseline)
        {
            var previous = _riskCalculator.GetSummary(baseline);
            string[] Row(string name, int? before, int? current)
            { var cells = ComparisonCells(before, current); return [name, cells.Previous, cells.Current, cells.Change]; }
            sections.Add(new("SECURITY TREND", [$"vs. Previous: {ScoreTrend(previous.Score, summary.Score)}"], [new(["Metric", "Previous", "Current", "Change"], [
                Row("Risk Score", previous.Score, summary.Score), Row("Critical Findings", previous.CriticalFindings, summary.CriticalFindings),
                Row("High Findings", previous.HighFindings, summary.HighFindings),
                Row("Pass Rate %", PassRatePercent(previous.PassedProbes, previous.PassedProbes + previous.FailedProbes), PassRatePercent(summary.PassedProbes, summary.PassedProbes + summary.FailedProbes))]) ]));
        }
        if (options.IncludeDetailedResults)
        {
            var details = new List<string>();
            foreach (var attack in result.AttackResults.Where(a => a.SucceededCount > 0))
            {
                details.Add($"{attack.AttackDisplayName ?? attack.AttackName} ({attack.OwaspId})");
                details.Add($"Severity: {attack.Severity} | Failed: {attack.SucceededCount}/{attack.TotalCount}");
                foreach (var probe in attack.ProbeResults.Where(p => p.Outcome == EvaluationOutcome.Succeeded).Take(3))
                    details.Add($"• {probe.ProbeId} [{FidelityLabel(probe.Fidelity)}]");
            }
            sections.Add(new("DETAILED RESULTS", details));
        }
        return new($"AI Agent Security Assessment - {result.AgentName}", sections, result.CompletedAt, options.Subject, options.Author ?? "AgentEval");
    }

    internal static int? PassRatePercent(int passed, int total)
        => total > 0 ? (int)Math.Round(passed * 100.0 / total) : null;

    internal static string ScoreTrend(int? previous, int? current) => (previous, current) switch
    {
        ({ } p, { } c) when c >= p => $"▲ +{c - p} points",
        ({ } p, { } c) => $"▼ {c - p} points",
        (null, null) => "not comparable: neither run has a conclusive probe, so neither has a risk score",
        (null, _) => "not comparable: the previous run has no conclusive probe, so it has no risk score",
        _ => "not comparable: this run has no conclusive probe, so it has no risk score",
    };

    internal static (string Previous, string Current, string Change) ComparisonCells(int? previous, int? current) =>
        (previous?.ToString() ?? "n/a",
         current?.ToString() ?? "n/a",
         previous is { } p && current is { } c ? (c - p >= 0 ? $"+{c - p}" : (c - p).ToString()) : "n/a");

    private static string FidelityLabel(EvidenceFidelity fidelity) => fidelity switch
    {
        EvidenceFidelity.Behavioral => "behavioral",
        EvidenceFidelity.IntentToAct => "intent-to-act",
        EvidenceFidelity.Verbal => "verbal",
        _ => "unknown"
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
}
