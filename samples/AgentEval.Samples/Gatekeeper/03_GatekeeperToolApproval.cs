// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#pragma warning disable AEGK001 // UseAgentEvalToolApproval rides MAF's evaluation-only approval API — fine for a demo.

using AgentEval.MAF.Gatekeeper;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Gatekeeper — Tool Approval (human-in-the-loop), on a <b>real</b> agent.
///
/// For actions too risky to auto-run but too legitimate to forbid, route the <em>borderline</em> ones to a
/// person. A real support agent has an <c>issue_refund</c> tool wrapped with <c>.RequiresApproval()</c>: a small
/// refund auto-approves and runs; a large one pauses and surfaces a <see cref="ToolApprovalRequestContent"/> for a
/// human, who then approves and the run resumes.
///
/// 🔑 Requires Azure OpenAI credentials (AZURE_OPENAI_ENDPOINT / _API_KEY / _DEPLOYMENT).
/// ⏱️ Time to understand: 2 minutes
/// </summary>
public static class GatekeeperToolApproval
{
    public static async Task RunAsync()
    {
        GatekeeperSampleContractRenderer.Print("03");
        PrintHeader();

        if (GatekeeperOfflineScenarioSuite.ShouldUseOffline)
        {
            await GatekeeperOfflineScenarioSuite.ExecuteAsync("03");
            return;
        }

        var chatClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential)
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient()
            .AsBuilder()
            .UseOpenTelemetry(sourceName: "AgentEval.Samples.Gatekeeper")
            .Build();
        Console.WriteLine($"   Model: {AIConfig.ModelDeployment} — a real agent whose large refunds need a human.\n");

        var refundsIssued = new List<int>();
        var refund = AIFunctionFactory.Create(
            (int amount) => { refundsIssued.Add(amount); return $"Refunded ${amount}."; },
            "issue_refund", "Issue a refund to the customer for the given whole-dollar amount.");

        // Routine refunds (under $1000) auto-approve; a 4+ digit amount ($1000+) is escalated to a human.
        var gate = new ArgumentPatternApprovalGate("\"amount\":\\s*[0-9]{4,}", "large-refund-approval");

        AIAgent BuildAgent() => new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "SupportAgent",
            ChatOptions = new ChatOptions { Tools = [refund.RequiresApproval()], MaxOutputTokens = 256 },
        }).AsBuilder().UseAgentEvalToolApproval([gate]).Build();

        // ── A routine refund flows straight through ──
        Section("A routine $20 refund");
        var routine = await BuildAgent().RunAsync("Please refund my $20 order.");
        Console.WriteLine($"   Agent: \"{Truncate(routine.Text)}\"");
        Console.WriteLine($"   Refunds issued: {refundsIssued.Count}  {(refundsIssued.Contains(20) ? "→ auto-approved, no human needed ✅" : "(model chose not to refund)")}");

        // ── A large refund pauses for a human ──
        Section("A large $5000 refund — pauses for human approval");
        var large = BuildAgent();
        var session = await large.CreateSessionAsync();
        var paused = await large.RunAsync("Please refund my $5000 order in full.", session);
        var request = paused.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().FirstOrDefault();
        if (request is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("   ⚠️ Expected the $5000 refund to pause for approval, but no request surfaced.");
            Console.WriteLine($"      (The model may not have called issue_refund; it said: {Truncate(paused.Text)})");
            Console.ResetColor();
            return;
        }

        var beforeApproval = refundsIssued.Count(a => a >= 1000);
        Console.WriteLine("   The agent wants a large refund — the gate escalated it.");
        Console.WriteLine($"   Large refunds run so far: {beforeApproval}  (paused — waiting for a human) ⏸️");

        // A human reviews and approves → resume on the same session with the approval response.
        await large.RunAsync([new ChatMessage(ChatRole.User, [request.CreateResponse(true)])], session);
        var afterApproval = refundsIssued.Count(a => a >= 1000);
        Console.WriteLine(afterApproval > beforeApproval
            ? $"   Human approved → the refund ran. Large refunds run: {afterApproval}. ✅"
            : $"   Human approved, but the model didn't complete the refund (large refunds run: {afterApproval}).");

        Console.WriteLine("\n   Takeaway: routine actions flow, risky ones pause for a person — one gate, softer than a hard block.");
        Console.WriteLine("\n=== Gatekeeper Tool Approval Complete ===");
    }

    private static string Truncate(string? s, int max = 140)
        => string.IsNullOrEmpty(s) ? "(none)" : s.Length <= max ? s : s[..max] + "…";

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"── {title}");
        Console.ResetColor();
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║   🚪 GATEKEEPER — TOOL APPROVAL (human-in-the-loop)                            ║
║   Routine actions auto-approve; risky ones pause for a human's OK              ║
╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }
}
