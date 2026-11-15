// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Reporting;
using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Compliance.EuAiAct.Reporting;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval compliance render</c> subcommand.
/// Reads a verified AEF compliance artifact (converting legacy reports when needed) and renders without LLM cost.
/// </summary>
public static class ComplianceRenderCommand
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Runs the compliance render command using auto-discovered workspace root.</summary>
    public static Task<int> RunAsync(
        string regulation,
        string subject,
        string? ts,
        string? rootOverride) =>
        RunAsync(regulation, subject, ts, rootOverride, registryOverride: null);

    /// <summary>Runs the compliance render command with optional overrides (used in tests).</summary>
    internal static async Task<int> RunAsync(
        string regulation,
        string subject,
        string? ts,
        string? rootOverride,
        ArticlesRegistry? registryOverride)
    {
        var isGdpr = string.Equals(regulation, "gdpr", StringComparison.OrdinalIgnoreCase);
        var isEuAiAct = string.Equals(regulation, "eu-ai-act", StringComparison.OrdinalIgnoreCase);
        if (!isGdpr && !isEuAiAct)
        {
            Console.Error.WriteLine($"Unsupported regulation '{regulation}'. Supported in v1: gdpr, eu-ai-act.");
            return 1;
        }

        // Phase-7 Task 7.1: validate the --ts path segment if supplied. The CLI
        // already canonicalises --root; --ts goes straight into a filesystem
        // join via Path.Combine, so a `..`-laced value would escape the
        // workspace. The MC REST endpoint already rejects this; the CLI should
        // too for symmetry.
        if (!string.IsNullOrWhiteSpace(ts)
            && !AgentEval.Output.FileSystemLayout.IsSafePathSegment(ts))
        {
            Console.Error.WriteLine($"Error: --ts '{ts}' is not a safe path segment.");
            return 1;
        }

        // ── Workspace setup ──────────────────────────────────────────────────
        if (rootOverride is not null)
        {
            var canonical = WorkspaceRootValidator.CanonicaliseOrNull(rootOverride);
            if (canonical is null) return 1;
            rootOverride = canonical;
        }
        var workspaceRoot = rootOverride ?? WorkspaceRootDiscovery.Find(Directory.GetCurrentDirectory());
        if (workspaceRoot is null)
        {
            Console.Error.WriteLine("Could not find a solution root. Provide --root or run from within a solution directory.");
            return 1;
        }

        var agentEvalDir = Path.Combine(workspaceRoot, ".agenteval");
        var sanitizedSubject = FileSystemLayout.Sanitize(subject);
        var regulationFolder = isGdpr ? "GDPR" : "EU-AI-Act";
        var complianceDir = Path.Combine(agentEvalDir, "compliance", regulationFolder, sanitizedSubject);

        AefComplianceReport? report;
        try
        {
            var legacyDirectories = Directory.Exists(complianceDir)
                ? Directory.EnumerateDirectories(complianceDir).Take(1001).ToArray()
                : [];
            report = new AefComplianceReportReader().Read(Path.Combine(agentEvalDir, "aef", "runs"),
                legacyDirectories, subject, regulationFolder, string.IsNullOrWhiteSpace(ts) ? null : ts);
            if (report is null)
            {
                Console.Error.WriteLine("No captured rich compliance report is available for this selection. Native evidence takes precedence; absent content cannot be reconstructed from legacy siblings.");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read verified compliance evidence: {ex.Message}");
            return 1;
        }
        var evidenceDir = Path.Combine(complianceDir, report.Timestamp);
        var markdownPath = Path.Combine(evidenceDir, "report.md");
        Console.WriteLine($"Evidence integrity: {report.Run.Integrity}; signature trust: {report.Run.SignatureTrust}.");
        if (report.Run.Imported)
            Console.WriteLine("Imported: not sealed when it ran. Original execution provenance is not established by the ingest seal.");
        if (isEuAiAct) return await RenderEuAiActAsync(report.Document, markdownPath);

        GdprComplianceEvidence evidence;
        try
        {
            evidence = BenchmarkReportJson.Read<GdprComplianceEvidence>(report.Document.Document, s_jsonOpts, "gdpr-evidence.schema.json");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read GDPR report: {ex.Message}");
            return 1;
        }

        try
        {
            Directory.CreateDirectory(evidenceDir);
            await File.WriteAllTextAsync(markdownPath, new AgentEval.Compliance.Gdpr.Reporting.MarkdownRenderer().Render(evidence));
            Console.WriteLine($"Markdown report written to: {markdownPath}");
            Console.WriteLine("PDF is optional: use agenteval-pdf render --run <verified-run> --output report.pdf.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to render Markdown: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RenderEuAiActAsync(AefComplianceDocument document, string markdownPath)
    {
        EuAiActComplianceEvidence evidence;
        try
        {
            evidence = BenchmarkReportJson.Read<EuAiActComplianceEvidence>(document.Document, s_jsonOpts, "eu-ai-act-evidence.schema.json");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read EU AI Act report: {ex.Message}");
            return 1;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(markdownPath)!);
            await File.WriteAllTextAsync(markdownPath, new AgentEval.Compliance.EuAiAct.Reporting.MarkdownRenderer().Render(evidence));
            Console.WriteLine($"Markdown report written to: {markdownPath}");
            Console.WriteLine("PDF is optional: use agenteval-pdf render --run <verified-run> --output report.pdf.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to render Markdown: {ex.Message}");
            return 1;
        }
    }
}
