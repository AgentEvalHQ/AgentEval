// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Reporting/Compliance/MITREATLASInvariantTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Tests.RedTeam.Reporting.Compliance;

public sealed class MITREATLASInvariantTests
{
    // Expectation of each ATLAS technique's tactic assignment, pinned against a separate literal so the invariant
    // catches a mis-edited TacticId (a drift guard) rather than self-projecting the reporter's own field.
    // H13: re-authored 2026-06-13 from the authoritative mitre-atlas/atlas-data dist/ATLAS.yaml (atlas.mitre.org) —
    // these technique→tactic pairings and the tactic names below are now source-verified (ATLAS renamed ML*→AI*;
    // AML.T0045 was retired → InferenceAPIAbuse maps to AML.T0034 Cost Harvesting).
    private static readonly IReadOnlyDictionary<string, string> ExpectedTechniqueTacticId =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AML.T0051"] = "TA0005",   // LLM Prompt Injection → Execution
            ["AML.T0054"] = "TA0007",   // LLM Jailbreak → Defense Evasion
            ["AML.T0056"] = "TA0010",   // Extract LLM System Prompt → Exfiltration
            ["AML.T0057"] = "TA0010",   // LLM Data Leakage → Exfiltration
            ["AML.T0037"] = "TA0009",   // Data from Local System → Collection
            ["AML.T0034"] = "TA0011",   // Cost Harvesting → Impact (replaces retired AML.T0045)
            ["AML.T0043"] = "TA0001",   // Craft Adversarial Data → AI Attack Staging
            ["AML.T0047"] = "TA0000",   // AI-Enabled Product or Service → AI Model Access
            ["AML.T0048"] = "TA0011",   // External Harms → Impact
            ["AML.T0052"] = "TA0004",   // Phishing → Initial Access
            ["AML.T0044"] = "TA0000",   // Full AI Model Access → AI Model Access
            ["AML.T0046"] = "TA0011",   // Spamming AI System with Chaff Data → Impact
            ["AML.T0053"] = "TA0005",   // AI Agent Tool Invocation → Execution
            ["AML.T0010"] = "TA0004",   // AI Supply Chain Compromise → Initial Access
            ["AML.T0020"] = "TA0003",   // Poison Training Data → Resource Development
        };

    // Independently-authored ATLAS tactic ID -> canonical name (verified vs ATLAS.yaml 2026-06-13).
    private static readonly IReadOnlyDictionary<string, string> ExpectedTacticName =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TA0000"] = "AI Model Access",
            ["TA0001"] = "AI Attack Staging",
            ["TA0003"] = "Resource Development",
            ["TA0004"] = "Initial Access",
            ["TA0005"] = "Execution",
            ["TA0007"] = "Defense Evasion",
            ["TA0009"] = "Collection",
            ["TA0010"] = "Exfiltration",
            ["TA0011"] = "Impact",
        };

    public static IEnumerable<object[]> AllTechniques() =>
        MITREATLASReporter.TechniqueCatalog.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(AllTechniques))]
    public void Technique_AssignsExpectedTacticIdAndName(MITREAtlasTechnique technique)
    {
        Assert.True(ExpectedTechniqueTacticId.TryGetValue(technique.Id, out var expectedTacticId),
            $"Technique {technique.Id} is not in the authored expectation map — update the test if this is intentional.");
        Assert.Equal(expectedTacticId, technique.TacticId);

        Assert.True(ExpectedTacticName.TryGetValue(expectedTacticId, out var expectedName),
            $"Tactic {expectedTacticId} has no authored name.");
        Assert.Equal(expectedName, technique.TacticName);
    }

    [Fact]
    public void TacticCatalog_MatchesAuthoredExpectation()
        => Assert.Equal(
            ExpectedTacticName.OrderBy(kv => kv.Key, StringComparer.Ordinal),
            MITREATLASReporter.TacticCatalog.OrderBy(kv => kv.Key, StringComparer.Ordinal));

    [Fact]
    public void EveryTechniqueTacticId_ExistsInTacticCatalog()
    {
        foreach (var technique in MITREATLASReporter.TechniqueCatalog)
        {
            Assert.True(MITREATLASReporter.TacticCatalog.ContainsKey(technique.TacticId),
                $"Technique {technique.Id} references TacticId '{technique.TacticId}' absent from the tactic catalog.");
        }
    }

    [Fact]
    public void Catalog_CoversEveryAuthoredTechnique()
        => Assert.Equal(
            ExpectedTechniqueTacticId.Keys.OrderBy(k => k, StringComparer.Ordinal),
            MITREATLASReporter.TechniqueCatalog.Select(t => t.Id).OrderBy(k => k, StringComparer.Ordinal));

    [Fact]
    public void Catalog_ContainsEveryAtlasIdTaggedByAnAttack()
    {
        // #9 anti-drift: every AML.T id an attack advertises MUST exist in the technique catalog. Otherwise that
        // attack's results are silently dropped from the ATLAS report rows and composite — exactly the bug where
        // a compromised SupplyChain (T0010) / DataPoisoning (T0020) probe could not fail the MITRE benchmark.
        var catalogIds = MITREATLASReporter.TechniqueCatalog.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = Attack.All
            .SelectMany(a => a.MitreAtlasIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => !catalogIds.Contains(id))
            .ToList();

        Assert.True(missing.Count == 0,
            $"Attack-tagged ATLAS IDs missing from MITREATLASReporter.TechniqueCatalog: {string.Join(", ", missing)}");
    }
}
