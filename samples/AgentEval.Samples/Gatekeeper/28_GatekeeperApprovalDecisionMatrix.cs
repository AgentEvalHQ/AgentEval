// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Guardrails.Judges;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>Offline approval decision matrix with real pause, approve, reject, and fail-closed paths.</summary>
public static class GatekeeperApprovalDecisionMatrix
{
    private const string LargeAmountPattern = "\"amount\":\\s*[0-9]{4,}";

    public static async Task RunAsync()
    {
        GatekeeperSampleContractRenderer.Print("28");
        Console.WriteLine("\n=== Gatekeeper — Approval Decision Matrix (offline) ===\n");

        Console.WriteLine("── The decision matrix, cell by cell ──");

        var arguments = new ArgumentPatternApprovalGate(LargeAmountPattern, "large-refund");
        Require(await arguments.IsAutoApprovableAsync(Call("issue_refund", ("amount", 20))),
            "routine arguments must be positively auto-approved");
        Console.WriteLine("   issue_refund(amount: 20)                      → AUTO-APPROVE  (positively shown routine)");
        Require(!await arguments.IsAutoApprovableAsync(Call("issue_refund", ("amount", 5000))),
            "risky arguments must escalate");
        Console.WriteLine("   issue_refund(amount: 5000)                    → ESCALATE      (risky argument shape)");

        var sensitiveName = new ToolNameApprovalGate(["rotate_root_key"]);
        Require(!await sensitiveName.IsAutoApprovableAsync(Call("rotate_root_key")),
            "a sensitive parameterless tool must escalate by identity");
        Console.WriteLine("   rotate_root_key()                             → ESCALATE      (sensitive identity, no args needed)");

        var mismatch = new ToolArgumentGoalCoherenceApprovalGate(
            new ScriptedChatClient().AddText(
                """{"incoherent":true,"confidence":0.97,"evidence":"destination"}"""),
            "refund $12 to the original payment method",
            new JudgeGateOptions { MaxOutputTokens = 64 },
            cache: false);
        Require(!await mismatch.IsAutoApprovableAsync(
                Call("send_wire", ("amount", 12000), ("destination", "external"))),
            "a confident goal/argument mismatch must escalate");
        Console.WriteLine("   send_wire($12,000) for goal \"refund $12\"      → ESCALATE      (confident goal mismatch)");

        var failure = new ToolArgumentGoalCoherenceApprovalGate(
            new ScriptedChatClient().AddThrow(),
            "refund $12 to the original payment method",
            new JudgeGateOptions
            {
                MaxOutputTokens = 64,
                Timeout = TimeSpan.FromSeconds(1),
            },
            cache: false);
        Require(!await failure.IsAutoApprovableAsync(Call("issue_refund", ("amount", 12))),
            "judge failure must escalate rather than auto-run");
        Console.WriteLine("   issue_refund(amount: 12), judge THROWS        → ESCALATE      (inconclusive never auto-runs)");

        Console.WriteLine("\n── The human moment: a real MAF pause/continuation, both branches ──");
        var rejectedEffects = await ExecuteHumanDecisionAsync(approved: false);
        var approvedEffects = await ExecuteHumanDecisionAsync(approved: true);
        Require(rejectedEffects == 0,
            "explicit human rejection must keep the fake effect at zero");
        Require(approvedEffects == 1,
            "approved continuation must execute the fake effect exactly once");
        Console.WriteLine($"   measured effects: rejected branch = {rejectedEffects} · approved branch = {approvedEffects}\n");

        Console.WriteLine("   routine arguments:    AUTO-APPROVE");
        Console.WriteLine("   sensitive/no args:    ESCALATE by tool identity");
        Console.WriteLine("   risky arguments:      ESCALATE");
        Console.WriteLine("   goal mismatch/error:  ESCALATE (inconclusive never auto-runs)");
        Console.WriteLine("   human reject/approve: 0 effects / exactly 1 fake effect");
        Console.WriteLine("   ✅ every ambiguous path paused and only explicit approval resumed execution.");
    }

    private static async Task<int> ExecuteHumanDecisionAsync(bool approved)
    {
        var executed = 0;
        var refund = AIFunctionFactory.Create(
            (int amount) =>
            {
                Interlocked.Increment(ref executed);
                return $"fake refund {amount}";
            },
            "issue_refund");
        var model = new ScriptedChatClient()
            .AddToolCall(
                approved ? "approved-call" : "rejected-call",
                "issue_refund",
                new Dictionary<string, object?> { ["amount"] = 5000 })
            .AddText(approved ? "approved" : "rejected");
        var agent = new ChatClientAgent(
            model,
            new ChatClientAgentOptions
            {
                Name = approved ? "approval-control" : "rejection-control",
                ChatOptions = new ChatOptions
                {
                    Tools = [refund.RequiresApproval()],
                    MaxOutputTokens = 64,
                },
            })
            .AsBuilder()
            .UseAgentEvalToolApproval([new ArgumentPatternApprovalGate(LargeAmountPattern)])
            .Build();
        var session = await agent.CreateSessionAsync();

        var paused = await agent.RunAsync("Process the fake large refund.", session);
        var request = paused.Messages
            .SelectMany(message => message.Contents)
            .OfType<ToolApprovalRequestContent>()
            .Single();
        Require(executed == 0,
            "the fake effect must remain zero while approval is pending");
        Console.WriteLine($"   ⏸ PAUSED — issue_refund(amount: 5000) is waiting for a human (effects so far: {executed})");
        Console.WriteLine($"   👤 operator decision: {(approved ? "APPROVE" : "REJECT")} → resuming the continuation…");

        await agent.RunAsync(
            [new ChatMessage(ChatRole.User, [request.CreateResponse(approved)])],
            session);
        return executed;
    }

    private static FunctionCallContent Call(
        string name,
        params (string Key, object? Value)[] arguments) =>
        new(
            "sample-call",
            name,
            arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Approval-matrix sample failed: " + message + ".");
        }
    }
}
