// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Reporting/Compliance/ControlMappingAttackNameTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Tests.RedTeam.Reporting.Compliance;

public sealed class ControlMappingAttackNameTests
{
    // Every built-in attack - the default roster (Attack.All) and the opt-in ones (Crescendo, PAIR, TAP, ToolEscalation),
    // which map where their default-roster counterparts do (#203 review round 10, B10ax). Still a typo guard.
    private static readonly HashSet<string> KnownAttackNames = typeof(Attack)
        .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(p => p.PropertyType == typeof(IAttackType))
        .Select(p => ((IAttackType)p.GetValue(null)!).Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<object[]> AllControls()
    {
        foreach (var c in SOC2Controls.All)     yield return new object[] { "SOC2", c };
        foreach (var c in ISO27001Controls.All) yield return new object[] { "ISO27001", c };
        foreach (var c in NistAiRmfControls.All) yield return new object[] { "NIST", c };
    }

    [Theory]
    [MemberData(nameof(AllControls))]
    public void EveryReferencedAttackName_ResolvesToAttackAll(string framework, ControlMapping control)
    {
        foreach (var name in control.RelevantAttacks)
        {
            Assert.True(KnownAttackNames.Contains(name),
                $"[{framework}/{control.ControlId}] references attack '{name}', which is not a built-in attack.");
            Assert.NotNull(Attack.ByName(name));
        }
    }

    [Fact]
    public void AttackAll_Names_MatchAvailableNames()
        => Assert.Equal(
            Attack.AvailableNames.OrderBy(n => n, StringComparer.Ordinal),
            Attack.All.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal));
}
