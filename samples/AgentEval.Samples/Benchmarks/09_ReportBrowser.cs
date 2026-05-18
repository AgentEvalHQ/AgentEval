// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Benchmarks B9: Report Browser — scans the local <c>samples/AgentEval.Samples/output/</c>
/// directory, lists the most-recent benchmark runs (newest first), and lets the user open
/// any one of them with the OS default app.
///
/// The sample is intentionally read-only: it never generates a new run — pick another
/// sample (Performance is the quickest, no Azure required) to populate the output tree
/// first, then come back here to browse the resulting artefacts.
/// </summary>
/// <remarks>
/// <para>
/// The samples write both a canonical run to <c>samples/AgentEval.Samples/.agenteval/</c>
/// (for Mission Control + <c>agenteval doctor</c>) and a sidecar
/// <c>output/{family}/run-{ts}/</c> (for direct human consumption). This browser
/// walks the sidecar because its bare <c>report.json</c> exposes the composite
/// <c>Score</c> tree directly. The canonical scenarios files store the result
/// tree as a JSON string embedded inside <c>ScenarioResult.Output</c> which
/// requires a deeper parse to extract the same fields — surfacing subject
/// identity + the audit hash from the canonical manifest would be a richer
/// view, but the writing samples drop both locations together, so nothing is
/// silently lost in the sidecar view.
/// </para>
/// <para>
/// Path resolution mirrors <see cref="BenchmarkSampleHelpers.EnsureRunDirectory(string)"/>
/// so the browser sees exactly the directories the writing samples produce.
/// </para>
/// <para>
/// Respects the same non-interactive sentinel as <c>OfferToOpenReports</c>: when stdin is
/// redirected or <c>AGENTEVAL_SAMPLES_NONINTERACTIVE=1</c> is set, the browser prints the
/// list and returns without prompting (CI / scripted runs stay non-blocking).
/// </para>
/// </remarks>
public static class ReportBrowserBenchmark
{
    private const int MaxRunsListed = 20;

    public static Task RunAsync()
    {
        BenchmarkSampleHelpers.PrintHeader(
            "Benchmarks B9: Report Browser",
            "Browse previously generated JSON / HTML / PDF reports under samples/.../output/");

        var outputRoot = ResolveOutputRoot();
        if (!Directory.Exists(outputRoot))
        {
            Console.WriteLine("   No report directory yet — pick a sample from the menu (e.g. Performance) to");
            Console.WriteLine($"   generate one. Expected location: {outputRoot}");
            return Task.CompletedTask;
        }

        var runs = EnumerateRuns(outputRoot)
            .OrderByDescending(r => r.Timestamp)
            .ToList();
        if (runs.Count == 0)
        {
            Console.WriteLine("   No runs yet — pick a sample from the menu (e.g. Performance) to generate one.");
            return Task.CompletedTask;
        }

        var listed = runs.Take(MaxRunsListed).ToList();
        var hidden = runs.Count - listed.Count;

        PrintRunList(listed);
        if (hidden > 0)
            Console.WriteLine($"   ... ({hidden} older run{(hidden == 1 ? "" : "s")} omitted)");

        if (BenchmarkSampleHelpers.IsNonInteractive())
        {
            Console.WriteLine();
            Console.WriteLine("   (non-interactive mode — listing only, no prompt)");
            return Task.CompletedTask;
        }

        Console.WriteLine();
        Console.Write($"   Pick a run [1-{listed.Count}] or Enter to skip: ");
        var raw = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(raw)) return Task.CompletedTask;

        if (!int.TryParse(raw, out var idx) || idx < 1 || idx > listed.Count)
        {
            Console.WriteLine("   Not a valid selection — skipping.");
            return Task.CompletedTask;
        }

        var picked = listed[idx - 1];
        Console.WriteLine();
        Console.WriteLine($"   Selected: {picked.Family} — {picked.Timestamp:u}");
        Console.WriteLine($"   Path:     {picked.Directory}");

        BenchmarkSampleHelpers.OfferToOpenReports((picked.JsonPath!, picked.HtmlPath!, picked.PdfPath));
        return Task.CompletedTask;
    }

    // ── Enumeration ──────────────────────────────────────────────────────────

    private static string ResolveOutputRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "output");
        return Path.GetFullPath(root);
    }

    private static IEnumerable<RunEntry> EnumerateRuns(string outputRoot)
    {
        // Hop one level for benchmark family, then enumerate run-* subdirectories.
        foreach (var familyDir in Directory.EnumerateDirectories(outputRoot))
        {
            string family;
            try { family = Path.GetFileName(familyDir) ?? "<unknown>"; }
            catch { continue; }

            IEnumerable<string> runDirs;
            try { runDirs = Directory.EnumerateDirectories(familyDir, "run-*"); }
            catch { continue; }

            foreach (var runDir in runDirs)
            {
                var entry = TryReadRun(family, runDir);
                if (entry is not null) yield return entry;
            }
        }
    }

    private static RunEntry? TryReadRun(string family, string runDir)
    {
        string? json = SafeFileIfExists(runDir, "report.json");
        string? html = SafeFileIfExists(runDir, "report.html");
        string? pdf = SafeFileIfExists(runDir, "report.pdf");

        // A run must at minimum have JSON + HTML — PDF is optional.
        if (json is null || html is null) return null;

        DateTimeOffset stamp;
        try
        {
            stamp = new DateTimeOffset(Directory.GetCreationTimeUtc(runDir), TimeSpan.Zero);
        }
        catch
        {
            stamp = DateTimeOffset.MinValue;
        }

        var (score, label) = TryReadScoreAndLabel(json);

        return new RunEntry(
            Family: family,
            Directory: runDir,
            Timestamp: stamp,
            JsonPath: json,
            HtmlPath: html,
            PdfPath: pdf,
            Score: score,
            Label: label);
    }

    private static string? SafeFileIfExists(string dir, string name)
    {
        try
        {
            var p = Path.Combine(dir, name);
            return File.Exists(p) ? p : null;
        }
        catch
        {
            return null;
        }
    }

    private static (double? score, string? label) TryReadScoreAndLabel(string jsonPath)
    {
        try
        {
            using var fs = File.OpenRead(jsonPath);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);

            if (!TryGetCaseInsensitive(root, "Score", out var scoreEl)
                || scoreEl.ValueKind != JsonValueKind.Object)
                return (null, null);

            double? score = null;
            if (TryGetCaseInsensitive(scoreEl, "Value", out var valueEl)
                && valueEl.ValueKind == JsonValueKind.Number
                && valueEl.TryGetDouble(out var v))
            {
                score = v;
            }

            string? label = null;
            if (TryGetCaseInsensitive(scoreEl, "Label", out var labelEl)
                && labelEl.ValueKind == JsonValueKind.String)
            {
                label = labelEl.GetString();
            }

            return (score, label);
        }
        catch
        {
            return (null, null);
        }
    }

    private static bool TryGetCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    // ── Output ───────────────────────────────────────────────────────────────

    private static void PrintRunList(IReadOnlyList<RunEntry> runs)
    {
        Console.WriteLine($"   {runs.Count} run{(runs.Count == 1 ? "" : "s")} found (newest first):");
        Console.WriteLine();

        for (var i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
            var when = r.Timestamp == DateTimeOffset.MinValue
                ? "                   "
                : r.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
            var labelDisplay = LabelDisplay(r.Label);
            var scoreDisplay = r.Score is double s
                ? $"score {s.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}"
                : "score n/a";
            var formats = "[JSON" + (r.HtmlPath is not null ? " HTML" : "") + (r.PdfPath is not null ? " PDF" : "") + "]";
            Console.WriteLine($"   [{i + 1,2}] {when}  {r.Family,-12}  {labelDisplay,-10}  {scoreDisplay,-11}  {formats}");
        }
    }

    private static string LabelDisplay(string? label)
    {
        if (string.IsNullOrEmpty(label)) return "?";
        if (string.Equals(label, "skipped", StringComparison.OrdinalIgnoreCase)) return "NOT TESTED";
        return label.ToUpperInvariant();
    }

    // ── Internal types ───────────────────────────────────────────────────────

    private sealed record RunEntry(
        string Family,
        string Directory,
        DateTimeOffset Timestamp,
        string? JsonPath,
        string? HtmlPath,
        string? PdfPath,
        double? Score,
        string? Label);
}
