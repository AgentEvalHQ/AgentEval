// Copyright (c) 2025 AgentEval. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Tracing;

namespace AgentEval.Samples;

/// <summary>
/// Sample 13: Trace Record &amp; Replay
/// 
/// Demonstrates how to record agent executions to trace files and replay them
/// for deterministic evaluation without calling external AI services.
/// 
/// Features covered:
/// - Single-agent trace recording and replay
/// - Multi-turn chat trace recording and replay
/// - Workflow trace recording and replay
/// - Streaming with timing preservation
/// - Trace serialization to JSON files
/// 
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
/// ⏱️ Time to understand: 10 minutes
/// </summary>
public static class Sample13_TraceRecordReplay
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            PrintMissingCredentialsBox();
            return;
        }

        Console.WriteLine($"   🔗 Endpoint: {AIConfig.Endpoint}");
        Console.WriteLine($"   🤖 Model: {AIConfig.ModelDeployment}\n");

        await DemoSingleAgentRecordReplay();
        await DemoMultiTurnChatRecordReplay();
        await DemoWorkflowTraceReplay();
        await DemoStreamingTraceReplay();

        PrintKeyTakeaways();
    }

    /// <summary>
    /// Demo 1: Record a real agent call, save to file, then replay from the saved trace.
    /// </summary>
    private static async Task DemoSingleAgentRecordReplay()
    {
        Console.WriteLine("┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ Demo 1: Single Agent Record & Replay (REAL)                 │");
        Console.WriteLine("└─────────────────────────────────────────────────────────────┘\n");

        var agent = CreateAgent();

        // Step 1: RECORD the execution
        Console.WriteLine("📼 RECORDING real agent execution...\n");

        AgentTrace recordedTrace;
        await using (var recorder = new TraceRecordingAgent(agent, "weather_query"))
        {
            var response = await recorder.InvokeAsync("What is the capital of Japan and what is it known for?");

            Console.WriteLine($"   Prompt: What is the capital of Japan?");
            Console.WriteLine($"   Response: {response.Text[..Math.Min(100, response.Text.Length)]}...");
            Console.WriteLine($"   Entries recorded: {recorder.Trace.Entries.Count}");

            recordedTrace = recorder.Trace;
        }

        // Save trace to file
        var tracePath = Path.Combine(Path.GetTempPath(), "agenteval_sample13_trace.json");
        await TraceSerializer.SaveToFileAsync(recordedTrace, tracePath);
        var traceJson = await TraceSerializer.SerializeToStringAsync(recordedTrace);
        Console.WriteLine($"\n   💾 Trace saved to: {tracePath} ({traceJson.Length} bytes)");

        // Step 2: REPLAY from the SAME recorded trace (no AI calls!)
        Console.WriteLine("\n▶️  REPLAYING from saved trace (no AI service calls)...\n");

        var loadedTrace = await TraceSerializer.LoadFromFileAsync(tracePath);
        var replayer = new TraceReplayingAgent(loadedTrace);
        var replayedResponse = await replayer.InvokeAsync("What is the capital of Japan and what is it known for?");

        Console.WriteLine($"   Replayed Response: {replayedResponse.Text[..Math.Min(100, replayedResponse.Text.Length)]}...");
        Console.WriteLine($"   Current replay index: {replayer.CurrentIndex} of {replayer.TotalPairs}");
        Console.WriteLine("\n   ✓ Same result replayed from saved file — zero AI calls!\n");
    }

    /// <summary>
    /// Demo 2: Record a multi-turn chat conversation and replay it.
    /// </summary>
    private static async Task DemoMultiTurnChatRecordReplay()
    {
        Console.WriteLine("┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ Demo 2: Multi-Turn Chat Record & Replay (REAL)              │");
        Console.WriteLine("└─────────────────────────────────────────────────────────────┘\n");

        var agent = CreateAgent();

        // Step 1: RECORD the conversation
        Console.WriteLine("📼 RECORDING multi-turn conversation...\n");

        await using var recorder = new ChatTraceRecorder(agent, "travel_chat");

        var response1 = await recorder.AddUserTurnAsync("I want to visit Rome. What should I see?");
        Console.WriteLine($"   User: I want to visit Rome. What should I see?");
        Console.WriteLine($"   Agent: {Truncate(response1, 80)}\n");

        var response2 = await recorder.AddUserTurnAsync("What about food recommendations?");
        Console.WriteLine($"   User: What about food recommendations?");
        Console.WriteLine($"   Agent: {Truncate(response2, 80)}\n");

        var chatResult = recorder.GetResult();
        Console.WriteLine($"   📊 Recorded {chatResult.TotalTurnCount} turns");

        // Save trace to file
        var trace = recorder.ToAgentTrace();
        var chatTracePath = Path.Combine(Path.GetTempPath(), "agenteval_sample13_chat_trace.json");
        await TraceSerializer.SaveToFileAsync(trace, chatTracePath);
        var chatJson = await TraceSerializer.SerializeToStringAsync(trace);
        Console.WriteLine($"   💾 Chat trace saved to: {chatTracePath} ({chatJson.Length} bytes)\n");

        // Step 2: REPLAY for verification
        Console.WriteLine("▶️  Replaying conversation from saved trace...\n");

        var loadedChatTrace = await TraceSerializer.LoadFromFileAsync(chatTracePath);
        var chatReplayer = new TraceReplayingAgent(loadedChatTrace);

        var r1 = await chatReplayer.InvokeAsync("I want to visit Rome. What should I see?");
        Console.WriteLine($"   Replay turn 1: {Truncate(r1.Text, 60)}");

        var r2 = await chatReplayer.InvokeAsync("What about food recommendations?");
        Console.WriteLine($"   Replay turn 2: {Truncate(r2.Text, 60)}");

        Console.WriteLine("\n   ✓ Multi-turn conversation replayed from file!\n");
    }

    /// <summary>
    /// Demo 3: Construct and replay a workflow trace (replay API demonstration).
    /// </summary>
    private static async Task DemoWorkflowTraceReplay()
    {
        Console.WriteLine("┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ Demo 3: Workflow Trace Replay                               │");
        Console.WriteLine("└─────────────────────────────────────────────────────────────┘\n");

        Console.WriteLine("   Demonstrating workflow trace construction and replay...\n");

        var workflowTrace = new WorkflowTrace
        {
            TraceName = "content_pipeline",
            OriginalPrompt = "Write a short article about AI testing",
            FinalOutput = "AI testing involves validating agent behavior through deterministic traces, tool accuracy checks, and response quality metrics.",
            Steps = new List<WorkflowTraceStep>
            {
                new()
                {
                    ExecutorId = "planner",
                    ExecutorName = "Planner Agent",
                    Input = "Write a short article about AI testing",
                    Output = "Outline: 1) Intro to AI testing, 2) Trace-based testing, 3) Metrics",
                    StepIndex = 0,
                    DurationMs = 500
                },
                new()
                {
                    ExecutorId = "writer",
                    ExecutorName = "Writer Agent",
                    Input = "Expand outline into article",
                    Output = "AI testing involves validating agent behavior through deterministic traces, tool accuracy checks, and response quality metrics.",
                    StepIndex = 1,
                    DurationMs = 800
                }
            },
            Performance = new WorkflowTracePerformance
            {
                TotalDurationMs = 1300,
                StepCount = 2,
                TotalToolCalls = 0,
                TotalPromptTokens = 200,
                TotalCompletionTokens = 150
            }
        };

        // Save workflow trace
        var workflowPath = Path.Combine(Path.GetTempPath(), "agenteval_sample13_workflow_trace.json");
        var workflowJson = JsonSerializer.Serialize(workflowTrace, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(workflowPath, workflowJson);
        Console.WriteLine($"   💾 Workflow trace saved to: {workflowPath}");

        // Replay
        var replayer = new WorkflowTraceReplayingAgent(workflowTrace);
        var result = await replayer.ExecuteWorkflowAsync("Write a short article about AI testing");

        Console.WriteLine($"\n   Replayed {result.Steps.Count} steps:");
        foreach (var step in result.Steps)
        {
            Console.WriteLine($"     • {step.ExecutorId}: {Truncate(step.Output ?? "", 60)}");
        }
        Console.WriteLine($"   Final: {Truncate(result.FinalOutput, 80)}");
        Console.WriteLine("\n   ✓ Workflow replayed from trace!\n");
    }

    /// <summary>
    /// Demo 4: Construct and replay a streaming trace with timing.
    /// </summary>
    private static async Task DemoStreamingTraceReplay()
    {
        Console.WriteLine("┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ Demo 4: Streaming Trace Replay                              │");
        Console.WriteLine("└─────────────────────────────────────────────────────────────┘\n");

        Console.WriteLine("   Demonstrating streaming trace replay with timing...\n");

        var streamingTrace = new AgentTrace
        {
            TraceName = "streaming_demo",
            AgentName = "RealAgent",
            Entries = new List<TraceEntry>
            {
                new() { Type = TraceEntryType.Request, Index = 0, Prompt = "Tell me a short joke" },
                new()
                {
                    Type = TraceEntryType.Response,
                    Index = 0,
                    Text = "Why did the developer go broke? Because he used up all his cache!",
                    IsStreaming = true,
                    StreamingChunks = new List<TraceStreamChunk>
                    {
                        new() { Index = 0, Text = "Why did ", DelayMs = 0 },
                        new() { Index = 1, Text = "the developer ", DelayMs = 50 },
                        new() { Index = 2, Text = "go broke? ", DelayMs = 50 },
                        new() { Index = 3, Text = "Because he ", DelayMs = 50 },
                        new() { Index = 4, Text = "used up all ", DelayMs = 50 },
                        new() { Index = 5, Text = "his cache!", DelayMs = 50 }
                    }
                }
            },
            Performance = new TracePerformance
            {
                TimeToFirstTokenMs = 100,
                TotalDurationMs = 350
            }
        };

        var replayer = new TraceReplayingAgent(streamingTrace, new TraceReplayOptions
        {
            SimulateStreamingDelay = true
        });

        Console.Write("   Replay: ");
        await foreach (var chunk in replayer.InvokeStreamingAsync("Tell me a short joke"))
        {
            Console.Write(chunk.Text);
        }
        Console.WriteLine();
        Console.WriteLine("\n   ✓ Streaming replayed with timing preserved!\n");
    }

    private static IEvaluableAgent CreateAgent()
    {
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();
        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "TraceableAgent",
            Description = "Agent for trace recording demo",
            ChatOptions = new() { Instructions = "You are a helpful assistant. Keep responses concise (2-3 sentences)." }
        });
        return new MAFAgentAdapter(agent);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        return text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   📼 SAMPLE 13: TRACE RECORD & REPLAY                                        ║
║   Record real agent calls, save traces, replay without AI costs               ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintMissingCredentialsBox()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
   ┌─────────────────────────────────────────────────────────────────────────────┐
   │  ⚠️  SKIPPING SAMPLE 13 - Azure OpenAI Credentials Required               │
   ├─────────────────────────────────────────────────────────────────────────────┤
   │  This sample records real agent traces for deterministic replay.            │
   │                                                                             │
   │  Set these environment variables:                                           │
   │    AZURE_OPENAI_ENDPOINT     - Your Azure OpenAI endpoint                   │
   │    AZURE_OPENAI_API_KEY      - Your API key                                 │
   │    AZURE_OPENAI_DEPLOYMENT   - Chat model (e.g., gpt-4o)                    │
   └─────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine("\n💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • TraceRecordingAgent wraps any IEvaluableAgent to record calls");
        Console.WriteLine("   • TraceSerializer.Serialize/Deserialize for JSON persistence");
        Console.WriteLine("   • TraceReplayingAgent replays saved traces — zero AI costs");
        Console.WriteLine("   • ChatTraceRecorder handles multi-turn conversations");
        Console.WriteLine("   • WorkflowTraceReplayingAgent replays multi-step workflows");
        Console.WriteLine("   • Use traces for deterministic CI testing!");
        Console.WriteLine("\n✅ Sample 13 complete!\n");
    }
}
