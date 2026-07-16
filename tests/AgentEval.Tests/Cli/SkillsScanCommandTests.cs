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
            () => SkillsScanCommand.ExecuteAsync(missing, "console", null, false, default));
    }

    [Fact]
    public async Task ExecuteAsync_UnknownFormat_Throws()
    {
        var dir = CreateCompliantFixture(out _);
        try
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => SkillsScanCommand.ExecuteAsync(dir, "yaml", null, false, default));
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
                var exit = await SkillsScanCommand.ExecuteAsync(dir, format, outFile, failOnNoncompliant: false, default);

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
            var exit = await SkillsScanCommand.ExecuteAsync(dir, "console", null, failOnNoncompliant: true, default);
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
            var exit = await SkillsScanCommand.ExecuteAsync(dir, "console", output: null, failOnNoncompliant: false, default);
            Assert.Equal(ExitCodes.Success, exit);
        }
        finally { dir.Delete(recursive: true); }
    }

    // ── ComputeExitCode: the --fail-on-noncompliant gate, tested directly against a hand-built report ──
    //
    // See SkillsScanCommand.ExecuteAsync's XML remarks: MAF's OWN AgentFileSkillsSource discovery silently
    // excludes a directory from the scan whenever its SKILL.md name: field fails the GA name-format rules —
    // confirmed empirically against 3 separately hand-crafted malformed fixtures (invalid chars, consecutive
    // hyphens, name/directory mismatch), each producing "0 skills scanned" rather than a High finding. Every
    // High-severity NAME rule in SkillComplianceValidator is therefore unreachable via a REAL on-disk scan —
    // so the exit-code CONTRACT is tested here against a manually-built report instead of fighting MAF's own
    // validation to manufacture one.

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
}
