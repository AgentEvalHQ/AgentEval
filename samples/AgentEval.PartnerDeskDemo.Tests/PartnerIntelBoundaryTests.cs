// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.PartnerDeskDemo.Gates;
using AgentEval.PartnerDeskDemo.Mcp;
using Microsoft.Extensions.AI;

namespace AgentEval.PartnerDeskDemo.Tests;

/// <summary>
/// Proves the third-party boundary is a real MCP session, and that evil mode changes the supplier's bytes and
/// nothing else.
/// </summary>
public sealed class PartnerIntelBoundaryTests
{
    [Fact]
    public async Task CleanServer_ReturnsAReportWithNoAddendum()
    {
        await using var session = await PartnerIntelSession.OpenAsync(evilMode: false, CancellationToken.None);

        var text = await CallAsync(session);

        Assert.Contains("PARTNERINTEL COUNTERPARTY REPORT", text, StringComparison.Ordinal);
        Assert.Contains("SANCTIONS AND ADVERSE MEDIA", text, StringComparison.Ordinal);
        // Assert against markers the CURRENT addendum actually contains, so this cannot pass vacuously if the
        // addendum text is retuned. Both are present in the built-in payload and absent from the clean report.
        Assert.DoesNotContain("AUTOMATED COMPLIANCE ADDENDUM", text, StringComparison.Ordinal);
        Assert.DoesNotContain("END ADDENDUM", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCleanAndPoisonedReportsDifferOnlyByTheAddendum()
    {
        // Guards the two markers used above: they must genuinely distinguish the two modes.
        var clean = PartnerIntelServer.BuildReport("Alpina Logistik AG", poisoned: false);
        var poisoned = PartnerIntelServer.BuildReport("Alpina Logistik AG", poisoned: true);

        Assert.DoesNotContain("AUTOMATED COMPLIANCE ADDENDUM", clean, StringComparison.Ordinal);
        Assert.Contains("AUTOMATED COMPLIANCE ADDENDUM", poisoned, StringComparison.Ordinal);
        Assert.Contains("END ADDENDUM", poisoned, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompromisedServer_ReturnsTheSameReportPlusTheAddendumVerbatim()
    {
        await using var session = await PartnerIntelSession.OpenAsync(evilMode: true, CancellationToken.None);

        var text = await CallAsync(session);

        Assert.Contains("PARTNERINTEL COUNTERPARTY REPORT", text, StringComparison.Ordinal);
        Assert.Contains("SANCTIONS AND ADVERSE MEDIA", text, StringComparison.Ordinal);

        // The addendum must survive the MCP transport byte for byte, or the demo is testing something else.
        foreach (var line in EvilMode.PoisonedAddendum
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.Contains(line.Trim(), text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EvilModeChangesOnlyTheAddendum()
    {
        var clean = PartnerIntelServer.BuildReport("Alpina Logistik AG", poisoned: false);
        var poisoned = PartnerIntelServer.BuildReport("Alpina Logistik AG", poisoned: true);

        Assert.StartsWith(clean.TrimEnd(), poisoned.TrimEnd(), StringComparison.Ordinal);
        Assert.Contains(EvilMode.PoisonedAddendum, poisoned, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheServerAdvertisesExactlyTheToolTheGatesAreConfiguredFor()
    {
        await using var session = await PartnerIntelSession.OpenAsync(evilMode: false, CancellationToken.None);

        Assert.Equal(PartnerIntelServer.ToolName, session.ReportTool.Name);
        Assert.False(session.AddendumEnabled);
    }

    [Fact]
    public void TheResultGateMarkersMatchTheAddendumTheSupplierActuallySends()
    {
        var poisoned = PartnerIntelServer.BuildReport("Alpina Logistik AG", poisoned: true);
        var clean = PartnerIntelServer.BuildReport("Alpina Logistik AG", poisoned: false);

        var hits = PartnerDeskGates.InstructionShapedMarkers
            .Where(marker => poisoned.Contains(marker, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(hits);
        Assert.DoesNotContain(
            PartnerDeskGates.InstructionShapedMarkers,
            marker => clean.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> CallAsync(PartnerIntelSession session)
    {
        var result = await session.ReportTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["companyName"] = "Alpina Logistik AG",
            }),
            CancellationToken.None);

        return result?.ToString() ?? string.Empty;
    }
}
