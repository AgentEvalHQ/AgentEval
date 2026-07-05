// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Benchmarks H13: Report Browser — scans the local <c>samples/AgentEval.Samples/output/</c>
/// directory, lists the most-recent benchmark runs (newest first), and lets the user open
/// any one of them with the OS default app.
///
/// The sample is intentionally read-only: it never generates a new run — pick another
/// sample to populate the output tree first, then come back here to browse the resulting
/// artefacts. (Registry Discovery runs without Azure credentials; the other running
/// samples need Azure OpenAI configured.)
/// </summary>
/// <remarks>
/// <para>
/// The samples write both a canonical run to the repo-root <c>.agenteval/</c>
/// workspace (the same one <c>agenteval init</c> creates — discovered via the
/// nearest ancestor with a <c>*.sln</c>, <c>*.slnx</c>, or <c>.git/</c>) and a
/// sidecar <c>samples/AgentEval.Samples/output/{family}/run-{ts}/</c> tree for
/// direct human consumption. Mission Control + <c>agenteval doctor</c> see the
/// canonical workspace.
/// </para>
/// <para>
/// <b>Option B canonical-walk (plan-13 T4.1b item 23)</b>: setting
/// <c>AGENTEVAL_SAMPLES_REPORT_BROWSER_MODE=canonical</c> switches this browser
/// from the sidecar walk to an Option-B canonical walk over
/// <c>.agenteval/subjects/agents/*/runs/*/</c>. The canonical view surfaces
/// audit-hash + subject identity from <c>manifest.json</c>; the sidecar view
/// surfaces <c>Score</c> + <c>Label</c> directly from the bare <c>report.json</c>.
/// Both views co-exist because the writing samples drop both — nothing is
/// silently lost regardless of which mode is selected. Default remains the
/// sidecar walk so existing demo flows are unchanged.
/// </para>
/// <para>
/// Sidecar path resolution mirrors <see cref="BenchmarkSampleHelpers.EnsureRunDirectory(string)"/>
/// so the browser sees exactly the directories the writing samples produce.
/// Canonical path resolution uses <c>BenchmarkSampleHelpers.SampleAgentEvalDir</c>.
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
        var mode = Environment.GetEnvironmentVariable("AGENTEVAL_SAMPLES_REPORT_BROWSER_MODE");
        var useCanonical = string.Equals(mode, "canonical", StringComparison.OrdinalIgnoreCase);

        BenchmarkSampleHelpers.PrintHeader(
            "Benchmarks H13: Report Browser",
            useCanonical
                ? "Browse runs under .agenteval/ (canonical Option-B walk — surfaces audit hash + subject identity)"
                : "Browse runs under samples/.../output/ (sidecar walk — surfaces score + label directly)");

        IReadOnlyList<RunEntry> runs;
        string sourceRoot;

        if (useCanonical)
        {
            sourceRoot = BenchmarkSampleHelpers.SampleAgentEvalDir;
            if (!Directory.Exists(sourceRoot))
            {
                Console.WriteLine("   No canonical workspace yet — pick a sample from the menu (e.g. Performance) to");
                Console.WriteLine($"   generate one. Expected location: {sourceRoot}");
                return Task.CompletedTask;
            }
            runs = EnumerateCanonicalRuns(sourceRoot)
                .OrderByDescending(r => r.Timestamp)
                .ToList();
        }
        else
        {
            sourceRoot = ResolveOutputRoot();
            if (!Directory.Exists(sourceRoot))
            {
                Console.WriteLine("   No report directory yet — pick a sample from the menu (e.g. Performance) to");
                Console.WriteLine($"   generate one. Expected location: {sourceRoot}");
                return Task.CompletedTask;
            }
            runs = EnumerateRuns(sourceRoot)
                .OrderByDescending(r => r.Timestamp)
                .ToList();
        }
        if (runs.Count == 0)
        {
            Console.WriteLine("   No runs yet — pick a sample from the menu (e.g. Performance) to generate one.");
            Console.WriteLine($"   Mode: {(useCanonical ? "canonical (.agenteval/)" : "sidecar (output/)")}");
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

    // ── Option B canonical walk (plan-13 T4.1b item 23) ──────────────────────

    /// <summary>
    /// Walks <c>{workspace}/.agenteval/subjects/agents/*/runs/*/</c>. Each run dir contains a
    /// <c>manifest.json</c> with audit hash + subject identity and a <c>summary.json</c> with
    /// the verdict. This is the surface Mission Control + <c>agenteval doctor</c> consume.
    /// </summary>
    private static IEnumerable<RunEntry> EnumerateCanonicalRuns(string agentevalDir)
    {
        var subjectsDir = Path.Combine(agentevalDir, "subjects");
        if (!Directory.Exists(subjectsDir)) yield break;

        IEnumerable<string> kindDirs;
        try { kindDirs = Directory.EnumerateDirectories(subjectsDir); }
        catch { yield break; }

        foreach (var kindDir in kindDirs)
        {
            var kindLabel = SafeFileName(kindDir);
            IEnumerable<string> subjectDirs;
            try { subjectDirs = Directory.EnumerateDirectories(kindDir); }
            catch { continue; }

            foreach (var subjectDir in subjectDirs)
            {
                var runsRoot = Path.Combine(subjectDir, "runs");
                if (!Directory.Exists(runsRoot)) continue;

                IEnumerable<string> runDirs;
                try { runDirs = Directory.EnumerateDirectories(runsRoot); }
                catch { continue; }

                foreach (var runDir in runDirs)
                {
                    var entry = TryReadCanonicalRun(kindLabel, SafeFileName(subjectDir), runDir);
                    if (entry is not null) yield return entry;
                }
            }
        }
    }

    private static string SafeFileName(string path)
    {
        try { return Path.GetFileName(path) ?? "<unknown>"; }
        catch { return "<unknown>"; }
    }

    private static RunEntry? TryReadCanonicalRun(string kind, string subject, string runDir)
    {
        var manifest = SafeFileIfExists(runDir, "manifest.json");
        if (manifest is null) return null;

        var summary = SafeFileIfExists(runDir, "summary.json");

        DateTimeOffset stamp;
        try { stamp = new DateTimeOffset(Directory.GetCreationTimeUtc(runDir), TimeSpan.Zero); }
        catch { stamp = DateTimeOffset.MinValue; }

        var (score, label) = summary is not null
            ? TryReadCanonicalScoreAndLabel(summary)
            : (null, null);

        return new RunEntry(
            Family: $"{kind}/{subject}",
            Directory: runDir,
            Timestamp: stamp,
            JsonPath: manifest,    // open manifest in default app — JSON viewer
            HtmlPath: SafeFileIfExists(Path.Combine(runDir, "reports"), "report.html"),
            PdfPath: SafeFileIfExists(Path.Combine(runDir, "reports"), "report.pdf"),
            Score: score,
            Label: label);
    }

    private static (double? score, string? label) TryReadCanonicalScoreAndLabel(string summaryPath)
    {
        try
        {
            using var fs = File.OpenRead(summaryPath);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);

            // Try both names — schema uses "verdict" in the latest version; older runs
            // may still carry "overallStatus".
            string? label = null;
            if (TryGetCaseInsensitive(root, "Verdict", out var labelEl)
                && labelEl.ValueKind == JsonValueKind.String)
            {
                label = labelEl.GetString();
            }
            else if (TryGetCaseInsensitive(root, "OverallStatus", out var statusEl)
                && statusEl.ValueKind == JsonValueKind.String)
            {
                label = statusEl.GetString();
            }

            double? score = null;
            if (TryGetCaseInsensitive(root, "Metrics", out var metricsEl)
                && metricsEl.ValueKind == JsonValueKind.Object
                && TryGetCaseInsensitive(metricsEl, "overallScore", out var scoreEl)
                && scoreEl.ValueKind == JsonValueKind.Number
                && scoreEl.TryGetDouble(out var v))
            {
                score = v;
            }

            return (score, label);
        }
        catch
        {
            return (null, null);
        }
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
