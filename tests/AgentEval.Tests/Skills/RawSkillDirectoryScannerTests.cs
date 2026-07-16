// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests.Skills;

/// <summary>
/// Locks in the discovery convention confirmed empirically against the live <c>Microsoft.Agents.AI</c>
/// assembly this session (temporary diagnostic tests, deleted before commit) — see
/// <see cref="RawSkillDirectoryScanner"/>'s remarks for the full write-up. These tests exist so a future
/// MAF upgrade that changes this convention fails loudly here rather than silently mis-reconciling.
/// </summary>
public class RawSkillDirectoryScannerTests
{
    private static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), "agenteval-raw-scanner-" + Guid.NewGuid().ToString("N"));

    private static void WriteSkillMd(string dir, string fileName = "SKILL.md")
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), "---\nname: x\ndescription: y\n---\n");
    }

    [Fact]
    public void FindCandidateSkillDirectories_NonexistentRoot_ReturnsEmpty()
    {
        var result = RawSkillDirectoryScanner.FindCandidateSkillDirectories(
            Path.Combine(Path.GetTempPath(), "agenteval-does-not-exist-" + Guid.NewGuid().ToString("N")));

        Assert.Empty(result);
    }

    [Fact]
    public void FindCandidateSkillDirectories_RootHasOwnSkillMd_SingleSkillMode_ReturnsOnlyRoot()
    {
        var root = NewTempRoot();
        WriteSkillMd(root);
        try
        {
            var result = RawSkillDirectoryScanner.FindCandidateSkillDirectories(root);
            Assert.Equal(new[] { root }, result);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindCandidateSkillDirectories_RootHasOwnSkillMd_SubdirectorySkillIsInvisible()
    {
        // Single-skill mode is exclusive — a root-level SKILL.md means subdirectories are never considered,
        // even if one of them is a perfectly well-formed skill on its own.
        var root = NewTempRoot();
        WriteSkillMd(root);
        WriteSkillMd(Path.Combine(root, "direct-skill"));
        try
        {
            var result = RawSkillDirectoryScanner.FindCandidateSkillDirectories(root);
            Assert.Equal(new[] { root }, result);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindCandidateSkillDirectories_ContainerMode_FindsDepth1AndDepth2_NotDepth3()
    {
        var root = NewTempRoot();
        var depth1 = Path.Combine(root, "direct-skill");
        var depth2 = Path.Combine(root, "group", "nested-skill-l2");
        var depth3 = Path.Combine(root, "group", "subgroup", "nested-skill-l3");
        WriteSkillMd(depth1);
        WriteSkillMd(depth2);
        WriteSkillMd(depth3);
        try
        {
            var result = RawSkillDirectoryScanner.FindCandidateSkillDirectories(root);
            Assert.Contains(depth1, result);
            Assert.Contains(depth2, result);
            Assert.DoesNotContain(depth3, result);
            Assert.Equal(2, result.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindCandidateSkillDirectories_ContainerMode_CaseInsensitiveSkillMdExtension()
    {
        var root = NewTempRoot();
        var upper = Path.Combine(root, "upper-case-skill");
        WriteSkillMd(upper, "SKILL.MD");
        try
        {
            var result = RawSkillDirectoryScanner.FindCandidateSkillDirectories(root);
            Assert.Contains(upper, result);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindCandidateSkillDirectories_ContainerMode_MalformedSiblingDoesNotHideValidOne()
    {
        var root = NewTempRoot();
        var valid = Path.Combine(root, "valid-skill");
        var malformed = Path.Combine(root, "bad--hyphen");
        WriteSkillMd(valid);
        WriteSkillMd(malformed);
        try
        {
            var result = RawSkillDirectoryScanner.FindCandidateSkillDirectories(root);
            Assert.Contains(valid, result);
            Assert.Contains(malformed, result); // raw pass finds BOTH — reconciliation decides validity, not this pass
            Assert.Equal(2, result.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindCandidateSkillDirectories_SkillDirectorysOwnSubtree_NotWalkedForNestedSkills()
    {
        var root = NewTempRoot();
        var outer = Path.Combine(root, "skill-x");
        var inner = Path.Combine(outer, "skill-y");
        WriteSkillMd(outer);
        WriteSkillMd(inner);
        try
        {
            var result = RawSkillDirectoryScanner.FindCandidateSkillDirectories(root);
            Assert.Equal(new[] { outer }, result);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TryFindSkillMdFile_CaseInsensitiveMatch_Found()
    {
        var dir = NewTempRoot();
        WriteSkillMd(dir, "Skill.Md");
        try
        {
            var found = RawSkillDirectoryScanner.TryFindSkillMdFile(dir, out var path);
            Assert.True(found);
            Assert.NotNull(path);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TryFindSkillMdFile_NoSkillMd_NotFound()
    {
        var dir = NewTempRoot();
        Directory.CreateDirectory(dir);
        try
        {
            var found = RawSkillDirectoryScanner.TryFindSkillMdFile(dir, out var path);
            Assert.False(found);
            Assert.Null(path);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
