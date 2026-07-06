// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Samples;

/// <summary>
/// Gatekeeper — Hello World (the 30-second version).
///
/// The simplest possible gate: wrap a MAF agent so one destructive tool can never run. Three lines change —
/// <c>.AsBuilder()</c>, <c>.UseAgentEvalToolGate(...)</c>, <c>.Build()</c> — and the call is blocked before it
/// executes, with an honest record in the trace. Start here, then see the Enforcement Walkthrough for the rest.
///
/// ★ No credentials needed — a scripted model calls the forbidden tool so the block is deterministic.
/// ⏱️ Time to understand: 1 minute
/// </summary>
public static class GatekeeperHelloWorld
{
    public static async Task RunAsync()
    {
        PrintHeader();

        // A destructive tool. Its body would really run without a gate — so the demo is honest.
        var deletions = 0;
        var deleteEverything = AIFunctionFactory.Create(
            () => { deletions++; return "everything deleted"; }, "delete_everything");

        // A scripted "model" that tries to call the forbidden tool.
        var scripted = new ScriptedChatClient()
            .AddToolCall("c1", "delete_everything", new Dictionary<string, object?>())
            .AddText("done");

        var trace = new AgentTrace();

        // ── The only three lines that matter ──
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions
        {
            Name = "Assistant",
            ChatOptions = new ChatOptions { Tools = [deleteEverything] },
        })
            .AsBuilder()
            .UseAgentEvalToolGate([new ForbiddenToolGate("delete_everything")], ToolGatePolicy.Terminate, trace)
            .Build();

        await agent.RunAsync("clean up the system");

        Console.WriteLine("   The model tried to call: delete_everything");
        Console.WriteLine($"   Times it actually ran:   {deletions}  {(deletions == 0 ? "✅ blocked by the gate" : "❌ ran")}");
        Console.WriteLine($"   Blocks recorded in the trace: {GlassBoxEvidence.FromTrace(trace)?.GateBlockCount ?? 0}");
        Console.WriteLine("\n   That's it. The gate ran in the request path and stopped the call before it happened.");
        Console.WriteLine("   → Next: the Enforcement Walkthrough shows the moat, canary, sequences, and more.");
        Console.WriteLine("\n=== Gatekeeper Hello World Complete ===");
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║   🚪 GATEKEEPER — HELLO WORLD                                                  ║
║   The simplest gate: block one destructive tool before it runs                 ║
╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }
}
