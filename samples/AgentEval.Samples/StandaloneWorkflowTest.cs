// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors
// Standalone MAF workflow verification — bypasses AgentEval harness entirely.
// Follows the official MAF pattern: ChatMessage input + TurnToken trigger.

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Standalone test that runs a MAF workflow directly (no AgentEval harness)
/// to verify that the ChatMessage + TurnToken pattern produces events.
/// This isolates MAF workflow issues from AgentEval bridge issues.
/// </summary>
public static class StandaloneWorkflowTest
{
    public static async Task RunAsync()
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   🔬 STANDALONE MAF WORKFLOW DIAGNOSTIC                                       ║
║   Runs a MAF workflow directly — no AgentEval harness — to verify events      ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();

        if (!AIConfig.IsConfigured)
        {
            Console.WriteLine("  ⚠️  Azure OpenAI credentials not configured. Skipping.");
            return;
        }

        // ── Build workflow (same pattern as Sample10) ──
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        // Use a simple 2-agent pipeline for faster testing
        var translator = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "Translator",
            Description = "Translates text to French",
            Instructions = "You are a translator. Translate the user's text to French. Reply with ONLY the French translation."
        });

        var summarizer = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "Summarizer",
            Description = "Summarizes text",
            Instructions = "You are a summarizer. Summarize the input in one sentence in English. Reply with ONLY the summary."
        });

        var translatorBinding = translator.BindAsExecutor(emitEvents: true);
        var summarizerBinding = summarizer.BindAsExecutor(emitEvents: true);

        var workflow = new WorkflowBuilder(translatorBinding)
            .AddEdge(translatorBinding, summarizerBinding)
            .WithOutputFrom(summarizerBinding)
            .WithName("TestPipeline")
            .Build();

        Console.WriteLine($"  Workflow: {workflow.Name}");
        Console.WriteLine($"  Start executor: {workflow.StartExecutorId}");
        Console.WriteLine($"  Model: {AIConfig.ModelDeployment}\n");

        // ── Execute with the OFFICIAL pattern: ChatMessage + TurnToken ──
        Console.WriteLine("  🚀 Executing with ChatMessage + TurnToken pattern...\n");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int eventCount = 0;
        string lastOutput = "";

        try
        {
            await using var run = await InProcessExecution
                .StreamAsync(workflow, new ChatMessage(ChatRole.User, "Hello, how are you today?"));

            // Send the TurnToken to trigger agent processing
            var tokenSent = await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
            Console.WriteLine($"  TurnToken sent: {tokenSent}\n");

            await foreach (var evt in run.WatchStreamAsync())
            {
                eventCount++;
                var typeName = evt.GetType().Name;

                // Print every event type for diagnosis
                switch (evt)
                {
                    case ExecutorInvokedEvent invoked:
                        Console.WriteLine($"  [{eventCount:D3}] {typeName}: {invoked.ExecutorId}");
                        break;

                    case ExecutorCompletedEvent completed:
                        Console.WriteLine($"  [{eventCount:D3}] {typeName}: {completed.ExecutorId}");
                        if (completed.Data is not null)
                        {
                            var dataType = completed.Data.GetType().Name;
                            Console.WriteLine($"         Data type: {dataType}");
                        }
                        break;

                    case AgentRunUpdateEvent update:
                        var text = update.Data?.ToString() ?? "(null)";
                        var snippet = text.Length > 80 ? text[..77] + "..." : text;
                        Console.WriteLine($"  [{eventCount:D3}] {typeName}: {snippet}");
                        lastOutput += text;
                        break;

                    case WorkflowOutputEvent output:
                        Console.WriteLine($"  [{eventCount:D3}] {typeName}: {output.Data}");
                        break;

                    default:
                        Console.WriteLine($"  [{eventCount:D3}] {typeName}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n  ❌ Error: {ex.GetType().Name}: {ex.Message}");
            Console.ResetColor();
        }

        sw.Stop();
        Console.WriteLine($"\n  ────────────────────────────────────────");
        Console.WriteLine($"  Total events : {eventCount}");
        Console.WriteLine($"  Duration     : {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  Last output  : {(lastOutput.Length > 100 ? lastOutput[..97] + "..." : lastOutput)}");
        Console.WriteLine($"\n  {(eventCount > 2 ? "✅ Workflow is producing events!" : "❌ No executor events — check credentials/model")}");
    }
}
