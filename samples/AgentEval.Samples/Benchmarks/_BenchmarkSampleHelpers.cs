// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Diagnostics;
using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.Core.Evals.Rendering;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Rendering.Pdf;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Reporting;
using AgentEval.Compliance.EuAiAct.Articles;
using AgentEval.Compliance.EuAiAct.Reporting;

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Bundle of paths returned by <see cref="BenchmarkSampleHelpers.WriteReportsViaStoreAsync"/>.
/// Both locations are populated for every successful run: the canonical
/// <c>.agenteval/</c> tree (which Mission Control + <c>agenteval doctor</c> read)
/// and the sidecar <c>output/{family}/run-{ts}/</c> tree (which the report
/// browser walks + users open directly).
/// </summary>
internal sealed record SampleRunPaths(
    string CanonicalRunDir,
    string SidecarDir,
    string SidecarJson,
    string SidecarHtml,
    string? SidecarPdf,
    string RunId,
    string? AuditHash);

/// <summary>
/// Selects the breadth / fidelity tier each benchmark sample runs at. Resolution
/// order is documented on <see cref="BenchmarkSampleHelpers.ResolvePreset"/>:
/// CLI args &gt; <c>AGENTEVAL_SAMPLES_PRESET</c> env var &gt; interactive prompt &gt;
/// <see cref="Smoke"/>.
/// </summary>
internal enum SamplePreset
{
    /// <summary>Fast CI-friendly run (~minute, cents). The default.</summary>
    Smoke,

    /// <summary>Broader coverage suitable for daily / weekly runs (~5–15 min, ~$0.50–2).</summary>
    Standard,

    /// <summary>Full audit-grade run with strictest aggregation / longest workloads (~15–45 min, ~$2–10).</summary>
    AuditGrade,
}

/// <summary>
/// Shared helpers for the v0.10.1 <c>Benchmarks/</c> sample suite. Centralises
/// output-directory resolution, JSON + HTML + PDF rendering, console summary
/// printing, and gentle "skip if Azure OpenAI is missing" boilerplate so the
/// per-family samples can stay tight and read like a story.
/// </summary>
internal static class BenchmarkSampleHelpers
{
    /// <summary>
    /// Returns a per-benchmark, per-run output directory under
    /// <c>samples/AgentEval.Samples/output/{benchmark}/run-{utc-timestamp}-{suffix}/</c>.
    /// The 8-char random suffix prevents same-second collisions (e.g., scripted /
    /// loop reruns that would otherwise overwrite an earlier run's reports). The
    /// directory is created if it does not exist.
    /// </summary>
    public static string EnsureRunDirectory(string benchmarkName)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "output", benchmarkName, $"run-{stamp}-{suffix}");
        var full = Path.GetFullPath(root);
        Directory.CreateDirectory(full);
        return full;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Canonical .agenteval/ workspace bootstrapping (v0.10.1 — Mission Control)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The sample suite's canonical AgentEval solution name. All samples write into
    /// one solution so Mission Control / <c>agenteval doctor</c> see them grouped.
    /// </summary>
    public const string SamplesSolutionName = "AgentEval Samples";

    private static readonly Lazy<FileSystemOutputStore> s_sharedStore =
        new(InitializeStore, isThreadSafe: true);

    /// <summary>
    /// Returns the sample workspace root — the directory whose <c>.agenteval/</c>
    /// subfolder receives the canonical run artefacts. This is
    /// <c>samples/AgentEval.Samples/</c>, regardless of the host process's CWD.
    /// </summary>
    public static string SampleWorkspaceRoot
    {
        get
        {
            // AppContext.BaseDirectory points at the bin/{config}/{tfm}/ output
            // dir during `dotnet run`, so three "..\" hops back to the project
            // root mirror EnsureRunDirectory's convention exactly.
            var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
            return Path.GetFullPath(root);
        }
    }

    /// <summary>
    /// Returns the canonical <c>.agenteval/</c> directory under the sample workspace.
    /// Mission Control reads from here; <see cref="WriteReportsViaStoreAsync"/> writes
    /// the manifest, scenarios, summary, and compliance evidence here.
    /// </summary>
    public static string SampleAgentEvalDir =>
        Path.Combine(SampleWorkspaceRoot, ".agenteval");

    /// <summary>
    /// Returns the lazily-initialised <see cref="FileSystemOutputStore"/> bound to
    /// the sample workspace. The store is a process-wide singleton so multiple
    /// samples in one run share the solution cache + run-locator cache.
    /// </summary>
    public static FileSystemOutputStore SharedStore => s_sharedStore.Value;

    /// <summary>
    /// Initialises the canonical <c>.agenteval/</c> workspace lazily: creates the
    /// directory if missing and seeds a <c>solution.json</c> when one isn't
    /// already on disk. Mirrors what <c>agenteval init</c> would write so the
    /// store's <see cref="FileSystemOutputStore.EnsureSolutionAsync"/> contract holds.
    /// </summary>
    private static FileSystemOutputStore InitializeStore()
    {
        var dir = SampleAgentEvalDir;
        Directory.CreateDirectory(dir);

        var solutionFile = Path.Combine(dir, "solution.json");
        if (!File.Exists(solutionFile))
        {
            var solutionDoc = new
            {
                schemaVersion = "1.0",
                id = Guid.NewGuid(),
                name = SamplesSolutionName,
            };
            var json = JsonSerializer.Serialize(solutionDoc, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
            File.WriteAllText(solutionFile, json);
        }

        var gitignoreFile = Path.Combine(dir, ".gitignore");
        if (!File.Exists(gitignoreFile))
        {
            // The default init template lives in the CLI's embedded resources; we
            // can't reach it from a sample, but a minimal ".lock + .tmp" gitignore
            // keeps sentinel files out of source control which is the practical goal.
            File.WriteAllText(gitignoreFile, "# AgentEval workspace gitignore (samples)\n*.lock\n*.tmp\n*.invalid.json\n");
        }

        return new FileSystemOutputStore(dir);
    }

    /// <summary>
    /// Persists <paramref name="result"/> through <em>both</em> the canonical
    /// <c>.agenteval/</c> output store (so Mission Control + <c>agenteval doctor</c>
    /// see the run with a valid audit chain) AND the legacy
    /// <c>output/{family}/run-{ts}/</c> sidecar (HTML / PDF / bare JSON for
    /// direct browser open + the <c>09_ReportBrowser</c> sample).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Architecture (v0.10.1, plan-25):
    /// <list type="bullet">
    ///   <item><description>Manifest, scenarios, summary, compliance evidence → canonical <c>.agenteval/</c> via <see cref="FileSystemOutputStore"/>.</description></item>
    ///   <item><description>HTML / PDF / bare JSON → sidecar <c>output/{family}/run-{ts}/</c>.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The HTML + PDF renderers receive the canonical manifest's
    /// <c>ContentHash</c> as their <c>auditHash</c> option so the rendered
    /// footer carries the real chain anchor — viewers can re-verify against
    /// the canonical run.
    /// </para>
    /// <para>
    /// For compliance benchmarks pass a non-null <paramref name="regulationCodeForEvidence"/>
    /// ("gdpr" / "eu-ai-act") to also persist the regulator-grade evidence
    /// document via the appropriate reporter. For agentic / performance runs
    /// pass <c>null</c> — only the manifest + scenarios + summary land.
    /// </para>
    /// </remarks>
    public static async Task<SampleRunPaths> WriteReportsViaStoreAsync(
        EvalResult result,
        SubjectIdentity subject,
        string benchmarkName,
        string regulationOrBenchmark,
        bool includePdf,
        string? regulationCodeForEvidence = null,
        string? presetLabel = null,
        ArticlesRegistry? gdprArticlesRegistry = null,
        EuAiActArticlesRegistry? euAiActArticlesRegistry = null,
        string? judgeModel = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkName);
        ArgumentException.ThrowIfNullOrWhiteSpace(regulationOrBenchmark);

        // ── 1. Bootstrap the shared store + register the subject ───────────────
        var store = SharedStore;
        await store.EnsureSolutionAsync(ct);
        await store.EnsureSubjectAsync(subject, ct);

        // ── 2. Start the run ────────────────────────────────────────────────────
        var runContext = new RunContext(
            EvalProject: "AgentEval.Samples",
            EvalProjectPath: "samples/AgentEval.Samples/",
            Harness: $"Samples.Benchmarks.{benchmarkName}",
            Seed: null,
            ParentInvocationId: null,
            Kind: regulationCodeForEvidence is null ? "benchmark" : "compliance");

        var manifest = await store.StartRunAsync(subject, runContext, ct);
        var runId = manifest.Run.RunId;

        // ── 3. Persist every leaf as a ScenarioResult ──────────────────────────
        // The composite result was built either by CompositeEval.EvaluateAsync
        // (B2 / B3 / B6 / B7) or by RunCompliancePresetWithAgentProbesAsync
        // (B4 / B5). Either way, walking SubResults yields the atomic leaves.
        var leafIndex = 0;
        foreach (var (scenarioId, leaf) in EnumerateAtomicLeaves(result))
        {
            // ScenarioResult.Id must be unique within a run — append the ordinal
            // index to disambiguate the case where two leaves share a Metric.Key
            // (multi-judge wrapping, scenario fan-out, etc.).
            var uniqueId = $"{leafIndex:D4}-{scenarioId}";
            var sr = EvalResultPersistence.ToScenarioResult(leaf, uniqueId, leaf.Metric.Name);
            await store.WriteScenarioResultAsync(runId, sr, ct);
            leafIndex++;
        }

        // ── 4. Complete the run (this fills in ContentHash) ────────────────────
        var summary = BuildRunSummary(result, runId);
        await store.CompleteRunAsync(manifest, summary, ct);

        // ── 5. Fetch the post-completion manifest to retrieve the audit hash ───
        var completedManifest = await store.GetRunManifestAsync(runId, ct);
        var auditHash = completedManifest?.ContentHash;

        // ── 6. Compliance evidence (regulator-grade reporters) ─────────────────
        if (!string.IsNullOrWhiteSpace(regulationCodeForEvidence))
        {
            try
            {
                await WriteComplianceEvidenceAsync(
                    store, subject, runId, result,
                    regulationCodeForEvidence,
                    presetLabel,
                    gdprArticlesRegistry,
                    euAiActArticlesRegistry,
                    judgeModel,
                    ct);
            }
            catch (Exception ex)
            {
                // Compliance-evidence write is best-effort: a malformed
                // composite shape (e.g. a domain pack the reporter wasn't
                // built for) must not lose the canonical run. Surface the
                // failure but keep the run intact.
                Console.WriteLine($"   Warning: compliance evidence write failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── 7. Sidecar artefacts (HTML / PDF / bare JSON) ──────────────────────
        var sidecarDir = EnsureRunDirectory(benchmarkName);
        var sidecarJsonPath = Path.Combine(sidecarDir, "report.json");
        var sidecarJsonOpts = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(sidecarJsonPath, JsonSerializer.Serialize(result, sidecarJsonOpts), ct);

        var renderOpts = new EvalResultRenderOptions(
            Subject: subject,
            Title: result.Metric.Name,
            RegulationOrBenchmark: regulationOrBenchmark,
            RunId: runId,
            GeneratedAt: DateTimeOffset.UtcNow,
            IncludeProvenance: true,
            AuditHash: auditHash,
            AgentEvalVersion: "0.10.1-beta");

        var htmlBytes = await new HtmlEvalResultRenderer().RenderAsync(result, renderOpts, ct);
        var sidecarHtmlPath = Path.Combine(sidecarDir, "report.html");
        await File.WriteAllBytesAsync(sidecarHtmlPath, htmlBytes, ct);

        string? sidecarPdfPath = null;
        if (includePdf)
        {
            var pdfBytes = await new PdfEvalResultRenderer().RenderAsync(result, renderOpts, ct);
            sidecarPdfPath = Path.Combine(sidecarDir, "report.pdf");
            await File.WriteAllBytesAsync(sidecarPdfPath, pdfBytes, ct);
        }

        // ── 8. Resolve the canonical run dir for the print-paths helper ────────
        // The store doesn't expose RunDir publicly, but we can reconstruct it
        // from FileSystemLayout's known convention: subjects/{kind.Folder()}/{name}/runs/{runId}/.
        var sanitizedKind = subject.Kind.Folder();
        var sanitizedName = SanitizeForPath(subject.Name);
        var canonicalRunDir = Path.Combine(
            SampleAgentEvalDir, "subjects", sanitizedKind, sanitizedName, "runs", runId);

        return new SampleRunPaths(
            CanonicalRunDir: canonicalRunDir,
            SidecarDir: sidecarDir,
            SidecarJson: sidecarJsonPath,
            SidecarHtml: sidecarHtmlPath,
            SidecarPdf: sidecarPdfPath,
            RunId: runId,
            AuditHash: auditHash);
    }

    /// <summary>
    /// Dispatches compliance-evidence persistence to the right reporter based on
    /// the <paramref name="regulationCode"/>. GDPR + EU AI Act use their composite-
    /// tree reporters; the OWASP / MITRE reporters take a <c>RedTeamResult</c>
    /// directly and are wired by the OWASP / MITRE samples themselves
    /// (which split the scan into <c>ScanAsync</c> + <c>BuildEvalResult</c>
    /// so the native shape stays available).
    /// </summary>
    private static async Task WriteComplianceEvidenceAsync(
        FileSystemOutputStore store,
        SubjectIdentity subject,
        string runId,
        EvalResult compositeResult,
        string regulationCode,
        string? presetLabel,
        ArticlesRegistry? gdprRegistry,
        EuAiActArticlesRegistry? euAiActRegistry,
        string? judgeModel,
        CancellationToken ct)
    {
        switch (regulationCode.ToLowerInvariant())
        {
            case "gdpr":
                if (gdprRegistry is null)
                    throw new InvalidOperationException("GDPR evidence requires ArticlesRegistry.");
                var gdprReporter = new GDPRComplianceReporter(gdprRegistry);
                await gdprReporter.SaveReportAsync(
                    store, subject, runId, compositeResult,
                    new GdprReportOptions(
                        Preset: presetLabel ?? "standard",
                        DomainPacks: Array.Empty<string>(),
                        JudgeMode: "mode-a",
                        JudgeModel: judgeModel),
                    ct);
                break;

            case "eu-ai-act":
                if (euAiActRegistry is null)
                    throw new InvalidOperationException("EU AI Act evidence requires EuAiActArticlesRegistry.");
                var euReporter = new EuAiActComplianceReporter(euAiActRegistry);
                await euReporter.SaveReportAsync(
                    store, subject, runId, compositeResult,
                    new EuAiActReportOptions(
                        Preset: presetLabel ?? "standard",
                        DomainPacks: Array.Empty<string>(),
                        JudgeMode: "mode-a",
                        JudgeModel: judgeModel),
                    ct);
                break;

            default:
                throw new ArgumentException(
                    $"Unknown regulationCodeForEvidence '{regulationCode}'. " +
                    "Known: gdpr, eu-ai-act. OWASP / MITRE samples must pass their " +
                    "RedTeamResult to WriteRedTeamComplianceEvidenceAsync instead.",
                    nameof(regulationCode));
        }
    }

    /// <summary>
    /// OWASP / MITRE samples call this after <c>WriteReportsViaStoreAsync</c> with
    /// the <see cref="RedTeamResult"/> they captured via <c>ScanAsync</c>. The
    /// reporter writes its native compliance evidence shape under
    /// <c>.agenteval/compliance/{REG}/{subject}/{ts}/evidence.json</c> with the
    /// audit chain validated against the canonical run.
    /// </summary>
    public static async Task WriteRedTeamComplianceEvidenceAsync(
        SubjectIdentity subject,
        string runId,
        RedTeamResult redTeamResult,
        string regulationCode,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(redTeamResult);
        ArgumentException.ThrowIfNullOrWhiteSpace(regulationCode);

        var store = SharedStore;
        switch (regulationCode.ToLowerInvariant())
        {
            case "owasp":
                await new OWASPComplianceReporter().SaveReportAsync(store, subject, runId, redTeamResult, options: null, ct);
                break;
            case "mitre":
                await new MITREATLASReporter().SaveReportAsync(store, subject, runId, redTeamResult, options: null, ct);
                break;
            default:
                throw new ArgumentException(
                    $"Unknown red-team regulationCode '{regulationCode}'. Known: owasp, mitre.",
                    nameof(regulationCode));
        }
    }

    /// <summary>
    /// Walks the <paramref name="node"/> EvalResult tree leaf-first, yielding
    /// each atomic leaf paired with its metric key. Mirrors
    /// <c>GdprBenchmarkRunner.EnumerateAtomicLeaves</c>.
    /// </summary>
    private static IEnumerable<(string Id, EvalResult Result)> EnumerateAtomicLeaves(EvalResult node)
    {
        var subs = node.Details.SubResults;
        if (subs is null || subs.Count == 0)
        {
            yield return (node.Metric.Key, node);
            yield break;
        }
        foreach (var child in subs)
            foreach (var leaf in EnumerateAtomicLeaves(child))
                yield return leaf;
    }

    /// <summary>
    /// Builds a <see cref="RunSummary"/> from a composite result's verdict and
    /// the rolled-up leaf statistics. Mirrors the shape the GDPR / OWASP CLI
    /// commands produce so Mission Control's view is identical.
    /// </summary>
    private static RunSummary BuildRunSummary(EvalResult root, string runId)
    {
        var leaves = EnumerateAtomicLeaves(root).Select(l => l.Result).ToList();
        var passed = leaves.Count(l => l.Score.Passed);
        var failed = leaves.Count(l => !l.Score.Passed && l.Score.Label != "warn");
        var warnings = leaves.Count(l => l.Score.Label == "warn");

        var verdict = root.Score.Label.ToUpperInvariant() switch
        {
            "PASS" => "PASS",
            "WARN" => "WARN",
            _      => "FAIL",
        };

        return new RunSummary(
            SchemaVersion: "1.0",
            RunId: runId,
            Verdict: verdict,
            Stats: new RunStats(leaves.Count, passed, failed, warnings),
            Metrics: new Dictionary<string, double>
            {
                ["overallScore"] = root.Score.Value,
            });
    }

    /// <summary>
    /// Sanitises a name for use as a path segment — mirrors the rule the CLI
    /// commands use so the canonical-run path the helper reconstructs matches
    /// the path the store actually wrote to.
    /// </summary>
    private static string SanitizeForPath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).ToArray();
        var s = string.Concat(name.Select(c => invalid.Contains(c) ? '-' : c));
        return s.Trim('.', ' ');
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
    /// Prints the v0.10.1 dual-location summary: canonical <c>.agenteval/</c> run dir
    /// (Mission Control reads here) plus sidecar <c>output/{family}/run-{ts}/</c>
    /// (HTML / PDF / JSON for direct human consumption).
    /// </summary>
    public static void PrintReportPaths(EvalResult result, SampleRunPaths paths)
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
        Console.WriteLine($"   Run ID:   {paths.RunId}");
        if (paths.AuditHash is not null)
            Console.WriteLine($"   Audit:    {paths.AuditHash}");
        Console.WriteLine();
        Console.WriteLine($"   Canonical run:    {paths.CanonicalRunDir}");
        Console.WriteLine($"   Sidecar reports:  {paths.SidecarDir}");
        Console.WriteLine($"     - JSON: {Path.GetFileName(paths.SidecarJson)}");
        Console.WriteLine($"     - HTML: {Path.GetFileName(paths.SidecarHtml)}");
        if (paths.SidecarPdf is not null)
            Console.WriteLine($"     - PDF:  {Path.GetFileName(paths.SidecarPdf)}");
        Console.WriteLine();
        Console.WriteLine("   Mission Control:");
        Console.WriteLine("     cd samples/AgentEval.Samples && dotnet agenteval mc");
        Console.WriteLine("     (MC discovers .agenteval/ automatically and lists this run)");
    }

    /// <summary>
    /// Companion to the tuple-based <c>OfferToOpenReports</c> overload for the
    /// dual-location <see cref="SampleRunPaths"/>. Opens the sidecar files —
    /// the canonical run dir is for tooling consumption.
    /// </summary>
    public static void OfferToOpenReports(SampleRunPaths paths)
        => OfferToOpenReports((paths.SidecarJson, paths.SidecarHtml, paths.SidecarPdf));

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

    // ═══════════════════════════════════════════════════════════════════════════
    //  Preset selection (Smoke / Standard / AuditGrade)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves the <see cref="SamplePreset"/> the caller should run at. Resolution
    /// order:
    /// <list type="number">
    ///   <item><description><paramref name="args"/> contains <c>--preset smoke|standard|audit-grade|audit</c></description></item>
    ///   <item><description><c>AGENTEVAL_SAMPLES_PRESET</c> env var (case-insensitive)</description></item>
    ///   <item><description>If <paramref name="args"/>=null AND non-interactive (input redirected or <c>AGENTEVAL_SAMPLES_NONINTERACTIVE=1</c>): default to <see cref="SamplePreset.Smoke"/></description></item>
    ///   <item><description>Otherwise: short console prompt (<c>[s]moke / [t]andard / [a]udit-grade</c>) with single-char read; Enter / unknown → Smoke</description></item>
    /// </list>
    /// </summary>
    /// <param name="envOverride">
    /// Optional in-process override (mostly for tests). When non-null, takes precedence
    /// over the <c>AGENTEVAL_SAMPLES_PRESET</c> env var.
    /// </param>
    /// <param name="args">
    /// Optional command-line argument list. When supplied, takes top precedence — the
    /// helper looks for the <c>--preset &lt;value&gt;</c> pair anywhere in the array.
    /// </param>
    /// <returns>The resolved <see cref="SamplePreset"/>.</returns>
    public static SamplePreset ResolvePreset(string? envOverride = null, string[]? args = null)
    {
        // 1. CLI args: --preset <value>
        if (args is { Length: > 0 })
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--preset", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParsePreset(args[i + 1], out var parsed))
                        return parsed;
                }
            }
        }

        // 2. env override (test hook) > AGENTEVAL_SAMPLES_PRESET env var.
        var envValue = envOverride ?? Environment.GetEnvironmentVariable("AGENTEVAL_SAMPLES_PRESET");
        if (!string.IsNullOrWhiteSpace(envValue) && TryParsePreset(envValue, out var fromEnv))
            return fromEnv;

        // 3. Non-interactive → Smoke. (No safe way to read a key without blocking.)
        if (IsNonInteractive())
            return SamplePreset.Smoke;

        // 4. Interactive prompt.
        Console.WriteLine();
        Console.Write("   Preset? [s]moke (default)  [t]andard  [a]udit-grade : ");
        ConsoleKeyInfo key;
        try
        {
            key = Console.ReadKey(intercept: false);
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine();
            return SamplePreset.Smoke;
        }
        Console.WriteLine();

        return char.ToLowerInvariant(key.KeyChar) switch
        {
            't' => SamplePreset.Standard,
            'a' => SamplePreset.AuditGrade,
            _ => SamplePreset.Smoke,
        };
    }

    private static bool TryParsePreset(string raw, out SamplePreset preset)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "s":
            case "smoke":
                preset = SamplePreset.Smoke;
                return true;
            case "t":
            case "std":
            case "standard":
                preset = SamplePreset.Standard;
                return true;
            case "a":
            case "audit":
            case "audit-grade":
            case "auditgrade":
                preset = SamplePreset.AuditGrade;
                return true;
            default:
                preset = SamplePreset.Smoke;
                return false;
        }
    }

    /// <summary>Prints a one-line banner identifying the resolved preset.</summary>
    public static void PrintPreset(SamplePreset preset)
    {
        var label = preset switch
        {
            SamplePreset.Smoke => "Smoke (fast CI, ~1 min, cents)",
            SamplePreset.Standard => "Standard (~5–15 min, ~$0.50–2)",
            SamplePreset.AuditGrade => "Audit-Grade (~15–45 min, ~$2–10)",
            _ => preset.ToString(),
        };
        Console.WriteLine($"   Preset:   {label}");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Real-agent + judge factories (Azure OpenAI–backed)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a real Azure OpenAI–backed <see cref="IStreamableAgent"/> using the
    /// repository's <see cref="AIConfig"/> (endpoint / key / deployment env vars).
    /// Call sites should gate on <see cref="AIConfig.IsConfigured"/> before invoking
    /// this — it throws if the credentials are missing.
    /// </summary>
    /// <param name="name">Agent name surfaced in reports.</param>
    /// <param name="systemPrompt">System prompt prepended to every turn.</param>
    /// <returns>A live agent that talks to Azure OpenAI.</returns>
    public static IStreamableAgent CreateAzureAgent(string name, string? systemPrompt = null)
    {
        var azure = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chat = azure.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();
        return chat.AsEvaluableAgent(name: name, systemPrompt: systemPrompt);
    }

    /// <summary>
    /// Builds a real Azure OpenAI–backed <see cref="IEvaluator"/> ("judge") using the
    /// repository's <see cref="AIConfig"/>. Same deployment as the agent for sample
    /// simplicity — production audits usually use a stronger judge than the SUT.
    /// </summary>
    public static IEvaluator CreateAzureJudge()
    {
        var azure = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chat = azure.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();
        return new ChatClientEvaluator(chat);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Per-scenario compliance runner (GDPR / EU AI Act samples)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Re-evaluates a compliance preset composite (e.g. <c>GdprBenchmark.Smoke(...)</c>)
    /// by probing the agent <em>per scenario</em> using each scenario's <c>input</c>
    /// text, then re-aggregating per-article and preset-level scores using the same
    /// <see cref="IAggregationStrategy"/> + threshold the original composite was built with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default <see cref="CompositeEval.EvaluateAsync"/> threads ONE <see cref="EvalInput"/>
    /// to every sub-eval. For compliance benchmarks each article has multiple distinct
    /// scenario probes, so a single hardcoded response can't faithfully represent compliance.
    /// This helper walks the article-level composites by index, looks up each article's
    /// <see cref="Func{T,TResult}"/> scenario list (per-article inputs come from
    /// <c>registry.GetSpec(controlId).Scenarios</c> in caller-supplied order), invokes the
    /// agent with each scenario's input, and feeds the live response into the matching
    /// atomic eval. The atomic ↔ scenario alignment is by ordinal index — which holds
    /// for v1 because <c>ArticleCompositeBuilder.Build</c> projects scenarios in YAML
    /// declaration order into the composite.
    /// </para>
    /// <para>
    /// <b>Limitations</b>: composites built via <c>WithExtraScenarios</c> (domain packs)
    /// renormalise weights after extension; if you pass such an augmented composite the
    /// ordinal alignment still holds because the spec lookup is keyed on control id and
    /// the augmented composite preserves scenario insertion order. Multi-judge wrapping
    /// (Critical-severity scenarios with multiple judges) is still respected — we hit the
    /// outer eval (<see cref="MultiJudgeWrapper"/>), which routes to each underlying judge.
    /// </para>
    /// </remarks>
    /// <param name="presetComposite">The preset-level <see cref="CompositeEval"/> (the value <c>GdprBenchmark.Smoke(...)</c> returns).</param>
    /// <param name="getArticleScenarioInputs">Given an article composite's <c>Key</c> (control id), return the ordered list of per-scenario input strings.</param>
    /// <param name="agent">The agent being audited.</param>
    /// <param name="reportProgress">Optional progress callback (article index, total, current control id).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A composite <see cref="EvalResult"/> shaped exactly like the original composite would produce, but built from per-scenario live agent probes.</returns>
    public static async Task<EvalResult> RunCompliancePresetWithAgentProbesAsync(
        CompositeEval presetComposite,
        Func<string, IReadOnlyList<string>> getArticleScenarioInputs,
        IStreamableAgent agent,
        Action<int, int, string>? reportProgress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(presetComposite);
        ArgumentNullException.ThrowIfNull(getArticleScenarioInputs);
        ArgumentNullException.ThrowIfNull(agent);

        // Count articles up-front so the progress callback can report N/M honestly even
        // when the preset is pillar-nested (Standard / AuditGrade) rather than flat (Smoke).
        var articlesTotal = CountArticles(presetComposite, getArticleScenarioInputs);
        var articlesCompleted = 0;

        var rootResult = await EvaluateNodeAsync(presetComposite);
        return rootResult;

        // Local recursive walker — handles flat presets (Smoke) and pillar-nested presets
        // (Standard / AuditGrade) uniformly. The "article" boundary is detected by checking
        // whether getArticleScenarioInputs has scenario data for the node's Key — that lets
        // a pillar (which has no scenarios of its own) propagate the recursion to its child
        // articles without us having to encode the GDPR / EU AI Act pillar topology here.
        async Task<EvalResult> EvaluateNodeAsync(CompositeEval node)
        {
            // Is this an "article" boundary? (We have per-scenario inputs for this control id.)
            IReadOnlyList<string>? scenarioInputs = null;
            try { scenarioInputs = getArticleScenarioInputs(node.Key); }
            catch (KeyNotFoundException) { /* not an article — keep descending */ }

            if (scenarioInputs is { Count: > 0 })
            {
                articlesCompleted++;
                reportProgress?.Invoke(articlesCompleted, articlesTotal, node.Key);

                var scenarioComponents = node.Components;
                var inputsCount = scenarioInputs.Count;
                var componentsCount = scenarioComponents.Count;
                var perScenarioCount = Math.Min(inputsCount, componentsCount);

                // Coverage-loss visibility: when the YAML-supplied probes count and the
                // composite-component count diverge, the truncated side is silently lost
                // unless we surface it. Emit a console warning + carry honest "skipped"
                // leaves for any extra components (we have a rubric but no probe to grade
                // against) so the verdict reflects the real coverage gap.
                if (inputsCount != componentsCount)
                {
                    Console.Error.WriteLine(
                        $"   ⚠  Article '{node.Key}' has {inputsCount} YAML probe input(s) but "
                        + $"{componentsCount} composite component(s). Honest 'skipped' leaves will "
                        + $"be emitted for the {Math.Abs(inputsCount - componentsCount)} missing "
                        + $"pair(s); compliance verdict will reflect this coverage gap.");
                }

                var scenarioResults = new List<EvalResult>(Math.Max(inputsCount, componentsCount));
                for (var si = 0; si < perScenarioCount; si++)
                {
                    var probe = scenarioInputs[si];
                    string responseText;
                    try
                    {
                        var resp = await agent.InvokeAsync(probe, ct);
                        responseText = resp.Text ?? string.Empty;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // Real-world compliance probes commonly trip provider safety filters
                        // (Azure content filter, OpenAI moderation, etc.) — a single rejection
                        // must not abort the entire preset run. Surface the rejection as a
                        // honest "skipped" result for that scenario; the rest of the audit
                        // continues. The leaf still appears in the tree with a recommendation
                        // explaining the rejection, so reviewers see exactly what was filtered.
                        scenarioResults.Add(new EvalResult(
                            Metric: new EvalMetadata(
                                Key: scenarioComponents[si].Eval.Key,
                                Name: scenarioComponents[si].Eval.Name,
                                Category: scenarioComponents[si].Eval.Category,
                                Version: scenarioComponents[si].Eval.Version),
                            Score: new EvalScore(0, null, "skipped", false, null, "none", null),
                            Details: new EvalDetails(
                                Dimensions: null,
                                Evidence: null,
                                Recommendations: new[]
                                {
                                    $"Agent invocation rejected by upstream guardrails: {ex.GetType().Name}: {ex.Message}",
                                    "The probe was not graded. A real audit would route this through a less-restricted endpoint or document the filter as a control in itself."
                                },
                                SubResults: null,
                                AggregationStrategy: null),
                            Provenance: new EvalProvenance(
                                Type: "skipped",
                                JudgeModel: null,
                                PromptId: null,
                                PromptHash: null,
                                TokensUsed: null,
                                EstimatedCost: 0,
                                CacheHit: false),
                            EvaluatedAt: DateTimeOffset.UtcNow));
                        continue;
                    }
                    var perScenarioInput = new EvalInput(Query: probe, Response: responseText);
                    var leaf = await scenarioComponents[si].Eval.EvaluateAsync(perScenarioInput, ct);
                    scenarioResults.Add(leaf);
                }

                // Coverage-loss visibility (extra components): emit honest skipped
                // leaves for components that have no matching probe input, so the
                // verdict aggregator sees the full component roster — not a truncated
                // pass-by-default subset.
                for (var ei = perScenarioCount; ei < componentsCount; ei++)
                {
                    scenarioResults.Add(EvalResult.Skipped(
                        scenarioComponents[ei].Eval,
                        $"no scenario probe input provided for '{scenarioComponents[ei].Eval.Key}'"));
                }

                return BuildAggregatedResult(
                    node, scenarioResults,
                    scenarioComponents.ToArray());
            }

            // Not an article: descend into child composites (typical pillar nodes).
            var subResults = new List<EvalResult>(node.Components.Count);
            foreach (var child in node.Components)
            {
                if (child.Eval is CompositeEval subComposite)
                {
                    subResults.Add(await EvaluateNodeAsync(subComposite));
                }
                else
                {
                    // Leaf at pillar level (rare). Use the original spec input — there's nothing
                    // scenario-shaped to probe with, so we fall back to the default behaviour:
                    // skip honestly rather than fake a probe.
                    subResults.Add(EvalResult.Skipped(child.Eval,
                        "sample helper requires CompositeEval at article boundary; non-composite leaves are skipped"));
                }
            }

            return BuildAggregatedResult(node, subResults, node.Components);
        }
    }

    /// <summary>
    /// Recursively counts article-shaped composite nodes (those whose Key has
    /// per-scenario inputs available in <paramref name="getArticleScenarioInputs"/>).
    /// Used so progress reporting can show "article k / N" honestly.
    /// </summary>
    private static int CountArticles(
        CompositeEval node,
        Func<string, IReadOnlyList<string>> getArticleScenarioInputs)
    {
        try
        {
            var scenarioInputs = getArticleScenarioInputs(node.Key);
            if (scenarioInputs is { Count: > 0 })
                return 1;
        }
        catch (KeyNotFoundException) { /* not an article — keep walking */ }

        var total = 0;
        foreach (var child in node.Components)
        {
            if (child.Eval is CompositeEval sub)
                total += CountArticles(sub, getArticleScenarioInputs);
        }
        return total;
    }

    /// <summary>
    /// Aggregates <paramref name="subs"/> using <paramref name="composite"/>'s aggregation
    /// strategy + threshold and assembles a fresh <see cref="EvalResult"/> with the
    /// composite's metadata. Mirrors <see cref="CompositeEval.EvaluateAsync"/>'s verdict
    /// matrix so the synthetic result behaves identically to one produced by direct
    /// composite evaluation.
    /// </summary>
    private static EvalResult BuildAggregatedResult(
        CompositeEval composite,
        IReadOnlyList<EvalResult> subs,
        IReadOnlyList<EvalComponent> components)
    {
        if (subs.Count == 0)
        {
            return EvalResult.Skipped(composite, "no sub-results were produced");
        }

        var (score, severity) = composite.Aggregation.Aggregate(subs, components);
        var (cost, allCacheHits) = CostRollup.Aggregate(subs);

        // Severity rollup honours optional components — same logic as CompositeEval.
        var verdictSeverity = severity;
        if (components.Any(c => !c.Required))
        {
            var requiredSeverities = subs
                .Zip(components, (s, c) => (Sub: s, Component: c))
                .Where(pair => pair.Component.Required)
                .Select(pair => pair.Sub.Score.Severity)
                .ToArray();
            verdictSeverity = requiredSeverities.Length > 0
                ? SeverityRollup.Max(requiredSeverities)
                : "none";
        }

        var label = composite.Threshold is { } t
            ? (score >= t ? "pass" : "fail")
            : verdictSeverity switch
            {
                "critical" or "high" => "fail",
                "medium" => "warn",
                _ => "pass"
            };
        var passed = label == "pass";

        return new EvalResult(
            Metric: new EvalMetadata(composite.Key, composite.Name, composite.Category, composite.Version),
            Score: new EvalScore(score, null, label, passed, composite.Threshold, severity, null),
            Details: new EvalDetails(
                Dimensions: null,
                Evidence: null,
                Recommendations: null,
                SubResults: subs,
                AggregationStrategy: composite.Aggregation.Name),
            Provenance: new EvalProvenance(
                Type: "composite",
                JudgeModel: null,
                PromptId: null,
                PromptHash: null,
                TokensUsed: null,
                EstimatedCost: cost,
                CacheHit: allCacheHits),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }
}
