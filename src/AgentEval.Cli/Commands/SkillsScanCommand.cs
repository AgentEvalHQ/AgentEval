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

    public static Command Create()
    {
        var skillsCmd = new Command("skills", "MAF Agent Skills utilities");
        skillsCmd.Subcommands.Add(BuildScanCommand());
        skillsCmd.Subcommands.Add(BuildBaselineCommand());
        return skillsCmd;
    }

    // ═══════════════════════════════════════════ scan ═══════════════════════════════════════════

    private static Command BuildScanCommand()
    {
        var scanCmd = new Command("scan", "Scan a directory of MAF Agent Skills for GA compliance + governance findings");

        var pathArg = new Argument<DirectoryInfo>("path")
            { Description = "Directory containing one or more SKILL.md-rooted skill folders (a repo root when --repo is set)" };
        var formatOpt = new Option<string>("--format")
            { DefaultValueFactory = _ => "console", Description = "Output format: console | markdown | json" };
        var outputOpt = new Option<FileInfo?>("-o", "--output") { Description = "Output file (default: stdout)" };
        var failOnOpt = new Option<bool>("--fail-on-noncompliant")
            { Description = "Exit non-zero (1) when the scan finds a High-severity finding (report.IsCompliant == false). Default off (informational-only)." };
        var writeBaselineOpt = new Option<bool>("--write-baseline")
            { Description = "After scanning, capture a timestamped baseline snapshot (content hash + structural fingerprint per skill) into the baseline ledger. See 'skills baseline list|diff|history'." };
        var repoOpt = new Option<bool>("--repo")
            { Description = "Treat <path> as a REPO ROOT: scan every known skill-directory convention found under it (.claude/skills, .agents/skills, ...) and aggregate the results, instead of treating <path> itself as one skill directory." };
        var baselineRootOpt = new Option<string>("--baseline-root")
            { DefaultValueFactory = _ => DefaultBaselineRoot, Description = "Baseline ledger root directory (used with --write-baseline)." };

        scanCmd.Arguments.Add(pathArg);
        scanCmd.Options.Add(formatOpt);
        scanCmd.Options.Add(outputOpt);
        scanCmd.Options.Add(failOnOpt);
        scanCmd.Options.Add(writeBaselineOpt);
        scanCmd.Options.Add(repoOpt);
        scanCmd.Options.Add(baselineRootOpt);

        scanCmd.SetAction(async (parseResult, ct) =>
        {
            try
            {
                return await ExecuteAsync(
                    parseResult.GetValue(pathArg)!,
                    parseResult.GetValue(formatOpt)!,
                    parseResult.GetValue(outputOpt),
                    parseResult.GetValue(failOnOpt),
                    parseResult.GetValue(writeBaselineOpt),
                    parseResult.GetValue(repoOpt),
                    parseResult.GetValue(baselineRootOpt)!,
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
        bool writeBaseline = false, bool repo = false, string baselineRoot = DefaultBaselineRoot, CancellationToken ct = default)
    {
        if (!path.Exists)
        {
            throw new DirectoryNotFoundException($"Skills directory not found: {path.FullName}");
        }

        AIAgent noOpAgent = new ChatClientAgent(new ScriptedChatClient(), new ChatClientAgentOptions { Name = "SkillsScanCommand" });

        SkillComplianceReport report;
        SkillBaselineSnapshot? snapshot;

        if (repo)
        {
            var (combinedReport, combinedSnapshot, conventionSummary) =
                await ScanRepoWideAsync(path.FullName, noOpAgent, writeBaseline, ct).ConfigureAwait(false);
            report = combinedReport;
            snapshot = combinedSnapshot;
            Console.Error.WriteLine(conventionSummary);
        }
        else
        {
            var result = await MafSkillScanner.ScanFileSkillsWithInfoAsync(path.FullName, noOpAgent, options: null, ct).ConfigureAwait(false);
            report = result.Report;
            snapshot = writeBaseline ? BuildSnapshot(path.FullName, result.Skills, report.Findings) : null;
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

        if (snapshot is not null)
        {
            var store = new JsonFileSkillBaselineStore(baselineRoot);
            var id = await store.SaveAsync(snapshot, ct).ConfigureAwait(false);
            Console.Error.WriteLine($"  Baseline snapshot saved: {id} ({snapshot.Skills.Count} skill(s) -> {baselineRoot})");
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
    /// </summary>
    private static async Task<(SkillComplianceReport Report, SkillBaselineSnapshot? Snapshot, string ConventionSummary)> ScanRepoWideAsync(
        string repoRoot, AIAgent agent, bool writeBaseline, CancellationToken ct)
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
            skillCount += result.Report.Coverage.SkillCount;
            withResources += result.Report.Coverage.WithResources;
            withScripts += result.Report.Coverage.WithScripts;
            silentlyExcluded += result.Report.Coverage.SilentlyExcludedCount;
            foreach (var (stage, count) in result.Report.Coverage.StageHistogram)
            {
                histogram[stage] = histogram.GetValueOrDefault(stage) + count;
            }

            if (writeBaseline)
            {
                foreach (var info in result.Skills)
                {
                    entries.Add(ToBaselineEntry(info, result.Report.Findings, convention));
                }
            }
        }

        var combinedReport = new SkillComplianceReport(
            allFindings, new SkillCoverageSummary(skillCount, withResources, withScripts, histogram, silentlyExcluded));
        var snapshot = writeBaseline
            ? new SkillBaselineSnapshot(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, repoRoot, entries)
            : null;

        return (combinedReport, snapshot, summary.ToString().TrimEnd());
    }

    // ═══════════════════════════════ baseline entry building (§4.1) ═══════════════════════════════

    private static SkillBaselineSnapshot BuildSnapshot(
        string scannedRoot, IReadOnlyList<ScannedSkillInfo> skills, IReadOnlyList<SkillComplianceFinding> findings)
    {
        var entries = skills.Select(info => ToBaselineEntry(info, findings, conventionPrefix: null)).ToList();
        return new SkillBaselineSnapshot(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, scannedRoot, entries);
    }

    private static SkillBaselineEntry ToBaselineEntry(
        ScannedSkillInfo info, IReadOnlyList<SkillComplianceFinding> allFindings, string? conventionPrefix)
    {
        var manifest = info.Manifest;
        var structuralFingerprint = SkillManifestPoisoningGate.Fingerprint(manifest);
        string? contentHash = null;
        if (!string.IsNullOrWhiteSpace(info.AbsolutePath) && Directory.Exists(info.AbsolutePath))
        {
            contentHash = SkillContentHasher.HashSkillFolder(info.AbsolutePath);
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

        var baselineBySkill = baseline.Skills.ToDictionary(e => e.Name, StringComparer.Ordinal);
        var currentBySkill = current.Skills.ToDictionary(e => e.Name, StringComparer.Ordinal);

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
    private static Dictionary<string, string> HashesByName(SkillBaselineSnapshot snapshot, bool useContent) =>
        snapshot.Skills
            .Where(e => !useContent || e.ContentHash is not null)
            .ToDictionary(e => e.Name, e => useContent ? e.ContentHash! : e.StructuralFingerprint, StringComparer.Ordinal);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
}
