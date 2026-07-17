// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests.Skills;

/// <summary>Agent Skills Wave 1 (§4.1) — the genuinely new hash function: full file-CONTENT coverage, complementing the structural (frontmatter-only) fingerprint.</summary>
public class SkillContentHasherTests : IDisposable
{
    private readonly string _root;

    public SkillContentHasherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-skill-content-hasher-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteSkill(string skillDir, string skillMd, string? resourceContent = null, string? scriptContent = null)
    {
        var dir = Path.Combine(_root, skillDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), skillMd);
        if (resourceContent is not null)
        {
            var resourcesDir = Path.Combine(dir, "resources");
            Directory.CreateDirectory(resourcesDir);
            File.WriteAllText(Path.Combine(resourcesDir, "policy.md"), resourceContent);
        }

        if (scriptContent is not null)
        {
            var scriptsDir = Path.Combine(dir, "scripts");
            Directory.CreateDirectory(scriptsDir);
            File.WriteAllText(Path.Combine(scriptsDir, "run.py"), scriptContent);
        }
    }

    [Fact]
    public void HashSkillFolder_SameContentTwice_SameHash()
    {
        WriteSkill("skill-a", "---\nname: skill-a\n---\nBody", "resource text", "script text");
        var dir = Path.Combine(_root, "skill-a");

        var h1 = SkillContentHasher.HashSkillFolder(dir);
        var h2 = SkillContentHasher.HashSkillFolder(dir);

        Assert.Equal(h1, h2);
    }

    [Fact]
    public void HashSkillFolder_ResourceContentChanged_DifferentHash_EvenThoughNameUnchanged()
    {
        // The whole point of this hasher: a resource file's CONTENT changing must be caught, even though
        // its NAME (what SkillManifestPoisoningGate.Fingerprint alone would see) never changed.
        WriteSkill("skill-a", "---\nname: skill-a\n---\nBody", "original resource text");
        var dir = Path.Combine(_root, "skill-a");
        var before = SkillContentHasher.HashSkillFolder(dir);

        File.WriteAllText(Path.Combine(dir, "resources", "policy.md"), "TAMPERED resource text");
        var after = SkillContentHasher.HashSkillFolder(dir);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void HashSkillFolder_ScriptContentChanged_DifferentHash()
    {
        WriteSkill("skill-a", "---\nname: skill-a\n---\nBody", scriptContent: "print('hi')");
        var dir = Path.Combine(_root, "skill-a");
        var before = SkillContentHasher.HashSkillFolder(dir);

        File.WriteAllText(Path.Combine(dir, "scripts", "run.py"), "print('PWNED')");
        var after = SkillContentHasher.HashSkillFolder(dir);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void HashSkillFolder_SkillMdBodyChanged_DifferentHash()
    {
        WriteSkill("skill-a", "---\nname: skill-a\n---\nOriginal body");
        var dir = Path.Combine(_root, "skill-a");
        var before = SkillContentHasher.HashSkillFolder(dir);

        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\nname: skill-a\n---\nChanged body");
        var after = SkillContentHasher.HashSkillFolder(dir);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void HashSkillFolder_TwoDifferentSkillsWithIdenticalContent_SameHash()
    {
        WriteSkill("skill-a", "---\nname: same\n---\nBody", "res");
        WriteSkill("skill-b", "---\nname: same\n---\nBody", "res");

        var h1 = SkillContentHasher.HashSkillFolder(Path.Combine(_root, "skill-a"));
        var h2 = SkillContentHasher.HashSkillFolder(Path.Combine(_root, "skill-b"));

        Assert.Equal(h1, h2);
    }

    [Fact]
    public void HashSkillFolder_NonExistentDirectory_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            SkillContentHasher.HashSkillFolder(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void HashSkillFolder_AddingAFile_ChangesHash()
    {
        WriteSkill("skill-a", "---\nname: skill-a\n---\nBody");
        var dir = Path.Combine(_root, "skill-a");
        var before = SkillContentHasher.HashSkillFolder(dir);

        var resourcesDir = Path.Combine(dir, "resources");
        Directory.CreateDirectory(resourcesDir);
        File.WriteAllText(Path.Combine(resourcesDir, "new-file.md"), "brand new");
        var after = SkillContentHasher.HashSkillFolder(dir);

        Assert.NotEqual(before, after);
    }
}
