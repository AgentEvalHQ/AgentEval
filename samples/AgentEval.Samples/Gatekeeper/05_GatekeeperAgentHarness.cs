// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#pragma warning disable MAAI001 // Microsoft.Agents.AI.Harness (AsHarnessAgent) is experimental.

using AgentEval.MAF.Gatekeeper;
using AgentEval.Tracing;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentTrace = AgentEval.Tracing.AgentTrace;
using RuntimeEnforcement = AgentEval.MAF.Gatekeeper.GatekeeperEnforcement;

namespace AgentEval.Samples;

/// <summary>
/// Gatekeeper × MAF Agent Harness (simple) — cap a REAL, autonomous HarnessAgent.
///
/// This uses the genuine MAF Agent Harness: <c>IChatClient.AsHarnessAgent(new HarnessAgentOptions { … })</c>,
/// which pre-wires planning, todo, mode, and an autonomous <b>loop</b> (LoopAgent + LoopEvaluators) onto a
/// <see cref="ChatClientAgent"/>. That autonomy is where a runaway loop burns budget — so we wrap the harness
/// agent in <c>RunBudgetGate</c> and let it stop the run when the loop exceeds its tool-call budget.
///
/// 🔑 Requires Azure OpenAI credentials (AZURE_OPENAI_ENDPOINT / _API_KEY / _DEPLOYMENT).
/// ⏱️ Time to understand: 2 minutes
/// </summary>
public static class GatekeeperAgentHarness
{
    private const int Budget = 3;

    public static async Task RunAsync()
    {
        GatekeeperSampleContractRenderer.Print("05");
        PrintHeader();

        if (GatekeeperOfflineScenarioSuite.ShouldUseOffline)
        {
            await GatekeeperOfflineScenarioSuite.ExecuteAsync("05");
            return;
        }

        var chatClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential)
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();

        var searches = 0;
        var search = AIFunctionFactory.Create(
            (string query) => { searches++; return $"[3 knowledge-base hits for '{query}']"; },
            "search", "Search the support knowledge base for a query.");

        // A REAL MAF Agent Harness agent — planning + todo + mode + an autonomous loop, via AsHarnessAgent.
        AIAgent harness = chatClient.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = "ResearchHarness",
            Description = "An autonomous research agent that plans and executes.",
            MaxContextWindowTokens = 120_000,
            MaxOutputTokens = 1_024,
            // MAF 1.17.0: DisableFileAccess removed — file access is now opt-in via FileAccessStore
            // ("When null (the default), no provider is added and the agent has no file access tools"),
            // so leaving FileAccessStore unset preserves this sample's original intent.
            DisableWebSearch = true,   // gpt-4o-mini (chat completions) doesn't support the harness's built-in web_search_options
            DisableFileMemory = true,  // keep the demo self-contained (no on-disk agent-files/ working dir)
            // In "execute" mode the harness keeps re-invoking itself (its loop) until its todos are done.
            LoopEvaluators = [new TodoCompletionLoopEvaluator(new TodoCompletionLoopEvaluatorOptions { Modes = ["execute"] })],
            LoopAgentOptions = new LoopAgentOptions { MaxIterations = 12 },
            ChatOptions = new ChatOptions
            {
                MaxOutputTokens = 1_024,
                Instructions =
                    "You are an autonomous research agent. Plan the ticket, then in execute mode use the `search` " +
                    "tool MANY times — at least a dozen distinct queries across billing, refunds, prior tickets and " +
                    "edge cases — before you answer.",
                Tools = [search],
            },
        });

        // Wrap the REAL harness agent in the Gatekeeper — cap the autonomous loop's tool calls.
        var trace = new AgentTrace();
        AIAgent gated = harness.AsBuilder()
            .UseGatekeeper(RuntimeEnforcement.Terminate, options =>
            {
                options.Trace = trace;
                options.Add(new RunBudgetGate(maxToolCalls: Budget));
            })
            .Build();

        Console.WriteLine($"   Model: {AIConfig.ModelDeployment} — a REAL MAF HarnessAgent (AsHarnessAgent), budget = {Budget}.\n");
        var response = await gated.RunAsync("Investigate ticket #4821 — a customer's recurring billing error.");

        var blocked = GlassBoxEvidence.FromTrace(trace)?.GateBlockCount ?? 0;
        Console.WriteLine($"   `search` calls that ran: {searches}   (over-budget call that stopped the run: {blocked})");
        Console.WriteLine($"   {(blocked > 0 ? $"✅ RunBudgetGate terminated the harness at the {Budget}-call budget — the runaway loop is capped" : "the harness stayed under budget on its own this run")}");
        GateVoice.Speak(trace);
        Console.WriteLine($"\n   Agent's answer: {Truncate(response.Text)}");
        Console.WriteLine("\n   → The Harness makes an agent loop autonomously; the Gatekeeper keeps the loop from running away.");
        Console.WriteLine("\n=== Gatekeeper × Agent Harness (simple) Complete ===");
    }

    private static string Truncate(string? s, int max = 160)
        => string.IsNullOrEmpty(s) ? "(none)" : s.Length <= max ? s : s[..max] + "…";

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║   🚪 GATEKEEPER × MAF AGENT HARNESS — SIMPLE                                   ║
║   A REAL AsHarnessAgent, its autonomous loop capped by RunBudgetGate            ║
╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }
}
