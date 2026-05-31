// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;

using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Tests.Tracing;

/// <summary>
/// Glass Box: <see cref="AgentBoundaryTraceBuilder"/> reconstructs the framework's self-report from a run's
/// final messages, and reconciles cleanly against the chat-boundary trace. These tests pin the load-bearing
/// invariant behind the MAF-vs-Glass Box comparison samples: byte-identical tool-call serialization (no
/// fabricated argument_drift) and the empirical fact that MEAI's function-invocation loop preserves tool calls
/// and SUMS token usage — so on a clean run the framework AGREES on totals (it hides detail, it does not lie).
/// </summary>
public class AgentBoundaryTraceBuilderTests
{
    private static string SearchFlights(string to) => $"AA-{to}";

    private static string SearchFlightsMulti(string to, string from, string date) => $"{from}->{to}@{date}";

    private static IList<ChatMessage> UserSays(string text) => new List<ChatMessage> { new(ChatRole.User, text) };

    [Fact]
    public void FromAgentResponse_ExtractsToolCallsTokensAndFinishReason()
    {
        // Arrange — a final message list carrying a tool call, plus aggregate usage
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("c1", "SearchFlights", new Dictionary<string, object?> { ["to"] = "NRT" }),
            }),
            new(ChatRole.Assistant, "Booked AA-NRT."),
        };

        // Act
        var trace = AgentBoundaryTraceBuilder.FromAgentResponse(
            messages, new UsageDetails { InputTokenCount = 30, OutputTokenCount = 12 }, "stop", "travel-agent");

        // Assert — one agent-INVOCATION-scoped Response entry carrying the call, tokens, finish, and text
        var entry = Assert.Single(trace.Entries);
        Assert.Equal(TraceEntryScope.AgentInvocation, entry.Scope);
        Assert.Equal("SearchFlights", Assert.Single(entry.ToolCalls!).Name);
        Assert.Equal(30, entry.TokenUsage!.PromptTokens);
        Assert.Equal(12, entry.TokenUsage.CompletionTokens);
        Assert.Equal("stop", entry.FinishReason);
        Assert.Contains("Booked AA-NRT.", entry.Text);
    }

    [Fact]
    public async Task Reconcile_SameRun_NoFabricatedArgumentDrift_EvenWithMultiKeyArgs()
    {
        // Arrange — a real FICC run where the model calls a tool with MULTI-KEY args. The chat recorder and the
        // agent-boundary builder both serialize the SAME FunctionCallContent.Arguments via the shared mapper; if
        // they serialized differently (key order/spacing), argument_drift would falsely fire on this clean run.
        var scripted = new ScriptedChatClient()
            .Add(new ScriptedTurn
            {
                ToolCallId = "c1",
                ToolName = "SearchFlightsMulti",
                ToolArgs = new Dictionary<string, object?> { ["to"] = "NRT", ["from"] = "SFO", ["date"] = "2026-01-01" },
                FinishReason = ChatFinishReason.ToolCalls,
            })
            .Add(new ScriptedTurn { Text = "Done.", FinishReason = ChatFinishReason.Stop });

        var chatTrace = new AgentTrace();
        var pipeline = scripted.AsBuilder()
            .UseFunctionInvocation()
            .UseTraceRecording("agent", chatTrace)
            .Build();
        var options = new ChatOptions { Tools = new List<AITool> { AIFunctionFactory.Create(SearchFlightsMulti, "SearchFlightsMulti") } };

        // Act
        var response = await pipeline.GetResponseAsync(UserSays("plan it"), options);
        var agentTrace = AgentBoundaryTraceBuilder.FromAgentResponse(response.Messages, response.Usage, response.FinishReason?.Value);
        var report = new TraceFidelityRunner().Reconcile(agentTrace, chatTrace);

        // Assert — no drift, no missing, no phantom: the shared mapper makes serialization byte-identical
        Assert.Equal(0, report.Discrepancies.Single(d => d.ClassKey == TraceFidelityRubric.ArgumentDrift).Count);
        Assert.Equal(0, report.Discrepancies.Single(d => d.ClassKey == TraceFidelityRubric.MissingToolCalls).Count);
        Assert.Equal(0, report.Discrepancies.Single(d => d.ClassKey == TraceFidelityRubric.PhantomToolCalls).Count);
    }

    [Fact]
    public async Task FiccRun_MafAgreesOnToolCallsAndTokens_HidesNotLies()
    {
        // Arrange — a REAL MEAI function-invocation loop with the chat recorder composed INNER of it.
        // The model calls a tool, then answers; both turns report token usage.
        var scripted = new ScriptedChatClient()
            .Add(new ScriptedTurn
            {
                ToolCallId = "c1",
                ToolName = "SearchFlights",
                ToolArgs = new Dictionary<string, object?> { ["to"] = "NRT" },
                FinishReason = ChatFinishReason.ToolCalls,
                InputTokens = 10,
                OutputTokens = 5,
            })
            .Add(new ScriptedTurn { Text = "Booked AA-NRT.", FinishReason = ChatFinishReason.Stop, InputTokens = 12, OutputTokens = 6 });

        var chatTrace = new AgentTrace();
        var pipeline = scripted.AsBuilder()
            .UseFunctionInvocation()
            .UseTraceRecording("agent", chatTrace)
            .Build();
        var options = new ChatOptions { Tools = new List<AITool> { AIFunctionFactory.Create(SearchFlights, "SearchFlights") } };

        // Act — run the loop; build the agent-boundary view from what the framework surfaces
        var response = await pipeline.GetResponseAsync(UserSays("fly me to Tokyo"), options);
        var agentTrace = AgentBoundaryTraceBuilder.FromAgentResponse(
            response.Messages, response.Usage, response.FinishReason?.Value, "agent");
        var report = new TraceFidelityRunner().Reconcile(agentTrace, chatTrace);

        // Assert — the framework's account AGREES with the chat boundary: no missing/phantom/drift/retry, and
        // token totals reconcile (FICC sums usage). Fidelity is ~perfect. MAF hides per-turn detail, it doesn't lie.
        Assert.Equal(0, report.Discrepancies.Single(d => d.ClassKey == TraceFidelityRubric.MissingToolCalls).Count);
        Assert.Equal(0, report.Discrepancies.Single(d => d.ClassKey == TraceFidelityRubric.PhantomToolCalls).Count);
        Assert.Equal(0, report.Discrepancies.Single(d => d.ClassKey == TraceFidelityRubric.HiddenRetries).Count);
        Assert.Equal(0, report.Discrepancies.Single(d => d.ClassKey == TraceFidelityRubric.TokenUnderReporting).Count);
        Assert.True(report.OverallScore >= 0.99, $"expected ~perfect fidelity on an honest run, got {report.OverallScore:P0}");
    }
}
