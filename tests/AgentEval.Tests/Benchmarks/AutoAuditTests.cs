// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Tracing;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// Glass Box Phase 9: the auto-audit comparison engine (<see cref="AutoAuditRunner"/> / <see cref="AutoAuditReport"/>)
/// and the offline demo (<see cref="AutoAuditDemo"/>).
/// </summary>
public class AutoAuditTests
{
    [Fact]
    public void Ranking_OrdersByCompletedThenFidelityThenGateBlocksThenTokens()
    {
        // Arrange
        var report = AutoAuditRunner.Compare(new[]
        {
            new AutoAuditEndpointResult("low-fidelity", 0.70, 0, 100, 50, 10, true, new[] { "hidden_retries ×1" }),
            new AutoAuditEndpointResult("clean", 1.00, 0, 200, 100, 20, true, Array.Empty<string>()),
            new AutoAuditEndpointResult("failed", 1.00, 0, 10, 5, 1, false, Array.Empty<string>()),
        });

        // Act
        var ranking = report.Ranking;

        // Assert — completed + highest fidelity first; the failed run sinks despite a high score
        Assert.Equal("clean", ranking[0].Endpoint);
        Assert.Equal("low-fidelity", ranking[1].Endpoint);
        Assert.Equal("failed", ranking[2].Endpoint);
        Assert.Equal("clean", report.Winner!.Endpoint);
    }

    [Fact]
    public void Evaluate_ComputesFidelityGateBlocksAndTokensFromTraces()
    {
        // Arrange — chat boundary saw a retry the agent boundary didn't report; one gate Block recorded.
        // Matching token totals so token_under_reporting does not fire (isolates the hidden retry).
        var agentBoundary = new AgentTrace
        {
            Performance = new TracePerformance { TotalPromptTokens = 40, TotalCompletionTokens = 20 },
        };
        agentBoundary.Entries.Add(new TraceEntry { Type = TraceEntryType.Response, Index = 0, ToolCalls = new() { new TraceToolCall { Name = "Lookup" } } });

        var chatBoundary = new AgentTrace();
        chatBoundary.Entries.Add(TraceEntry.ForChatResponse(0, "inv", "…", 5, new TraceTokenUsage { PromptTokens = 20, CompletionTokens = 10 }, new() { new TraceToolCall { Name = "Lookup" } }, "tool_calls", null));
        chatBoundary.Entries.Add(TraceEntry.ForChatResponse(1, "inv", "…", 7, new TraceTokenUsage { PromptTokens = 20, CompletionTokens = 10 }, new() { new TraceToolCall { Name = "Lookup" } }, "tool_calls", null));
        chatBoundary.Metadata = new Dictionary<string, object>
        {
            ["gate.post.1.pii-detection"] = new Dictionary<string, object?> { ["action"] = "Block", ["reason"] = "PII" },
        };

        // Act
        var result = AutoAuditRunner.Evaluate("ep", agentBoundary, chatBoundary);

        // Assert
        Assert.Equal(0.90, result.FidelityScore, 6);       // one hidden retry (High) → 0.90
        Assert.Equal(1, result.GateBlocks);
        Assert.Equal(40, result.PromptTokens);
        Assert.Equal(20, result.CompletionTokens);
        Assert.Equal(12, result.LatencyMs);
        Assert.Contains(result.TopDiscrepancies, d => d.StartsWith("hidden_retries", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OfflineDemo_RanksCleanAboveRetryAboveLeak_AndRendersMarkdown()
    {
        // Act
        var report = await AutoAuditDemo.BuildOfflineReportAsync();

        // Assert — three endpoints, the clean one wins, the PII-leaking one has a gate block
        Assert.Equal(3, report.Results.Count);
        Assert.Equal("Azure-GPT-4o-mini", report.Winner!.Endpoint);
        Assert.Equal(1.0, report.Winner.FidelityScore, 6);

        var leak = report.Results.Single(r => r.Endpoint == "DeepSeek-V3");
        Assert.Equal(1, leak.GateBlocks);                  // SSN caught by the PII post-gate
        Assert.True(leak.FidelityScore < 1.0);             // suppressed content_filter

        var retry = report.Results.Single(r => r.Endpoint == "Ollama-Llama3.1");
        Assert.Equal(0.90, retry.FidelityScore, 6);        // one hidden retry

        var markdown = report.ToMarkdown();
        Assert.Contains("Auto-Audit", markdown);
        Assert.Contains("Winner: Azure-GPT-4o-mini", markdown);
    }
}
