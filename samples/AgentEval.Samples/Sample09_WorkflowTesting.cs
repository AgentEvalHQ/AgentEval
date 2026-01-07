// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

using AgentEval.Models;
using AgentEval.Assertions;
using AgentEval.Models.Serialization;

namespace AgentEval.Samples;

/// <summary>
/// Sample 09: Workflow Testing - Multi-agent orchestration testing
/// 
/// This demonstrates:
/// - Testing multi-agent workflows (AgentGroupChat, Semantic Kernel, etc.)
/// - Workflow assertions for execution paths
/// - Edge assertions for routing decisions
/// - Mermaid diagram generation
/// - Timeline export for debugging
/// 
/// ⏱️ Time to understand: 10 minutes
/// </summary>
public static class Sample09_WorkflowTesting
{
    public static async Task RunAsync()
    {
        PrintHeader();

        // ═══════════════════════════════════════════════════════════════
        // STEP 1: Simulate a multi-agent workflow execution
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 1: Creating a multi-agent workflow result...\n");
        
        var workflowResult = CreateSampleWorkflowResult();
        
        Console.WriteLine($"   Workflow: Content Generation Pipeline");
        Console.WriteLine($"   Executors: {workflowResult.Steps.Count}");
        Console.WriteLine($"   Duration: {workflowResult.TotalDuration.TotalMilliseconds:F0}ms");
        Console.WriteLine($"   Success: {(workflowResult.IsSuccess ? "✅ Yes" : "❌ No")}");
        Console.WriteLine();
        
        Console.WriteLine("   📊 Execution Path:");
        foreach (var step in workflowResult.Steps)
        {
            Console.WriteLine($"      {step.StepIndex + 1}. {step.ExecutorId} ({step.Duration.TotalMilliseconds:F0}ms)");
        }

        // ═══════════════════════════════════════════════════════════════
        // STEP 2: Workflow Assertions
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 2: Running workflow assertions...\n");
        
        try
        {
            workflowResult.Should()
                .HaveStepCount(4, because: "content pipeline requires 4 stages")
                .HaveExecutedInOrder("planner", "researcher", "writer", "editor")
                .HaveCompletedWithin(TimeSpan.FromSeconds(5),
                    because: "SLA requires completion under 5 seconds")
                .HaveNoErrors(because: "partial execution is not acceptable")
                .HaveNonEmptyOutput()
                .HaveFinalOutputContaining("article")
                .Validate();
            
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("   ✅ All workflow assertions passed!");
            Console.ResetColor();
        }
        catch (WorkflowAssertionException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"   ❌ Workflow assertion failed: {ex.Message}");
            Console.ResetColor();
        }

        // ═══════════════════════════════════════════════════════════════
        // STEP 3: Executor Step Assertions
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 3: Asserting on individual executors...\n");
        
        try
        {
            workflowResult.Should()
                .ForExecutor("researcher")
                    .HaveOutputContaining("research")
                    .HaveCompletedWithin(TimeSpan.FromSeconds(2),
                        because: "research phase has strict timeout")
                    .HaveCalledTool("search_web",
                        because: "researcher must use search for data")
                    .And()
                .ForExecutor("writer")
                    .HaveNonEmptyOutput()
                    .HaveToolCallCount(0)  // Writer just writes, no tools
                    .And()
                .Validate();
            
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("   ✅ All executor assertions passed!");
            Console.ResetColor();
        }
        catch (WorkflowAssertionException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"   ❌ Executor assertion failed: {ex.Message}");
            Console.ResetColor();
        }

        // ═══════════════════════════════════════════════════════════════
        // STEP 4: Edge and Graph Assertions
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 4: Asserting on edges and graph structure...\n");
        
        try
        {
            workflowResult.Should()
                .HaveGraphStructure()
                .HaveNodes("planner", "researcher", "writer", "editor")
                .HaveEntryPoint("planner",
                    because: "planner must be the starting point")
                .HaveTraversedEdge("researcher", "writer")
                .HaveUsedEdgeType(EdgeType.Sequential)
                .HaveExecutionPath("planner", "researcher", "writer", "editor")
                .Validate();
            
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("   ✅ All graph assertions passed!");
            Console.ResetColor();
        }
        catch (WorkflowAssertionException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"   ❌ Graph assertion failed: {ex.Message}");
            Console.ResetColor();
        }

        // ═══════════════════════════════════════════════════════════════
        // STEP 5: Mermaid Diagram Export
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 5: Generating Mermaid diagram...\n");
        
        var mermaid = WorkflowSerializer.ToMermaid(workflowResult);
        Console.WriteLine("   Generated Mermaid diagram:\n");
        Console.ForegroundColor = ConsoleColor.Cyan;
        foreach (var line in mermaid.Split('\n').Take(15))
        {
            Console.WriteLine($"   {line}");
        }
        Console.ResetColor();
        Console.WriteLine("\n   💡 Paste this into https://mermaid.live to visualize!");

        // ═══════════════════════════════════════════════════════════════
        // STEP 6: Conditional Workflow Example
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 6: Conditional workflow testing...\n");
        
        var conditionalResult = CreateConditionalWorkflowResult();
        
        try
        {
            conditionalResult.Should()
                .HaveConditionalRouting()
                .ForExecutor("approver")
                    .HaveBeenConditionallyRouted()
                    .And()
                .HaveTraversedEdge("reviewer", "approver")
                .ForEdge("reviewer", "approver")
                    .BeOfType(EdgeType.Conditional)
                    .HaveConditionResult(true)
                    .And()
                .Validate();
            
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("   ✅ Conditional workflow assertions passed!");
            Console.ResetColor();
        }
        catch (WorkflowAssertionException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"   ❌ Conditional assertion failed: {ex.Message}");
            Console.ResetColor();
        }

        // ═══════════════════════════════════════════════════════════════
        // STEP 7: JSON & Timeline Export
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 7: Exporting to JSON and Timeline...\n");
        
        var json = WorkflowSerializer.ToJson(workflowResult);
        Console.WriteLine($"   JSON export: {json.Length} characters");
        
        var timeline = WorkflowSerializer.ToTimelineJson(workflowResult);
        Console.WriteLine($"   Timeline export: {timeline.Length} characters");
        
        Console.WriteLine("\n   Sample JSON structure:");
        Console.ForegroundColor = ConsoleColor.Cyan;
        // Show first few lines
        foreach (var line in json.Split('\n').Take(10))
        {
            Console.WriteLine($"   {line}");
        }
        Console.WriteLine("   ...");
        Console.ResetColor();

        await Task.Delay(1); // Keep async signature
        
        // ═══════════════════════════════════════════════════════════════
        // KEY TAKEAWAYS
        // ═══════════════════════════════════════════════════════════════
        PrintKeyTakeaways();
    }

    private static WorkflowExecutionResult CreateSampleWorkflowResult()
    {
        var now = DateTimeOffset.UtcNow;
        
        // Create graph structure
        var graph = new WorkflowGraphSnapshot
        {
            EntryNodeId = "planner",
            ExitNodeIds = ["editor"],
            Nodes = 
            [
                new WorkflowNode { NodeId = "planner", DisplayName = "Planner Agent", IsEntryPoint = true },
                new WorkflowNode { NodeId = "researcher", DisplayName = "Research Agent" },
                new WorkflowNode { NodeId = "writer", DisplayName = "Writer Agent" },
                new WorkflowNode { NodeId = "editor", DisplayName = "Editor Agent", IsExitNode = true }
            ],
            Edges = 
            [
                new WorkflowEdge { EdgeId = "e1", SourceExecutorId = "planner", TargetExecutorId = "researcher" },
                new WorkflowEdge { EdgeId = "e2", SourceExecutorId = "researcher", TargetExecutorId = "writer" },
                new WorkflowEdge { EdgeId = "e3", SourceExecutorId = "writer", TargetExecutorId = "editor" }
            ],
            TraversedEdges = 
            [
                new EdgeExecution { EdgeId = "e1", SourceExecutorId = "planner", TargetExecutorId = "researcher", TraversedAt = TimeSpan.FromMilliseconds(100) },
                new EdgeExecution { EdgeId = "e2", SourceExecutorId = "researcher", TargetExecutorId = "writer", TraversedAt = TimeSpan.FromMilliseconds(600) },
                new EdgeExecution { EdgeId = "e3", SourceExecutorId = "writer", TargetExecutorId = "editor", TraversedAt = TimeSpan.FromMilliseconds(900) }
            ]
        };

        return new WorkflowExecutionResult
        {
            FinalOutput = "Here is the completed article about AI testing frameworks...",
            TotalDuration = TimeSpan.FromMilliseconds(1200),
            OriginalPrompt = "Write an article about AI testing",
            Graph = graph,
            Steps = 
            [
                new ExecutorStep
                {
                    ExecutorId = "planner",
                    ExecutorName = "Planner Agent",
                    StepIndex = 0,
                    Output = "Plan: 1) Research topic, 2) Write draft, 3) Edit for quality",
                    Duration = TimeSpan.FromMilliseconds(100),
                    StartOffset = TimeSpan.Zero
                },
                new ExecutorStep
                {
                    ExecutorId = "researcher",
                    ExecutorName = "Research Agent",
                    StepIndex = 1,
                    Output = "Research findings: AI testing frameworks include...",
                    Duration = TimeSpan.FromMilliseconds(500),
                    StartOffset = TimeSpan.FromMilliseconds(100),
                    ToolCalls = 
                    [
                        new ToolCallRecord { Name = "search_web", CallId = "call_1", Order = 1, Result = "Search results..." }
                    ]
                },
                new ExecutorStep
                {
                    ExecutorId = "writer",
                    ExecutorName = "Writer Agent",
                    StepIndex = 2,
                    Output = "Draft: AI Testing Frameworks: A Comprehensive Guide...",
                    Duration = TimeSpan.FromMilliseconds(300),
                    StartOffset = TimeSpan.FromMilliseconds(600)
                },
                new ExecutorStep
                {
                    ExecutorId = "editor",
                    ExecutorName = "Editor Agent",
                    StepIndex = 3,
                    Output = "Final article with improvements...",
                    Duration = TimeSpan.FromMilliseconds(300),
                    StartOffset = TimeSpan.FromMilliseconds(900)
                }
            ]
        };
    }

    private static WorkflowExecutionResult CreateConditionalWorkflowResult()
    {
        var graph = new WorkflowGraphSnapshot
        {
            EntryNodeId = "reviewer",
            ExitNodeIds = ["approver", "rejector"],
            Nodes = 
            [
                new WorkflowNode { NodeId = "reviewer", DisplayName = "Content Reviewer", IsEntryPoint = true },
                new WorkflowNode { NodeId = "approver", DisplayName = "Approval Agent", IsExitNode = true },
                new WorkflowNode { NodeId = "rejector", DisplayName = "Rejection Agent", IsExitNode = true }
            ],
            Edges = 
            [
                new WorkflowEdge { EdgeId = "approve_edge", SourceExecutorId = "reviewer", TargetExecutorId = "approver", EdgeType = EdgeType.Conditional, Condition = "quality >= 80" },
                new WorkflowEdge { EdgeId = "reject_edge", SourceExecutorId = "reviewer", TargetExecutorId = "rejector", EdgeType = EdgeType.Conditional, Condition = "quality < 80" }
            ],
            TraversedEdges = 
            [
                new EdgeExecution { EdgeId = "approve_edge", SourceExecutorId = "reviewer", TargetExecutorId = "approver", EdgeType = EdgeType.Conditional, ConditionResult = true, TraversedAt = TimeSpan.FromMilliseconds(100), RoutingReason = "Quality score 92 >= 80" }
            ]
        };

        return new WorkflowExecutionResult
        {
            FinalOutput = "Content approved for publication.",
            TotalDuration = TimeSpan.FromMilliseconds(250),
            Graph = graph,
            RoutingDecisions = 
            [
                new RoutingDecision
                {
                    DeciderExecutorId = "reviewer",
                    PossibleEdgeIds = ["approve_edge", "reject_edge"],
                    SelectedEdgeId = "approve_edge",
                    EvaluatedValue = "92",
                    SelectionReason = "Quality score 92 >= 80 threshold"
                }
            ],
            Steps = 
            [
                new ExecutorStep
                {
                    ExecutorId = "reviewer",
                    ExecutorName = "Content Reviewer",
                    StepIndex = 0,
                    Output = "Content quality score: 92/100",
                    Duration = TimeSpan.FromMilliseconds(100),
                    StartOffset = TimeSpan.Zero
                },
                new ExecutorStep
                {
                    ExecutorId = "approver",
                    ExecutorName = "Approval Agent",
                    StepIndex = 1,
                    Output = "Content approved for publication.",
                    Duration = TimeSpan.FromMilliseconds(150),
                    StartOffset = TimeSpan.FromMilliseconds(100),
                    IncomingEdge = new EdgeExecution 
                    { 
                        EdgeId = "approve_edge", 
                        SourceExecutorId = "reviewer", 
                        TargetExecutorId = "approver", 
                        EdgeType = EdgeType.Conditional,
                        ConditionResult = true 
                    }
                }
            ]
        };
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║                     Sample 09: Workflow Testing                               ║
║                                                                               ║
║   Learn how to:                                                               ║
║   • Test multi-agent workflows (AgentGroupChat, Semantic Kernel, etc.)        ║
║   • Assert on execution paths and individual executors                        ║
║   • Validate edge routing and conditional branching                           ║
║   • Export to Mermaid diagrams and timeline JSON                              ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
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
│  1. WorkflowExecutionResult captures multi-agent execution:                     │
│     var result = await myWorkflow.RunAsync(prompt);                             │
│                                                                                 │
│  2. Fluent workflow assertions:                                                 │
│     result.Should()                                                             │
│         .HaveStepCount(4)                                                       │
│         .HaveExecutedInOrder(""planner"", ""researcher"", ""writer"")               │
│         .HaveNoErrors()                                                         │
│         .Validate();                                                            │
│                                                                                 │
│  3. Assert on individual executors:                                             │
│     result.Should()                                                             │
│         .ForExecutor(""researcher"")                                              │
│             .HaveCalledTool(""search_web"")                                       │
│             .HaveCompletedWithin(TimeSpan.FromSeconds(2))                       │
│         .And()                                                                  │
│         .Validate();                                                            │
│                                                                                 │
│  4. Assert on edges and routing:                                                │
│     result.Should()                                                             │
│         .HaveTraversedEdge(""reviewer"", ""approver"")                              │
│         .ForEdge(""reviewer"", ""approver"")                                        │
│             .BeOfType(EdgeType.Conditional)                                     │
│             .HaveConditionResult(true)                                          │
│         .And()                                                                  │
│         .Validate();                                                            │
│                                                                                 │
│  5. Export for visualization:                                                   │
│     var mermaid = WorkflowSerializer.ToMermaid(result);                         │
│     var json = WorkflowSerializer.ToJson(result);                               │
│     var timeline = WorkflowSerializer.ToTimelineJson(result);                   │
│                                                                                 │
└─────────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }
}
