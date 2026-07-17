// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// <c>agenteval skills scan</c> — closes the "reachable from the CLI" gap for the Phase 2 compliance
/// scanner (<c>strategy/FutureFeatures/Skills/Skills-Scan-CLI-Verb-Design.md</c>). Credential-free by
/// construction: the command's no-op agent is never invoked (see <see cref="SkillsScanCommand"/>'s own
/// remarks for the empirically-confirmed reason <c>--fail-on-noncompliant</c> is tested against a
/// hand-built report here rather than a real malformed fixture).
/// </summary>
public class SkillsScanCommandTests
{
    // ── command wiring ──

    [Fact]
    public void Create_ReturnsSkillsCommand_WithScanSubcommand()
    {
        var command = SkillsScanCommand.Create();
        Assert.Equal("skills", command.Name);
        Assert.Contains(command.Subcommands, c => c.Name == "scan");
    }

    [Fact]
    public void Create_ScanSubcommand_HasExpectedOptions()
    {
        var scanCmd = SkillsScanCommand.Create().Subcommands.Single(c => c.Name == "scan");
        Assert.Contains(scanCmd.Arguments, a => a.Name == "path");
        Assert.Contains(scanCmd.Options, o => o.Name == "--format");
        Assert.Contains(scanCmd.Options, o => o.Name == "--output" || o.Aliases.Contains("--output"));
        Assert.Contains(scanCmd.Options, o => o.Name == "--fail-on-noncompliant");
    }

    // ── ExecuteAsync: real, offline, credential-free scan ──

    [Fact]
    public async Task ExecuteAsync_NonexistentDirectory_Throws()
    {
        var missing = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "agenteval-skills-scan-missing-" + Guid.NewGuid().ToString("N")));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => SkillsScanCommand.ExecuteAsync(missing, "console", null, false, ct: default));
    }

    [Fact]
    public async Task ExecuteAsync_UnknownFormat_Throws()
    {
        var dir = CreateCompliantFixture(out _);
        try
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => SkillsScanCommand.ExecuteAsync(dir, "yaml", null, false, ct: default));
            Assert.Contains("--format", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("console")]
    [InlineData("markdown")]
    [InlineData("md")]
    [InlineData("json")]
    public async Task ExecuteAsync_RealFixture_EachFormat_WritesNonEmptyOutputAndReturnsSuccess(string format)
    {
        var dir = CreateCompliantFixture(out var outputCapture);
        try
        {
            var outFile = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-skills-scan-out-" + Guid.NewGuid().ToString("N") + ".txt"));
            try
            {
                var exit = await SkillsScanCommand.ExecuteAsync(dir, format, outFile, failOnNoncompliant: false, ct: default);

                Assert.Equal(ExitCodes.Success, exit);
                Assert.True(outFile.Exists, "the rendered report should have been written to --output");
                var content = await File.ReadAllTextAsync(outFile.FullName);
                Assert.False(string.IsNullOrWhiteSpace(content), $"the {format} report should be non-empty");
            }
            finally { if (outFile.Exists) outFile.Delete(); }
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_CompliantFixture_FailOnNoncompliant_StillReturnsSuccess()
    {
        // The real expense-report-shaped fixture built here carries only a Medium ScriptRequiresGovernanceReview
        // finding (has a script) — Medium never flips IsCompliant, so --fail-on-noncompliant must NOT trip.
        var dir = CreateCompliantFixture(out _, withScript: true);
        try
        {
            var exit = await SkillsScanCommand.ExecuteAsync(dir, "console", null, failOnNoncompliant: true, ct: default);
            Assert.Equal(ExitCodes.Success, exit);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_NoOutput_WritesToStdOut_NeverThrows()
    {
        var dir = CreateCompliantFixture(out _);
        try
        {
            var exit = await SkillsScanCommand.ExecuteAsync(dir, "console", output: null, failOnNoncompliant: false, ct: default);
            Assert.Equal(ExitCodes.Success, exit);
        }
        finally { dir.Delete(recursive: true); }
    }

    // ── real, on-disk reachability of --fail-on-noncompliant via a genuinely malformed fixture (Item 5) ──
    // The MAF-silent-exclusion gap documented in SkillsScanCommand.ExecuteAsync's XML remarks used to make
    // every High-severity NAME rule unreachable via a real on-disk scan (see git history for the prior
    // "hand-built report only" version of this test group). MafSkillScanner.ScanFileSkillsAsync now detects
    // and reports the exclusion itself (SkillExcludedFromDiscovery), so this is exercised end-to-end here.

    [Fact]
    public async Task ExecuteAsync_MalformedFixture_FailOnNoncompliant_ReturnsTestFailure()
    {
        var dir = CreateMalformedFixture();
        try
        {
            var exit = await SkillsScanCommand.ExecuteAsync(dir, "console", null, failOnNoncompliant: true, ct: default);
            Assert.Equal(ExitCodes.TestFailure, exit);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_MalformedFixture_ConsoleOutput_MentionsExcludedFromDiscovery()
    {
        var dir = CreateMalformedFixture();
        try
        {
            var outFile = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-skills-scan-malformed-out-" + Guid.NewGuid().ToString("N") + ".txt"));
            try
            {
                await SkillsScanCommand.ExecuteAsync(dir, "console", outFile, failOnNoncompliant: false, ct: default);
                var content = await File.ReadAllTextAsync(outFile.FullName);
                Assert.Contains("SkillExcludedFromDiscovery", content, StringComparison.Ordinal);
                Assert.Contains("SILENTLY EXCLUDED", content, StringComparison.Ordinal);
            }
            finally { if (outFile.Exists) outFile.Delete(); }
        }
        finally { dir.Delete(recursive: true); }
    }

    // ── ComputeExitCode: the --fail-on-noncompliant gate, tested directly against a hand-built report ──
    // (kept as a fast, isolated contract test alongside the real-fixture coverage above).

    [Fact]
    public void ComputeExitCode_HighFinding_FailOnTrue_ReturnsTestFailure()
    {
        var report = ReportWith(Severity.High);
        Assert.Equal(ExitCodes.TestFailure, SkillsScanCommand.ComputeExitCode(report, failOnNoncompliant: true));
    }

    [Fact]
    public void ComputeExitCode_HighFinding_FailOnFalse_ReturnsSuccess()
    {
        // Default off (informational-only) — a High finding must NOT fail the run unless explicitly asked.
        var report = ReportWith(Severity.High);
        Assert.Equal(ExitCodes.Success, SkillsScanCommand.ComputeExitCode(report, failOnNoncompliant: false));
    }

    [Fact]
    public void ComputeExitCode_MediumOnly_FailOnTrue_ReturnsSuccess()
    {
        var report = ReportWith(Severity.Medium);
        Assert.Equal(ExitCodes.Success, SkillsScanCommand.ComputeExitCode(report, failOnNoncompliant: true));
    }

    [Fact]
    public void ComputeExitCode_NoFindings_FailOnTrue_ReturnsSuccess()
    {
        var report = new SkillComplianceReport([], new SkillCoverageSummary(0, 0, 0, new Dictionary<string, int>()));
        Assert.Equal(ExitCodes.Success, SkillsScanCommand.ComputeExitCode(report, failOnNoncompliant: true));
    }

    // ── helpers ──

    private static SkillComplianceReport ReportWith(Severity severity)
    {
        var finding = new SkillComplianceFinding("test-skill", SkillComplianceRule.NameTooLong, severity, "test finding", "name");
        return new SkillComplianceReport([finding], new SkillCoverageSummary(1, 0, 0, new Dictionary<string, int>()));
    }

    /// <summary>
    /// A real, on-disk, MAF-discoverable skill fixture — name matches its directory (the one constraint MAF's
    /// own discovery enforces, per the honesty finding above), so it is genuinely reachable via the real
    /// <c>ScanFileSkillsAsync</c> path, not a synthetic report.
    /// </summary>
    private static DirectoryInfo CreateCompliantFixture(out string skillName, bool withScript = true)
    {
        skillName = "compliant-skill-" + Guid.NewGuid().ToString("N")[..8];
        var root = Path.Combine(Path.GetTempPath(), "agenteval-skills-scan-fixture-" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, skillName);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            $"---\nname: {skillName}\ndescription: A compliant fixture skill for SkillsScanCommandTests.\n---\n\nBody.\n");

        if (withScript)
        {
            Directory.CreateDirectory(Path.Combine(skillDir, "scripts"));
            File.WriteAllText(Path.Combine(skillDir, "scripts", "run.csx"), "// test script");
        }

        return new DirectoryInfo(root);
    }

    /// <summary>
    /// A real, on-disk skill folder MAF's own discovery will silently exclude (consecutive hyphens in
    /// <c>name:</c>) — the exact fixture shape that used to produce "0 skills scanned" with no findings
    /// before Item 5's reconciliation pass, now used to prove <c>--fail-on-noncompliant</c> is genuinely
    /// reachable end-to-end rather than only via <see cref="ReportWith"/>'s hand-built report.
    /// </summary>
    private static DirectoryInfo CreateMalformedFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "agenteval-skills-scan-malformed-" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, "bad--hyphen");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: bad--hyphen\ndescription: A malformed fixture skill for SkillsScanCommandTests.\n---\n\nBody.\n");

        return new DirectoryInfo(root);
    }
}
