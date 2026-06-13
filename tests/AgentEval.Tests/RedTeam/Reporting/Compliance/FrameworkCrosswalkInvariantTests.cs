// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Reporting/Compliance/FrameworkCrosswalkInvariantTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Tests.RedTeam.Reporting.Compliance;

/// <summary>
/// Anti-drift guard for the unified framework crosswalk (Wave D, §7). Mirrors <see cref="MITREATLASInvariantTests"/>:
/// every framework control row's <see cref="ControlMapping.RelevantAttacks"/> must resolve against the canonical
/// attack taxonomy (<see cref="Attack.ByName"/>), governance rows must stay empty (NEVER PASS), and OWASP categories
/// must derive — never be hand-authored — so a renamed attack can never silently orphan a mapping.
/// </summary>
public sealed class FrameworkCrosswalkInvariantTests
{
    // The five frameworks the crosswalk must carry. Authored as a separate literal so a dropped/renamed
    // framework registry is caught against an independent expectation rather than a self-projection.
    private static readonly string[] ExpectedFrameworks =
        ["CSF", "ISO27001", "ISO42001", "NIST-AI-RMF", "SOC2"];

    public static IEnumerable<object[]> AllControls() =>
        FrameworkCrosswalk.AllControls.Select(c => new object[] { c });

    public static IEnumerable<object[]> NonGovernanceControls() =>
        FrameworkCrosswalk.AllControls
            .Where(c => c.Fidelity != ControlFidelity.Governance)
            .Select(c => new object[] { c });

    public static IEnumerable<object[]> GovernanceControls() =>
        FrameworkCrosswalk.AllControls
            .Where(c => c.Fidelity == ControlFidelity.Governance)
            .Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(AllControls))]
    public void EveryRelevantAttack_ResolvesInCanonicalTaxonomy(ControlMapping control)
    {
        // A control MAY list zero attacks (e.g. SOC2 CC8.1 baseline-comparison placeholder); that is drift-proof.
        // What must never happen: a LISTED name that no longer resolves — a renamed/typo'd attack silently orphaned.
        foreach (var attackName in control.RelevantAttacks)
        {
            Assert.True(Attack.ByName(attackName) is not null,
                $"{control.Framework} {control.ControlId} references attack '{attackName}' that does not resolve via Attack.ByName — taxonomy drift.");
        }
    }

    [Theory]
    [MemberData(nameof(AllControls))]
    public void OwaspCategories_AreDerivedFromAttacks_NotHandAuthored(ControlMapping control)
    {
        // OwaspCategories is derived (RC-5), so it must be the distinct OwaspLlmId set of its attacks — no more, no less.
        var expected = control.RelevantAttacks
            .Select(Attack.ByName)
            .Where(a => a is not null)
            .Select(a => a!.OwaspLlmId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expected, control.OwaspCategories);
        Assert.All(control.OwaspCategories, id => Assert.Matches(@"^LLM\d{2}$", id));
    }

    [Theory]
    [MemberData(nameof(GovernanceControls))]
    public void GovernanceControl_HasNoAttacks_AndDerivesNoOwaspCategories_NeverPass(ControlMapping control)
    {
        // A governance row with an attack behind it could be scored PASS — exactly what the fidelity taxonomy forbids.
        Assert.Empty(control.RelevantAttacks);
        Assert.Empty(control.OwaspCategories);
    }

    [Fact]
    public void Crosswalk_CarriesEveryExpectedFramework_AndNoOthers()
        => Assert.Equal(ExpectedFrameworks, FrameworkCrosswalk.Frameworks);

    [Fact]
    public void Crosswalk_HasNoDuplicateControlIdWithinAFramework()
    {
        var dupes = FrameworkCrosswalk.AllControls
            .GroupBy(c => (c.Framework, c.ControlId))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Framework}/{g.Key.ControlId}")
            .ToList();

        Assert.Empty(dupes);
    }

    [Fact]
    public void EveryNistControl_DeclaresNistFramework()
        => Assert.All(NistAiRmfControls.All, c => Assert.Equal(NistAiRmfControls.Framework, c.Framework));

    [Fact]
    public void Crosswalk_ContainsEveryNistControl()
    {
        // The unified registry must not silently omit a framework's rows.
        foreach (var nist in NistAiRmfControls.All)
        {
            Assert.Contains(FrameworkCrosswalk.AllControls,
                c => c.Framework == nist.Framework && c.ControlId == nist.ControlId);
        }
    }
}
