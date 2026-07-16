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

    // ── Item 5: silent-discovery-exclusion detection ──
    // strategy/FutureFeatures/Skills/Skill-Discovery-Exclusion-Detection-Design.md. These replace the
    // "unreachable via a REAL on-disk scan" honesty finding documented on SkillsScanCommand.ExecuteAsync:
    // that gap is now CLOSED for ScanFileSkillsAsync specifically (the CLI verb's entry point).

    private static string NewFixtureRoot() =>
        Path.Combine(Path.GetTempPath(), "agenteval-exclusion-fixture-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ScanFileSkillsAsync_MalformedNameFolder_ProducesExcludedFromDiscoveryFinding()
    {
        var root = NewFixtureRoot();
        var badDir = Path.Combine(root, "bad--hyphen");
        Directory.CreateDirectory(badDir);
        await File.WriteAllTextAsync(Path.Combine(badDir, "SKILL.md"),
            "---\nname: bad--hyphen\ndescription: has consecutive hyphens in its name.\n---\n\nBody.\n");

        try
        {
            var report = await MafSkillScanner.ScanFileSkillsAsync(root, FakeAgent());

            // The folder vanished from MAF's own count (the original bug)...
            Assert.Equal(0, report.Coverage.SkillCount);
            // ...but Item 5 now surfaces it explicitly instead of silence.
            Assert.Equal(1, report.Coverage.SilentlyExcludedCount);
            var finding = Assert.Single(report.Findings, f => f.Rule == SkillComplianceRule.SkillExcludedFromDiscovery);
            Assert.Equal(Severity.High, finding.Severity);
            Assert.Contains("bad--hyphen", finding.Message, StringComparison.Ordinal);
            Assert.Contains("NEVER be loaded", finding.Message, StringComparison.Ordinal);
            Assert.Contains("consecutive hyphens", finding.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(report.IsCompliant);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanFileSkillsAsync_MissingDescriptionFolder_ProducesExcludedFromDiscoveryFinding()
    {
        // Empirically confirmed this session: MAF silently excludes on a missing/empty description too,
        // not just name-format violations — a real widening of the design doc's original name-only hypothesis.
        var root = NewFixtureRoot();
        var dir = Path.Combine(root, "no-description");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.md"), "---\nname: no-description\n---\n\nBody.\n");

        try
        {
            var report = await MafSkillScanner.ScanFileSkillsAsync(root, FakeAgent());

            Assert.Equal(1, report.Coverage.SilentlyExcludedCount);
            var finding = Assert.Single(report.Findings, f => f.Rule == SkillComplianceRule.SkillExcludedFromDiscovery);
            Assert.Contains("description", finding.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanFileSkillsAsync_MismatchedDirectoryName_ProducesExcludedFromDiscoveryFinding()
    {
        var root = NewFixtureRoot();
        var dir = Path.Combine(root, "mismatched-dir");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.md"),
            "---\nname: totally-different\ndescription: the name does not match its folder.\n---\n\nBody.\n");

        try
        {
            var report = await MafSkillScanner.ScanFileSkillsAsync(root, FakeAgent());

            Assert.Equal(1, report.Coverage.SilentlyExcludedCount);
            var finding = Assert.Single(report.Findings, f => f.Rule == SkillComplianceRule.SkillExcludedFromDiscovery);
            Assert.Contains("totally-different", finding.Message, StringComparison.Ordinal);
            Assert.Contains("directory", finding.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanFileSkillsAsync_WellFormedSkillSet_ZeroExclusionFindings_NoFalsePositives()
    {
        var root = NewFixtureRoot();
        for (var i = 0; i < 3; i++)
        {
            var name = $"good-skill-{i}";
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.md"),
                $"---\nname: {name}\ndescription: A well-formed fixture skill number {i}.\n---\n\nBody.\n");
        }

        try
        {
            var report = await MafSkillScanner.ScanFileSkillsAsync(root, FakeAgent());

            Assert.Equal(3, report.Coverage.SkillCount);
            Assert.Equal(0, report.Coverage.SilentlyExcludedCount);
            Assert.DoesNotContain(report.Findings, f => f.Rule == SkillComplianceRule.SkillExcludedFromDiscovery);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanFileSkillsAsync_RelativePath_WellFormedSkillSet_ZeroExclusionFindings()
    {
        // Regression guard: returnedDirectories is built from MAF's always-ABSOLUTE AgentFileSkill.Path,
        // while the raw-scanner's candidates preserved whatever form the caller's skillPath argument had.
        // Before NormalizeDirectory canonicalized both sides via Path.GetFullPath, passing a genuinely
        // RELATIVE path here made every well-formed skill falsely flag as excluded (relative-string
        // candidates could never string-match absolute returned paths). This test uses a path relative to
        // the CURRENT process working directory (via Path.GetRelativePath) rather than mutating
        // Environment.CurrentDirectory, which would be unsafe under parallel test execution.
        var root = NewFixtureRoot();
        var dir = Path.Combine(root, "relative-path-skill");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.md"),
            "---\nname: relative-path-skill\ndescription: reached via a relative skillPath argument.\n---\n\nBody.\n");

        try
        {
            var relativeRoot = Path.GetRelativePath(Directory.GetCurrentDirectory(), root);
            Assert.False(Path.IsPathRooted(relativeRoot), "test precondition: the constructed path must actually be relative");

            var report = await MafSkillScanner.ScanFileSkillsAsync(relativeRoot, FakeAgent());

            Assert.Equal(1, report.Coverage.SkillCount);
            Assert.Equal(0, report.Coverage.SilentlyExcludedCount);
            Assert.DoesNotContain(report.Findings, f => f.Rule == SkillComplianceRule.SkillExcludedFromDiscovery);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanFileSkillsAsync_DescriptionTooLong_ReasonIsTheRealDescriptionRule_NotTheGenericFallback()
    {
        // Regression guard: FindSilentExclusions used to filter candidate reasons to Severity.High only, so
        // DescriptionTooLong (Medium — but a CONFIRMED real MAF silent-exclusion trigger) fell through to
        // the generic "might be malformed YAML, inspect directly" fallback instead of the real, already-
        // detected reason.
        var root = NewFixtureRoot();
        var dir = Path.Combine(root, "long-description");
        Directory.CreateDirectory(dir);
        var longDescription = new string('d', 1100); // > SkillComplianceValidator's 1024-char GA limit
        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.md"),
            $"---\nname: long-description\ndescription: {longDescription}\n---\n\nBody.\n");

        try
        {
            var report = await MafSkillScanner.ScanFileSkillsAsync(root, FakeAgent());

            Assert.Equal(1, report.Coverage.SilentlyExcludedCount);
            var finding = Assert.Single(report.Findings, f => f.Rule == SkillComplianceRule.SkillExcludedFromDiscovery);
            Assert.Contains("1100 characters", finding.Message, StringComparison.Ordinal); // the REAL DescriptionTooLong message
            Assert.DoesNotContain("malformed YAML", finding.Message, StringComparison.OrdinalIgnoreCase); // not the generic fallback
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanFileSkillsAsync_MixedValidAndMalformedSiblings_OnlyMalformedOneExcluded()
    {
        var root = NewFixtureRoot();
        var validDir = Path.Combine(root, "valid-sibling");
        Directory.CreateDirectory(validDir);
        await File.WriteAllTextAsync(Path.Combine(validDir, "SKILL.md"),
            "---\nname: valid-sibling\ndescription: a perfectly fine sibling skill.\n---\n\nBody.\n");

        var badDir = Path.Combine(root, "Invalid_Chars");
        Directory.CreateDirectory(badDir);
        await File.WriteAllTextAsync(Path.Combine(badDir, "SKILL.md"),
            "---\nname: Invalid_Chars\ndescription: uppercase and underscore are not allowed.\n---\n\nBody.\n");

        try
        {
            var report = await MafSkillScanner.ScanFileSkillsAsync(root, FakeAgent());

            Assert.Equal(1, report.Coverage.SkillCount); // only valid-sibling was returned by MAF
            Assert.Equal(1, report.Coverage.SilentlyExcludedCount);
            Assert.DoesNotContain(report.Findings, f => f.SkillName == "valid-sibling" && f.Rule == SkillComplianceRule.SkillExcludedFromDiscovery);
            var excluded = Assert.Single(report.Findings, f => f.Rule == SkillComplianceRule.SkillExcludedFromDiscovery);
            Assert.Equal("Invalid_Chars", excluded.SkillName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanFileSkillsAsync_CompatibilityOverHardLimit_GracefulHighFinding_NeverThrows()
    {
        // Bonus defensive fix found during this same verification: a too-long 'compatibility' field makes
        // MAF's GetSkillsAsync THROW (not silently exclude) — confirmed empirically. Without the try/catch
        // in ScanFileSkillsAsync this test would fail with an unhandled ArgumentException instead of
        // asserting a graceful report.
        var root = NewFixtureRoot();
        var dir = Path.Combine(root, "long-compat");
        Directory.CreateDirectory(dir);
        var longCompat = new string('c', 600);
        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.md"),
            $"---\nname: long-compat\ndescription: valid desc.\ncompatibility: {longCompat}\n---\n\nBody.\n");

        try
        {
            var report = await MafSkillScanner.ScanFileSkillsAsync(root, FakeAgent());

            var finding = Assert.Single(report.Findings);
            Assert.Equal(Severity.High, finding.Severity);
            Assert.Equal(SkillComplianceRule.SkillExcludedFromDiscovery, finding.Rule);
            Assert.Contains("failed entirely", finding.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(report.IsCompliant);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanFileSkillsAsync_MalformedFolder_FailOnNoncompliant_IsNoncompliant()
    {
        var root = NewFixtureRoot();
        var badDir = Path.Combine(root, "bad--hyphen");
        Directory.CreateDirectory(badDir);
        await File.WriteAllTextAsync(Path.Combine(badDir, "SKILL.md"),
            "---\nname: bad--hyphen\ndescription: has consecutive hyphens.\n---\n\nBody.\n");

        try
        {
            var report = await MafSkillScanner.ScanFileSkillsAsync(root, FakeAgent());

            // A SkillExcludedFromDiscovery finding is High severity, so the existing IsCompliant contract
            // (Findings.All(f => f.Severity < High)) already treats it as non-compliant — no gate code
            // change needed, this locks the behavior in.
            Assert.False(report.IsCompliant);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
