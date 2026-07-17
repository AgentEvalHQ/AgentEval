// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Agent Skills Wave 1 (§1.2/§4.1/§4.3) — <c>skills scan --write-baseline</c> / <c>--repo</c>, and the
/// <c>skills baseline list|diff|history</c> subcommand group. All real, on-disk, credential-free (same
/// no-op-agent discipline as <see cref="SkillsScanCommandTests"/>).
/// </summary>
public class SkillsBaselineCommandTests : IDisposable
{
    private readonly string _baselineRoot;

    public SkillsBaselineCommandTests()
    {
        _baselineRoot = Path.Combine(Path.GetTempPath(), "agenteval-skills-baseline-cmd-test-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_baselineRoot, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── command wiring ──

    [Fact]
    public void Create_ReturnsSkillsCommand_WithBaselineSubcommand()
    {
        var command = SkillsScanCommand.Create();
        Assert.Contains(command.Subcommands, c => c.Name == "baseline");
    }

    [Fact]
    public void Create_BaselineSubcommand_HasListDiffHistory()
    {
        var baselineCmd = SkillsScanCommand.Create().Subcommands.Single(c => c.Name == "baseline");
        Assert.Contains(baselineCmd.Subcommands, c => c.Name == "list");
        Assert.Contains(baselineCmd.Subcommands, c => c.Name == "diff");
        Assert.Contains(baselineCmd.Subcommands, c => c.Name == "history");
    }

    [Fact]
    public void Create_ScanSubcommand_HasWriteBaselineAndRepoOptions()
    {
        var scanCmd = SkillsScanCommand.Create().Subcommands.Single(c => c.Name == "scan");
        Assert.Contains(scanCmd.Options, o => o.Name == "--write-baseline");
        Assert.Contains(scanCmd.Options, o => o.Name == "--repo");
        Assert.Contains(scanCmd.Options, o => o.Name == "--baseline-root");
    }

    [Fact]
    public void Create_ScanSubcommand_HasCheckBaselineOption()
    {
        // Wave 2 (§4.2)
        var scanCmd = SkillsScanCommand.Create().Subcommands.Single(c => c.Name == "scan");
        Assert.Contains(scanCmd.Options, o => o.Name == "--check-baseline");
    }

    // ── --write-baseline: scan a real fixture, confirm a snapshot lands in the ledger ──

    [Fact]
    public async Task ExecuteAsync_WriteBaseline_SavesSnapshotWithContentAndStructuralHashes()
    {
        var dir = CreateCompliantFixture(out var skillName);
        try
        {
            var exit = await SkillsScanCommand.ExecuteAsync(
                dir, "console", output: null, failOnNoncompliant: false,
                writeBaseline: true, repo: false, baselineRoot: _baselineRoot, ct: default);

            Assert.Equal(ExitCodes.Success, exit);

            var store = new JsonFileSkillBaselineStore(_baselineRoot);
            var snapshots = await store.ListAsync();
            var snapshot = Assert.Single(snapshots);
            Assert.Equal(dir.FullName, snapshot.ScannedRoot);

            var entry = Assert.Single(snapshot.Skills, s => s.Name == skillName);
            Assert.False(string.IsNullOrEmpty(entry.StructuralFingerprint));
            Assert.NotNull(entry.ContentHash);
            Assert.Equal(SkillSourceKind.File, entry.SourceKind);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_NoWriteBaseline_NoSnapshotSaved()
    {
        var dir = CreateCompliantFixture(out _);
        try
        {
            await SkillsScanCommand.ExecuteAsync(
                dir, "console", output: null, failOnNoncompliant: false,
                writeBaseline: false, repo: false, baselineRoot: _baselineRoot, ct: default);

            Assert.False(Directory.Exists(_baselineRoot), "no baseline directory should be created when --write-baseline is not passed");
        }
        finally { dir.Delete(recursive: true); }
    }

    // ── skills baseline list ──

    [Fact]
    public async Task ListAsync_NoSnapshots_ReturnsSuccessWithMessage()
    {
        var exit = await SkillsScanCommand.ListAsync(_baselineRoot, default);
        Assert.Equal(ExitCodes.Success, exit);
    }

    [Fact]
    public async Task ListAsync_AfterTwoScans_ListsBoth()
    {
        var dir1 = CreateCompliantFixture(out _);
        var dir2 = CreateCompliantFixture(out _);
        try
        {
            await SkillsScanCommand.ExecuteAsync(dir1, "console", null, false, writeBaseline: true, repo: false, baselineRoot: _baselineRoot, ct: default);
            await SkillsScanCommand.ExecuteAsync(dir2, "console", null, false, writeBaseline: true, repo: false, baselineRoot: _baselineRoot, ct: default);

            var store = new JsonFileSkillBaselineStore(_baselineRoot);
            var snapshots = await store.ListAsync();
            Assert.Equal(2, snapshots.Count);

            var exit = await SkillsScanCommand.ListAsync(_baselineRoot, default);
            Assert.Equal(ExitCodes.Success, exit);
        }
        finally { dir1.Delete(recursive: true); dir2.Delete(recursive: true); }
    }

    // ── skills baseline diff ──

    [Fact]
    public async Task DiffAsync_FewerThanTwoSnapshots_ReturnsSuccessWithMessage_NotError()
    {
        var dir = CreateCompliantFixture(out _);
        try
        {
            await SkillsScanCommand.ExecuteAsync(dir, "console", null, false, writeBaseline: true, repo: false, baselineRoot: _baselineRoot, ct: default);
            var exit = await SkillsScanCommand.DiffAsync(_baselineRoot, sinceId: null, skillFilter: null, hashKind: "content", default);
            Assert.Equal(ExitCodes.Success, exit);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task DiffAsync_UnknownHashKind_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => SkillsScanCommand.DiffAsync(_baselineRoot, null, null, "bogus", default));
    }

    [Fact]
    public async Task DiffAsync_ContentChanged_DetectedAsChanged()
    {
        var dir = CreateCompliantFixture(out var skillName);
        try
        {
            await SkillsScanCommand.ExecuteAsync(dir, "console", null, false, writeBaseline: true, repo: false, baselineRoot: _baselineRoot, ct: default);

            // Mutate the skill's resource content between the two scans.
            var skillDir = Path.Combine(dir.FullName, skillName);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
                $"---\nname: {skillName}\ndescription: A compliant fixture skill for SkillsScanCommandTests. CHANGED.\n---\n\nBody changed.\n");

            await SkillsScanCommand.ExecuteAsync(dir, "console", null, false, writeBaseline: true, repo: false, baselineRoot: _baselineRoot, ct: default);

            var store = new JsonFileSkillBaselineStore(_baselineRoot);
            var snapshots = await store.ListAsync();
            Assert.Equal(2, snapshots.Count);

            var exit = await SkillsScanCommand.DiffAsync(_baselineRoot, sinceId: null, skillFilter: skillName, hashKind: "content", default);
            Assert.Equal(ExitCodes.Success, exit);
            // (Console output assertion intentionally omitted here — DiffAsync writes to Console.Out, which
            // isn't captured by this test; the exit-code + no-throw path plus the dedicated ManifestDriftDetector
            // unit coverage in PromptTemplateDriftGateTests/McpToolDescriptionPoisoningGateTests already prove
            // the underlying Changed-detection logic. See SkillContentHasherTests for the hash-sensitivity proof.)
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task DiffAsync_UnchangedContent_NoChangedFinding()
    {
        var dir = CreateCompliantFixture(out _);
        try
        {
            await SkillsScanCommand.ExecuteAsync(dir, "console", null, false, writeBaseline: true, repo: false, baselineRoot: _baselineRoot, ct: default);
            await SkillsScanCommand.ExecuteAsync(dir, "console", null, false, writeBaseline: true, repo: false, baselineRoot: _baselineRoot, ct: default);

            var store = new JsonFileSkillBaselineStore(_baselineRoot);
            var snapshots = await store.ListAsync();
            var oldest = snapshots[0];
            var newest = snapshots[1];

            var oldHash = oldest.Skills.Single().ContentHash;
            var newHash = newest.Skills.Single().ContentHash;
            Assert.Equal(oldHash, newHash);   // unchanged fixture -> unchanged hash
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task DiffAsync_SinceUnknownId_ReturnsRuntimeError()
    {
        var dir = CreateCompliantFixture(out _);
        try
        {
            await SkillsScanCommand.ExecuteAsync(dir, "console", null, false, writeBaseline: true, repo: false, baselineRoot: _baselineRoot, ct: default);
            var exit = await SkillsScanCommand.DiffAsync(_baselineRoot, sinceId: "does-not-exist", skillFilter: null, hashKind: "content", default);
            Assert.Equal(ExitCodes.RuntimeError, exit);
        }
        finally { dir.Delete(recursive: true); }
    }

    // ── skills baseline history ──

    [Fact]
    public async Task HistoryAsync_NeverSeen_ReturnsSuccess()
    {
        var exit = await SkillsScanCommand.HistoryAsync(_baselineRoot, "never-existed-skill", default);
        Assert.Equal(ExitCodes.Success, exit);
    }

    [Fact]
    public async Task HistoryAsync_ContentChangedOnce_ReturnsSuccess_AfterTwoScans()
    {
        var dir = CreateCompliantFixture(out var skillName);
        try
        {
            await SkillsScanCommand.ExecuteAsync(dir, "console", null, false, writeBaseline: true, repo: false, baselineRoot: _baselineRoot, ct: default);

            var skillDir = Path.Combine(dir.FullName, skillName);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
                $"---\nname: {skillName}\ndescription: Changed description for history test.\n---\n\nBody.\n");

            await SkillsScanCommand.ExecuteAsync(dir, "console", null, false, writeBaseline: true, repo: false, baselineRoot: _baselineRoot, ct: default);

            var exit = await SkillsScanCommand.HistoryAsync(_baselineRoot, skillName, default);
            Assert.Equal(ExitCodes.Success, exit);
        }
        finally { dir.Delete(recursive: true); }
    }

    // ── --repo: multi-convention discovery ──

    [Fact]
    public async Task ExecuteAsync_Repo_ScansKnownConventions_AggregatesAcrossThem()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "agenteval-skills-repo-test-" + Guid.NewGuid().ToString("N"));
        var claudeSkillsDir = Path.Combine(repoRoot, ".claude", "skills", "skill-a");
        var agentsSkillsDir = Path.Combine(repoRoot, ".agents", "skills", "skill-b");
        Directory.CreateDirectory(claudeSkillsDir);
        Directory.CreateDirectory(agentsSkillsDir);
        File.WriteAllText(Path.Combine(claudeSkillsDir, "SKILL.md"), "---\nname: skill-a\ndescription: Present under .claude/skills.\n---\n\nBody.\n");
        File.WriteAllText(Path.Combine(agentsSkillsDir, "SKILL.md"), "---\nname: skill-b\ndescription: Present under .agents/skills.\n---\n\nBody.\n");
        // .windsurf/skills etc. deliberately absent — proves the "not present" branch is exercised too.

        try
        {
            var outFile = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-skills-repo-out-" + Guid.NewGuid().ToString("N") + ".txt"));
            try
            {
                var exit = await SkillsScanCommand.ExecuteAsync(
                    new DirectoryInfo(repoRoot), "json", outFile, failOnNoncompliant: false,
                    writeBaseline: true, repo: true, baselineRoot: _baselineRoot, ct: default);

                Assert.Equal(ExitCodes.Success, exit);
                var content = await File.ReadAllTextAsync(outFile.FullName);
                // Both fixture skills are fully compliant (no findings expected) — the JSON report's
                // Findings array is legitimately empty; SkillCount in the merged Coverage is the real
                // signal that BOTH conventions' skills were folded into one combined report.
                Assert.Contains("\"SkillCount\": 2", content, StringComparison.Ordinal);

                var store = new JsonFileSkillBaselineStore(_baselineRoot);
                var snapshots = await store.ListAsync();
                var snapshot = Assert.Single(snapshots);
                Assert.Equal(2, snapshot.Skills.Count);
                Assert.Contains(snapshot.Skills, s => s.RelativePath.Contains(".claude/skills", StringComparison.Ordinal));
                Assert.Contains(snapshot.Skills, s => s.RelativePath.Contains(".agents/skills", StringComparison.Ordinal));
            }
            finally { if (outFile.Exists) outFile.Delete(); }
        }
        finally { Directory.Delete(repoRoot, recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_Repo_NoConventionsPresent_ZeroSkillsFound_StillSucceeds()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "agenteval-skills-repo-empty-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoRoot);
        try
        {
            var exit = await SkillsScanCommand.ExecuteAsync(
                new DirectoryInfo(repoRoot), "console", null, failOnNoncompliant: false,
                writeBaseline: false, repo: true, baselineRoot: _baselineRoot, ct: default);

            Assert.Equal(ExitCodes.Success, exit);
        }
        finally { Directory.Delete(repoRoot, recursive: true); }
    }

    // ── Wave 2 (§4.2): --repo cross-location content drift ──

    [Fact]
    public async Task ExecuteAsync_Repo_SameSkillName_DifferentContentAcrossConventions_FlagsCrossLocationDrift()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "agenteval-skills-drift-test-" + Guid.NewGuid().ToString("N"));
        var claudeDir = Path.Combine(repoRoot, ".claude", "skills", "shared-skill");
        var cursorDir = Path.Combine(repoRoot, ".cursor", "skills", "shared-skill");
        Directory.CreateDirectory(claudeDir);
        Directory.CreateDirectory(cursorDir);
        // Same skill NAME, deliberately different body content -> different content hash.
        File.WriteAllText(Path.Combine(claudeDir, "SKILL.md"),
            "---\nname: shared-skill\ndescription: Present under .claude/skills.\n---\n\nBody variant A.\n");
        File.WriteAllText(Path.Combine(cursorDir, "SKILL.md"),
            "---\nname: shared-skill\ndescription: Present under .claude/skills.\n---\n\nBody variant B — DIFFERENT.\n");

        try
        {
            var outFile = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-skills-drift-out-" + Guid.NewGuid().ToString("N") + ".json"));
            try
            {
                var exit = await SkillsScanCommand.ExecuteAsync(
                    new DirectoryInfo(repoRoot), "json", outFile, failOnNoncompliant: false,
                    writeBaseline: false, repo: true, baselineRoot: _baselineRoot, ct: default);

                // Medium severity, not High -> IsCompliant stays true, exit stays Success (informational-only default).
                // Note: SkillComplianceReportRenderer's JSON has no JsonStringEnumConverter, so Rule serializes
                // as its raw int value, not the enum name — assert on the human-readable Message text instead.
                Assert.Equal(ExitCodes.Success, exit);
                var content = await File.ReadAllTextAsync(outFile.FullName);
                Assert.Contains("DIFFERENT content across", content, StringComparison.Ordinal);
                Assert.Contains("shared-skill", content, StringComparison.Ordinal);
            }
            finally { if (outFile.Exists) outFile.Delete(); }
        }
        finally { Directory.Delete(repoRoot, recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_Repo_SameSkillName_SameContentAcrossConventions_NoDriftFinding()
    {
        // Regression guard: identical content in two locations must NOT be flagged as drift.
        var repoRoot = Path.Combine(Path.GetTempPath(), "agenteval-skills-nodrift-test-" + Guid.NewGuid().ToString("N"));
        var claudeDir = Path.Combine(repoRoot, ".claude", "skills", "identical-skill");
        var cursorDir = Path.Combine(repoRoot, ".cursor", "skills", "identical-skill");
        Directory.CreateDirectory(claudeDir);
        Directory.CreateDirectory(cursorDir);
        const string identicalBody = "---\nname: identical-skill\ndescription: Byte-identical in both locations.\n---\n\nSame body.\n";
        File.WriteAllText(Path.Combine(claudeDir, "SKILL.md"), identicalBody);
        File.WriteAllText(Path.Combine(cursorDir, "SKILL.md"), identicalBody);

        try
        {
            var outFile = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-skills-nodrift-out-" + Guid.NewGuid().ToString("N") + ".json"));
            try
            {
                await SkillsScanCommand.ExecuteAsync(
                    new DirectoryInfo(repoRoot), "json", outFile, failOnNoncompliant: false,
                    writeBaseline: false, repo: true, baselineRoot: _baselineRoot, ct: default);

                var content = await File.ReadAllTextAsync(outFile.FullName);
                Assert.DoesNotContain("DIFFERENT content across", content, StringComparison.Ordinal);
            }
            finally { if (outFile.Exists) outFile.Delete(); }
        }
        finally { Directory.Delete(repoRoot, recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_RepoWriteBaseline_DuplicateSkillNameAcrossConventions_ThenDiffAndHistory_DoNotThrow()
    {
        // Regression: a --repo --write-baseline snapshot can legitimately contain 2+ entries with the SAME
        // Name (the exact cross-location-drift scenario) — `baseline diff`/`baseline history` must not crash
        // with an unhandled ArgumentException ("same key already added") when that happens.
        var repoRoot = Path.Combine(Path.GetTempPath(), "agenteval-skills-dupname-test-" + Guid.NewGuid().ToString("N"));
        var claudeDir = Path.Combine(repoRoot, ".claude", "skills", "shared-skill");
        var cursorDir = Path.Combine(repoRoot, ".cursor", "skills", "shared-skill");
        Directory.CreateDirectory(claudeDir);
        Directory.CreateDirectory(cursorDir);
        File.WriteAllText(Path.Combine(claudeDir, "SKILL.md"),
            "---\nname: shared-skill\ndescription: Present under .claude/skills.\n---\n\nBody variant A.\n");
        File.WriteAllText(Path.Combine(cursorDir, "SKILL.md"),
            "---\nname: shared-skill\ndescription: Present under .claude/skills.\n---\n\nBody variant B — DIFFERENT.\n");

        try
        {
            // Two --write-baseline runs so `diff`/`history` have real snapshot history to walk, each one
            // containing the duplicate-Name pair that would previously crash HashesByName/baselineBySkill.
            await SkillsScanCommand.ExecuteAsync(
                new DirectoryInfo(repoRoot), "console", null, failOnNoncompliant: false,
                writeBaseline: true, repo: true, baselineRoot: _baselineRoot, ct: default);
            await SkillsScanCommand.ExecuteAsync(
                new DirectoryInfo(repoRoot), "console", null, failOnNoncompliant: false,
                writeBaseline: true, repo: true, baselineRoot: _baselineRoot, ct: default);

            var diffExit = await SkillsScanCommand.DiffAsync(_baselineRoot, sinceId: null, skillFilter: null, hashKind: "content", default);
            Assert.Equal(ExitCodes.Success, diffExit);

            var historyExit = await SkillsScanCommand.HistoryAsync(_baselineRoot, "shared-skill", default);
            Assert.Equal(ExitCodes.Success, historyExit);
        }
        finally { Directory.Delete(repoRoot, recursive: true); }
    }

    // ── Wave 2 (§4.2): --check-baseline trust-on-first-use ──

    [Fact]
    public async Task ExecuteAsync_CheckBaseline_UnchangedSinceLastSnapshot_FlagsMatchesPreviouslyVettedCopy()
    {
        var dir = CreateCompliantFixture(out var skillName);
        try
        {
            // First run: capture a baseline snapshot.
            await SkillsScanCommand.ExecuteAsync(
                dir, "console", output: null, failOnNoncompliant: false,
                writeBaseline: true, repo: false, baselineRoot: _baselineRoot, ct: default);

            // Second run: unchanged fixture, --check-baseline (no --write-baseline this time).
            var outFile = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-skills-tofu-out-" + Guid.NewGuid().ToString("N") + ".json"));
            try
            {
                var exit = await SkillsScanCommand.ExecuteAsync(
                    dir, "json", outFile, failOnNoncompliant: false,
                    writeBaseline: false, repo: false, baselineRoot: _baselineRoot, checkBaseline: true, ct: default);

                Assert.Equal(ExitCodes.Success, exit);
                var content = await File.ReadAllTextAsync(outFile.FullName);
                Assert.Contains("byte-identical to a previously-captured baseline snapshot", content, StringComparison.Ordinal);
                Assert.Contains(skillName, content, StringComparison.Ordinal);
            }
            finally { if (outFile.Exists) outFile.Delete(); }
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_CheckBaseline_NoPriorHistory_DoesNotThrow_NoFindingEmitted()
    {
        // First-ever scan: --check-baseline is meaningless (nothing to compare against) but must not crash.
        var dir = CreateCompliantFixture(out _);
        try
        {
            var outFile = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-skills-tofu-empty-out-" + Guid.NewGuid().ToString("N") + ".json"));
            try
            {
                var exit = await SkillsScanCommand.ExecuteAsync(
                    dir, "json", outFile, failOnNoncompliant: false,
                    writeBaseline: false, repo: false, baselineRoot: _baselineRoot, checkBaseline: true, ct: default);

                Assert.Equal(ExitCodes.Success, exit);
                var content = await File.ReadAllTextAsync(outFile.FullName);
                Assert.DoesNotContain("byte-identical to a previously-captured baseline snapshot", content, StringComparison.Ordinal);
            }
            finally { if (outFile.Exists) outFile.Delete(); }
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_CheckBaseline_ContentChangedSincePriorSnapshot_NoMatchFinding()
    {
        var dir = CreateCompliantFixture(out var skillName);
        try
        {
            await SkillsScanCommand.ExecuteAsync(
                dir, "console", output: null, failOnNoncompliant: false,
                writeBaseline: true, repo: false, baselineRoot: _baselineRoot, ct: default);

            // Mutate the skill's content after the baseline was captured.
            var skillFile = Path.Combine(dir.FullName, skillName, "SKILL.md");
            File.WriteAllText(skillFile, File.ReadAllText(skillFile) + "\nAn extra line changing the content hash.\n");

            var outFile = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-skills-tofu-changed-out-" + Guid.NewGuid().ToString("N") + ".json"));
            try
            {
                await SkillsScanCommand.ExecuteAsync(
                    dir, "json", outFile, failOnNoncompliant: false,
                    writeBaseline: false, repo: false, baselineRoot: _baselineRoot, checkBaseline: true, ct: default);

                var content = await File.ReadAllTextAsync(outFile.FullName);
                Assert.DoesNotContain("byte-identical to a previously-captured baseline snapshot", content, StringComparison.Ordinal);
            }
            finally { if (outFile.Exists) outFile.Delete(); }
        }
        finally { dir.Delete(recursive: true); }
    }

    // ── Wave 2: direct unit coverage for the pure detection helpers ──

    [Fact]
    public void DetectCrossLocationDrift_EmptyEntries_ReturnsEmpty()
    {
        Assert.Empty(SkillsScanCommand.DetectCrossLocationDrift([]));
    }

    [Fact]
    public void DetectCrossLocationDrift_SingleLocationPerName_ReturnsEmpty()
    {
        var entries = new[]
        {
            new SkillBaselineEntry("a", SkillSourceKind.File, ".claude/skills/a", "struct-a", "hash-a", null, null, null, []),
            new SkillBaselineEntry("b", SkillSourceKind.File, ".claude/skills/b", "struct-b", "hash-b", null, null, null, []),
        };
        Assert.Empty(SkillsScanCommand.DetectCrossLocationDrift(entries));
    }

    [Fact]
    public void DetectPreviouslyVetted_EmptyHistory_ReturnsEmpty()
    {
        var entries = new[]
        {
            new SkillBaselineEntry("a", SkillSourceKind.File, ".claude/skills/a", "struct-a", "hash-a", null, null, null, []),
        };
        Assert.Empty(SkillsScanCommand.DetectPreviouslyVetted(entries, []));
    }

    // ── helpers (mirrors SkillsScanCommandTests' fixture builder) ──

    private static DirectoryInfo CreateCompliantFixture(out string skillName)
    {
        skillName = "compliant-skill-" + Guid.NewGuid().ToString("N")[..8];
        var root = Path.Combine(Path.GetTempPath(), "agenteval-skills-baseline-fixture-" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, skillName);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            $"---\nname: {skillName}\ndescription: A compliant fixture skill for SkillsBaselineCommandTests.\n---\n\nBody.\n");
        return new DirectoryInfo(root);
    }
}
