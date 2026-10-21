// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Tracing;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// An endpoint whose chat boundary recorded no model response has nothing to reconcile, so its fidelity of 1.0 is not
/// a measurement and cannot win; an endpoint that reported no token usage does not win the token tiebreak on 0 (T13-11).
/// </summary>
public class AutoAuditNotMeasuredTests
{
    private static AgentTrace AgentBoundary()
    {
        var trace = new AgentTrace();
        trace.Entries.Add(new TraceEntry { Type = TraceEntryType.Response, Index = 0 });
        return trace;
    }

    [Fact]
    public void Evaluate_WithAnEmptyChatBoundary_DoesNotMeasureFidelity()
    {
        // The recorder was not wired: no chat-boundary response, so nothing disagrees and fidelity reads 1.0.
        var result = AutoAuditRunner.Evaluate("ep", AgentBoundary(), new AgentTrace(), completed: true);

        Assert.False(result.FidelityMeasured);
        Assert.Equal(1.0, result.FidelityScore, 6);
    }

    [Fact]
    public void Evaluate_WithChatBoundaryResponses_MeasuresFidelity_AndSeesTheirUsage()
    {
        var chat = new AgentTrace();
        chat.Entries.Add(TraceEntry.ForChatResponse(0, "inv", "ok", 5, new TraceTokenUsage { PromptTokens = 20, CompletionTokens = 10 }, null, "stop", null));

        var result = AutoAuditRunner.Evaluate("ep", AgentBoundary(), chat, completed: true);

        Assert.True(result.FidelityMeasured);
        Assert.True(result.TokensReported);
    }

    [Fact]
    public void Evaluate_WhenAChatResponseReportedNoUsage_SaysTheTokensAreNotReported()
    {
        var chat = new AgentTrace();
        chat.Entries.Add(TraceEntry.ForChatResponse(0, "inv", "ok", 5, new TraceTokenUsage { PromptTokens = 20, CompletionTokens = 10 }, null, "stop", null));
        chat.Entries.Add(TraceEntry.ForChatResponse(1, "inv", "ok", 5, null, null, "stop", null));

        var result = AutoAuditRunner.Evaluate("ep", AgentBoundary(), chat, completed: true);

        Assert.False(result.TokensReported);
        Assert.Equal(30, result.TotalTokens);   // what was reported, not the endpoint's cost
    }

    [Fact]
    public void AnEndpointWithNoChatBoundaryTruth_DoesNotWin_OverAMeasuredOne()
    {
        var report = AutoAuditRunner.Compare(new[]
        {
            new AutoAuditEndpointResult("no-recorder", 1.00, 0, 0, 0, 5, true, Array.Empty<string>()) { FidelityMeasured = false },
            new AutoAuditEndpointResult("measured", 0.80, 0, 100, 50, 10, true, new[] { "hidden_retries ×1" }),
        });

        Assert.Equal("measured", report.Winner!.Endpoint);
        Assert.Equal("measured", report.Ranking[0].Endpoint);

        var markdown = report.ToMarkdown();
        var row = markdown.Split('\n').Single(l => l.Contains("no-recorder", StringComparison.Ordinal));
        Assert.Contains("not measured", row);
        Assert.DoesNotContain("100%", row);
    }

    [Fact]
    public void OnlyEndpointsWithNoChatBoundaryTruth_HaveNoWinner()
    {
        var report = AutoAuditRunner.Compare(new[]
        {
            new AutoAuditEndpointResult("no-recorder", 1.00, 0, 0, 0, 5, true, Array.Empty<string>()) { FidelityMeasured = false },
        });

        Assert.Null(report.Winner);
    }

    [Fact]
    public void AnEndpointThatReportedNoUsage_DoesNotWinTheTokenTiebreak_OnZero()
    {
        var report = AutoAuditRunner.Compare(new[]
        {
            new AutoAuditEndpointResult("no-usage", 1.00, 0, 0, 0, 5, true, Array.Empty<string>()) { TokensReported = false },
            new AutoAuditEndpointResult("reported", 1.00, 0, 100, 50, 10, true, Array.Empty<string>()),
        });

        Assert.Equal("reported", report.Ranking[0].Endpoint);
        Assert.Contains("usage not reported", report.ToMarkdown());
    }
}
