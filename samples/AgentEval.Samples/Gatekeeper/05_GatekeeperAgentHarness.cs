// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.MAF.Gatekeeper;
using AgentEval.Tracing;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentTrace = AgentEval.Tracing.AgentTrace;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Samples;

/// <summary>
/// Gatekeeper × MAF Agent Harness (simple) — cap an autonomous, looping agent.
///
/// The <b>MAF Agent Harness</b> makes a <see cref="ChatClientAgent"/> more capable by composing
/// <see cref="AIContextProvider"/>s (mission brief, planning, file access) and letting it <b>loop</b> until the
/// task is done. That autonomy is where a runaway loop burns budget — so the most valuable gate for a harness
/// agent is <c>RunBudgetGate</c>.
///
/// A <b>real</b> model drives the loop here: a harness agent (a <see cref="ChatClientAgent"/> + an
/// <see cref="AIContextProvider"/> mission) is told to research exhaustively, so it wants many <c>search</c>
/// calls — and <c>RunBudgetGate</c> caps how many actually run.
///
/// 🔑 Requires Azure OpenAI credentials (AZURE_OPENAI_ENDPOINT / _API_KEY / _DEPLOYMENT).
/// ⏱️ Time to understand: 1 minute
/// </summary>
public static class GatekeeperAgentHarness
{
    private const int Budget = 3;

    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            return;
        }

        var chatClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential)
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();

        var searches = 0;
        var search = AIFunctionFactory.Create(
            (string query) => { searches++; return $"[3 knowledge-base hits for '{query}']"; },
            "search", "Search the support knowledge base for a query.");

        // The Harness capability-injection pattern: a provider that supplies the agent's mission each turn.
        var brief = new MissionBriefProvider(
            "You are an autonomous research agent. Investigate the ticket EXHAUSTIVELY: search the knowledge base " +
            "for many distinct angles (billing system, refunds, prior tickets, edge cases, ...) — a dozen or more " +
            "separate searches — before you answer.");

        var trace = new AgentTrace();
        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "HarnessAgent",
            AIContextProviders = [brief],                 // ← MAF Agent Harness: capability injection
            ChatOptions = new ChatOptions { Tools = [search] },
        })
            .AsBuilder()
            .UseAgentEvalGate()                            // establishes the per-run RunLedger scope
            .UseAgentEvalToolGate([new RunBudgetGate(maxToolCalls: Budget)], ToolGatePolicy.ReplaceResult, trace)
            .Build();

        Console.WriteLine($"   Model: {AIConfig.ModelDeployment} — driving a real autonomous research loop (budget = {Budget}).\n");
        var response = await agent.RunAsync("Investigate ticket #4821 — a customer's recurring billing error.");

        var blocked = GlassBoxEvidence.FromTrace(trace)?.GateBlockCount ?? 0;
        Console.WriteLine($"   `search` calls the model attempted: {searches + blocked}");
        Console.WriteLine($"   `search` calls that actually ran:   {searches}   (blocked by budget: {blocked})");
        Console.WriteLine($"   {(searches > Budget ? "❌ over budget" : blocked > 0 ? $"✅ the autonomous loop was capped at {Budget} by RunBudgetGate" : "the model stayed under budget on its own this run — nothing for the gate to cap (re-run to see the cap)")}");
        Console.WriteLine($"\n   Agent's answer: {Truncate(response.Text)}");
        Console.WriteLine("\n   → The Harness makes an agent loop autonomously; the Gatekeeper keeps the loop from running away.");
        Console.WriteLine("\n=== Gatekeeper × Agent Harness (simple) Complete ===");
    }

    private static string Truncate(string? s, int max = 160)
        => string.IsNullOrEmpty(s) ? "(none)" : s.Length <= max ? s : s[..max] + "…";

    // The MAF AIContextProvider pattern — injects the agent's mission into context before each model call.
    private sealed class MissionBriefProvider : AIContextProvider
    {
        private readonly string _mission;
        public MissionBriefProvider(string mission) => _mission = mission;
        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
            => new(new AIContext { Messages = [new ChatMessage(ChatRole.System, _mission)] });
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║   🚪 GATEKEEPER × MAF AGENT HARNESS — SIMPLE                                   ║
║   A real autonomous, looping harness agent, capped by RunBudgetGate             ║
╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }
}
