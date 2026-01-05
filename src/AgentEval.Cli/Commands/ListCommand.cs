// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;

namespace AgentEval.Cli.Commands;

/// <summary>
/// The 'list' command - lists available metrics, assertions, etc.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var metricsCommand = new Command("metrics", "List all available evaluation metrics");
        metricsCommand.SetHandler(() =>
        {
            Console.WriteLine("Available Metrics:");
            Console.WriteLine();
            
            // Show built-in metrics (these are the metrics available in the library)
            var builtIn = new[]
            {
                // RAG Metrics
                ("faithfulness", "Measures if response is grounded in provided context"),
                ("relevance", "Measures if response addresses the user's question"),
                ("context-precision", "Measures precision of retrieved context"),
                ("context-recall", "Measures recall of retrieved context"),
                ("answer-correctness", "Measures factual correctness of response"),
                
                // Agentic Metrics
                ("tool-selection", "Measures if correct tools were selected"),
                ("tool-arguments", "Validates tool call arguments"),
                ("tool-success", "Measures tool execution success rate"),
                ("task-completion", "Measures if task was fully completed"),
                ("tool-efficiency", "Measures efficiency of tool usage"),
                
                // Embedding Metrics
                ("answer-similarity", "Semantic similarity between response and expected answer"),
                ("response-context-similarity", "Semantic similarity between response and context"),
                ("query-context-similarity", "Semantic similarity between query and context"),
            };

            foreach (var (name, desc) in builtIn)
            {
                Console.WriteLine($"  {name,-30} {desc}");
            }
        });

        var assertionsCommand = new Command("assertions", "List all available assertion types");
        assertionsCommand.SetHandler(() =>
        {
            Console.WriteLine("Available Assertion Types:");
            Console.WriteLine();
            
            var assertions = new[]
            {
                // Response assertions
                ("contains", "Response contains substring"),
                ("not-contains", "Response does not contain substring"),
                ("contains-all", "Response contains all of the specified substrings"),
                ("contains-any", "Response contains at least one of the specified substrings"),
                ("matches-pattern", "Response matches regex pattern"),
                ("starts-with", "Response starts with prefix"),
                ("ends-with", "Response ends with suffix"),
                ("length-between", "Response length is within range"),
                ("not-empty", "Response is not empty"),
                
                // Tool assertions  
                ("tool-called", "Specific tool was called"),
                ("tool-not-called", "Specific tool was not called"),
                ("tool-call-count", "Specific number of tool calls made"),
                ("tool-order", "Tools called in specific order"),
                ("tool-argument", "Tool called with specific argument"),
                ("no-tool-errors", "No tool call errors occurred"),
                
                // Performance assertions
                ("duration-under", "Total duration under threshold"),
                ("ttft-under", "Time to first token under threshold"),
                ("tokens-under", "Token count under threshold"),
                ("cost-under", "Estimated cost under threshold"),
                
                // Semantic assertions
                ("similar", "Semantically similar to expected (cosine similarity)"),
                ("is-valid-json", "Response is valid JSON"),
                ("is-valid-function-call", "Response is valid function call format"),
            };

            foreach (var (name, desc) in assertions)
            {
                Console.WriteLine($"  {name,-25} {desc}");
            }
        });

        var formatsCommand = new Command("formats", "List available output formats");
        formatsCommand.SetHandler(() =>
        {
            Console.WriteLine("Available Output Formats:");
            Console.WriteLine();
            Console.WriteLine("  json      JSON format for programmatic consumption");
            Console.WriteLine("  junit     JUnit XML for CI/CD integration (GitHub Actions, Azure DevOps, Jenkins)");
            Console.WriteLine("  markdown  Markdown for PR comments and documentation");
            Console.WriteLine("  trx       Visual Studio TRX format for .NET tooling");
        });

        var command = new Command("list", "List available metrics, assertions, and formats")
        {
            metricsCommand,
            assertionsCommand,
            formatsCommand
        };

        return command;
    }
}
