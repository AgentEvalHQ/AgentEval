// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Tracing;

namespace AgentEval.Tests.Tracing;

/// <summary>
/// Glass Box Phase 0 (T0.1/T0.2): the additive AgentTrace v1.1 schema and v1.0 backward compatibility.
/// Verifies the new fields/enum/factories AND that existing v1.0 traces load and round-trip with no
/// spurious v1.1 keys (the additive-only guarantee).
/// </summary>
public class AgentTraceV11SchemaTests
{
    /// <summary>A realistic v1.0 trace exactly as <c>TraceRecordingAgent</c> emitted before Glass Box.</summary>
    private const string V10TraceJson = """
        {
          "version": "1.0",
          "traceName": "legacy_weather",
          "capturedAt": "2026-01-01T00:00:00+00:00",
          "agentName": "weather",
          "modelId": "gpt-4o-mini",
          "entries": [
            { "type": "Request", "index": 0, "timestamp": "2026-01-01T00:00:00+00:00", "prompt": "What's the weather?" },
            { "type": "Response", "index": 0, "timestamp": "2026-01-01T00:00:01+00:00", "text": "Sunny", "durationMs": 1000,
              "tokenUsage": { "promptTokens": 10, "completionTokens": 5 } }
          ],
          "performance": { "totalDurationMs": 1000, "totalPromptTokens": 10, "totalCompletionTokens": 5, "callCount": 1, "toolCallCount": 0 }
        }
        """;

    [Fact]
    public void NewAgentTrace_DefaultsToVersion11()
    {
        // Arrange / Act
        var trace = new AgentTrace();

        // Assert
        Assert.Equal("1.1", trace.Version);
    }

    [Fact]
    public void NewTraceEntry_HasNullScope_AndEffectiveScopeIsAgentInvocation()
    {
        // Arrange / Act
        var entry = new TraceEntry { Type = TraceEntryType.Request, Index = 0, Prompt = "hi" };

        // Assert — absent scope ⇒ v1.0 semantics
        Assert.Null(entry.Scope);
        Assert.Equal(TraceEntryScope.AgentInvocation, entry.EffectiveScope);
    }

    [Fact]
    public async Task AgentBoundaryEntry_SerializesWithoutScopeKey_PreservingV10Shape()
    {
        // Arrange — an entry with no v1.1 fields set (the v1.0 agent-boundary shape)
        var trace = new AgentTrace { Version = "1.0" };
        trace.Entries.Add(new TraceEntry { Type = TraceEntryType.Request, Index = 0, Prompt = "hi" });

        // Act
        var json = await TraceSerializer.SerializeToStringAsync(trace);

        // Assert — nullable v1.1 fields are omitted (WhenWritingNull), so existing traces stay byte-shape-identical
        Assert.DoesNotContain("scope", json);
        Assert.DoesNotContain("correlationId", json);
        Assert.DoesNotContain("systemPrompt", json);
        Assert.DoesNotContain("toolDefinitions", json);
        Assert.DoesNotContain("finishReason", json);
        Assert.DoesNotContain("providerMetadata", json);
    }

    [Fact]
    public async Task LoadV10Trace_DeserializesWithNullScope_AndEffectiveScopeIsAgentInvocation()
    {
        // Arrange / Act
        var trace = await TraceSerializer.DeserializeFromStringAsync(V10TraceJson);

        // Assert
        Assert.Equal("1.0", trace.Version);
        Assert.Equal(2, trace.Entries.Count);
        Assert.All(trace.Entries, e => Assert.Null(e.Scope));
        Assert.All(trace.Entries, e => Assert.Equal(TraceEntryScope.AgentInvocation, e.EffectiveScope));
        // Existing fields still deserialize correctly.
        Assert.Equal("What's the weather?", trace.Entries[0].Prompt);
        Assert.Equal("Sunny", trace.Entries[1].Text);
        Assert.Equal(10, trace.Entries[1].TokenUsage!.PromptTokens);
    }

    [Fact]
    public async Task V10Trace_RoundTrip_PreservesVersionAndEmitsNoV11Keys()
    {
        // Arrange
        var loaded = await TraceSerializer.DeserializeFromStringAsync(V10TraceJson);

        // Act
        var reserialized = await TraceSerializer.SerializeToStringAsync(loaded);
        var roundTripped = await TraceSerializer.DeserializeFromStringAsync(reserialized);

        // Assert — version preserved, no v1.1 keys leaked, structure stable
        Assert.Equal("1.0", roundTripped.Version);
        Assert.DoesNotContain("scope", reserialized);
        Assert.DoesNotContain("correlationId", reserialized);
        Assert.Equal(loaded.Entries.Count, roundTripped.Entries.Count);
        Assert.Equal(loaded.Entries[1].Text, roundTripped.Entries[1].Text);
    }

    [Fact]
    public async Task ChatTurnEntry_SerializesScopeAsString_NotNumeric()
    {
        // Arrange — a v1.1 ChatTurn entry built via the factory
        var trace = new AgentTrace();
        trace.Entries.Add(TraceEntry.ForChatResponse(
            index: 0, correlationId: "inv-1", text: "answer", durationMs: 12,
            usage: null, toolCalls: null, finishReason: "stop", providerMetadata: null));

        // Act
        var json = await TraceSerializer.SerializeToStringAsync(trace);

        // Assert — JsonStringEnumConverter ⇒ "ChatTurn", never 1
        Assert.Contains("\"scope\": \"ChatTurn\"", json);
        Assert.DoesNotContain("\"scope\": 1", json);
    }

    [Fact]
    public void ForChatRequest_PopulatesChatTurnRequestFields()
    {
        // Arrange / Act
        var entry = TraceEntry.ForChatRequest(
            index: 3, correlationId: "inv-9", systemPrompt: "be helpful", promptText: "hello",
            toolDefinitions: new List<TraceToolDefinition> { new() { Name = "Search" } },
            requestOptions: new Dictionary<string, object?> { ["temperature"] = 0.2 });

        // Assert
        Assert.Equal(TraceEntryType.Request, entry.Type);
        Assert.Equal(TraceEntryScope.ChatTurn, entry.Scope);
        Assert.Equal(3, entry.Index);
        Assert.Equal("inv-9", entry.CorrelationId);
        Assert.Equal("be helpful", entry.SystemPrompt);
        Assert.Equal("hello", entry.Prompt);
        Assert.Equal("Search", entry.ToolDefinitions!.Single().Name);
        Assert.Equal(0.2, entry.RequestOptions!["temperature"]);
    }

    [Fact]
    public void ForToolExecution_ProducesToolCallScopedEntryWithCorrelation()
    {
        // Arrange / Act
        var entry = TraceEntry.ForToolExecution(
            index: 7, correlationId: "inv-2", toolName: "SearchFlights", arguments: "{\"from\":\"ZRH\"}",
            result: "[...]", durationMs: 42, succeeded: true, error: null);

        // Assert
        Assert.Equal(TraceEntryType.ToolCall, entry.Type);
        Assert.Equal(TraceEntryScope.ToolExecution, entry.Scope);
        Assert.Equal("inv-2", entry.CorrelationId);
        var call = Assert.Single(entry.ToolCalls!);
        Assert.Equal("SearchFlights", call.Name);
        Assert.True(call.Succeeded);
        Assert.Equal(42, call.DurationMs);
    }

    [Fact]
    public void ForChatError_RecordsErrorOnResponseScopedEntry()
    {
        // Arrange / Act
        var entry = TraceEntry.ForChatError(index: 1, correlationId: "inv-3", ex: new InvalidOperationException("boom"), durationMs: 5);

        // Assert
        Assert.Equal(TraceEntryType.Response, entry.Type);
        Assert.Equal(TraceEntryScope.ChatTurn, entry.Scope);
        Assert.Equal("InvalidOperationException", entry.Error!.Type);
        Assert.Equal("boom", entry.Error.Message);
    }
}
