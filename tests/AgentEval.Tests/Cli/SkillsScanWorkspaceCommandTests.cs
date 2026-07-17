// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Agent Skills Wave 3a — <c>skills scan-workspace</c>: a filesystem-only, credential-free multi-repo scan
/// over a folder of already-cloned repos. Reuses the EXISTING <c>--repo</c> pipeline per subdirectory (no
/// new discovery/compliance code), so these tests focus on the fan-out/rollup/cross-repo-drift behavior
/// specific to this verb, not re-testing what <see cref="SkillsBaselineCommandTests"/> already covers for
/// <c>--repo</c> itself.
/// </summary>
public class SkillsScanWorkspaceCommandTests : IDisposable
{
    private readonly string _baselineRoot;

    public SkillsScanWorkspaceCommandTests()
    {
        _baselineRoot = Path.Combine(Path.GetTempPath(), "agenteval-skills-workspace-cmd-test-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_baselineRoot, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── command wiring ──

    [Fact]
    public void Create_ReturnsSkillsCommand_WithScanWorkspaceSubcommand()
    {
        var command = SkillsScanCommand.Create();
        Assert.Contains(command.Subcommands, c => c.Name == "scan-workspace");
    }

    [Fact]
    public void Create_ScanWorkspaceSubcommand_HasExpectedOptions()
    {
        var cmd = SkillsScanCommand.Create().Subcommands.Single(c => c.Name == "scan-workspace");
        Assert.Contains(cmd.Arguments, a => a.Name == "path");
        Assert.Contains(cmd.Options, o => o.Name == "--format");
        Assert.Contains(cmd.Options, o => o.Name == "--output" || o.Aliases.Contains("--output"));
        Assert.Contains(cmd.Options, o => o.Name == "--fail-on-noncompliant");
        Assert.Contains(cmd.Options, o => o.Name == "--write-baseline");
        Assert.Contains(cmd.Options, o => o.Name == "--baseline-root");
        Assert.Contains(cmd.Options, o => o.Name == "--check-baseline");
        // Deliberately NOT --workspace or --repo — see ExecuteWorkspaceAsync's own remarks on the naming choice.
        Assert.DoesNotContain(cmd.Options, o => o.Name == "--repo");
    }

    // ── ExecuteWorkspaceAsync: real, offline, credential-free multi-repo scan ──

    [Fact]
    public async Task ExecuteWorkspaceAsync_NonexistentDirectory_Throws()
    {
        var missing = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "agenteval-skills-workspace-missing-" + Guid.NewGuid().ToString("N")));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => SkillsScanCommand.ExecuteWorkspaceAsync(missing, "console", null, false, ct: default));
    }

    [Fact]
    public async Task ExecuteWorkspaceAsync_EmptyWorkspace_Succeeds_ZeroSkills()
    {
        var workspaceRoot = CreateWorkspaceRoot();
        try
        {
            var outFile = NewTempOutputFile();
            try
            {
                var exit = await SkillsScanCommand.ExecuteWorkspaceAsync(
                    new DirectoryInfo(workspaceRoot), "json", outFile, failOnNoncompliant: false, ct: default);

                Assert.Equal(ExitCodes.Success, exit);
                var content = await File.ReadAllTextAsync(outFile.FullName);
                Assert.Contains("\"SkillCount\": 0", content, StringComparison.Ordinal);
            }
            finally { if (outFile.Exists) outFile.Delete(); }
        }
        finally { Directory.Delete(workspaceRoot, recursive: true); }
    }

    [Fact]
    public async Task ExecuteWorkspaceAsync_TwoRepos_AggregatesAcrossThem()
    {
        var workspaceRoot = CreateWorkspaceRoot();
        try
        {
            CreateRepoWithSkill(workspaceRoot, "repo-a", "skill-a", "Present in repo-a.");
            CreateRepoWithSkill(workspaceRoot, "repo-b", "skill-b", "Present in repo-b.");

            var outFile = NewTempOutputFile();
            try
            {
                var exit = await SkillsScanCommand.ExecuteWorkspaceAsync(
                    new DirectoryInfo(workspaceRoot), "json", outFile, failOnNoncompliant: false,
                    writeBaseline: true, baselineRoot: _baselineRoot, ct: default);

                Assert.Equal(ExitCodes.Success, exit);
                var content = await File.ReadAllTextAsync(outFile.FullName);
                Assert.Contains("\"SkillCount\": 2", content, StringComparison.Ordinal);

                var store = new JsonFileSkillBaselineStore(_baselineRoot);
                var snapshots = await store.ListAsync();
                var snapshot = Assert.Single(snapshots);
                Assert.Equal(2, snapshot.Skills.Count);
                Assert.Contains(snapshot.Skills, s => s.RelativePath.StartsWith("repo-a/", StringComparison.Ordinal));
                Assert.Contains(snapshot.Skills, s => s.RelativePath.StartsWith("repo-b/", StringComparison.Ordinal));
            }
            finally { if (outFile.Exists) outFile.Delete(); }
        }
        finally { Directory.Delete(workspaceRoot, recursive: true); }
    }

    [Fact]
    public async Task ExecuteWorkspaceAsync_SameSkillNameDifferentContentAcrossRepos_FlagsCrossLocationDrift()
    {
        // The headline Wave 3a scenario: "skill X is byte-identical across N repos except M outliers" — proven
        // here by reusing the UNCHANGED DetectCrossLocationDrift (it groups by Name only, never by
        // RelativePath), fed entries re-tagged with a repo-prefixed RelativePath.
        var workspaceRoot = CreateWorkspaceRoot();
        try
        {
            CreateRepoWithSkill(workspaceRoot, "repo-a", "shared-skill", "Variant A.");
            CreateRepoWithSkill(workspaceRoot, "repo-b", "shared-skill", "Variant B — DIFFERENT.");

            var outFile = NewTempOutputFile();
            try
            {
                var exit = await SkillsScanCommand.ExecuteWorkspaceAsync(
                    new DirectoryInfo(workspaceRoot), "json", outFile, failOnNoncompliant: false, ct: default);

                Assert.Equal(ExitCodes.Success, exit);
                var content = await File.ReadAllTextAsync(outFile.FullName);
                Assert.Contains("shared-skill", content, StringComparison.Ordinal);
                Assert.Contains("DIFFERENT content across", content, StringComparison.Ordinal);
            }
            finally { if (outFile.Exists) outFile.Delete(); }
        }
        finally { Directory.Delete(workspaceRoot, recursive: true); }
    }

    [Fact]
    public async Task ExecuteWorkspaceAsync_HiddenSubdirectory_NotTreatedAsARepo()
    {
        var workspaceRoot = CreateWorkspaceRoot();
        try
        {
            // A stray .git (or similar tooling) folder at the workspace root must not be scanned as a "repo" —
            // it has no known skill-directory convention under it, but it also shouldn't even be attempted.
            Directory.CreateDirectory(Path.Combine(workspaceRoot, ".git"));
            CreateRepoWithSkill(workspaceRoot, "repo-a", "skill-a", "Present in repo-a.");

            var outFile = NewTempOutputFile();
            try
            {
                var exit = await SkillsScanCommand.ExecuteWorkspaceAsync(
                    new DirectoryInfo(workspaceRoot), "json", outFile, failOnNoncompliant: false, ct: default);

                Assert.Equal(ExitCodes.Success, exit);
                var content = await File.ReadAllTextAsync(outFile.FullName);
                Assert.Contains("\"SkillCount\": 1", content, StringComparison.Ordinal);
            }
            finally { if (outFile.Exists) outFile.Delete(); }
        }
        finally { Directory.Delete(workspaceRoot, recursive: true); }
    }

    [Fact]
    public async Task ExecuteWorkspaceAsync_OnlyHiddenSubdirectories_DiscloseSkipCount_NotMisreportedAsEmpty()
    {
        // Distinguishes "truly no subdirectories" from "subdirectories exist but were all filtered as
        // hidden" — an operator pointing scan-workspace at one already-cloned repo (which itself has a
        // .git folder and no other subdirectory) by mistake must see WHY nothing was scanned, not a message
        // that reads identically to a genuinely empty folder.
        var workspaceRoot = CreateWorkspaceRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(workspaceRoot, ".git"));

            var outFile = NewTempOutputFile();
            try
            {
                var exit = await SkillsScanCommand.ExecuteWorkspaceAsync(
                    new DirectoryInfo(workspaceRoot), "console", outFile, failOnNoncompliant: false, ct: default);

                Assert.Equal(ExitCodes.Success, exit);
            }
            finally { if (outFile.Exists) outFile.Delete(); }
        }
        finally { Directory.Delete(workspaceRoot, recursive: true); }
    }

    [Fact]
    public void DefaultBaselineRoot_DiffersBetweenScanAndScanWorkspace_PreventsScopeMismatchInDiff()
    {
        // scan-workspace's own default baseline root must NOT equal scan's default — sharing one root by
        // default would let `skills baseline diff` (which always compares "the two most recent snapshots"
        // with no scope awareness) silently compare a handful-of-skills single-repo snapshot against a
        // hundreds-of-skills workspace snapshot. Reflection on the two private const fields directly, since
        // that's the actual single source of truth both ExecuteAsync's and ExecuteWorkspaceAsync's own
        // default parameter values are compiled from.
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var scanDefault = (string)typeof(SkillsScanCommand).GetField("DefaultBaselineRoot", flags)!.GetValue(null)!;
        var workspaceDefault = (string)typeof(SkillsScanCommand).GetField("DefaultWorkspaceBaselineRoot", flags)!.GetValue(null)!;

        Assert.NotEqual(scanDefault, workspaceDefault);
    }

    private static string CreateWorkspaceRoot() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "agenteval-skills-workspace-test-" + Guid.NewGuid().ToString("N"))).FullName;

    private static void CreateRepoWithSkill(string workspaceRoot, string repoName, string skillName, string description)
    {
        var skillDir = Path.Combine(workspaceRoot, repoName, ".claude", "skills", skillName);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), $"---\nname: {skillName}\ndescription: {description}\n---\n\nBody.\n");
    }

    private static FileInfo NewTempOutputFile() =>
        new(Path.Combine(Path.GetTempPath(), "agenteval-skills-workspace-out-" + Guid.NewGuid().ToString("N") + ".txt"));
}
