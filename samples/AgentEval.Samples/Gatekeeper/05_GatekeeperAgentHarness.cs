// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Gatekeeper × MAF Agent Harness (simple) — cap an autonomous, looping agent.
///
/// The <b>MAF Agent Harness</b> makes a <see cref="ChatClientAgent"/> more capable by composing
/// <see cref="AIContextProvider"/>s (mission brief, planning, file access, background tasks) and letting it
/// <b>loop</b> until the task is done. That autonomy is exactly where a runaway loop burns budget — so the single
/// most valuable gate for a harness agent is <c>RunBudgetGate</c>.
///
/// Here a harness-style agent (a <see cref="ChatClientAgent"/> + an <see cref="AIContextProvider"/> that injects
/// its mission each turn) tries to call its <c>search</c> tool many times; <c>RunBudgetGate</c> caps the run.
///
/// ★ No credentials — a scripted model drives the loop deterministically.
/// ⏱️ Time to understand: 1 minute
/// </summary>
public static class GatekeeperAgentHarness
{
    public static async Task RunAsync()
    {
        PrintHeader();

        var searches = 0;
        var search = AIFunctionFactory.Create((string query) => { searches++; return $"result for '{query}'"; }, "search");

        // The Harness capability-injection pattern: a provider that supplies the agent's mission each turn.
        var brief = new MissionBriefProvider("Research the customer's issue thoroughly using the search tool.");

        var scripted = new ScriptedChatClient();
        for (var i = 1; i <= 8; i++)   // an over-eager autonomous loop
        {
            scripted.AddToolCall($"c{i}", "search", new Dictionary<string, object?> { ["query"] = $"lead {i}" });
        }

        scripted.AddText("done");

        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions
        {
            Name = "HarnessAgent",
            AIContextProviders = [brief],                 // ← MAF Agent Harness: capability injection
            ChatOptions = new ChatOptions { Tools = [search] },
        })
            .AsBuilder()
            .UseAgentEvalGate()                            // establishes the per-run RunLedger scope
            .UseAgentEvalToolGate([new RunBudgetGate(maxToolCalls: 4)], ToolGatePolicy.ReplaceResult)
            .Build();

        await agent.RunAsync("look into ticket #4821");

        Console.WriteLine("   The harness agent tried to call `search` 8 times in its autonomous loop; budget = 4.");
        Console.WriteLine($"   Searches that actually ran: {searches}  {(searches == 4 ? "✅ runaway loop capped by RunBudgetGate" : "❌")}");
        Console.WriteLine("\n   → The Harness makes agents loop autonomously; the Gatekeeper keeps the loop from running away.");
        Console.WriteLine("   → Next: the DEFENDED harness sample adds exfiltration + sequence defenses around the same agent.");
        Console.WriteLine("\n=== Gatekeeper × Agent Harness (simple) Complete ===");
    }

    // The MAF AIContextProvider pattern — injects the agent's mission into context before each model call.
    private sealed class MissionBriefProvider : AIContextProvider
    {
        private readonly string _mission;
        public MissionBriefProvider(string mission) => _mission = mission;
        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
            => new(new AIContext { Messages = [new ChatMessage(ChatRole.System, $"MISSION: {_mission}")] });
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║   🚪 GATEKEEPER × MAF AGENT HARNESS — SIMPLE                                   ║
║   An autonomous, looping harness agent, capped by RunBudgetGate                 ║
╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }
}
