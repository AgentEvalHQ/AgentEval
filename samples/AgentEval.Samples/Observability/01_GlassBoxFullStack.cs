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
/// Glass Box — Full Stack (Observability). Offline API tour (no credentials): a single
/// <see cref="ScriptedChatClient"/> drives a REAL pipeline — injection pre-gate, the MEAI tool loop, the
/// chat-boundary recorder, and a PII post-gate — with one tool wrapped to capture its execution. Everything
/// up to the redaction is genuine. The closing Trace Fidelity section is <b>illustrative</b>: it scores two
/// <b>hand-built</b> traces to show what the reconciler reports — it is NOT a real detection.
/// For a real framework-vs-Glass-Box comparison on a live model, run "Real vs Framework: Agent / Workflow".
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

        // 5) Trace Fidelity — ILLUSTRATIVE ONLY. The two traces below are HAND-BUILT to show what the
        //    reconciler reports for a hidden retry; this is NOT a detection from the run above. For a REAL
        //    framework-vs-Glass-Box comparison on a live model, run "Real vs Framework: Agent / Workflow".
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("\n[illustrative — synthetic hand-built traces, not a real detection]");
        Console.ResetColor();
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

        Console.WriteLine("\nGlass Box (this sample): recorded every round-trip, captured the tool execution, and redacted PII inline.");
        Console.WriteLine("For the framework-vs-Glass-Box audit on a REAL model, run \"Real vs Framework: Agent / Workflow\".");
    }
}
