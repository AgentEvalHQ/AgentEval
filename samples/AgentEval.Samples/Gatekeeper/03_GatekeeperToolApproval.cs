// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#pragma warning disable AEGK001 // UseAgentEvalToolApproval rides MAF's evaluation-only approval API — fine for a demo.

using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Samples;

/// <summary>
/// Gatekeeper — Tool Approval (human-in-the-loop).
///
/// Not every risky action should be hard-blocked — some should pause for a person. This support agent can issue
/// refunds: a small one is routine (auto-approved), a large one is escalated to a human. The Gatekeeper approval
/// gate classifies each call; MAF's native <c>UseToolApproval</c> pauses the run and surfaces the request, and a
/// human approve/reject resumes it — the softer sibling of a hard tool-gate block.
///
/// ★ No credentials needed — a scripted model makes both flows deterministic.
/// ⏱️ Time to understand: 3 minutes
/// </summary>
public static class GatekeeperToolApproval
{
    public static async Task RunAsync()
    {
        PrintHeader();

        var refundsIssued = new List<int>();
        var refund = AIFunctionFactory.Create(
            (int amount) => { refundsIssued.Add(amount); return $"Refunded ${amount}."; }, "issue_refund");

        // Routine refunds (under $1000) auto-approve; a 4+ digit amount ($1000+) is escalated to a human.
        var gate = new ArgumentPatternApprovalGate("\"amount\":\\s*[0-9]{4,}", "large-refund-approval");

        AIAgent BuildAgent(ScriptedChatClient model) => new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Name = "SupportAgent",
            ChatOptions = new ChatOptions { Tools = [refund.RequiresApproval()] },
        }).AsBuilder().UseAgentEvalToolApproval([gate]).Build();

        // ── A routine refund flows straight through ──
        Section("A routine $20 refund");
        var small = BuildAgent(new ScriptedChatClient()
            .AddToolCall("c1", "issue_refund", new Dictionary<string, object?> { ["amount"] = 20 })
            .AddText("Refunded $20 — anything else?"));
        var routine = await small.RunAsync("Please refund my $20 order.");
        Console.WriteLine($"   Agent: \"{routine.Text}\"");
        Console.WriteLine($"   → auto-approved, no human needed. Refunds issued: {refundsIssued.Count}. ✅");

        // ── A large refund pauses for a human ──
        Section("A large $5000 refund — pauses for human approval");
        var large = BuildAgent(new ScriptedChatClient()
            .AddToolCall("c2", "issue_refund", new Dictionary<string, object?> { ["amount"] = 5000 })
            .AddText("Refund issued."));
        var session = await large.CreateSessionAsync();
        var paused = await large.RunAsync("Refund my $5000 order.", session);
        var request = paused.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().FirstOrDefault();
        if (request is null)
        {
            // The large refund MUST escalate; if it didn't, don't print a misleading "approved" success path.
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("   ⚠️ Expected the $5000 refund to pause for approval, but no request surfaced — aborting.");
            Console.ResetColor();
            return;
        }
        Console.WriteLine($"   The agent wants to issue a $5000 refund — the gate escalated it.");
        Console.WriteLine($"   Large refunds issued so far: {refundsIssued.Count(a => a == 5000)}  (paused — waiting for a human) ⏸️");

        // A human reviews and approves → resume on the same session with the approval response.
        await large.RunAsync([new ChatMessage(ChatRole.User, [request.CreateResponse(true)])], session);
        Console.WriteLine($"   Human approved → the refund runs. Large refunds issued: {refundsIssued.Count(a => a == 5000)}. ✅");

        Console.WriteLine("\n   Takeaway: routine actions flow, risky ones pause for a person — one gate,");
        Console.WriteLine("   softer than a hard block. (Rejecting instead would simply never run the tool.)");
        Console.WriteLine("\n=== Gatekeeper Tool Approval Complete ===");
    }

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
