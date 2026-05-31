// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Guardrails;
using AgentEval.Guardrails.Gates;
using AgentEval.MAF.Tracing;
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;

using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Samples;

/// <summary>
/// Glass Box — Full Stack (Observability). Offline (no credentials): a single
/// <see cref="ScriptedChatClient"/> drives a pipeline composed of an injection pre-gate, the MEAI tool
/// loop, the chat-boundary recorder, and a PII post-gate; one tool is wrapped to capture its execution.
/// Then a Trace Fidelity reconciliation surfaces a hidden retry the agent boundary would have hidden.
/// </summary>
public static class GlassBoxFullStack
{
    private static string Lookup() => "record #42";

    /// <summary>Runs the demonstration.</summary>
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Glass Box — Full Stack (offline) ===\n");

        // The model: turn 1 calls a tool, turn 2 returns a final answer that leaks an SSN.
        var scripted = new ScriptedChatClient()
            .AddToolCall("c1", "Lookup", new Dictionary<string, object?>())
            .AddText("Found it. The customer SSN is 123-45-6789.", inTok: 40, outTok: 12);

        var trace = new AgentTrace { TraceName = "glassbox-fullstack" };
        var pipeline = scripted
            .AsBuilder()
            .UseEvalGate(pre: new IChatGate[] { new TokenInjectionGate() }, policy: EvalGatePolicy.WarnOnly, trace: trace)
            .UseFunctionInvocation()                                  // the tool loop
            .UseTraceRecording("demo-agent", trace, SamplePreset.AuditGrade)  // INNER of FICC → one entry per round-trip
            .UseEvalGate(post: new IChatGate[] { new RegexPiiGate() }, policy: EvalGatePolicy.Redact, trace: trace)
            .Build();

        var options = new ChatOptions { Tools = new List<AITool> { AIFunctionFactory.Create(Lookup).WithEvaluation(trace) } };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful support agent."),
            new(ChatRole.User, "Look up the latest record and summarise it."),
        };

        ChatResponse response;
        using (new ToolCorrelationScope("demo-invocation-1"))
        {
            response = await pipeline.GetResponseAsync(messages, options);
        }

        // 1) Per-turn chat-boundary capture.
        var chatTurns = trace.Entries.Count(e => e is { Scope: TraceEntryScope.ChatTurn, Type: TraceEntryType.Response });
        Console.WriteLine($"Chat-boundary round-trips recorded : {chatTurns}  (the model loop hit the LLM {chatTurns}×)");

        // 2) Tool-execution capture.
        var toolExec = trace.Entries.FirstOrDefault(e => e.Scope == TraceEntryScope.ToolExecution);
        if (toolExec is not null)
        {
            Console.WriteLine($"Tool executed                      : {toolExec.ToolCalls![0].Name} -> {toolExec.ToolCalls[0].Result} ({toolExec.DurationMs} ms)");
        }

        // 3) Post-gate redaction (the SSN never reaches the caller).
        Console.WriteLine($"Final answer (PII-redacted)        : {response.Text}");

        // 4) Gate verdicts (recorded at the trace level).
        var gateKeys = trace.Metadata?.Keys.Where(k => k.StartsWith("gate.", StringComparison.Ordinal)).ToList() ?? new List<string>();
        Console.WriteLine($"Gate verdicts recorded             : {string.Join(", ", gateKeys)}");

        // 5) Trace Fidelity: the agent boundary reported ONE Lookup; the chat boundary saw a retry.
        var agentBoundary = new AgentTrace
        {
            Entries =
            {
                new TraceEntry { Type = TraceEntryType.Response, Index = 0, ToolCalls = new() { new TraceToolCall { Name = "Lookup" } } },
            },
        };
        var chatWithRetry = new AgentTrace
        {
            Entries =
            {
                TraceEntry.ForChatResponse(0, "inv", "…", 5, null, new() { new TraceToolCall { Name = "Lookup" } }, "tool_calls", null),
                TraceEntry.ForChatResponse(1, "inv", "…", 5, null, new() { new TraceToolCall { Name = "Lookup" } }, "tool_calls", null),
            },
        };
        var report = new TraceFidelityRunner().Reconcile(agentBoundary, chatWithRetry);
        var hidden = report.Discrepancies.Single(d => d.ClassKey == TraceFidelityRubric.HiddenRetries);
        Console.WriteLine($"\nTrace Fidelity score               : {report.OverallScore * 100:F0}%");
        Console.WriteLine($"  hidden_retries ({hidden.Severity})         : count={hidden.Count} — {(hidden.Examples.Count > 0 ? hidden.Examples[0] : "none")}");

        Console.WriteLine("\nGlass Box: recorded what actually happened, policed what was about to happen, and audited the framework's account.");
    }
}
