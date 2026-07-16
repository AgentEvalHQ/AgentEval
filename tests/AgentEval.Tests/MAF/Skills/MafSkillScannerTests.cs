// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Skills;
using AgentEval.Skills;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Skills;

public class MafSkillScannerTests
{
    private static AIAgent FakeAgent() =>
        new ChatClientAgent(new ScriptedChatClient(), new ChatClientAgentOptions { Name = "ScannerTestAgent" });

    [Fact]
    public void ToManifest_InlineSkill_MapsFrontmatter_NoResourcesOrScripts()
    {
        var inline = new AgentInlineSkill("expense-report", "Summarizes expense reports.", "Use this skill to summarize expense reports.");

        var manifest = MafSkillScanner.ToManifest(inline);

        Assert.Equal("expense-report", manifest.Name);
        Assert.Equal("Summarizes expense reports.", manifest.Description);
        // Honest, documented limitation: non-file sources have no public resource/script enumeration.
        Assert.Empty(manifest.ResourceNames);
        Assert.Empty(manifest.ScriptNames);
        Assert.Equal(SkillSourceKind.InMemory, manifest.SourceKind);
        Assert.Null(manifest.ParentDirectoryName);
    }

    [Fact]
    public void ToManifest_SourceKindOverride_UsedVerbatim()
    {
        var inline = new AgentInlineSkill("third-party-skill", "An MCP-served skill.", "Fetches third-party content.");
        var manifest = MafSkillScanner.ToManifest(inline, sourceKindOverride: SkillSourceKind.Mcp);
        Assert.Equal(SkillSourceKind.Mcp, manifest.SourceKind);
    }

    [Fact]
    public async Task ScanAsync_InMemorySource_WellFormedSkill_RunsThroughValidator_IsCompliant()
    {
        // Note: MAF's own AgentSkillFrontmatter constructor already enforces the GA name/description
        // presence rules (throws ArgumentException on an empty description) — so a live AgentInlineSkill
        // can never itself be a NameMissing/DescriptionMissing violation. Those rules are still real and
        // covered directly against the pure SkillManifest DTO in SkillComplianceValidatorTests (they
        // matter for a scanner reading a raw SKILL.md file MAF hasn't parsed yet).
        var goodSkill = new AgentInlineSkill("good-skill", description: "Does something useful.", instructions: "Do something.");
        using var source = new AgentInMemorySkillsSource([goodSkill]);
        var context = new AgentSkillsSourceContext(FakeAgent(), session: null);

        var report = await MafSkillScanner.ScanAsync(source, context);

        Assert.Equal(1, report.Coverage.SkillCount);
        Assert.True(report.IsCompliant);
    }

    [Fact]
    public async Task ScanAsync_InMemorySource_AddedScript_IsNotDetectable_DocumentedGap()
    {
        // Honesty-discipline regression guard: AgentInlineSkill.AddScript is add-only — there is no public
        // getter to enumerate scripts back (verified via reflection: no such property exists on
        // AgentInlineSkill or the base AgentSkill). So MafSkillScanner honestly reports ZERO scripts for a
        // non-file skill even though one was added — it does NOT guess or fabricate a script count, and as
        // a direct consequence ScriptRequiresGovernanceReview can never fire for a non-file skill either.
        // This is a real, documented gap (see MafSkillScanner's remarks) — this test locks in that the gap
        // stays honestly-reported (empty), not silently "fixed" by a fabricated non-zero count.
        var skillWithScript = new AgentInlineSkill("scripty-skill", description: "Has a script.", instructions: "Do something.")
            .AddScript("compute", (int x) => x * 2, "Doubles a number.");
        using var source = new AgentInMemorySkillsSource([skillWithScript]);
        var context = new AgentSkillsSourceContext(FakeAgent(), session: null);

        var report = await MafSkillScanner.ScanAsync(source, context);

        Assert.Equal(0, report.Coverage.WithScripts);
        Assert.DoesNotContain(report.Findings, f => f.Rule == SkillComplianceRule.ScriptRequiresGovernanceReview);
    }

    [Fact]
    public async Task ScanFileSkillsAsync_RealFixture_DiscoversResourcesAndScriptsFromDisk()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "agenteval-skill-scan-" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(fixtureRoot, "demo-skill");
        Directory.CreateDirectory(Path.Combine(skillDir, "resources"));
        Directory.CreateDirectory(Path.Combine(skillDir, "scripts"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"),
                "---\nname: demo-skill\ndescription: A demo skill for scanner tests.\n---\n\nBody.\n");
            await File.WriteAllTextAsync(Path.Combine(skillDir, "resources", "policy.md"), "policy text");
            await File.WriteAllTextAsync(Path.Combine(skillDir, "scripts", "compute.csx"), "// script");

            var report = await MafSkillScanner.ScanFileSkillsAsync(skillDir, FakeAgent());

            Assert.Equal(1, report.Coverage.SkillCount);
            Assert.Equal(1, report.Coverage.WithResources);
            Assert.Equal(1, report.Coverage.WithScripts);
            Assert.True(report.IsCompliant || report.Findings.All(f => f.Severity != Severity.High),
                $"Expected a well-formed fixture skill to have no High findings; got: {string.Join("; ", report.Findings.Select(f => f.Rule))}");
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_NullSource_Throws()
    {
        var context = new AgentSkillsSourceContext(FakeAgent(), session: null);
        await Assert.ThrowsAsync<ArgumentNullException>(() => MafSkillScanner.ScanAsync(null!, context));
    }

    [Fact]
    public void ToManifest_NullSkill_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MafSkillScanner.ToManifest(null!));
    }
}
