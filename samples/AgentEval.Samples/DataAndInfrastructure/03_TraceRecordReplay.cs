// Copyright (c) 2025 AgentEval. All rights reserved.
// Licensed under the MIT License.

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Tracing;

namespace AgentEval.Samples;

/// <summary>
/// Sample F3: Trace Record &amp; Replay
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
public static class TraceRecordReplay
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

            Console.WriteLine($"   Prompt: What is the capital of Japan and what is it known for?");
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
    /// Demo 3: Record a real MAF workflow execution, save the trace, then replay it.
    /// </summary>
    private static async Task DemoWorkflowTraceReplay()
    {
        Console.WriteLine("┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ Demo 3: Workflow Trace Record & Replay (REAL)               │");
        Console.WriteLine("└─────────────────────────────────────────────────────────────┘\n");

        // Step 1: BUILD a real 2-step MAF workflow (Planner → Writer)
        Console.WriteLine("🏗️  Building real MAF Workflow (Planner → Writer)...\n");

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        var planner = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "Planner",
            Description = "Plans content structure",
            ChatOptions = new() { Instructions = "You are a content planner. Given a topic, create a brief outline with 3 key points. Be concise (3-4 lines max)." }
        });

        var writer = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "Writer",
            Description = "Writes content from a plan",
            ChatOptions = new() { Instructions = "You are a concise writer. Given an outline, write a brief paragraph (3-4 sentences) covering the key points." }
        });

        var plannerBinding = planner.BindAsExecutor(emitEvents: true);
        var writerBinding = writer.BindAsExecutor(emitEvents: true);

        var workflow = new WorkflowBuilder(plannerBinding)
            .AddEdge(plannerBinding, writerBinding)
            .WithOutputFrom(writerBinding)
            .WithName("ContentPipeline")
            .WithDescription("Simple plan → write pipeline")
            .Build();

        var workflowAdapter = MAFWorkflowAdapter.FromMAFWorkflow(
            workflow, "ContentPipeline", ["Planner", "Writer"], workflowType: "PromptChaining");

        Console.WriteLine($"   Workflow: {workflowAdapter.Name} ({string.Join(" → ", workflowAdapter.ExecutorIds)})");

        // Step 2: RECORD the workflow execution
        Console.WriteLine("\n📼 RECORDING real workflow execution...\n");
        Console.WriteLine("   ⏳ Executing real LLM calls — this may take 10–30 seconds...\n");

        var prompt = "Write about the benefits of trace-based testing for AI agents";
        WorkflowTrace recordedTrace;

        await using (var recorder = new WorkflowTraceRecorder(workflowAdapter, "content_pipeline"))
        {
            var result = await recorder.ExecuteWorkflowAsync(prompt);

            Console.WriteLine($"   Prompt: {Truncate(prompt, 60)}");
            Console.WriteLine($"   Steps executed: {result.Steps.Count}");
            foreach (var step in result.Steps)
            {
                Console.WriteLine($"     • {step.ExecutorId}: {Truncate(step.Output ?? "", 60)}");
            }
            Console.WriteLine($"   Final output: {Truncate(result.FinalOutput, 80)}");

            recordedTrace = recorder.Trace;
        }

        // Save workflow trace
        var workflowPath = Path.Combine(Path.GetTempPath(), "agenteval_sample13_workflow_trace.json");
        await WorkflowTraceSerializer.SaveToFileAsync(recordedTrace, workflowPath);
        var workflowJson = await WorkflowTraceSerializer.SerializeToStringAsync(recordedTrace);
        Console.WriteLine($"\n   💾 Workflow trace saved to: {workflowPath} ({workflowJson.Length} bytes)");

        // Step 3: REPLAY from saved trace (no AI calls!)
        Console.WriteLine("\n▶️  REPLAYING workflow from saved trace (no AI service calls)...\n");

        var loadedTrace = await WorkflowTraceSerializer.LoadFromFileAsync(workflowPath);
        var replayer = new WorkflowTraceReplayingAgent(loadedTrace, new WorkflowTraceReplayOptions
        {
            SimulateExecutionDelay = true,
            DelayMultiplier = 0.1  // 10% of original timing for fast demo
        });

        var replayResult = await replayer.ExecuteWorkflowAsync(prompt);

        Console.WriteLine($"   Replayed {replayResult.Steps.Count} steps:");
        foreach (var step in replayResult.Steps)
        {
            Console.WriteLine($"     • {step.ExecutorId}: {Truncate(step.Output ?? "", 60)}");
        }
        Console.WriteLine($"   Final: {Truncate(replayResult.FinalOutput, 80)}");
        Console.WriteLine($"   Executions replayed: {replayer.ExecutionCount}");
        Console.WriteLine("\n   ✓ Workflow replayed from saved trace — zero AI calls!\n");
    }

    /// <summary>
    /// Demo 4: Record real streaming output, save the trace, then replay with timing.
    /// </summary>
    private static async Task DemoStreamingTraceReplay()
    {
        Console.WriteLine("┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ Demo 4: Streaming Trace Record & Replay (REAL)              │");
        Console.WriteLine("└─────────────────────────────────────────────────────────────┘\n");

        var agent = CreateAgent();

        // Step 1: RECORD real streaming output
        Console.WriteLine("📼 RECORDING real streaming response...\n");

        var prompt = "Tell me a short joke about software testing";
        AgentTrace recordedTrace;

        await using (var recorder = new TraceRecordingAgent(agent, "streaming_demo"))
        {
            Console.Write("   Live stream: ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            await foreach (var chunk in recorder.InvokeStreamingAsync(prompt))
            {
                Console.Write(chunk.Text);
            }
            Console.ResetColor();
            Console.WriteLine();

            recordedTrace = recorder.Trace;
            var streamingEntry = recordedTrace.Entries.FirstOrDefault(e => e.Type == TraceEntryType.Response);
            var chunkCount = streamingEntry?.StreamingChunks?.Count ?? 0;
            Console.WriteLine($"\n   Streaming chunks recorded: {chunkCount}");
        }

        // Save trace
        var tracePath = Path.Combine(Path.GetTempPath(), "agenteval_sample13_streaming_trace.json");
        await TraceSerializer.SaveToFileAsync(recordedTrace, tracePath);
        var traceJson = await TraceSerializer.SerializeToStringAsync(recordedTrace);
        Console.WriteLine($"   💾 Streaming trace saved to: {tracePath} ({traceJson.Length} bytes)");

        // Step 2: REPLAY with timing preserved
        Console.WriteLine("\n▶️  REPLAYING streaming from saved trace (with timing)...\n");

        var loadedTrace = await TraceSerializer.LoadFromFileAsync(tracePath);
        var replayer = new TraceReplayingAgent(loadedTrace, new TraceReplayOptions
        {
            SimulateStreamingDelay = true
        });

        Console.Write("   Replay:      ");
        Console.ForegroundColor = ConsoleColor.Green;
        await foreach (var chunk in replayer.InvokeStreamingAsync(prompt))
        {
            Console.Write(chunk.Text);
        }
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine($"\n   IsComplete: {replayer.IsComplete}");
        Console.WriteLine("\n   ✓ Streaming replayed with timing preserved — zero AI calls!\n");
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
║   📼 SAMPLE F3: TRACE RECORD & REPLAY                                        ║
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
   │  ⚠️  SKIPPING SAMPLE F3 - Azure OpenAI Credentials Required               │
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
        Console.WriteLine("   • TraceSerializer.SaveToFileAsync/LoadFromFileAsync for JSON persistence");
        Console.WriteLine("   • TraceReplayingAgent replays saved traces — zero AI costs");
        Console.WriteLine("   • ChatTraceRecorder handles multi-turn conversations");
        Console.WriteLine("   • WorkflowTraceReplayingAgent replays multi-step workflows");
        Console.WriteLine("   • Use traces for deterministic CI testing!");
        Console.WriteLine("\n✅ Sample F3 complete!\n");
    }
}
