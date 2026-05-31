// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Tracing;

namespace AgentEval.Tests.Tracing;

/// <summary>
/// Glass Box Phase 6: <see cref="GlassBoxEvidence"/> — the regulation-neutral, additive compliance-evidence
/// summary extracted from a v1.1 trace (never an input to a compliance score).
/// </summary>
public class GlassBoxEvidenceTests
{
    [Fact]
    public void FromTrace_V10AgentTrace_ReturnsNull()
    {
        // Arrange — a pure v1.0 agent-boundary trace has no Glass Box detail
        var trace = new AgentTrace { Version = "1.0" };
        trace.Entries.Add(new TraceEntry { Type = TraceEntryType.Request, Index = 0, Prompt = "hi" });
        trace.Entries.Add(new TraceEntry { Type = TraceEntryType.Response, Index = 0, Text = "ok" });

        // Act / Assert — null so compliance packs omit the section for v1.0 runs
        Assert.Null(GlassBoxEvidence.FromTrace(trace));
    }

    [Fact]
    public void FromTrace_SummarisesChatTurnsToolsTokensAndSuppressedFinishes()
    {
        // Arrange
        var trace = new AgentTrace();
        trace.Entries.Add(TraceEntry.ForChatResponse(0, "inv", "a", 5, new TraceTokenUsage { PromptTokens = 10, CompletionTokens = 4 }, null, "stop", null));
        trace.Entries.Add(TraceEntry.ForChatResponse(1, "inv", "b", 5, new TraceTokenUsage { PromptTokens = 20, CompletionTokens = 6 }, null, "content_filter", null));
        trace.Entries.Add(TraceEntry.ForToolExecution(2, "inv", "Lookup", "{}", "ok", 3, true, null));

        // Act
        var evidence = GlassBoxEvidence.FromTrace(trace)!;

        // Assert
        Assert.Equal(2, evidence.ChatTurnCount);
        Assert.Equal(1, evidence.ToolExecutionCount);
        Assert.Equal(1, evidence.SuppressedFinishTurns);   // the content_filter turn
        Assert.Equal(30, evidence.TotalPromptTokens);
        Assert.Equal(10, evidence.TotalCompletionTokens);
    }

    [Fact]
    public void FromTrace_CountsGateBlocksAndDistinctPolicies()
    {
        // Arrange — a trace with gate verdicts recorded in Metadata (as EvalGatingChatClient writes them)
        var trace = new AgentTrace();
        trace.Entries.Add(TraceEntry.ForChatResponse(0, "inv", "a", 5, null, null, "stop", null));
        trace.Metadata = new Dictionary<string, object>
        {
            ["gate.pre.1.prompt-injection"] = new Dictionary<string, object?> { ["action"] = "Allow" },
            ["gate.post.2.pii-detection"] = new Dictionary<string, object?> { ["action"] = "Block" },
            ["gate.post.3.pii-detection"] = new Dictionary<string, object?> { ["action"] = "Block" },
        };

        // Act
        var evidence = GlassBoxEvidence.FromTrace(trace)!;

        // Assert — two Blocks, one distinct blocking policy
        Assert.Equal(2, evidence.GateBlockCount);
        Assert.Equal(new[] { "pii-detection" }, evidence.GateBlockPolicies);
    }

    [Fact]
    public async Task FromTrace_CountsGateBlocks_AfterSerializeReloadRoundTrip()
    {
        // Arrange — gate verdicts recorded in Metadata, then persisted and reloaded (compliance reads .trace.json,
        // where Metadata values deserialize to JsonElement, not the in-memory dictionary shape).
        var trace = new AgentTrace();
        trace.Entries.Add(TraceEntry.ForChatResponse(0, "inv", "a", 5, null, null, "stop", null));
        trace.Metadata = new Dictionary<string, object>
        {
            ["gate.pre.1.prompt-injection"] = new Dictionary<string, object?> { ["action"] = "Allow" },
            ["gate.post.2.pii-detection"] = new Dictionary<string, object?> { ["action"] = "Block" },
            ["gate.post.3.pii-detection"] = new Dictionary<string, object?> { ["action"] = "Block" },
        };

        // Act — round-trip through the canonical trace serializer, then extract
        var reloaded = await TraceSerializer.DeserializeFromStringAsync(await TraceSerializer.SerializeToStringAsync(trace));
        var evidence = GlassBoxEvidence.FromTrace(reloaded)!;

        // Assert — the reloaded trace reports the SAME block count as the in-memory one (no silent zero)
        Assert.Equal(2, evidence.GateBlockCount);
        Assert.Equal(new[] { "pii-detection" }, evidence.GateBlockPolicies);
    }
}
