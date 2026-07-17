// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text;
using AgentEval.Guardrails;
using AgentEval.MAF.Skills;
using AgentEval.Skills;
using AgentEval.Testing;
using Microsoft.Agents.AI;

namespace AgentEval.Cli.Commands;

/// <summary>
/// The 'agenteval skills scan' + 'agenteval skills baseline' commands — a static, offline SKILL.md +
/// governance-flag scan for a directory of MAF Agent Skills (<see cref="MafSkillScanner"/>/
/// <see cref="SkillComplianceValidator"/>), plus Agent Skills Wave 1 (§4.1/§4.3): a timestamped baseline
/// ledger (<c>--write-baseline</c> / <c>skills baseline list|diff|history</c>) and repo-wide multi-convention
/// discovery (<c>--repo</c>). v1 scan scope is deliberately compliance-only, not the full Skill Health &amp;
/// Security Index (see <c>strategy/FutureFeatures/Skills/Skills-Scan-CLI-Verb-Design.md</c> §2.3).
/// </summary>
internal static class SkillsScanCommand
{
    private const string DefaultBaselineRoot = ".agenteval/skills-baselines";

    // A DIFFERENT default from `scan`/`scan --repo`'s DefaultBaselineRoot, deliberately — `skills baseline
    // diff`/`history` (unchanged, pre-existing code) always compare "the two most recent snapshots" with no
    // concept of which verb/scope produced a given snapshot. Sharing one default root across scan-workspace
    // (which can capture hundreds of skills across dozens of repos) and a plain `scan`/`scan --repo` (a
    // handful of skills) would let an operator's next `scan` silently get diffed against a wildly
    // different-scoped workspace snapshot, or vice versa — most of the "diff" would be spurious [new]/
    // [removed] noise from scope mismatch, not real drift. Separate defaults prevent that footgun for anyone
    // who doesn't pass --baseline-root explicitly; passing the SAME root to both verbs is still possible if
    // an operator genuinely wants a unified ledger.
    private const string DefaultWorkspaceBaselineRoot = ".agenteval/skills-baselines-workspace";

    public static Command Create()
    {
        var skillsCmd = new Command("skills", "MAF Agent Skills utilities");
        skillsCmd.Subcommands.Add(BuildScanCommand());
        skillsCmd.Subcommands.Add(BuildScanWorkspaceCommand());
        skillsCmd.Subcommands.Add(BuildBaselineCommand());
        return skillsCmd;
    }

    // ═══════════════════════════════════════════ scan ═══════════════════════════════════════════

    /// <summary>The options <c>scan</c> and <c>scan-workspace</c> share verbatim — see <see cref="BuildCommonScanOptions"/>.</summary>
    private sealed record CommonScanOptions(
        Option<string> Format, Option<FileInfo?> Output, Option<bool> FailOnNoncompliant,
        Option<bool> WriteBaseline, Option<string> BaselineRoot, Option<bool> CheckBaseline,
        Option<FileInfo?> SaveManifestBaseline, Option<FileInfo?> ManifestBaseline, Option<string?> BaselineNotes)
    {
        public void AddAllTo(Command command)
        {
            command.Options.Add(Format);
            command.Options.Add(Output);
            command.Options.Add(FailOnNoncompliant);
            command.Options.Add(WriteBaseline);
            command.Options.Add(BaselineRoot);
            command.Options.Add(CheckBaseline);
            command.Options.Add(SaveManifestBaseline);
            command.Options.Add(ManifestBaseline);
            command.Options.Add(BaselineNotes);
        }
    }

    /// <summary>
    /// The options <c>scan</c> and <c>scan-workspace</c> share verbatim (everything except <c>--repo</c>,
    /// which only <c>scan</c> has, and each command's own default <c>--baseline-root</c>). Built ONCE here so
    /// the two commands' help text cannot silently drift apart the way it already had before this helper
    /// existed (the two copies' <c>--check-baseline</c> descriptions had gone out of sync).
    /// </summary>
    private static CommonScanOptions BuildCommonScanOptions(string defaultBaselineRoot) => new(
        new Option<string>("--format") { DefaultValueFactory = _ => "console", Description = "Output format: console | markdown | json" },
        new Option<FileInfo?>("-o", "--output") { Description = "Output file (default: stdout)" },
        new Option<bool>("--fail-on-noncompliant")
            { Description = "Exit non-zero (1) when the scan finds a High-severity finding (report.IsCompliant == false). Default off (informational-only)." },
        new Option<bool>("--write-baseline")
            { Description = "After scanning, capture a timestamped baseline snapshot (content hash + structural fingerprint per skill) into the baseline ledger. See 'skills baseline list|diff|history'." },
        new Option<string>("--baseline-root")
            { DefaultValueFactory = _ => defaultBaselineRoot, Description = "Baseline ledger root directory (used with --write-baseline / --check-baseline)." },
        new Option<bool>("--check-baseline")
            { Description = "Trust-on-first-use: compare each scanned skill's content hash against the baseline ledger's history (--baseline-root). A match against ANY prior snapshot for the same skill name is reported as an informational 'matches a previously-vetted copy' finding. Meaningless on a first-ever scan (no history yet) — pair with --write-baseline on earlier runs." },
        new Option<FileInfo?>("--save-manifest-baseline")
            { Description = "Capture a trust-time hash-pin of every scanned skill's manifest content (name, description, resource/script inventory, allowed-tools, compatibility) to this JSON file. A SINGLE pinned file, distinct from --write-baseline's multi-snapshot ledger — mirrors the RedTeam baseline/diff CI pattern: commit this file, then re-check future scans against it with --manifest-baseline." },
        new Option<FileInfo?>("--manifest-baseline")
            { Description = "Check every scanned skill's manifest content against a file captured by --save-manifest-baseline. A skill whose content changed since the pin was captured is reported as a High-severity ManifestChangedSinceBaseline finding — gated by --fail-on-noncompliant like any other High finding, no separate flag needed. A possible rug-pull: a previously-approved skill silently changing after approval." },
        new Option<string?>("--baseline-note")   // singular, matching RedTeamCommand's established --baseline-note precedent
            { Description = "Optional human note saved alongside --save-manifest-baseline (e.g. who approved it, why). Has no effect without --save-manifest-baseline." });

    private static Command BuildScanCommand()
    {
        var scanCmd = new Command("scan", "Scan a directory of MAF Agent Skills for GA compliance + governance findings");

        var pathArg = new Argument<DirectoryInfo>("path")
            { Description = "Directory containing one or more SKILL.md-rooted skill folders (a repo root when --repo is set)" };
        var opts = BuildCommonScanOptions(DefaultBaselineRoot);
        var repoOpt = new Option<bool>("--repo")
            { Description = "Treat <path> as a REPO ROOT: scan every known skill-directory convention found under it (.claude/skills, .agents/skills, ...) and aggregate the results, instead of treating <path> itself as one skill directory. Always content-hashes every skill folder (real file I/O) to check for cross-location drift — the same name resolving to different content in two conventions — even without --write-baseline/--check-baseline." };

        scanCmd.Arguments.Add(pathArg);
        opts.AddAllTo(scanCmd);
        scanCmd.Options.Add(repoOpt);

        scanCmd.SetAction(async (parseResult, ct) =>
        {
            try
            {
                return await ExecuteAsync(
                    parseResult.GetValue(pathArg)!,
                    parseResult.GetValue(opts.Format)!,
                    parseResult.GetValue(opts.Output),
                    parseResult.GetValue(opts.FailOnNoncompliant),
                    parseResult.GetValue(opts.WriteBaseline),
                    parseResult.GetValue(repoOpt),
                    parseResult.GetValue(opts.BaselineRoot)!,
                    parseResult.GetValue(opts.CheckBaseline),
                    parseResult.GetValue(opts.SaveManifestBaseline),
                    parseResult.GetValue(opts.ManifestBaseline),
                    parseResult.GetValue(opts.BaselineNotes),
                    ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Error: {ex.Message}");
                return ExitCodes.RuntimeError;
            }
        });

        return scanCmd;
    }

    /// <summary>
    /// Core execution logic — separated from command wiring for testability. The scan itself never invokes
    /// a model — confirmed empirically: <see cref="MafSkillScanner.ScanFileSkillsWithInfoAsync"/> only
    /// reads <see cref="AgentSkillsSourceContext"/>'s <c>Agent</c> reference to satisfy MAF's own
    /// constructor requirement and never calls it, exactly like the existing
    /// <c>MafSkillScannerTests.FakeAgent()</c> precedent, reused (not reinvented) below via
    /// <see cref="ScriptedChatClient"/> — so no credentials are ever required for this verb.
    /// </summary>
    internal static async Task<int> ExecuteAsync(
        DirectoryInfo path, string format, FileInfo? output, bool failOnNoncompliant,
        bool writeBaseline = false, bool repo = false, string baselineRoot = DefaultBaselineRoot,
        bool checkBaseline = false, FileInfo? saveManifestBaseline = null, FileInfo? manifestBaseline = null,
        string? baselineNotes = null, CancellationToken ct = default)
    {
        if (!path.Exists)
        {
            throw new DirectoryNotFoundException($"Skills directory not found: {path.FullName}");
        }

        AIAgent noOpAgent = new ChatClientAgent(new ScriptedChatClient(), new ChatClientAgentOptions { Name = "SkillsScanCommand" });

        SkillComplianceReport report;
        IReadOnlyList<SkillBaselineEntry> entries;

        // Agent Skills Wave 2: entries (content hash + structural fingerprint per skill — real disk I/O,
        // SkillContentHasher reads every byte of every file in every skill folder) are needed for
        // cross-location drift detection, trust-on-first-use matching, persisting a snapshot, and
        // the manifest-baseline hash-pin drift gate — the latter reuses StructuralFingerprint (already
        // SkillManifestPoisoningGate.Fingerprint(manifest) under the hood), no raw manifest list needed.
        // --repo builds them unconditionally: drift detection has no opt-out flag, and a repo-wide
        // governance scan is expected to cost more than a single-directory one (documented in the --repo
        // option's own help text). A single-directory scan is NOT --repo-wide, though — a plain
        // `skills scan <path>` with no flags must keep paying zero extra I/O, exactly like before Wave 2
        // (previously this cost was gated behind --write-baseline only), so entries are built there ONLY
        // when something will actually consume them.
        if (repo)
        {
            var (combinedReport, combinedEntries, conventionSummary) =
                await ScanRepoWideAsync(path.FullName, noOpAgent, ct).ConfigureAwait(false);
            report = combinedReport;
            entries = combinedEntries;
            Console.Error.WriteLine(conventionSummary);
        }
        else
        {
            var result = await MafSkillScanner.ScanFileSkillsWithInfoAsync(path.FullName, noOpAgent, options: null, ct).ConfigureAwait(false);
            report = result.Report;
            entries = (writeBaseline || checkBaseline || saveManifestBaseline is not null || manifestBaseline is not null)
                ? result.Skills.Select(info => ToBaselineEntry(info, report.Findings, conventionPrefix: null)).ToList()
                : [];
        }

        // Agent Skills Wave 2 (§4.2): --repo cross-location drift is always checked (no extra flag — a
        // repo-wide scan is the only mode where "cross-location" is even meaningful for a single repo).
        return await FinishScanAsync(
            report, entries, checkCrossLocationDrift: repo, format, output, failOnNoncompliant,
            writeBaseline, baselineRoot, checkBaseline, saveManifestBaseline, manifestBaseline, baselineNotes,
            path.FullName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The tail shared by every scan mode (single-directory, <c>--repo</c>, and Wave 3a's
    /// <c>scan-workspace</c>) once discovery has produced a <see cref="SkillComplianceReport"/> and its
    /// matching <see cref="SkillBaselineEntry"/> list: merge in cross-location-drift / trust-on-first-use /
    /// manifest-baseline-drift findings, render, write the output, persist a baseline snapshot if asked, and
    /// compute the exit code.
    /// </summary>
    private static async Task<int> FinishScanAsync(
        SkillComplianceReport report, IReadOnlyList<SkillBaselineEntry> entries, bool checkCrossLocationDrift,
        string format, FileInfo? output, bool failOnNoncompliant, bool writeBaseline, string baselineRoot,
        bool checkBaseline, FileInfo? saveManifestBaseline, FileInfo? manifestBaseline, string? baselineNotes,
        string scannedRoot, CancellationToken ct)
    {
        // --check-baseline trust-on-first-use matching is opt-in (meaningless without prior baseline history).
        var extraFindings = new List<SkillComplianceFinding>();
        if (checkCrossLocationDrift)
        {
            extraFindings.AddRange(DetectCrossLocationDrift(entries));
        }

        if (checkBaseline)
        {
            var historyStore = new JsonFileSkillBaselineStore(baselineRoot);
            var history = await historyStore.ListAsync(ct).ConfigureAwait(false);
            extraFindings.AddRange(DetectPreviouslyVetted(entries, history));
        }

        // NOT all extraFindings sources are equally severe: DetectCrossLocationDrift (Medium) and
        // DetectPreviouslyVetted (Low) never trip --fail-on-noncompliant; DetectManifestDrift below is
        // High and DOES. Don't assume a future addition here is non-blocking by pattern-matching these two
        // — check the actual Severity you give it.

        // Computed at most once, shared by the drift-check below AND the save block further down — avoids
        // hashing every entry twice when --manifest-baseline and --save-manifest-baseline are both passed
        // in the same run (a real workflow this feature explicitly mirrors from the RedTeam CI pattern:
        // "verify the old pin, then re-pin the new baseline" in one command).
        Dictionary<string, string>? currentManifestHashes = null;

        // A Changed finding requires the same skill name present in BOTH the baseline and the current scan
        // — with zero entries, that's mathematically impossible, so skip the file load entirely rather than
        // risk a spurious error for a --manifest-baseline path that happens to be missing/corrupted on a
        // scan that had nothing to check in the first place.
        if (manifestBaseline is not null && entries.Count > 0)
        {
            currentManifestHashes = ManifestFingerprintsByName(entries);
            SkillManifestBaseline? baseline;
            try
            {
                baseline = await SkillManifestBaseline.LoadAsync(manifestBaseline.FullName, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Same graceful "nothing to compare against yet" precedent --check-baseline already
                // establishes for an empty ledger — a --manifest-baseline path that doesn't exist yet is not
                // fatal, just uninformative. Lets `--save-manifest-baseline x.json --manifest-baseline x.json`
                // work in ONE command on a first-ever run instead of requiring two separate invocations.
                baseline = null;
                Console.Error.WriteLine($"  Note: --manifest-baseline file not found ({manifestBaseline.FullName}) — nothing to compare against yet.");
            }

            if (baseline is not null)
            {
                extraFindings.AddRange(DetectManifestDrift(currentManifestHashes, baseline));
            }
        }

        if (extraFindings.Count > 0)
        {
            report = report with { Findings = [.. report.Findings, .. extraFindings] };
        }

        var rendered = RenderReport(report, format);

        if (output is not null)
        {
            await File.WriteAllTextAsync(output.FullName, rendered, ct).ConfigureAwait(false);
            Console.Error.WriteLine($"  Report written to: {output.FullName}");
        }
        else
        {
            Console.WriteLine(rendered);
        }

        if (writeBaseline)
        {
            var snapshot = new SkillBaselineSnapshot(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, scannedRoot, entries);
            var store = new JsonFileSkillBaselineStore(baselineRoot);
            var id = await store.SaveAsync(snapshot, ct).ConfigureAwait(false);
            Console.Error.WriteLine($"  Baseline snapshot saved: {id} ({snapshot.Skills.Count} skill(s) -> {baselineRoot})");
        }

        if (saveManifestBaseline is not null)
        {
            var hashes = currentManifestHashes ?? ManifestFingerprintsByName(entries);
            var baseline = new SkillManifestBaseline(DateTimeOffset.UtcNow, hashes, baselineNotes);
            // Unlike --write-baseline's JsonFileSkillBaselineStore (whose constructor creates its root
            // directory), SkillManifestBaseline.SaveAsync is a bare File.WriteAllTextAsync with no directory
            // creation — create the parent directory first so a fresh ".agenteval/..."-style path (the same
            // convention --baseline-root's own default already establishes) doesn't throw
            // DirectoryNotFoundException on a repo that's never captured a manifest baseline before.
            var parentDir = saveManifestBaseline.Directory;
            if (parentDir is not null && !parentDir.Exists)
            {
                parentDir.Create();
            }

            await baseline.SaveAsync(saveManifestBaseline.FullName, ct).ConfigureAwait(false);
            Console.Error.WriteLine($"  Manifest baseline saved: {saveManifestBaseline.FullName} ({hashes.Count} skill(s))");
        }
        else if (baselineNotes is not null)
        {
            Console.Error.WriteLine("  Warning: --baseline-note has no effect without --save-manifest-baseline — ignored.");
        }

        return ComputeExitCode(report, failOnNoncompliant);
    }

    private static string RenderReport(SkillComplianceReport report, string format) =>
        (format ?? "console").Trim().ToLowerInvariant() switch
        {
            "json" => SkillComplianceReportRenderer.RenderJson(report),
            "markdown" or "md" => SkillComplianceReportRenderer.RenderMarkdown(report),
            "console" or "" => SkillComplianceReportRenderer.RenderConsole(report),
            _ => throw new ArgumentException($"Unknown --format '{format}'. Valid: console, markdown, json."),
        };

    /// <summary>
    /// Pure exit-code gate, separated for direct testing (see the honesty finding on
    /// <see cref="ExecuteAsync"/> for why this is tested against a hand-built report rather than a real scan).
    /// </summary>
    internal static int ComputeExitCode(SkillComplianceReport report, bool failOnNoncompliant)
        => (failOnNoncompliant && !report.IsCompliant) ? ExitCodes.TestFailure : ExitCodes.Success;

    // ═══════════════════════════════ scan-workspace: filesystem multi-repo scan (Wave 3a) ═══════════════════════════════

    private static Command BuildScanWorkspaceCommand()
    {
        var scanWorkspaceCmd = new Command(
            "scan-workspace",
            "Scan a folder of ALREADY-CLONED repos (each immediate subdirectory is one repo) for MAF Agent Skills compliance + cross-repo drift. Filesystem-only — no network, no credentials; the operator's own clone step is what already handles repo access.");

        var pathArg = new Argument<DirectoryInfo>("path")
            { Description = "A folder whose immediate subdirectories are repo roots (e.g. where you 'gh repo clone'd your org's repos)" };
        var opts = BuildCommonScanOptions(DefaultWorkspaceBaselineRoot);

        scanWorkspaceCmd.Arguments.Add(pathArg);
        opts.AddAllTo(scanWorkspaceCmd);

        scanWorkspaceCmd.SetAction(async (parseResult, ct) =>
        {
            try
            {
                return await ExecuteWorkspaceAsync(
                    parseResult.GetValue(pathArg)!,
                    parseResult.GetValue(opts.Format)!,
                    parseResult.GetValue(opts.Output),
                    parseResult.GetValue(opts.FailOnNoncompliant),
                    parseResult.GetValue(opts.WriteBaseline),
                    parseResult.GetValue(opts.BaselineRoot)!,
                    parseResult.GetValue(opts.CheckBaseline),
                    parseResult.GetValue(opts.SaveManifestBaseline),
                    parseResult.GetValue(opts.ManifestBaseline),
                    parseResult.GetValue(opts.BaselineNotes),
                    ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Error: {ex.Message}");
                return ExitCodes.RuntimeError;
            }
        });

        return scanWorkspaceCmd;
    }

    /// <summary>
    /// Core execution logic for <c>scan-workspace</c> — separated from command wiring for testability, mirroring
    /// <see cref="ExecuteAsync"/>. Deliberately a SEPARATE verb from <c>scan --repo</c> rather than a bolted-on
    /// flag on it (naming a flag <c>--workspace</c> would collide with Mission Control's unrelated
    /// <c>--workspace</c> concept and its own honesty history — see <c>strategy/AgentEval-Status-and-Plan-Forward.md</c>
    /// Front D) — so this verb's own name states plainly what it does, without borrowing an already-loaded word.
    /// </summary>
    internal static async Task<int> ExecuteWorkspaceAsync(
        DirectoryInfo path, string format, FileInfo? output, bool failOnNoncompliant,
        bool writeBaseline = false, string baselineRoot = DefaultWorkspaceBaselineRoot, bool checkBaseline = false,
        FileInfo? saveManifestBaseline = null, FileInfo? manifestBaseline = null, string? baselineNotes = null,
        CancellationToken ct = default)
    {
        if (!path.Exists)
        {
            throw new DirectoryNotFoundException($"Workspace directory not found: {path.FullName}");
        }

        AIAgent noOpAgent = new ChatClientAgent(new ScriptedChatClient(), new ChatClientAgentOptions { Name = "SkillsScanCommand" });

        var (report, entries, workspaceSummary) = await ScanWorkspaceAsync(path.FullName, noOpAgent, ct).ConfigureAwait(false);
        Console.Error.WriteLine(workspaceSummary);

        // Cross-location drift is always checked, same as --repo — a multi-repo workspace scan is exactly
        // the scenario "skill X is byte-identical across 45 repos except 3 outliers" describes; cross-REPO
        // drift IS cross-location drift, just at one more directory level (see ScanWorkspaceAsync's remarks
        // for how entries are re-tagged to make this fall out of the EXISTING DetectCrossLocationDrift as-is).
        return await FinishScanAsync(
            report, entries, checkCrossLocationDrift: true, format, output, failOnNoncompliant,
            writeBaseline, baselineRoot, checkBaseline, saveManifestBaseline, manifestBaseline, baselineNotes,
            path.FullName, ct).ConfigureAwait(false);
    }

    // Bounded so a workspace of many repos doesn't open unbounded concurrent file handles; matches the same
    // "cap it, don't leave it unbounded" discipline GateCalibrationHarness's MaxConcurrency already uses
    // elsewhere in this codebase. Each repo's scan is independent I/O (its own directory tree, its own
    // content hashing) — safe to run concurrently.
    private const int MaxConcurrentRepoScans = 4;

    /// <summary>
    /// Wave 3a (filesystem multi-repo scan): treats <paramref name="workspaceRoot"/> as a folder of
    /// already-cloned repos — every immediate, non-hidden subdirectory is one repo — and runs the EXISTING
    /// <see cref="ScanRepoWideAsync"/> (the <c>--repo</c> pipeline, unchanged) against each one, combining
    /// every repo's findings/entries into one report. Zero new discovery/compliance code: this is a fan-out
    /// loop over an already-shipped pipeline, not a reimplementation. Entries are re-tagged with a
    /// <c>"{repoFolderName}/{conventionRelativePath}"</c> <see cref="SkillBaselineEntry.RelativePath"/> so the
    /// UNCHANGED <see cref="DetectCrossLocationDrift"/> — which only ever groups by
    /// <see cref="SkillBaselineEntry.Name"/>, never by <c>RelativePath</c> — also catches drift ACROSS repos
    /// for free, exactly as it already does across conventions within one repo.
    /// <para>Deliberately filesystem-only: no GitHub/GitLab API client, no token, no network call. The
    /// operator's own clone step (their own credentials, their own tooling) is what decides which repos are
    /// visible here — that access-control question is intentionally kept OUTSIDE this verb's trust boundary,
    /// unlike the live, API-driven org-wide scan a security/credential-scope review is still pending for.</para>
    /// <para><b>Fault-isolated per repo.</b> One repo whose skill folder contains a permission-denied file
    /// (MafSkillScanner's own file enumeration has no guard against this — a pre-existing gap in shared
    /// scanning code, not something this method can safely patch without touching every other caller of that
    /// pipeline) must not lose every OTHER repo's already-computed results — a real risk at workspace scale
    /// that a single-repo <c>--repo</c> scan rarely hits. Caught, reported as a per-repo "SKIPPED" line plus a
    /// stderr warning, and the scan continues with the remaining repos.</para>
    /// <para><b>Known scoping limit, disclosed not hidden:</b> a skill name shared by two UNRELATED repos
    /// (different teams, coincidentally the same name) is indistinguishable from real drift/poisoning to
    /// <see cref="DetectCrossLocationDrift"/> — it groups by name only, with no concept of "these repos are
    /// unrelated." At org scale this is more likely to fire than within one repo's own conventions; the
    /// finding's <c>Medium</c> severity and "verify this is intentional" wording already account for that
    /// ambiguity, so this is not a bug, but expect more such findings to review as workspace size grows.</para>
    /// </summary>
    private static async Task<(SkillComplianceReport Report, IReadOnlyList<SkillBaselineEntry> Entries, string WorkspaceSummary)> ScanWorkspaceAsync(
        string workspaceRoot, AIAgent agent, CancellationToken ct)
    {
        var allSubdirs = Directory.GetDirectories(workspaceRoot);
        var repoDirs = allSubdirs
            .Where(d => !Path.GetFileName(d).StartsWith('.'))   // skip .git and similar tooling folders, not repo clones
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hiddenSkipped = allSubdirs.Length - repoDirs.Count;

        var summary = new StringBuilder();
        summary.AppendLine("  Workspace repos scanned:");

        if (allSubdirs.Length == 0)
        {
            summary.AppendLine("    (no subdirectories found)");
        }
        else if (repoDirs.Count == 0)
        {
            summary.AppendLine($"    (no non-hidden subdirectories — {hiddenSkipped} hidden folder(s) skipped)");
        }

        // Collect-then-reduce: each task writes only to its own array slot (no shared mutable state during
        // the parallel phase), and the reduce step below walks repoDirs' own sorted order — so the combined
        // report/summary is deterministic regardless of which repo's scan actually finished first.
        var results = new (string RepoName, SkillComplianceReport Report, IReadOnlyList<SkillBaselineEntry> Entries, string? Error)[repoDirs.Count];
        using var throttle = new SemaphoreSlim(Math.Min(MaxConcurrentRepoScans, Math.Max(1, repoDirs.Count)));

        var scanTasks = repoDirs.Select(async (repoPath, i) =>
        {
            var repoName = Path.GetFileName(repoPath);
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var (repoReport, repoEntries, _) = await ScanRepoWideAsync(repoPath, agent, ct).ConfigureAwait(false);
                results[i] = (repoName, repoReport, repoEntries, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results[i] = (repoName, EmptyReport, [], ex.Message);
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(scanTasks).ConfigureAwait(false);

        var allFindings = new List<SkillComplianceFinding>();
        int skillCount = 0, withResources = 0, withScripts = 0, silentlyExcluded = 0;
        var histogram = new Dictionary<string, int>(StringComparer.Ordinal);
        var entries = new List<SkillBaselineEntry>();

        foreach (var (repoName, repoReport, repoEntries, error) in results)
        {
            if (error is not null)
            {
                summary.AppendLine($"    {repoName}: SKIPPED — could not scan ({error})");
                Console.Error.WriteLine($"  Warning: could not scan repo '{repoName}': {error}");
                continue;
            }

            summary.AppendLine($"    {repoName}: {repoReport.Coverage.SkillCount} skill(s), {repoReport.Findings.Count} finding(s)");

            allFindings.AddRange(repoReport.Findings);
            AccumulateCoverage(repoReport.Coverage, ref skillCount, ref withResources, ref withScripts, ref silentlyExcluded, histogram);
            entries.AddRange(repoEntries.Select(e => e with { RelativePath = $"{repoName}/{e.RelativePath}" }));
        }

        if (hiddenSkipped > 0)
        {
            summary.AppendLine($"    ({hiddenSkipped} hidden folder(s) skipped, e.g. .git)");
        }

        var combinedReport = new SkillComplianceReport(
            allFindings, new SkillCoverageSummary(skillCount, withResources, withScripts, histogram, silentlyExcluded));

        return (combinedReport, entries, summary.ToString().TrimEnd());
    }

    private static readonly SkillComplianceReport EmptyReport =
        new([], new SkillCoverageSummary(0, 0, 0, new Dictionary<string, int>(StringComparer.Ordinal)));

    /// <summary>
    /// Folds one repo/convention's <see cref="SkillCoverageSummary"/> into the caller's running totals — the
    /// ONE place this accumulation is defined, shared by <see cref="ScanRepoWideAsync"/> (across conventions)
    /// and <see cref="ScanWorkspaceAsync"/> (across repos), so a future new <see cref="SkillCoverageSummary"/>
    /// field only needs threading through here once, not into two independently-hand-rolled loops.
    /// </summary>
    private static void AccumulateCoverage(
        SkillCoverageSummary from, ref int skillCount, ref int withResources, ref int withScripts,
        ref int silentlyExcluded, Dictionary<string, int> histogram)
    {
        skillCount += from.SkillCount;
        withResources += from.WithResources;
        withScripts += from.WithScripts;
        silentlyExcluded += from.SilentlyExcludedCount;
        foreach (var (stage, count) in from.StageHistogram)
        {
            histogram[stage] = histogram.GetValueOrDefault(stage) + count;
        }
    }

    // ═══════════════════════════════ --repo multi-convention discovery (§4.3) ═══════════════════════════════

    /// <summary>
    /// Loops <see cref="AgentSkillDirectoryConventions.KnownConventions"/>, running the EXISTING
    /// <see cref="MafSkillScanner.ScanFileSkillsWithInfoAsync"/> pipeline per hit — a discovery-layer loop
    /// calling the existing pipeline N times, no changes to <see cref="SkillComplianceValidator"/>/
    /// <see cref="MafSkillScanner"/>/<see cref="RawSkillDirectoryScanner"/>. Findings/coverage are merged
    /// into ONE combined <see cref="SkillComplianceReport"/> (so every <c>--format</c> renders it exactly
    /// like a single-root scan); the per-convention breakdown (which conventions existed, which didn't) is
    /// returned separately and printed to stderr — keeping stdout's shape (console/markdown/JSON) identical
    /// across <c>--repo</c> and non-<c>--repo</c> scans, rather than inventing a second JSON schema.
    /// Entries (content hash + structural fingerprint per skill, tagged with which convention it came from
    /// via <see cref="SkillBaselineEntry.RelativePath"/>) are now always built (Wave 2) — the caller decides
    /// whether to persist them as a baseline snapshot and/or run cross-location drift detection over them.
    /// </summary>
    private static async Task<(SkillComplianceReport Report, IReadOnlyList<SkillBaselineEntry> Entries, string ConventionSummary)> ScanRepoWideAsync(
        string repoRoot, AIAgent agent, CancellationToken ct)
    {
        var allFindings = new List<SkillComplianceFinding>();
        int skillCount = 0, withResources = 0, withScripts = 0, silentlyExcluded = 0;
        var histogram = new Dictionary<string, int>(StringComparer.Ordinal);
        var entries = new List<SkillBaselineEntry>();
        var summary = new StringBuilder();
        summary.AppendLine("  Repo-wide skill directory conventions:");

        foreach (var convention in AgentSkillDirectoryConventions.KnownConventions)
        {
            var conventionPath = Path.Combine(repoRoot, convention);
            if (!Directory.Exists(conventionPath))
            {
                summary.AppendLine($"    {convention}: not present");
                continue;
            }

            var result = await MafSkillScanner.ScanFileSkillsWithInfoAsync(conventionPath, agent, options: null, ct).ConfigureAwait(false);
            summary.AppendLine($"    {convention}: {result.Skills.Count} skill(s), {result.Report.Findings.Count} finding(s)");

            allFindings.AddRange(result.Report.Findings);
            AccumulateCoverage(result.Report.Coverage, ref skillCount, ref withResources, ref withScripts, ref silentlyExcluded, histogram);

            foreach (var info in result.Skills)
            {
                entries.Add(ToBaselineEntry(info, result.Report.Findings, convention));
            }
        }

        var combinedReport = new SkillComplianceReport(
            allFindings, new SkillCoverageSummary(skillCount, withResources, withScripts, histogram, silentlyExcluded));

        return (combinedReport, entries, summary.ToString().TrimEnd());
    }

    // ═══════════════════════════════ cross-location drift + trust-on-first-use (Wave 2) ═══════════════════════════════

    /// <summary>
    /// Groups <paramref name="entries"/> by skill <see cref="SkillBaselineEntry.Name"/> (case-insensitive —
    /// GA requires lowercase-only names, so a name differing only by case across two conventions is the
    /// SAME poisoning/drift scenario this rule targets, not two unrelated skills) and flags any name present
    /// with 2+ DISTINCT <see cref="SkillBaselineEntry.ContentHash"/> values — the same skill name resolves
    /// to different content depending on which convention/agent-tool (or, since Wave 3a, which REPO) reads
    /// it. Only meaningful when <paramref name="entries"/> spans 2+ locations — a single-directory scan has
    /// exactly one location by definition, so it never fires there. Location granularity is entirely a
    /// function of how the caller populated <see cref="SkillBaselineEntry.RelativePath"/>: <c>--repo</c>
    /// tags by convention, <c>scan-workspace</c> additionally tags by repo folder — this method itself never
    /// inspects <c>RelativePath</c> for grouping, only for the finding's human-readable location list, so it
    /// needs no change to work at either granularity. A name shared by two genuinely UNRELATED
    /// repos/conventions is indistinguishable from real drift to this method (no "these are unrelated"
    /// signal exists) — the <c>Medium</c> severity and "verify this is intentional" wording below already
    /// account for that ambiguity, more likely to matter at workspace scale than within one repo. Entries
    /// with an empty/missing name are excluded from grouping entirely — two independently-malformed skills
    /// that both lack a <c>name:</c> field are NOT "the same skill drifting," and grouping them by their
    /// shared empty name would produce a misleading finding.
    /// </summary>
    internal static IReadOnlyList<SkillComplianceFinding> DetectCrossLocationDrift(IReadOnlyList<SkillBaselineEntry> entries)
    {
        var findings = new List<SkillComplianceFinding>();

        foreach (var group in entries
            .Where(e => e.ContentHash is not null && !string.IsNullOrEmpty(e.Name))
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var distinctHashes = group.Select(e => e.ContentHash!).Distinct(StringComparer.Ordinal).ToList();
            if (distinctHashes.Count <= 1)
            {
                continue; // same content everywhere it was found — no drift
            }

            var locations = string.Join(", ", group.Select(e => $"{e.RelativePath} ({e.ContentHash![..Math.Min(8, e.ContentHash.Length)]}…)"));
            findings.Add(new SkillComplianceFinding(
                group.Key,
                SkillComplianceRule.CrossLocationContentDrift,
                Severity.Medium,
                $"Skill '{group.Key}' has DIFFERENT content across {group.Count()} location(s): {locations} — " +
                "the same skill name resolves to different content depending on which agent tool reads it. " +
                "Verify this is intentional per-tool customization, not drift or poisoning.",
                Field: null));
        }

        return findings;
    }

    /// <summary>
    /// Trust-on-first-use: for each of <paramref name="entries"/> with a non-null
    /// <see cref="SkillBaselineEntry.ContentHash"/>, checks whether that exact (name, hash) pair already
    /// appears somewhere in <paramref name="history"/> — i.e. this exact copy was captured and vetted in a
    /// prior <c>--write-baseline</c> run. Reports the MOST RECENT matching snapshot's capture date (not the
    /// first ever seen) so the finding answers "when did I last confirm this," not "when did this first appear."
    /// </summary>
    internal static IReadOnlyList<SkillComplianceFinding> DetectPreviouslyVetted(
        IReadOnlyList<SkillBaselineEntry> entries, IReadOnlyList<SkillBaselineSnapshot> history)
    {
        // States the invariant directly (Max per key) instead of depending on an implicit
        // "OrderByDescending + TryAdd keeps the first, which is the newest" ordering trick — a future edit
        // reordering these calls can't silently start reporting the OLDEST match instead of the newest.
        var mostRecentMatchByNameAndHash = history
            .SelectMany(s => s.Skills.Where(e => e.ContentHash is not null).Select(e => (Key: (e.Name, e.ContentHash!), s.CapturedAt)))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Max(x => x.CapturedAt));

        var findings = new List<SkillComplianceFinding>();
        foreach (var entry in entries)
        {
            if (entry.ContentHash is null)
            {
                continue;
            }

            if (mostRecentMatchByNameAndHash.TryGetValue((entry.Name, entry.ContentHash), out var matchedAt))
            {
                findings.Add(new SkillComplianceFinding(
                    entry.Name,
                    SkillComplianceRule.MatchesPreviouslyVettedCopy,
                    Severity.Low,
                    $"✓ Skill '{entry.Name}' is byte-identical to a previously-captured baseline snapshot " +
                    $"(most recent match: {matchedAt:yyyy-MM-dd}) — no content drift since it was last vetted.",
                    Field: null));
            }
        }

        return findings;
    }

    // ═══════════════════════════════ manifest-baseline hash-pin drift gate (Phase 4b CLI surface) ═══════════════════════════════

    /// <summary>
    /// <c>--manifest-baseline &lt;file&gt;</c>: compares every scanned skill's current
    /// <see cref="SkillManifestPoisoningGate.Fingerprint"/> (already computed once, into
    /// <see cref="SkillBaselineEntry.StructuralFingerprint"/> — reused here verbatim, no re-hashing) against
    /// a previously trust-time-pinned <see cref="SkillManifestBaseline"/>. Only a <see cref="ManifestDriftKind.Changed"/>
    /// skill becomes a finding — the actual rug-pull signal; <see cref="ManifestDriftKind.New"/> (not yet
    /// pinned) and <see cref="ManifestDriftKind.Removed"/> (no longer present) are housekeeping, not
    /// poisoning, and are deliberately not surfaced here.
    /// </summary>
    internal static IReadOnlyList<SkillComplianceFinding> DetectManifestDrift(
        IReadOnlyList<SkillBaselineEntry> entries, SkillManifestBaseline baseline)
        => DetectManifestDrift(ManifestFingerprintsByName(entries), baseline);

    /// <summary>
    /// Overload accepting an already-computed fingerprint map — lets <see cref="FinishScanAsync"/> compute
    /// it at most ONCE and share it with the <c>--save-manifest-baseline</c> save block, instead of hashing
    /// every entry twice when both flags are passed in the same run.
    /// </summary>
    internal static IReadOnlyList<SkillComplianceFinding> DetectManifestDrift(
        IReadOnlyDictionary<string, string> currentHashesByName, SkillManifestBaseline baseline)
    {
        var drift = ManifestDriftDetector.Detect(baseline.HashesBySkillName, currentHashesByName);

        var findings = new List<SkillComplianceFinding>();
        foreach (var d in drift.Where(d => d.Kind == ManifestDriftKind.Changed))
        {
            // ManifestDriftDetector.Detect only checks dictionary KEY presence via TryGetValue, not that the
            // VALUE is non-null — a hand-edited/corrupted --manifest-baseline JSON file can deserialize a
            // null hash for a present key (System.Text.Json's default options don't enforce NRT
            // non-nullability at runtime), so "Changed" does NOT actually guarantee non-null hashes. Defend
            // against that instead of trusting it and null-forgiving into a NullReferenceException.
            var hashSummary = (d.BaselineHash, d.CurrentHash) switch
            {
                (string before, string after) =>
                    $" (fingerprint {before[..Math.Min(8, before.Length)]}… -> {after[..Math.Min(8, after.Length)]}…)",
                _ => " (the baseline file has a missing or malformed hash for this skill — re-capture it with --save-manifest-baseline)",
            };

            findings.Add(new SkillComplianceFinding(
                d.Key,
                SkillComplianceRule.ManifestChangedSinceBaseline,
                Severity.High,
                $"Skill '{d.Key}' manifest content changed since the trust-time baseline was captured{hashSummary} — " +
                "verify this change was reviewed and re-approved before trusting it again.",
                Field: null));
        }

        return findings;
    }

    // Wave 2: a --repo/scan-workspace entries list can legitimately contain 2+ entries sharing the same
    // Name (the exact cross-location-drift scenario) — GroupBy+First (not ToDictionary directly), same
    // tolerance DiffAsync's own baselineBySkill/currentBySkill already use, rather than crashing.
    // Keys are lowercased, not just grouped with StringComparer.Ordinal — ManifestDriftDetector.Detect's own
    // key union (ManifestFingerprint.cs) is hardcoded to StringComparer.Ordinal and can't be told to ignore
    // case, so relying on THIS dictionary's comparer alone wouldn't actually make the comparison
    // case-insensitive. Lowercasing the key here (both when capturing --save-manifest-baseline and when
    // checking --manifest-baseline, since both paths call this same method) means the SAME skill differing
    // only by name casing produces the SAME dictionary key either way, closing what would otherwise be a
    // real detection bypass: DetectCrossLocationDrift already treats a case-only name difference as GA
    // requires lowercase-only names, so it's the SAME skill, not two unrelated ones — a rug-pull that also
    // re-cases the name must not be able to evade this gate by looking like an unrelated New+Removed pair.
    private static Dictionary<string, string> ManifestFingerprintsByName(IReadOnlyList<SkillBaselineEntry> entries) =>
        entries
            .GroupBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().StructuralFingerprint, StringComparer.Ordinal);

    // ═══════════════════════════════ baseline entry building (§4.1) ═══════════════════════════════

    private static SkillBaselineEntry ToBaselineEntry(
        ScannedSkillInfo info, IReadOnlyList<SkillComplianceFinding> allFindings, string? conventionPrefix)
    {
        var manifest = info.Manifest;
        var structuralFingerprint = SkillManifestPoisoningGate.Fingerprint(manifest);
        string? contentHash = null;
        if (!string.IsNullOrWhiteSpace(info.AbsolutePath) && Directory.Exists(info.AbsolutePath))
        {
            try
            {
                contentHash = SkillContentHasher.HashSkillFolder(info.AbsolutePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A locked/permission-denied file inside ONE skill's folder must not abort the whole scan
                // for a reason unrelated to skill compliance (this call is now reachable from every --repo
                // scan, not just --write-baseline — see this method's own callers). ContentHash stays null
                // (same value a non-file-sourced skill already legitimately gets) — Wave 2's drift/TOFU
                // detection already skip null-ContentHash entries, so this skill is simply excluded from
                // those two checks rather than crashing the run. Surfaced to the operator, not swallowed.
                Console.Error.WriteLine($"  Warning: could not compute content hash for skill '{manifest.Name}' ({info.AbsolutePath}): {ex.Message}");
            }
        }

        var dirName = manifest.ParentDirectoryName ?? manifest.Name;
        var relativePath = string.IsNullOrEmpty(conventionPrefix) ? dirName : $"{conventionPrefix}/{dirName}";
        var skillFindings = allFindings
            .Where(f => string.Equals(f.SkillName, manifest.Name, StringComparison.Ordinal))
            .ToList();

        return new SkillBaselineEntry(
            manifest.Name, manifest.SourceKind, relativePath, structuralFingerprint, contentHash,
            Source: null, SourceUrl: null, Ref: null, skillFindings);
    }

    // ═══════════════════════════════ skills baseline list|diff|history (§4.1) ═══════════════════════════════

    private static Command BuildBaselineCommand()
    {
        var baselineCmd = new Command("baseline", "Inspect the skill-scan baseline ledger (see 'skills scan --write-baseline')");

        var listCmd = new Command("list", "List every captured baseline snapshot");
        var listRootOpt = new Option<string>("--baseline-root") { DefaultValueFactory = _ => DefaultBaselineRoot, Description = "Baseline ledger root directory." };
        listCmd.Options.Add(listRootOpt);
        listCmd.SetAction(async (parseResult, ct) =>
        {
            try
            {
                return await ListAsync(parseResult.GetValue(listRootOpt)!, ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Error: {ex.Message}");
                return ExitCodes.RuntimeError;
            }
        });

        var diffCmd = new Command("diff", "Diff two baseline snapshots (default: the two most recent)");
        var diffRootOpt = new Option<string>("--baseline-root") { DefaultValueFactory = _ => DefaultBaselineRoot, Description = "Baseline ledger root directory." };
        var sinceOpt = new Option<string?>("--since") { Description = "Baseline snapshot Id to diff against the most recent snapshot (default: the two most recent snapshots)." };
        var skillOpt = new Option<string?>("--skill") { Description = "Only show the diff for this skill name." };
        var hashOpt = new Option<string>("--hash") { DefaultValueFactory = _ => "content", Description = "Which hash to diff: structural | content (default: content, the stronger signal)." };
        diffCmd.Options.Add(diffRootOpt);
        diffCmd.Options.Add(sinceOpt);
        diffCmd.Options.Add(skillOpt);
        diffCmd.Options.Add(hashOpt);
        diffCmd.SetAction(async (parseResult, ct) =>
        {
            try
            {
                return await DiffAsync(
                    parseResult.GetValue(diffRootOpt)!,
                    parseResult.GetValue(sinceOpt),
                    parseResult.GetValue(skillOpt),
                    parseResult.GetValue(hashOpt)!,
                    ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Error: {ex.Message}");
                return ExitCodes.RuntimeError;
            }
        });

        var historyCmd = new Command("history", "Show when a skill's content last changed across the baseline ledger");
        var skillNameArg = new Argument<string>("skill-name") { Description = "The skill to show content-hash history for." };
        var historyRootOpt = new Option<string>("--baseline-root") { DefaultValueFactory = _ => DefaultBaselineRoot, Description = "Baseline ledger root directory." };
        historyCmd.Arguments.Add(skillNameArg);
        historyCmd.Options.Add(historyRootOpt);
        historyCmd.SetAction(async (parseResult, ct) =>
        {
            try
            {
                return await HistoryAsync(parseResult.GetValue(historyRootOpt)!, parseResult.GetValue(skillNameArg)!, ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Error: {ex.Message}");
                return ExitCodes.RuntimeError;
            }
        });

        baselineCmd.Subcommands.Add(listCmd);
        baselineCmd.Subcommands.Add(diffCmd);
        baselineCmd.Subcommands.Add(historyCmd);
        return baselineCmd;
    }

    internal static async Task<int> ListAsync(string baselineRoot, CancellationToken ct)
    {
        var store = new JsonFileSkillBaselineStore(baselineRoot);
        var snapshots = await store.ListAsync(ct).ConfigureAwait(false);
        if (snapshots.Count == 0)
        {
            Console.WriteLine("No baseline snapshots captured yet. Run 'agenteval skills scan <path> --write-baseline' first.");
            return ExitCodes.Success;
        }

        Console.WriteLine($"{"Id",-33} {"CapturedAt (UTC)",-20} {"ScannedRoot",-38} {"Skills",6}");
        foreach (var s in snapshots)
        {
            Console.WriteLine($"{s.Id,-33} {s.CapturedAt:yyyy-MM-dd HH:mm:ss} {Truncate(s.ScannedRoot, 38),-38} {s.Skills.Count,6}");
        }

        return ExitCodes.Success;
    }

    internal static async Task<int> DiffAsync(string baselineRoot, string? sinceId, string? skillFilter, string hashKind, CancellationToken ct)
    {
        if (!string.Equals(hashKind, "structural", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(hashKind, "content", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unknown --hash '{hashKind}'. Valid: structural, content.");
        }

        var store = new JsonFileSkillBaselineStore(baselineRoot);
        var snapshots = await store.ListAsync(ct).ConfigureAwait(false);

        SkillBaselineSnapshot baseline;
        SkillBaselineSnapshot current;
        if (sinceId is not null)
        {
            // Note: no separate "snapshots.Count == 0" check needed here — FirstOrDefault over an empty
            // list already returns null, which the check above already handles.
            var found = snapshots.FirstOrDefault(s => string.Equals(s.Id, sinceId, StringComparison.Ordinal));
            if (found is null)
            {
                Console.Error.WriteLine($"  Error: no snapshot with Id '{sinceId}' found in {baselineRoot}.");
                return ExitCodes.RuntimeError;
            }

            baseline = found;
            current = snapshots[^1];
            if (string.Equals(baseline.Id, current.Id, StringComparison.Ordinal))
            {
                Console.WriteLine("The --since snapshot IS the most recent snapshot — nothing to diff.");
                return ExitCodes.Success;
            }
        }
        else
        {
            if (snapshots.Count < 2)
            {
                Console.WriteLine($"Need at least 2 baseline snapshots to diff (found {snapshots.Count}). Run 'agenteval skills scan <path> --write-baseline' again after a change, or pass --since <id>.");
                return ExitCodes.Success;
            }

            baseline = snapshots[^2];
            current = snapshots[^1];
        }

        var useContent = string.Equals(hashKind, "content", StringComparison.OrdinalIgnoreCase);
        var baselineHashes = HashesByName(baseline, useContent);
        var currentHashes = HashesByName(current, useContent);
        var findings = ManifestDriftDetector.Detect(baselineHashes, currentHashes);

        if (skillFilter is not null)
        {
            findings = findings.Where(f => string.Equals(f.Key, skillFilter, StringComparison.Ordinal)).ToList();
            if (findings.Count == 0)
            {
                Console.WriteLine($"'{skillFilter}' is not present (with a {hashKind} hash) in either snapshot ({baseline.Id} / {current.Id}).");
                return ExitCodes.Success;
            }
        }

        Console.WriteLine($"Diffing {baseline.Id} ({baseline.CapturedAt:yyyy-MM-dd HH:mm:ss} UTC) -> {current.Id} ({current.CapturedAt:yyyy-MM-dd HH:mm:ss} UTC), hash={hashKind}");
        Console.WriteLine();

        // Wave 2: a --repo snapshot can legitimately contain 2+ entries with the SAME Name (the exact
        // cross-location scenario CrossLocationContentDrift exists to surface) — plain ToDictionary(e =>
        // e.Name) would throw ArgumentException ("same key already added") the first time that happens.
        // GroupBy + First deterministically keeps the first-encountered location (KnownConventions'
        // iteration order) instead of crashing; a true per-location diff is a real, larger feature this
        // fix deliberately does not attempt — see DiffAsync/HistoryAsync's own remarks.
        var baselineBySkill = baseline.Skills.GroupBy(e => e.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var currentBySkill = current.Skills.GroupBy(e => e.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var f in findings.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            switch (f.Kind)
            {
                case ManifestDriftKind.Unchanged:
                    Console.WriteLine($"     [unchanged] {f.Key}");
                    break;
                case ManifestDriftKind.New:
                    Console.WriteLine($"     [new]       {f.Key}");
                    break;
                case ManifestDriftKind.Removed:
                    Console.WriteLine($"     [removed]   {f.Key}");
                    break;
                case ManifestDriftKind.Changed:
                    // "Cry wolf on every cosmetic edit" guard (§1.2): distinguish a content change that also
                    // introduced a NEW High compliance finding from one that didn't — the former is the
                    // actually-alarming case.
                    var beforeHigh = baselineBySkill.TryGetValue(f.Key, out var beforeEntry)
                        ? beforeEntry.ComplianceFindings.Count(cf => cf.Severity == Severity.High) : 0;
                    var afterHigh = currentBySkill.TryGetValue(f.Key, out var afterEntry)
                        ? afterEntry.ComplianceFindings.Count(cf => cf.Severity == Severity.High) : 0;
                    if (afterHigh > beforeHigh)
                    {
                        Console.WriteLine($"  !! [CHANGED + NEW HIGH FINDING] {f.Key} ({beforeHigh} -> {afterHigh} High finding(s))");
                    }
                    else
                    {
                        Console.WriteLine($"     [changed, no new High finding] {f.Key}");
                    }

                    break;
            }
        }

        return ExitCodes.Success;
    }

    // Wave 2 known limitation: a --repo snapshot can contain 2+ entries sharing the same Name (the
    // cross-location scenario CrossLocationContentDrift surfaces). HistoryAsync's FirstOrDefault below
    // picks an arbitrary one of them per snapshot (stable within one run, since KnownConventions' order
    // is fixed, but not a real per-location history) rather than crashing — a true per-location history
    // view is a real, larger feature this fix deliberately does not attempt.
    internal static async Task<int> HistoryAsync(string baselineRoot, string skillName, CancellationToken ct)
    {
        var store = new JsonFileSkillBaselineStore(baselineRoot);
        var snapshots = await store.ListAsync(ct).ConfigureAwait(false);   // oldest -> newest
        if (snapshots.Count == 0)
        {
            Console.WriteLine("No baseline snapshots captured yet.");
            return ExitCodes.Success;
        }

        Console.WriteLine($"Content-hash history for '{skillName}':");

        string? previousHash = null;
        var changePoints = 0;
        var everSeen = false;
        foreach (var snapshot in snapshots)
        {
            var entry = snapshot.Skills.FirstOrDefault(s => string.Equals(s.Name, skillName, StringComparison.Ordinal));
            if (entry is null)
            {
                continue;   // skill absent from this particular snapshot — not itself a tracked "change"
            }

            everSeen = true;
            if (previousHash is not null && !string.Equals(previousHash, entry.ContentHash, StringComparison.Ordinal))
            {
                changePoints++;
                var newHighFindings = entry.ComplianceFindings.Count(f => f.Severity == Severity.High);
                var suffix = newHighFindings > 0 ? $" — {newHighFindings} High finding(s) present at this point" : string.Empty;
                Console.WriteLine($"  {snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss} UTC — content changed (snapshot {snapshot.Id}){suffix}");
            }

            previousHash = entry.ContentHash;
        }

        if (!everSeen)
        {
            Console.WriteLine($"  '{skillName}' does not appear in any captured snapshot.");
        }
        else if (changePoints == 0)
        {
            Console.WriteLine($"  No content changes detected across {snapshots.Count} snapshot(s) that include it.");
        }

        return ExitCodes.Success;
    }

    // Non-file-sourced skills (InMemory/Class/Mcp/Custom) have no folder to content-hash — ContentHash is
    // null for them (see SkillBaselineEntry's remarks). With --hash content (the default), such a skill is
    // silently OMITTED from the diff entirely rather than reported as New/Removed/Changed/Unchanged with a
    // fabricated hash — pass --hash structural to diff those skills too (their StructuralFingerprint always exists).
    // Wave 2: GroupBy + First (not ToDictionary directly) — see baselineBySkill/currentBySkill's own
    // comment above for why a --repo snapshot can contain duplicate Names and why ToDictionary would crash.
    private static Dictionary<string, string> HashesByName(SkillBaselineSnapshot snapshot, bool useContent) =>
        snapshot.Skills
            .Where(e => !useContent || e.ContentHash is not null)
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => useContent ? g.First().ContentHash! : g.First().StructuralFingerprint, StringComparer.Ordinal);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
}
