// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Reporting/Compliance/ControlMappingAttackNameTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Tests.RedTeam.Reporting.Compliance;

public sealed class ControlMappingAttackNameTests
{
    private static readonly HashSet<string> KnownAttackNames =
        Attack.All.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<object[]> AllControls()
    {
        foreach (var c in SOC2Controls.All)     yield return new object[] { "SOC2", c };
        foreach (var c in ISO27001Controls.All) yield return new object[] { "ISO27001", c };
    }

    [Theory]
    [MemberData(nameof(AllControls))]
    public void EveryReferencedAttackName_ResolvesToAttackAll(string framework, ControlMapping control)
    {
        foreach (var name in control.RelevantAttacks)
        {
            Assert.True(KnownAttackNames.Contains(name),
                $"[{framework}/{control.ControlId}] references attack '{name}' which is not in Attack.All.");
            Assert.NotNull(Attack.ByName(name));
        }
    }

    [Fact]
    public void AttackAll_Names_MatchAvailableNames()
        => Assert.Equal(
            Attack.AvailableNames.OrderBy(n => n, StringComparer.Ordinal),
            Attack.All.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal));
}
