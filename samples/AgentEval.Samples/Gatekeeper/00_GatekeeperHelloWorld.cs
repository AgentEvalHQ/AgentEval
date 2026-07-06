// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.MAF.Gatekeeper;      // GateCost, UseAgentEvalToolGate, ToolGatePolicy
using AgentEval.RedTeam.Evaluators;  // ContainsTokenEvaluator
using AgentEval.RedTeam.Gatekeeper;  // ProbeEvaluatorGate — the moat
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Samples;

/// <summary>
/// Gatekeeper — Hello World (the 30-second version).
///
/// The simplest gate that also shows the whole point: the <b>same</b> red-team check you test with becomes a
/// <b>live guard</b> on every tool call. Here a token detector (`ContainsTokenEvaluator`) — an offline oracle —
/// runs as a `ProbeEvaluatorGate`, so a tool call carrying an attack marker is blocked before it executes. Three
/// lines change: <c>.AsBuilder()</c>, <c>.UseAgentEvalToolGate(...)</c>, <c>.Build()</c>.
///
/// ★ No credentials needed — a scripted model emits the poisoned call so the block is deterministic.
/// ⏱️ Time to understand: 1 minute
/// </summary>
public static class GatekeeperHelloWorld
{
    public static async Task RunAsync()
    {
        PrintHeader();

        // A tool that publishes content. Without a gate, the poisoned content would go live.
        var published = 0;
        var writePage = AIFunctionFactory.Create(
            (string content) => { published++; return "published"; }, "write_page");

        // A scripted "model" that tries to publish content carrying an attack marker ("PWNED").
        var scripted = new ScriptedChatClient()
            .AddToolCall("c1", "write_page", new Dictionary<string, object?> { ["content"] = "<script>steal()</script> PWNED" })
            .AddText("done");

        var trace = new AgentTrace();

        // ── The only lines that matter ──
        // The SAME ContainsToken check you'd use to SCORE an attack offline, now ENFORCING it at runtime:
        var gate = new ProbeEvaluatorGate(new ContainsTokenEvaluator("PWNED"), GateCost.PureCode);

        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions
        {
            Name = "Assistant",
            ChatOptions = new ChatOptions { Tools = [writePage] },
        })
            .AsBuilder()
            .UseAgentEvalToolGate([gate], ToolGatePolicy.ReplaceResult, trace)
            .Build();

        await agent.RunAsync("publish the page");

        Console.WriteLine("   The model tried to publish content carrying the attack marker 'PWNED'.");
        Console.WriteLine($"   Times write_page actually ran: {published}  {(published == 0 ? "✅ blocked by the probe-as-gate" : "❌ ran")}");
        Console.WriteLine($"   Blocks recorded in the trace:  {GlassBoxEvidence.FromTrace(trace)?.GateBlockCount ?? 0}");
        Console.WriteLine("\n   That's the whole idea: your red-team oracle runs in the request path and stops the call.");
        Console.WriteLine("   → Next: the Enforcement Walkthrough adds canary honeypots, dangerous sequences, and more.");
        Console.WriteLine("\n=== Gatekeeper Hello World Complete ===");
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║   🚪 GATEKEEPER — HELLO WORLD                                                  ║
║   The simplest gate: your red-team check, now blocking a live call             ║
╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }
}
