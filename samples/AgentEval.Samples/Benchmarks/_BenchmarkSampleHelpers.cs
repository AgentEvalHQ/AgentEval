// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Diagnostics;
using System.Text.Json;
using AgentEval.Core.Evals.Rendering;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Rendering.Pdf;

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Shared helpers for the v0.10.1 <c>Benchmarks/</c> sample suite. Centralises
/// output-directory resolution, JSON + HTML + PDF rendering, console summary
/// printing, and gentle "skip if Azure OpenAI is missing" boilerplate so the
/// per-family samples can stay tight and read like a story.
/// </summary>
internal static class BenchmarkSampleHelpers
{
    /// <summary>
    /// Returns a per-benchmark, per-timestamp output directory under
    /// <c>samples/AgentEval.Samples/output/{benchmark}/run-{utc-timestamp}/</c>.
    /// The directory is created if it does not exist.
    /// </summary>
    public static string EnsureRunDirectory(string benchmarkName)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "output", benchmarkName, $"run-{stamp}");
        var full = Path.GetFullPath(root);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>
    /// Writes the result as canonical JSON next to an HTML report rendered by
    /// <see cref="HtmlEvalResultRenderer"/>. PDF is optional — pass
    /// <paramref name="includePdf"/>=true for audit-grade families.
    /// </summary>
    /// <returns>Tuple of (jsonPath, htmlPath, pdfPath?).</returns>
    public static async Task<(string json, string html, string? pdf)> WriteReportsAsync(
        EvalResult result,
        SubjectIdentity subject,
        string benchmarkName,
        string regulationOrBenchmark,
        bool includePdf,
        string? auditHash = null,
        CancellationToken ct = default)
    {
        var outDir = EnsureRunDirectory(benchmarkName);

        // ── JSON (canonical) ─────────────────────────────────────────────────
        var jsonPath = Path.Combine(outDir, "report.json");
        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(result, jsonOpts), ct);

        // ── HTML (always) ────────────────────────────────────────────────────
        var renderOpts = new EvalResultRenderOptions(
            Subject: subject,
            Title: result.Metric.Name,
            RegulationOrBenchmark: regulationOrBenchmark,
            RunId: Path.GetFileName(outDir),
            GeneratedAt: DateTimeOffset.UtcNow,
            IncludeProvenance: true,
            AuditHash: auditHash,
            AgentEvalVersion: "0.10.1-beta");

        var htmlBytes = await new HtmlEvalResultRenderer().RenderAsync(result, renderOpts, ct);
        var htmlPath = Path.Combine(outDir, "report.html");
        await File.WriteAllBytesAsync(htmlPath, htmlBytes, ct);

        // ── PDF (opt-in) ─────────────────────────────────────────────────────
        string? pdfPath = null;
        if (includePdf)
        {
            var pdfBytes = await new PdfEvalResultRenderer().RenderAsync(result, renderOpts, ct);
            pdfPath = Path.Combine(outDir, "report.pdf");
            await File.WriteAllBytesAsync(pdfPath, pdfBytes, ct);
        }

        return (jsonPath, htmlPath, pdfPath);
    }

    /// <summary>Prints a per-sample header banner.</summary>
    public static void PrintHeader(string title, string subtitle)
    {
        Console.WriteLine();
        Console.WriteLine("+============================================================================+");
        Console.WriteLine($"|  {title,-72}  |");
        Console.WriteLine($"|  {subtitle,-72}  |");
        Console.WriteLine("+============================================================================+");
        Console.WriteLine();
    }

    /// <summary>Prints the canonical "skipping — Azure OpenAI required" box.</summary>
    public static void PrintMissingCredentialsBox(string sampleName)
    {
        Console.WriteLine("+-----------------------------------------------------------------------------+");
        Console.WriteLine($"|  SKIPPING {sampleName,-66}|");
        Console.WriteLine("|  Azure OpenAI credentials required — set:                                   |");
        Console.WriteLine("|    AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT     |");
        Console.WriteLine("+-----------------------------------------------------------------------------+");
    }

    /// <summary>Prints a short summary table with the report file paths.</summary>
    public static void PrintReportPaths(EvalResult result, (string json, string html, string? pdf) paths)
    {
        Console.WriteLine();
        Console.WriteLine("   +------------------------------------------------------------+");
        Console.WriteLine("   |                       SUMMARY                              |");
        Console.WriteLine("   +------------------------------------------------------------+");
        var label = string.Equals(result.Score.Label, "skipped", StringComparison.OrdinalIgnoreCase)
            ? "NOT TESTED"
            : result.Score.Label.ToUpperInvariant();
        Console.WriteLine($"   Verdict:  {label}   Score: {result.Score.Value:P1}   Severity: {result.Score.Severity}");
        Console.WriteLine();
        Console.WriteLine($"   JSON: {paths.json}");
        Console.WriteLine($"   HTML: {paths.html}");
        if (paths.pdf is not null)
            Console.WriteLine($"   PDF:  {paths.pdf}");
    }

    /// <summary>
    /// Prompts the user to open one of the just-saved report files with the OS default
    /// application (HTML / JSON / PDF). Skipped automatically in non-interactive contexts
    /// (input redirected, or <c>AGENTEVAL_SAMPLES_NONINTERACTIVE=1</c> set in the
    /// environment) so CI / scripted runs do not hang waiting on a keypress.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cross-platform: uses <see cref="Process.Start(ProcessStartInfo)"/> with
    /// <c>UseShellExecute = true</c>, which on Windows hands the path to the shell
    /// (default app for the extension) and on macOS/Linux routes through the platform's
    /// xdg-open / open handler. Any failure falls back to printing the path for manual open.
    /// </para>
    /// <para>
    /// Accepted keys (case-insensitive): <c>h</c> = HTML, <c>j</c> = JSON,
    /// <c>p</c> = PDF (only offered when a PDF was produced), <c>n</c> / Enter / anything
    /// else = skip.
    /// </para>
    /// </remarks>
    public static void OfferToOpenReports((string json, string html, string? pdf) paths)
    {
        if (IsNonInteractive()) return;

        Console.WriteLine();
        var prompt = paths.pdf is not null
            ? "   Open the report? [h] HTML  [j] JSON  [p] PDF  [n] no: "
            : "   Open the report? [h] HTML  [j] JSON  [n] no: ";
        Console.Write(prompt);

        ConsoleKeyInfo key;
        try
        {
            key = Console.ReadKey(intercept: false);
        }
        catch (InvalidOperationException)
        {
            // No console available (e.g. dotnet run launched without a TTY) — skip gracefully.
            Console.WriteLine();
            return;
        }
        Console.WriteLine();

        var ch = char.ToLowerInvariant(key.KeyChar);
        switch (ch)
        {
            case 'h':
                TryOpen(paths.html);
                break;
            case 'j':
                TryOpen(paths.json);
                break;
            case 'p':
                if (paths.pdf is not null) TryOpen(paths.pdf);
                else Console.WriteLine("   (no PDF was produced for this sample)");
                break;
            default:
                // 'n', Enter, or any other key → skip silently.
                break;
        }
    }

    /// <summary>
    /// Attempts to open the file at <paramref name="path"/> with the OS default app
    /// using <see cref="Process.Start(ProcessStartInfo)"/> + <c>UseShellExecute=true</c>.
    /// Falls back to printing the path for manual open if the shell-execute call fails.
    /// </summary>
    public static void TryOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   Could not open — open manually at: {path}");
            Console.WriteLine($"   ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// Returns true when the host is non-interactive (no stdin TTY, or
    /// <c>AGENTEVAL_SAMPLES_NONINTERACTIVE=1</c> set). Used by the open-after-save and
    /// report-browser prompts so CI / scripted runs never hang.
    /// </summary>
    public static bool IsNonInteractive() =>
        Console.IsInputRedirected ||
        string.Equals(
            Environment.GetEnvironmentVariable("AGENTEVAL_SAMPLES_NONINTERACTIVE"),
            "1",
            StringComparison.Ordinal);
}
