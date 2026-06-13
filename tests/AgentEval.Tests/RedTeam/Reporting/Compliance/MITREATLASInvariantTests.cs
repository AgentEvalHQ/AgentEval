// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Reporting/Compliance/MITREATLASInvariantTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Tests.RedTeam.Reporting.Compliance;

public sealed class MITREATLASInvariantTests
{
    // Independently-authored expectation of each ATLAS technique's tactic assignment.
    // Pinning the technique -> tactic-ID edge against a separate literal makes the
    // invariant catch real drift (a mis-edited TacticId) rather than a self-projection.
    // RC-5/T4-2: remapped verbatim against the ATLAS matrix — SPE/PII extraction → T0056/T0057,
    // T0024 retired, T0043/T0047/T0048 demoted to out-of-band (NotApplicable).
    private static readonly IReadOnlyDictionary<string, string> ExpectedTechniqueTacticId =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AML.T0051"] = "TA0001",
            ["AML.T0054"] = "TA0004",
            ["AML.T0056"] = "TA0007",
            ["AML.T0057"] = "TA0010",
            ["AML.T0037"] = "TA0009",
            ["AML.T0045"] = "TA0000",
            ["AML.T0043"] = "TA0040",
            ["AML.T0047"] = "TA0009",
            ["AML.T0048"] = "TA0010",
            ["AML.T0052"] = "TA0001",
            ["AML.T0044"] = "TA0010",
            ["AML.T0046"] = "TA0003",
            ["AML.T0053"] = "TA0005",
            ["AML.T0010"] = "TA0001",   // #9: SupplyChain (LLM03) — was silently dropped from the catalog
            ["AML.T0020"] = "TA0003",   // #9: DataPoisoning (LLM04) — was silently dropped from the catalog
        };

    // Independently-authored ATLAS tactic ID -> canonical name.
    private static readonly IReadOnlyDictionary<string, string> ExpectedTacticName =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TA0000"] = "ML Model Access",
            ["TA0001"] = "Initial Access",
            ["TA0003"] = "Resource Development",
            ["TA0004"] = "Privilege Escalation",
            ["TA0005"] = "Defense Evasion",
            ["TA0007"] = "Discovery",
            ["TA0009"] = "Collection",
            ["TA0010"] = "Exfiltration",
            ["TA0040"] = "ML Attack Staging",
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
