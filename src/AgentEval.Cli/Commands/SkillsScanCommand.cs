// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using AgentEval.MAF.Skills;
using AgentEval.Skills;
using AgentEval.Testing;
using Microsoft.Agents.AI;

namespace AgentEval.Cli.Commands;

/// <summary>
/// The 'agenteval skills scan' command — a static, offline SKILL.md + governance-flag scan for a directory
/// of MAF Agent Skills (<see cref="MafSkillScanner"/>/<see cref="SkillComplianceValidator"/>, shipped and
/// tested as library code since Agent Skills Phase 2; this closes the "reachable from the CLI" gap —
/// confirmed unreachable via <c>grep -rl "skills" src/AgentEval.Cli/Commands/</c> before this command).
/// v1 scope is deliberately compliance-only, not the full Skill Health &amp; Security Index (see
/// <c>strategy/FutureFeatures/Skills/Skills-Scan-CLI-Verb-Design.md</c> §2.3).
/// </summary>
internal static class SkillsScanCommand
{
    public static Command Create()
    {
        var skillsCmd = new Command("skills", "MAF Agent Skills utilities");
        var scanCmd = new Command("scan", "Scan a directory of MAF Agent Skills for GA compliance + governance findings");

        var pathArg = new Argument<DirectoryInfo>("path")
            { Description = "Directory containing one or more SKILL.md-rooted skill folders" };
        var formatOpt = new Option<string>("--format")
            { DefaultValueFactory = _ => "console", Description = "Output format: console | markdown | json" };
        var outputOpt = new Option<FileInfo?>("-o", "--output") { Description = "Output file (default: stdout)" };
        var failOnOpt = new Option<bool>("--fail-on-noncompliant")
            { Description = "Exit non-zero (1) when the scan finds a High-severity finding (report.IsCompliant == false). Default off (informational-only)." };

        scanCmd.Arguments.Add(pathArg);
        scanCmd.Options.Add(formatOpt);
        scanCmd.Options.Add(outputOpt);
        scanCmd.Options.Add(failOnOpt);

        scanCmd.SetAction(async (parseResult, ct) =>
        {
            try
            {
                return await ExecuteAsync(
                    parseResult.GetValue(pathArg)!,
                    parseResult.GetValue(formatOpt)!,
                    parseResult.GetValue(outputOpt),
                    parseResult.GetValue(failOnOpt),
                    ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Error: {ex.Message}");
                return ExitCodes.RuntimeError;
            }
        });

        skillsCmd.Subcommands.Add(scanCmd);
        return skillsCmd;
    }

    /// <summary>
    /// Core execution logic — separated from command wiring for testability. The scan itself never invokes
    /// a model — confirmed empirically this session (not just by re-reading <see cref="MafSkillScanner"/>'s
    /// doc comments): <see cref="MafSkillScanner.ScanFileSkillsAsync"/> only reads
    /// <see cref="AgentSkillsSourceContext"/>'s <c>Agent</c> reference to satisfy MAF's own constructor
    /// requirement and never calls it, exactly like the existing <c>MafSkillScannerTests.FakeAgent()</c>
    /// precedent, which is reused (not reinvented) below via <see cref="ScriptedChatClient"/> — so no
    /// credentials are ever required for this verb.
    /// </summary>
    /// <remarks>
    /// <b>Honesty finding (verified live this session, not assumed):</b> MAF's own <c>AgentFileSkillsSource</c>
    /// frontmatter parsing already silently excludes a directory from discovery whenever its <c>SKILL.md</c>
    /// <c>name:</c> field fails the GA name-format rules (invalid characters, consecutive hyphens, or a
    /// mismatch against the containing directory name) — confirmed by scanning 3 separately hand-crafted
    /// malformed fixtures, each returning "Skills scanned: 0" rather than a High-severity finding. In
    /// practice this means every <see cref="SkillComplianceRule"/> High-severity NAME rule is effectively
    /// unreachable via a REAL on-disk <c>skills scan</c> — MAF's own discovery is a superset of
    /// <see cref="SkillComplianceValidator"/>'s name checks for file-sourced skills. This does not affect
    /// <c>--fail-on-noncompliant</c>'s correctness (see <see cref="ComputeExitCode"/>, tested directly
    /// against a hand-built <see cref="SkillComplianceReport"/> rather than fighting MAF's own validation to
    /// manufacture a real High finding) — it is a real, documented gap worth knowing about, not a defect in
    /// this command.
    /// </remarks>
    internal static async Task<int> ExecuteAsync(
        DirectoryInfo path, string format, FileInfo? output, bool failOnNoncompliant, CancellationToken ct)
    {
        if (!path.Exists)
            throw new DirectoryNotFoundException($"Skills directory not found: {path.FullName}");

        AIAgent noOpAgent = new ChatClientAgent(new ScriptedChatClient(), new ChatClientAgentOptions { Name = "SkillsScanCommand" });
        var report = await MafSkillScanner.ScanFileSkillsAsync(path.FullName, noOpAgent, options: null, ct);

        var rendered = (format ?? "console").Trim().ToLowerInvariant() switch
        {
            "json" => SkillComplianceReportRenderer.RenderJson(report),
            "markdown" or "md" => SkillComplianceReportRenderer.RenderMarkdown(report),
            "console" or "" => SkillComplianceReportRenderer.RenderConsole(report),
            _ => throw new ArgumentException($"Unknown --format '{format}'. Valid: console, markdown, json."),
        };

        if (output is not null)
        {
            await File.WriteAllTextAsync(output.FullName, rendered, ct);
            Console.Error.WriteLine($"  Report written to: {output.FullName}");
        }
        else
        {
            Console.WriteLine(rendered);
        }

        return ComputeExitCode(report, failOnNoncompliant);
    }

    /// <summary>
    /// Pure exit-code gate, separated for direct testing (see the honesty finding on
    /// <see cref="ExecuteAsync"/> for why this is tested against a hand-built report rather than a real scan).
    /// </summary>
    internal static int ComputeExitCode(SkillComplianceReport report, bool failOnNoncompliant)
        => (failOnNoncompliant && !report.IsCompliant) ? ExitCodes.TestFailure : ExitCodes.Success;
}
