// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

using System.ComponentModel;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Assertions;
using AgentEval.Models.Serialization;

namespace AgentEval.Samples;

/// <summary>
/// Sample 09: Real MAF Workflow Evaluation — WorkflowBuilder + InProcessExecution
/// 
/// This demonstrates:
/// - Building a real MAF Workflow using <see cref="WorkflowBuilder"/>
/// - Binding <see cref="ChatClientAgent"/> instances via <c>BindAsExecutor(emitEvents: true)</c>
/// - Streaming events through <see cref="MAFWorkflowAdapter.FromMAFWorkflow"/>
/// - Evaluating a genuine MAF workflow with the AgentEval harness
/// - Graph extraction from actual MAF edge reflection
/// 
/// Azure OpenAI credentials are required — this sample executes real LLM calls
/// through the MAF workflow engine.
/// 
/// ⏱️ Time to understand: 15 minutes
/// ⏱️ Time to run: ~30–90 seconds (4 sequential LLM calls)
/// </summary>
public static class Sample09_WorkflowEvaluationReal
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            PrintMissingCredentialsBox();
            return;
        }

        Console.WriteLine("📝 Step 1: Building real MAF Workflow with WorkflowBuilder...\n");

        var (workflow, executorIds) = CreateWorkflow();

        Console.WriteLine($"   Workflow name : {workflow.Name}");
        Console.WriteLine($"   Start executor: {workflow.StartExecutorId}");
        Console.WriteLine($"   Executors     : {string.Join(" → ", executorIds)}");
        Console.WriteLine($"   Mode          : 🚀 REAL (Azure OpenAI — {AIConfig.ModelDeployment})\n");

        Console.WriteLine("📝 Step 2: Creating MAFWorkflowAdapter.FromMAFWorkflow()...\n");

        var workflowAdapter = MAFWorkflowAdapter.FromMAFWorkflow(
            workflow,
            "ContentPipeline",
            executorIds,
            workflowType: "PromptChaining");

        Console.WriteLine($"   Adapter name   : {workflowAdapter.Name}");
        Console.WriteLine($"   Adapter type   : {workflowAdapter.WorkflowType}");
        Console.WriteLine($"   Graph nodes    : {workflowAdapter.GraphDefinition?.Nodes.Count ?? 0}");
        Console.WriteLine($"   Graph edges    : {workflowAdapter.GraphDefinition?.Edges.Count ?? 0}");
        Console.WriteLine($"   Entry node     : {workflowAdapter.GraphDefinition?.EntryNodeId}");
        Console.WriteLine($"   Exit node(s)   : {string.Join(", ", workflowAdapter.GraphDefinition?.ExitNodeIds ?? [])}\n");

        Console.WriteLine("📝 Step 3: Creating workflow test case...\n");

        var testCase = new WorkflowTestCase
        {
            Name = "Content Generation Pipeline — AI Testing Article",
            Input = "Write a comprehensive article about AI agent evaluation testing, covering both traditional and modern approaches.",
            Description = "Tests the full content creation workflow through a real MAF WorkflowBuilder pipeline",
            ExpectedExecutors = ["Planner", "Researcher", "Writer", "Editor"],
            StrictExecutorOrder = true,
            ExpectedOutputContains = "evaluation",
            MaxDuration = TimeSpan.FromMinutes(3), // Generous for real LLM calls
            Tags = ["content", "pipeline", "real-workflow", "maf-workflowbuilder"]
        };

        Console.WriteLine($"   Test     : {testCase.Name}");
        Console.WriteLine($"   Input    : \"{testCase.Input[..Math.Min(70, testCase.Input.Length)]}...\"");
        Console.WriteLine($"   Flow     : {string.Join(" → ", testCase.ExpectedExecutors!)}");
        Console.WriteLine($"   Timeout  : {testCase.MaxDuration!.Value.TotalSeconds}s\n");

        Console.WriteLine("📝 Step 4: Running workflow test with full evaluation...\n");
        Console.WriteLine("   ⏳ Executing real LLM calls — this may take 30–90 seconds...\n");

        var harness = new WorkflowEvaluationHarness(verbose: true);
        var testOptions = new WorkflowTestOptions
        {
            Timeout = TimeSpan.FromMinutes(5),
            Verbose = true
        };

        WorkflowTestResult testResult;
        try
        {
            // Hard timeout wrapper — MAF's InProcessExecution may not honor
            // cancellation tokens during active LLM calls, so we enforce a
            // ceiling via Task.WhenAny to prevent indefinite hangs.
            var workflowTask = harness.RunWorkflowTestAsync(workflowAdapter, testCase, testOptions);
            var hardTimeout = Task.Delay(TimeSpan.FromMinutes(5));

            if (await Task.WhenAny(workflowTask, hardTimeout) == hardTimeout)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("   ⏱️ Workflow exceeded 5-minute hard timeout — moving on.");
                Console.ResetColor();
                Console.WriteLine("   💡 This can happen with slow LLM backends or long content-generation chains.");
                return;
            }

            testResult = await workflowTask;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"   ❌ Workflow test failed: {ex.Message}");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("   💡 Tip: Ensure your Azure OpenAI deployment supports the model and has enough quota.");
            return;
        }

        Console.WriteLine("\n📊 DETAILED WORKFLOW RESULTS:");
        Console.WriteLine(new string('═', 80));

        if (testResult.ExecutionResult != null)
        {
            var result = testResult.ExecutionResult;

            Console.WriteLine($"   🎯 Overall : {(testResult.Passed ? "✅ PASSED" : "❌ FAILED")}");
            Console.WriteLine($"   ⏱️ Duration: {result.TotalDuration.TotalMilliseconds:F0}ms ({result.TotalDuration.TotalSeconds:F1}s)");
            Console.WriteLine($"   🔗 Steps   : {result.Steps.Count}");
            Console.WriteLine($"   🔧 Tools   : {result.Steps.Sum(s => s.ToolCalls?.Count ?? 0)}");
            Console.WriteLine($"   ❌ Errors   : {result.Errors?.Count ?? 0}\n");

            // Execution timeline
            Console.WriteLine("   📈 EXECUTION TIMELINE:");
            foreach (var step in result.Steps)
            {
                var startMs = step.StartOffset.TotalMilliseconds;
                var durMs = step.Duration.TotalMilliseconds;
                var toolInfo = step.HasToolCalls ? $" 🔧×{step.ToolCalls!.Count}" : "";

                Console.WriteLine($"      {step.StepIndex + 1}. [{startMs:F0}ms] {step.ExecutorId} ({durMs:F0}ms){toolInfo}");

                if (!string.IsNullOrEmpty(step.Output))
                {
                    var snippet = step.Output.Length > 120 ? step.Output[..117] + "..." : step.Output;
                    Console.WriteLine($"         → \"{snippet}\"");
                }
                Console.WriteLine();
            }
        }

        Console.WriteLine("📝 Step 6: Workflow assertions...\n");

        if (testResult.ExecutionResult != null)
        {
            try
            {
                var result = testResult.ExecutionResult;

                // Basic workflow structure assertions
                result.Should()
                    .HaveStepCount(4, because: "pipeline has 4 distinct stages")
                    .HaveExecutedInOrder("Planner", "Researcher", "Writer", "Editor")
                    .HaveCompletedWithin(TimeSpan.FromMinutes(3), because: "reasonable time for content generation")
                    .HaveNoErrors(because: "clean execution is required")
                    .HaveNonEmptyOutput()
                    .Validate();

                Console.WriteLine("   ✅ Basic workflow structure assertions PASSED!\n");

                // Per-executor assertions
                result.Should()
                    .ForExecutor("Planner")
                        .HaveNonEmptyOutput()
                        .HaveCompletedWithin(TimeSpan.FromSeconds(60), because: "planning should be reasonably fast")
                        .And()
                    .ForExecutor("Researcher")
                        .HaveNonEmptyOutput()
                        .And()
                    .ForExecutor("Writer")
                        .HaveNonEmptyOutput()
                        .And()
                    .ForExecutor("Editor")
                        .HaveNonEmptyOutput()
                        .And()
                    .Validate();

                Console.WriteLine("   ✅ Per-executor assertions PASSED!\n");

                // Graph structure assertions
                result.Should()
                    .HaveGraphStructure()
                    .HaveNodes("Planner", "Researcher", "Writer", "Editor")
                    .HaveEntryPoint("Planner", because: "planning is the starting point")
                    .HaveTraversedEdge("Researcher", "Writer")
                    .HaveUsedEdgeType(EdgeType.Sequential)
                    .HaveExecutionPath("Planner", "Researcher", "Writer", "Editor")
                    .Validate();

                Console.WriteLine("   ✅ Graph structure and routing assertions PASSED!\n");
            }
            catch (WorkflowAssertionException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"   ❌ Workflow assertion failed: {ex.Message}");
                if (ex.Data.Contains("Suggestions"))
                    Console.WriteLine($"   💡 Suggestion: {ex.Data["Suggestions"]}");
                Console.ResetColor();
                Console.WriteLine();
            }
        }

        Console.WriteLine("📝 Step 7: Generating workflow visualization...\n");

        if (testResult.ExecutionResult != null)
        {
            // Mermaid diagram
            var mermaid = WorkflowSerializer.ToMermaid(testResult.ExecutionResult);
            Console.WriteLine("   🎨 Mermaid diagram:");
            Console.ForegroundColor = ConsoleColor.Cyan;
            foreach (var line in mermaid.Split('\n').Take(15))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    Console.WriteLine($"      {line}");
            }
            Console.ResetColor();

            var mermaidPath = Path.Combine(Path.GetTempPath(), "agenteval_sample09_workflow.mmd");
            await File.WriteAllTextAsync(mermaidPath, mermaid);
            Console.WriteLine($"\n   💾 Full Mermaid diagram saved to: {mermaidPath}");
            Console.WriteLine("   💡 Open at https://mermaid.live or paste into any Mermaid-compatible viewer!");

            // Timeline JSON
            var timeline = WorkflowSerializer.ToTimelineJson(testResult.ExecutionResult);
            Console.WriteLine($"\n   📊 Timeline JSON generated: {timeline.Length} characters\n");
        }

        PrintKeyTakeaways();
    }

    /// <summary>
    /// Builds a real MAF Workflow using WorkflowBuilder with 4 ChatClientAgent executors.
    /// Planner → Researcher → Writer → Editor (sequential prompt-chaining pipeline).
    /// </summary>
    private static (Workflow workflow, string[] executorIds) CreateWorkflow()
    {
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        // ── Create 4 agents with distinct system prompts ──

        var planner = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "Planner",
            Description = "Plans content structure",
            ChatOptions = new() { Instructions = """
                You are a content planning specialist. Given a writing request, create a clear, structured plan.
                
                Your output should include:
                1. A logical outline with main sections and sub-topics
                2. Key points to research for each section
                3. Target audience and appropriate tone
                4. Suggested structure and approximate word count
                
                Be concise and actionable — your plan will be passed to a researcher.
                """ }
        });

        var researcher = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "Researcher",
            Description = "Researches topics based on a plan",
            ChatOptions = new() { Instructions = """
                You are a research specialist. Given a content plan, gather comprehensive information.
                
                Your role:
                1. Take the content plan and identify research needs for each section
                2. Synthesize information into well-organized research notes
                3. Identify key facts, data points, and expert insights
                4. Note current trends and credible sources
                
                Organize your findings clearly — they will be passed to a content writer.
                """ }
        });

        var writer = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "Writer",
            Description = "Writes comprehensive articles from research",
            ChatOptions = new() { Instructions = """
                You are an experienced technical content writer. Given research findings, create an engaging article.
                
                Your approach:
                1. Transform research into well-structured, flowing prose
                2. Use clear, accessible language while maintaining technical accuracy
                3. Include practical examples and actionable insights
                4. Create an engaging introduction and strong conclusion
                5. Use headings, lists, and formatting for readability
                
                Focus on practical applications and real-world relevance.
                """ }
        });

        var editor = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "Editor",
            Description = "Polishes and refines articles",
            ChatOptions = new() { Instructions = """
                You are a professional editor. Given a draft article, polish it for publication.
                
                Your responsibilities:
                1. Review for clarity, flow, and engagement
                2. Improve sentence structure and word choice
                3. Ensure consistent tone and style
                4. Fix grammar, punctuation, and formatting
                5. Enhance transitions and logical flow between sections
                
                Deliver publication-ready content. Maintain the author's voice while elevating quality.
                """ }
        });

        // ── Bind agents as workflow executors (emitEvents: true for streaming) ──

        var plannerBinding = planner.BindAsExecutor(emitEvents: true);
        var researcherBinding = researcher.BindAsExecutor(emitEvents: true);
        var writerBinding = writer.BindAsExecutor(emitEvents: true);
        var editorBinding = editor.BindAsExecutor(emitEvents: true);

        // ── Build the sequential workflow: Planner → Researcher → Writer → Editor ──

        var workflow = new WorkflowBuilder(plannerBinding)
            .AddEdge(plannerBinding, researcherBinding)
            .AddEdge(researcherBinding, writerBinding)
            .AddEdge(writerBinding, editorBinding)
            .WithOutputFrom(editorBinding)
            .WithName("ContentPipeline")
            .WithDescription("Sequential content creation pipeline: plan → research → write → edit")
            .Build();

        return (workflow, ["Planner", "Researcher", "Writer", "Editor"]);
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   🔄 SAMPLE 09: REAL MAF WORKFLOW EVALUATION                                 ║
║   WorkflowBuilder + InProcessExecution                                        ║
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
   │  ⚠️  SKIPPING SAMPLE 09 - Azure OpenAI Credentials Required               │
   ├─────────────────────────────────────────────────────────────────────────────┤
   │  This sample runs a real 4-agent MAF workflow pipeline.                     │
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
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
┌─────────────────────────────────────────────────────────────────────────────────┐
│                              🎯 KEY TAKEAWAYS                                   │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                 │
│  1. Real MAF Workflow via WorkflowBuilder:                                      │
│     var workflow = new WorkflowBuilder(plannerBinding)                          │
│         .AddEdge(plannerBinding, researcherBinding)                             │
│         .Build();                                                              │
│                                                                                 │
│  2. Agent binding with event emission:                                          │
│     var binding = agent.BindAsExecutor(emitEvents: true);                      │
│     Events: ExecutorInvoked, AgentResponseUpdate, ExecutorCompleted, ...       │
│                                                                                 │
│  3. One-line adapter from real MAF Workflow:                                    │
│     var adapter = MAFWorkflowAdapter.FromMAFWorkflow(                          │
│         workflow, ""Pipeline"", executorIds, ""PromptChaining"");                 │
│     Wires EventBridge + GraphExtractor automatically                           │
│                                                                                 │
│  4. Graph extracted from MAF's ReflectEdges():                                  │
│     Nodes, edges, entry/exit points — no manual graph definition needed        │
│                                                                                 │
│  5. Same assertion APIs work for mock AND real workflows:                       │
│     result.Should().HaveStepCount(4).HaveExecutedInOrder(...)                  │
│     The evaluation harness abstracts away the execution mode                   │
│                                                                                 │
│  6. See also Sample10 for workflow with tools:                                  │
│     Sample09  → Content pipeline (plan → research → write → edit)              │
│     Sample10  → TripPlanner with tools (tools tracked per executor)            │
│                                                                                 │
└─────────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }
}