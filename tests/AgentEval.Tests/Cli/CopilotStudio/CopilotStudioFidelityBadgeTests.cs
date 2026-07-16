// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.CopilotStudio;
using AgentEval.RedTeam;
using AgentTrace = AgentEval.Tracing.AgentTrace;
using Xunit;

namespace AgentEval.Tests.Cli.CopilotStudio;

/// <summary>
/// P6 item C1 (narrow cut — <c>strategy/CopilotStudio/Copilot-Studio-P6-Connector-Health-and-Resilience-Design.md</c>
/// §1C): <c>CopilotStudioRedTeamTarget.WritePostScanSummary</c> now renders the aggregate
/// <see cref="EvidenceFidelity"/> breakdown. A snapshot-style test on the target's own summary output — does
/// NOT touch the shared RedTeam report renderers (that's C2, explicitly out of scope here).
/// </summary>
public class CopilotStudioFidelityBadgeTests
{
    [Fact]
    public void WritePostScanSummary_AllVerbalProbes_RendersFidelityLine()
    {
        var result = ResultWith((EvidenceFidelity.Verbal, "V-1"), (EvidenceFidelity.Verbal, "V-2"));
        var writer = new StringWriter();

        new CopilotStudioRedTeamTarget().WritePostScanSummary(result, new AgentTrace(), writer);

        var output = writer.ToString();
        Assert.Contains("Evidence fidelity", output);
        Assert.Contains("Verbal 2/2", output);
        Assert.Contains("IntentToAct 0/2", output);
        Assert.Contains("Behavioral 0/2", output);
        Assert.Contains("text-only", output);
    }

    [Fact]
    public void WritePostScanSummary_MixedFidelities_CountsEachBucketCorrectly()
    {
        var result = ResultWith(
            (EvidenceFidelity.Verbal, "P-1"),
            (EvidenceFidelity.Verbal, "P-2"),
            (EvidenceFidelity.IntentToAct, "P-3"));
        var writer = new StringWriter();

        new CopilotStudioRedTeamTarget().WritePostScanSummary(result, new AgentTrace(), writer);

        var output = writer.ToString();
        Assert.Contains("Verbal 2/3", output);
        Assert.Contains("IntentToAct 1/3", output);
        Assert.Contains("Behavioral 0/3", output);
    }

    [Fact]
    public void WritePostScanSummary_NoProbes_WritesNothing()
    {
        var result = ResultWith();
        var writer = new StringWriter();

        new CopilotStudioRedTeamTarget().WritePostScanSummary(result, new AgentTrace(), writer);

        Assert.Equal(string.Empty, writer.ToString());
    }

    private static RedTeamResult ResultWith(params (EvidenceFidelity Fidelity, string Id)[] probes)
    {
        var probeResults = probes
            .Select(p => new ProbeResult
            {
                ProbeId = p.Id,
                Prompt = "p",
                Response = "r",
                Outcome = EvaluationOutcome.Resisted,
                Reason = "test",
                Fidelity = p.Fidelity,
            })
            .ToList();

        return new RedTeamResult
        {
            AgentName = "TestAgent",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromSeconds(1),
            TotalProbes = probeResults.Count,
            ResistedProbes = probeResults.Count,
            SucceededProbes = 0,
            InconclusiveProbes = 0,
            AttackResults = probeResults.Count == 0
                ? []
                :
                [
                    new AttackResult
                    {
                        AttackName = "PromptInjection",
                        AttackDisplayName = "Prompt Injection",
                        OwaspId = "LLM01",
                        MitreAtlasIds = ["AML.T0051"],
                        Severity = Severity.Medium,
                        ResistedCount = probeResults.Count,
                        SucceededCount = 0,
                        InconclusiveCount = 0,
                        ProbeResults = probeResults,
                    }
                ],
        };
    }
}
