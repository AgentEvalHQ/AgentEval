// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Reporting/Compliance/MITREATLASInvariantTests.cs
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Tests.RedTeam.Reporting.Compliance;

public sealed class MITREATLASInvariantTests
{
    // Independently-authored expectation of each ATLAS technique's tactic assignment.
    // Pinning the technique -> tactic-ID edge against a separate literal makes the
    // invariant catch real drift (a mis-edited TacticId) rather than a self-projection.
    private static readonly IReadOnlyDictionary<string, string> ExpectedTechniqueTacticId =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AML.T0051"] = "TA0001",
            ["AML.T0054"] = "TA0005",
            ["AML.T0043"] = "TA0040",
            ["AML.T0024"] = "TA0042",
            ["AML.T0037"] = "TA0009",
            ["AML.T0045"] = "TA0001",
            ["AML.T0047"] = "TA0009",
            ["AML.T0048"] = "TA0010",
            ["AML.T0052"] = "TA0001",
            ["AML.T0044"] = "TA0010",
            ["AML.T0046"] = "TA0003",
            ["AML.T0053"] = "TA0005",
        };

    // Independently-authored ATLAS tactic ID -> canonical name.
    private static readonly IReadOnlyDictionary<string, string> ExpectedTacticName =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TA0001"] = "Initial Access",
            ["TA0003"] = "Persistence",
            ["TA0005"] = "Defense Evasion",
            ["TA0009"] = "Collection",
            ["TA0010"] = "Exfiltration",
            ["TA0040"] = "ML Attack Staging",
            ["TA0042"] = "Resource Development",
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
}
