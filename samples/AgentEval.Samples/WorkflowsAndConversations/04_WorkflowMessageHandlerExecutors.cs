// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Microsoft.Agents.AI.Workflows;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Assertions;
using AgentEval.Models.Serialization;

namespace AgentEval.Samples;

/// <summary>
/// Workflow with [MessageHandler] Source-Generated Executors
/// 
/// This sample demonstrates:
/// - MAF's recommended [MessageHandler] partial class executor pattern
/// - Source-generated routing (zero reflection, AOT-compatible)
/// - Evaluating [MessageHandler] workflows with AgentEval's standard assertions
/// - Deterministic pipelines: no LLM needed — runs 100% offline
/// 
/// Pattern difference:
///   Old: Executor&lt;TIn, TOut&gt; with virtual HandleAsync (reflection-based)
///   New: partial class Executor + [MessageHandler] methods (source-generated)
///   Both work identically with AgentEval's MAFWorkflowAdapter.
/// 
/// Pipeline: Sanitizer → Classifier → Formatter
///   Input: raw text → sanitized → classified → formatted output
/// 
/// ⏱️ Time to understand: 10 minutes
/// ⏱️ Time to run: ~1 second (no LLM calls)
/// </summary>
public static partial class WorkflowMessageHandlerExecutors
{
    public static async Task RunAsync()
    {
        PrintHeader();

        Console.WriteLine("📝 Step 1: Building workflow with [MessageHandler] executors...\n");

        var (workflow, executorIds) = CreateWorkflow();

        Console.WriteLine($"   Workflow name : {workflow.Name}");
        Console.WriteLine($"   Start executor: {workflow.StartExecutorId}");
        Console.WriteLine($"   Executors     : {string.Join(" → ", executorIds)}");
        Console.WriteLine($"   Mode          : 💻 OFFLINE (deterministic — no LLM calls)\n");

        Console.WriteLine("📝 Step 2: Creating MAFWorkflowAdapter (same API as LLM workflows)...\n");

        var workflowAdapter = MAFWorkflowAdapter.FromMAFWorkflow(
            workflow,
            "TextPipeline",
            executorIds,
            workflowType: "PromptChaining");

        Console.WriteLine($"   Adapter name   : {workflowAdapter.Name}");
        Console.WriteLine($"   Graph nodes    : {workflowAdapter.GraphDefinition?.Nodes.Count ?? 0}");
        Console.WriteLine($"   Graph edges    : {workflowAdapter.GraphDefinition?.Edges.Count ?? 0}");
        Console.WriteLine($"   Entry node     : {workflowAdapter.GraphDefinition?.EntryNodeId}");
        Console.WriteLine($"   Exit node(s)   : {string.Join(", ", workflowAdapter.GraphDefinition?.ExitNodeIds ?? [])}\n");

        Console.WriteLine("📝 Step 3: Creating workflow test case...\n");

        var testCase = new WorkflowTestCase
        {
            Name = "Text Pipeline — Sanitize, Classify, Format",
            Input = "  Hello, WORLD!  This is a <b>TEST</b> message from sample #11.  ",
            Description = "Tests deterministic [MessageHandler] executor pipeline (no LLM)",
            ExpectedExecutors = ["Sanitizer", "Classifier", "Formatter"],
            StrictExecutorOrder = true,
            MaxDuration = TimeSpan.FromSeconds(10),
            Tags = ["message-handler", "source-gen", "offline", "deterministic"]
        };

        Console.WriteLine($"   Test     : {testCase.Name}");
        Console.WriteLine($"   Input    : \"{testCase.Input}\"");
        Console.WriteLine($"   Flow     : {string.Join(" → ", testCase.ExpectedExecutors!)}");
        Console.WriteLine($"   Timeout  : {testCase.MaxDuration!.Value.TotalSeconds}s\n");

        Console.WriteLine("📝 Step 4: Running [MessageHandler] workflow...\n");
        Console.WriteLine("   ⏳ Executing deterministic pipeline (instant — no LLM)...\n");

        var harness = new WorkflowEvaluationHarness(verbose: true);
        var testOptions = new WorkflowTestOptions
        {
            Timeout = TimeSpan.FromSeconds(10),
            Verbose = true
        };

        WorkflowTestResult testResult;
        try
        {
            testResult = await harness.RunWorkflowTestAsync(workflowAdapter, testCase, testOptions);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"   ❌ Workflow test failed: {ex.Message}");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("\n📊 DETAILED WORKFLOW RESULTS:");
        Console.WriteLine(new string('═', 80));

        if (testResult.ExecutionResult != null)
        {
            var result = testResult.ExecutionResult;

            Console.WriteLine($"   🎯 Overall : {(testResult.Passed ? "✅ PASSED" : "❌ FAILED")}");
            Console.WriteLine($"   ⏱️ Duration: {result.TotalDuration.TotalMilliseconds:F0}ms");
            Console.WriteLine($"   🔗 Steps   : {result.Steps.Count}");
            Console.WriteLine($"   ❌ Errors   : {result.Errors?.Count ?? 0}\n");

            Console.WriteLine("   📈 EXECUTION TIMELINE:");
            foreach (var step in result.Steps)
            {
                var startMs = step.StartOffset.TotalMilliseconds;
                var durMs = step.Duration.TotalMilliseconds;

                Console.WriteLine($"      {step.StepIndex + 1}. [{startMs:F0}ms] {step.ExecutorId} ({durMs:F0}ms)");

                if (!string.IsNullOrEmpty(step.Output))
                {
                    var snippet = step.Output.Length > 120 ? step.Output[..117] + "..." : step.Output;
                    Console.WriteLine($"         → \"{snippet}\"");
                }
                Console.WriteLine();
            }
        }

        Console.WriteLine("📝 Step 5: Workflow assertions...\n");

        if (testResult.ExecutionResult != null)
        {
            try
            {
                var result = testResult.ExecutionResult;

                result.Should()
                    .HaveStepCount(3, because: "pipeline has 3 [MessageHandler] executors")
                    .HaveExecutedInOrder("Sanitizer", "Classifier", "Formatter")
                    .HaveCompletedWithin(TimeSpan.FromSeconds(5), because: "deterministic pipeline is instant")
                    .HaveNoErrors(because: "deterministic executors always succeed")
                    .HaveNonEmptyOutput()
                    .Validate();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("   ✅ Workflow structure assertions PASSED!\n");
                Console.ResetColor();

                result.Should()
                    .ForExecutor("Sanitizer")
                        .HaveNonEmptyOutput()
                        .And()
                    .ForExecutor("Classifier")
                        .HaveNonEmptyOutput()
                        .And()
                    .ForExecutor("Formatter")
                        .HaveNonEmptyOutput()
                        .And()
                    .Validate();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("   ✅ Per-executor assertions PASSED!\n");
                Console.ResetColor();

                result.Should()
                    .HaveGraphStructure()
                    .HaveNodes("Sanitizer", "Classifier", "Formatter")
                    .HaveEntryPoint("Sanitizer", because: "sanitization is the first step")
                    .HaveTraversedEdge("Sanitizer", "Classifier")
                    .HaveTraversedEdge("Classifier", "Formatter")
                    .HaveUsedEdgeType(EdgeType.Sequential)
                    .HaveExecutionPath("Sanitizer", "Classifier", "Formatter")
                    .Validate();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("   ✅ Graph structure assertions PASSED!\n");
                Console.ResetColor();
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

        Console.WriteLine("📝 Step 6: Generating workflow visualization...\n");

        if (testResult.ExecutionResult != null)
        {
            var mermaid = WorkflowSerializer.ToMermaid(testResult.ExecutionResult);
            Console.WriteLine("   🎨 Mermaid diagram:");
            Console.ForegroundColor = ConsoleColor.Cyan;
            foreach (var line in mermaid.Split('\n').Take(15))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    Console.WriteLine($"      {line}");
            }
            Console.ResetColor();

            var timeline = WorkflowSerializer.ToTimelineJson(testResult.ExecutionResult);
            Console.WriteLine($"\n   📊 Timeline JSON generated: {timeline.Length} characters\n");
        }

        PrintKeyTakeaways();
    }

    /// <summary>
    /// Builds a deterministic workflow using [MessageHandler] source-generated executors.
    /// Pipeline: Sanitizer → Classifier → Formatter
    /// </summary>
    private static (Workflow workflow, string[] executorIds) CreateWorkflow()
    {
        var sanitizer = new SanitizerExecutor();
        var classifier = new ClassifierExecutor();
        var formatter = new FormatterExecutor();

        var workflow = new WorkflowBuilder(sanitizer)
            .AddEdge(sanitizer, classifier)
            .AddEdge(classifier, formatter)
            .WithOutputFrom(formatter)
            .WithName("TextPipeline")
            .WithDescription("Deterministic text pipeline: sanitize → classify → format")
            .Build();

        return (workflow, ["Sanitizer", "Classifier", "Formatter"]);
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   🔧 SAMPLE 11: [MessageHandler] SOURCE-GENERATED EXECUTORS                  ║
║   Deterministic Pipeline — No LLM Required                                    ║
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
│  1. [MessageHandler] executors use source generation (not reflection):          │
│     internal sealed partial class SanitizerExecutor : Executor                  │
│     {                                                                           │
│         [MessageHandler]                                                        │
│         public ValueTask<string> HandleAsync(string msg, IWorkflowContext ctx)  │
│     }                                                                           │
│                                                                                 │
│  2. Same WorkflowBuilder API — no change in how you build workflows:            │
│     var workflow = new WorkflowBuilder(sanitizer)                               │
│         .AddEdge(sanitizer, classifier)                                         │
│         .WithOutputFrom(formatter).Build();                                     │
│                                                                                 │
│  3. Same MAFWorkflowAdapter.FromMAFWorkflow() — zero adapter changes:           │
│     AgentEval evaluates [MessageHandler] and ChatClientAgent workflows          │
│     identically. The evaluation API is executor-pattern agnostic.               │
│                                                                                 │
│  4. Same assertion APIs work for all executor types:                            │
│     result.Should().HaveStepCount(3).HaveExecutedInOrder(...)                  │
│                                                                                 │
│  5. Deterministic executors make great CI tests:                                │
│     No LLM → no stochasticity → passes 100% of the time → instant              │
│                                                                                 │
│  6. Mix and match freely in real workflows:                                     │
│     [MessageHandler] executors + ChatClientAgent.BindAsExecutor() work          │
│     together in the same workflow. Evaluate both in one test.                   │
│                                                                                 │
└─────────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  [MessageHandler] Source-Generated Executors
    //  These are MAF's recommended pattern for custom (non-LLM) executors.
    //  The source generator creates ConfigureRoutes() at compile time.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sanitizer: strips HTML tags, trims whitespace, normalizes spacing.
    /// Uses [MessageHandler] — source generator wires the route at compile time.
    /// </summary>
    [YieldsOutput(typeof(string))]
    internal sealed partial class SanitizerExecutor() : Executor("Sanitizer")
    {
        [MessageHandler]
        public ValueTask<string> HandleAsync(string message, IWorkflowContext context)
        {
            // Strip HTML tags
            var sanitized = System.Text.RegularExpressions.Regex.Replace(message, "<[^>]+>", "");
            // Normalize whitespace
            sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized.Trim(), @"\s+", " ");
            return ValueTask.FromResult(sanitized);
        }
    }

    /// <summary>
    /// Classifier: detects content type (greeting, question, statement) and word count.
    /// Passes a structured classification string to the next executor.
    /// </summary>
    [YieldsOutput(typeof(string))]
    internal sealed partial class ClassifierExecutor() : Executor("Classifier")
    {
        [MessageHandler]
        public ValueTask<string> HandleAsync(string message, IWorkflowContext context)
        {
            var wordCount = message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var category = message.TrimEnd('.', '!', ' ') switch
            {
                var s when s.EndsWith('?') => "question",
                var s when s.StartsWith("Hello", StringComparison.OrdinalIgnoreCase)
                        || s.StartsWith("Hi", StringComparison.OrdinalIgnoreCase) => "greeting",
                _ => "statement"
            };

            var classified = $"[{category}|{wordCount}w] {message}";
            return ValueTask.FromResult(classified);
        }
    }

    /// <summary>
    /// Formatter: applies final output formatting — uppercases the category tag,
    /// adds a timestamp marker, wraps in a standard envelope.
    /// </summary>
    [YieldsOutput(typeof(string))]
    internal sealed partial class FormatterExecutor() : Executor("Formatter")
    {
        [MessageHandler]
        public ValueTask<string> HandleAsync(string message, IWorkflowContext context)
        {
            // Extract and uppercase the category tag
            var formatted = System.Text.RegularExpressions.Regex.Replace(
                message,
                @"\[([a-z]+)\|",
                m => $"[{m.Groups[1].Value.ToUpperInvariant()}|");

            formatted = $"=== Processed Output ===\n{formatted}\n========================";
            return ValueTask.FromResult(formatted);
        }
    }
}
