// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Metrics;
using AgentEval.Metrics.ResponsibleAI;
using AgentEval.Models;
using AgentEval.Testing;

namespace AgentEval.Tests.Metrics;

/// <summary>
/// The tool-results section the claim-support judges (misinformation, groundedness) are sent: which calls it lists,
/// how a value reads, and how it is bounded.
/// </summary>
public class ToolResultEvidenceTests
{
    [Fact]
    public void Render_NoUsage_OrNoCallWithAResult_IsEmpty()
    {
        var pending = new ToolUsageReport();
        pending.AddCall(new ToolCallRecord { Name = "SearchFlights", CallId = "c1", Order = 1 });

        Assert.Equal("", ToolResultEvidence.Render(null));
        Assert.Equal("", ToolResultEvidence.Render(new ToolUsageReport()));
        Assert.Equal("", ToolResultEvidence.Render(pending));
    }

    [Fact]
    public void Render_ListsOnlyTheCallsThatReturnedSomething_InOrder()
    {
        var usage = new ToolUsageReport();
        usage.AddCall(new ToolCallRecord { Name = "First", CallId = "a", Order = 1, Result = "one" });
        usage.AddCall(new ToolCallRecord { Name = "Pending", CallId = "b", Order = 2 });
        usage.AddCall(new ToolCallRecord { Name = "Failed", CallId = "c", Order = 3, Exception = new TimeoutException("too slow") });

        var section = ToolResultEvidence.Render(usage);

        Assert.StartsWith(ToolResultEvidence.Header, section);
        Assert.Contains("\n1. {\"name\":\"First\",\"result\":\"one\"}", section);
        Assert.Contains("\n2. {\"name\":\"Failed\",\"error\":\"too slow\"}", section);
        Assert.DoesNotContain("Pending", section);
    }

    [Fact]
    public void Render_AJsonStringResult_ReadsAsText_AndStructuredResultsAsJson()
    {
        using var text = JsonDocument.Parse("\"Paris: 18°C\"");
        using var structured = JsonDocument.Parse("""{"temp":18,"sky":"sunny"}""");
        var usage = new ToolUsageReport();
        usage.AddCall(new ToolCallRecord { Name = "A", CallId = "a", Order = 1, Result = text.RootElement.Clone() });
        usage.AddCall(new ToolCallRecord { Name = "B", CallId = "b", Order = 2, Result = structured.RootElement.Clone() });

        var section = ToolResultEvidence.Render(usage);

        Assert.Contains("\"result\":\"Paris: 18°C\"", section);
        Assert.Contains("\"result\":{\"temp\":18,\"sky\":\"sunny\"}", section);
    }

    [Fact]
    public void Render_ALongResult_IsCut_WithTheCutStated()
    {
        var usage = new ToolUsageReport();
        usage.AddCall(new ToolCallRecord
        {
            Name = "Dump", CallId = "a", Order = 1, Result = new string('x', ToolResultEvidence.ValueCharacterLimit + 500),
        });

        var section = ToolResultEvidence.Render(usage);

        Assert.Contains("…[cut: 500 more characters]", section);
        Assert.DoesNotContain(new string('x', ToolResultEvidence.ValueCharacterLimit + 1), section);
    }

    [Fact]
    public void Render_PastTheSectionLimit_NamesTheCallsItDoesNotShow()
    {
        var usage = new ToolUsageReport();
        for (var i = 1; i <= 12; i++)
        {
            usage.AddCall(new ToolCallRecord
            {
                Name = $"Read{i}", CallId = $"c{i}", Order = i, Result = new string('y', ToolResultEvidence.ValueCharacterLimit),
            });
        }

        var section = ToolResultEvidence.Render(usage);

        Assert.True(section.Length < ToolResultEvidence.SectionCharacterLimit + ToolResultEvidence.Header.Length + 400);
        Assert.Matches(@"…\[\d+ more tool result\(s\) not shown", section);
        Assert.Contains("Read12", section);   // named, though its result is not sent
    }

    [Fact]
    public async Task TheDigest_DoesNotDependOnWhatTheToolsReturned()
    {
        // The digest fingerprints the instrument, never the case: two runs whose tools returned different data were
        // graded by the same instrument.
        async Task<MetricResult> Run(string toolResult)
        {
            var usage = new ToolUsageReport();
            usage.AddCall(new ToolCallRecord { Name = "Search", CallId = "c1", Order = 1, Result = toolResult });
            return await new MisinformationMetric(new FakeChatClient("""{"score": 90}"""))
                .EvaluateAsync(new EvaluationContext { Input = "q", Output = "a", ToolUsage = usage });
        }

        var first = await Run("first private result");
        var second = await Run("second private result");

        Assert.Equal(first.RubricDigest, second.RubricDigest);
    }
}
