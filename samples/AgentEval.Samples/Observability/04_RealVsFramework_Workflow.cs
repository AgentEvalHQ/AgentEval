// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Models;
using AgentEval.Tracing;
using CoreTokenUsage = AgentEval.Core.TokenUsage;

namespace AgentEval.Samples;

/// <summary>
/// Glass Box — Real vs Framework: Workflow (Observability, Phase 3). Offline PREVIEW (no credentials): the
/// per-executor comparison is <b>scripted</b> to show what <see cref="WorkflowTraceFidelityReconciler"/>
/// surfaces — for each executor, the framework's self-reported ledger (summed tokens + finish reason) vs.
/// chat-boundary truth. It is the workflow analogue of "Real vs Framework: Agent": where a multi-agent
/// workflow's own accounting silently diverges from what each model actually saw.
/// </summary>
public static class RealVsFrameworkWorkflow
{
    /// <summary>Runs the offline per-executor workflow-fidelity demonstration.</summary>
    public static Task RunAsync()
    {
        Console.WriteLine("=== Glass Box — Real vs Framework: Workflow (OFFLINE PREVIEW, scripted executors) ===\n");
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("  [synthetic — the executors are scripted to show the per-executor fidelity table shape]");
        Console.WriteLine("  [for a real audit, capture a live workflow trace + per-executor chat traces]\n");
        Console.ResetColor();

        // Framework's own per-executor ledger (what a WorkflowExecutionResult reports).
        var result = new WorkflowExecutionResult
        {
            FinalOutput = "trip plan delivered",
            Steps = new[]
            {
                Step("planner",    prompt: 80,  completion: 40, finish: "stop"),   // honest
                Step("researcher", prompt: 120, completion: 80, finish: "stop"),   // under-reports (hid a silent retry)
                Step("writer",     prompt: 60,  completion: 30, finish: null),     // suppressed finish reason
            },
        };

        // Chat-boundary truth (what each model actually saw / returned).
        var chatTraths = new Dictionary<string, AgentTrace>
        {
            ["planner"]    = Chat(totalTokens: 120, finish: "stop"),            // matches framework 120 → Agree
            ["researcher"] = Chat(totalTokens: 350, finish: "stop"),            // truth 350 vs framework 200 → TokenMismatch
            ["writer"]     = Chat(totalTokens: 90,  finish: "content_filter"),  // truth content_filter vs framework null → FinishMismatch
        };

        var report = new WorkflowTraceFidelityReconciler().Reconcile(result, chatTraths);

        Console.WriteLine($"  {"executor",-12} {"framework",-22} {"chat-truth",-24} {"verdict",-16} score");
        Console.WriteLine($"  {new string('-', 12)} {new string('-', 22)} {new string('-', 24)} {new string('-', 16)} -----");
        foreach (var e in report.Executors)
        {
            var fw = $"{e.TokensFramework} tok / {Finish(e.FinishFramework)}";
            var truth = e.TokensChatTruth is null ? "(no chat truth)" : $"{e.TokensChatTruth} tok / {Finish(e.FinishChatTruth)}";
            var color = e.DiffKind == WorkflowFidelityDiff.Agree ? ConsoleColor.Green : ConsoleColor.Red;
            Console.ForegroundColor = color;
            Console.WriteLine($"  {e.ExecutorId,-12} {fw,-22} {truth,-24} {e.DiffKind,-16} {e.Score * 100,3:F0}%");
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.WriteLine($"  Overall workflow fidelity: {report.OverallScore * 100:F0}%  " +
            $"({report.VerifiedCount} of {report.Executors.Count} executors had chat truth)");
        Console.WriteLine();
        Console.WriteLine("  The framework's tidy ledger said everything was fine. Glass Box shows 'researcher' hid a");
        Console.WriteLine("  retry's tokens and 'writer' hid a content-filter intervention — per executor, not just overall.");
        return Task.CompletedTask;
    }

    private static ExecutorStep Step(string executorId, int prompt, int completion, string? finish) =>
        new()
        {
            ExecutorId = executorId,
            Output = executorId,
            StepIndex = 0,
            TokenUsage = new CoreTokenUsage { PromptTokens = prompt, CompletionTokens = completion },
            FinishReason = finish,
        };

    private static AgentTrace Chat(int totalTokens, string? finish)
    {
        var trace = new AgentTrace();
        trace.Entries.Add(TraceEntry.ForChatResponse(0, null, "r", 1,
            new TraceTokenUsage { PromptTokens = totalTokens, CompletionTokens = 0 }, null, finish, null));
        return trace;
    }

    private static string Finish(string? reason) => reason ?? "<null>";
}
