// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Cli.Commands;
using AgentEval.Compliance.Gdpr.Reporting;
using AgentEval.Compliance.Gdpr.Reporting.Pdf;
using AgentEval.Compliance.EuAiAct.Reporting;
using AgentEval.Compliance.EuAiAct.Reporting.Pdf;
using AgentEval.Evals.Agentic.Reporting;
using AgentEval.Evals.Agentic.Reporting.Pdf;
using AgentEval.Rendering.Pdf;
using AgentEval.Rendering.PdfSharp;
using AgentEval.Reporting;
using AgentEval.Results.Adapters.Reading;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.Cli.Pdf;

/// <summary>Explicit optional PDF output from an exact verified run, never from a new model invocation.</summary>
public static class PdfCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Runs explicit PDF output from verified evidence; returns usage, output-failure or cancellation exit codes.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || args.Contains("--help"))
        {
            Console.WriteLine("agenteval-pdf render --run <sealed-aef-directory> --output <report.pdf> [--family auto|generic|gdpr|eu-ai-act|agentic] [--overwrite]");
            Console.WriteLine("Optional PDFsharp provider; no evaluation or model calls. Output must be outside the source run. Rich reports require captured original content.");
            return 0;
        }
        try
        {
            if (args[0] != "render") throw new ArgumentException("Expected the render command.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            bool overwrite = false;
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--overwrite") { overwrite = true; continue; }
                if (args[i] is not ("--run" or "--output" or "--family") || i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Unknown option or missing value: " + args[i]);
                if (!values.TryAdd(args[i], args[++i])) throw new ArgumentException("Duplicate option: " + args[i - 1]);
            }
            if (!values.TryGetValue("--run", out var input) || !values.TryGetValue("--output", out var output))
                throw new ArgumentException("Both --run and --output are required.");
            var source = Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var destination = Path.GetFullPath(output);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (destination.Equals(source, comparison) || destination.StartsWith(source + Path.DirectorySeparatorChar, comparison))
                throw new ArgumentException("Derived output must be outside the sealed source run.");
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(destination)!); directory is not null; directory = directory.Parent)
                if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new ArgumentException("Output ancestors must not be symbolic links or reparse points.");
            if (File.Exists(destination) && !overwrite) throw new ArgumentException("Output already exists; select another path or explicitly pass --overwrite.");
            var family = values.GetValueOrDefault("--family", "auto");
            if (family is not ("auto" or "generic" or "gdpr" or "eu-ai-act" or "agentic")) throw new ArgumentException("Unsupported report family: " + family);

            var services = new ServiceCollection().AddAgentEvalPdfSharp();
            using var host = services.BuildServiceProvider();
            var provider = host.GetRequiredService<IPdfRendererRegistry>().Get("pdfsharp");
            var run = new AefRunEvidenceReader().Read(source, ct: cancellationToken);
            var document = Project(run, family, cancellationToken);
            await PdfReportFiles.RenderAsync(provider, document, destination, cancellationToken: cancellationToken, overwrite: overwrite).ConfigureAwait(false);
            Console.WriteLine($"PDF report: {destination}");
            Console.WriteLine($"Evidence integrity: {run.Info.Integrity}; signature trust: {run.Info.SignatureTrust}; PDF provider: pdfsharp (6.2.4).");
            if (run.Info.Imported) Console.WriteLine("Imported evidence: its seal does not establish original execution custody.");
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 130; }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("Expected: explicit valid PDF selection. Actual: " + ex.Message + " Suggestions: run agenteval-pdf --help. Because: PDF output must preserve source evidence.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("PDF output failed; the evaluation evidence is unchanged. " + ex.Message);
            return 3;
        }
    }

    internal static ReportDocument Project(AefRunEvidence run, string family, CancellationToken ct)
    {
        IReadOnlyList<AefComplianceDocument> compliance = family is "auto" or "gdpr" or "eu-ai-act"
            ? new AefComplianceArtifactReader().Read(run, ct).Documents : [];
        if (family == "auto") family = ResolveFamily(run, compliance);
        if (family is "gdpr" or "eu-ai-act")
        {
            var regulation = family == "gdpr" ? "GDPR" : "EU-AI-Act";
            var reports = compliance.Where(d => d.IsRich && string.Equals(d.Evidence.Regulation, regulation, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (reports.Length != 1) throw Missing("Expected exactly one captured rich " + regulation + " report.");
            return family == "gdpr"
                ? new GDPRPdfRenderer().CreateDocument(BenchmarkReportJson.Read<GdprComplianceEvidence>(reports[0].Document, Json, "gdpr-evidence.schema.json"))
                : new EuAiActPdfRenderer().CreateDocument(BenchmarkReportJson.Read<EuAiActComplianceEvidence>(reports[0].Document, Json, "eu-ai-act-evidence.schema.json"));
        }
        if (family == "agentic")
        {
            var report = new AefAgenticReportReader().Read(run) ?? throw Missing("The original agentic report was not captured.");
            return new AgenticPdfRenderer().CreateDocument(BenchmarkReportJson.Read<AgenticBenchmarkResult>(report.Document, Json, "agentic-result.schema.json"));
        }
        var sections = new List<ReportSection>
        {
            new("Recorded AEF evidence", [$"Subject: {run.Info.SubjectRef}", $"Integrity: {run.Info.Integrity}", $"Signature trust: {run.Info.SignatureTrust}",
                "This summary presents recorded fields; it does not reconstruct absent evidence or infer a passing evaluation."]),
            new("Metric definitions", [run.Metrics.GetRawText()]),
        };
        if (run.Summary is { } summary) sections.Add(new("Recorded summary", [summary.GetRawText()]));
        for (var i = 0; i < run.Results.Count; i++) sections.Add(new($"Recorded result {i + 1}", [run.Results[i].GetRawText()]));
        if (run.Gates.Count > 0) sections.Add(new("Recorded gates", run.Gates.Select(g => g.GetRawText())));
        return new("AgentEval Recorded Evidence", sections, DateTimeOffset.UtcNow, run.Info.SubjectRef, "AgentEval");
    }

    private static string ResolveFamily(AefRunEvidence run, IReadOnlyList<AefComplianceDocument> compliance)
    {
        // All inputs come from the same verified snapshot. Never infer a family from a path, importer prose or scores.
        var families = new HashSet<string>(StringComparer.Ordinal);
        var suite = run.Header.TryGetProperty("suite", out var s) && s.TryGetProperty("ref", out var r) ? r.GetString() : null;
        foreach (var candidate in new[] { "gdpr", "eu-ai-act", "agentic" })
            if (suite?.StartsWith($"bench:{candidate}/", StringComparison.Ordinal) == true) families.Add(candidate);

        if (run.Header.TryGetProperty("ext", out var ext) && ext.TryGetProperty("agenteval.report", out var descriptor)
            && descriptor.ValueKind == JsonValueKind.Object && descriptor.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String && type.GetString() == "agentic-result")
            families.Add("agentic"); // The family reader validates descriptor identity and capture policy before rendering.

        foreach (var document in compliance.Where(d => d.IsRich))
        {
            if (string.Equals(document.Evidence.Regulation, "GDPR", StringComparison.OrdinalIgnoreCase)) families.Add("gdpr");
            if (string.Equals(document.Evidence.Regulation, "EU-AI-Act", StringComparison.OrdinalIgnoreCase)) families.Add("eu-ai-act");
        }

        return families.Count switch
        {
            1 => families.Single(),
            0 => throw Missing("No supported rich report family is identified by verified metadata; explicitly select --family generic for recorded fields."),
            _ => throw Missing("Multiple rich report families are identified (" + string.Join(", ", families.Order(StringComparer.Ordinal))
                + "); explicitly select --family gdpr, eu-ai-act or agentic."),
        };
    }

    private static InvalidDataException Missing(string actual) => new("Expected: captured original rich report. Actual: " + actual +
        " Suggestions: select a captured run, or explicitly request --family generic for its available recorded facts. Because: missing report content must not be invented.");
}
