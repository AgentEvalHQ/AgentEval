// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Ported from AgentEvalHQ/AgentEval.Cli v0.2.0-alpha during the v1.1 CLI consolidation.
// `agenteval list` predates `agenteval bench --list`. They serve different purposes:
//   - `list` enumerates the FOUR catalogues (metrics, attacks, exporters, datasets) used
//     by the eval/redteam command surface — preserved verbatim from the released CLI.
//   - `bench --list` enumerates BenchmarkFamilyRegistry (gdpr, eu-ai-act, agentic, etc.) —
//     a v0.10.0-beta addition tied to the unified benchmark architecture.

using System.CommandLine;
using System.CommandLine.Parsing;
using AgentEval.RedTeam;

namespace AgentEval.Cli.Commands;

/// <summary>
/// The 'agenteval list' command — list available metrics, attacks, exporters, and dataset formats.
/// </summary>
internal static class ListCommand
{
    public static Command Create()
    {
        var command = new Command("list", "List available metrics, attack types, export formats, and dataset formats");

        var typeOpt = new Option<string?>("--type")
            { Description = "Filter: metrics, attacks, exporters, datasets (default: all)" };

        command.Options.Add(typeOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var type = parseResult.GetValue(typeOpt);
            return await Task.FromResult(Execute(type));
        });

        return command;
    }

    /// <summary>
    /// Core execution logic — separated from command wiring for testability.
    /// </summary>
    internal static int Execute(string? type)
    {
        if (type is null or "all")
        {
            PrintMetrics();
            Console.WriteLine();
            PrintAttacks();
            Console.WriteLine();
            PrintExporters();
            Console.WriteLine();
            PrintDatasets();
            return ExitCodes.Success;
        }

        switch (type.ToLowerInvariant())
        {
            case "metrics":
                PrintMetrics();
                return ExitCodes.Success;
            case "attacks":
                PrintAttacks();
                return ExitCodes.Success;
            case "exporters":
                PrintExporters();
                return ExitCodes.Success;
            case "datasets":
                PrintDatasets();
                return ExitCodes.Success;
            default:
                Console.Error.WriteLine($"  Error: Unknown type '{type}'. Use: metrics, attacks, exporters, datasets");
                return ExitCodes.UsageError;
        }
    }

    internal static void PrintMetrics()
    {
        Console.WriteLine("  Metrics");
        Console.WriteLine("  ─────────────────────────────────────────────────────────");

        // RAG metrics (LLM-evaluated)
        Console.WriteLine("  RAG (LLM-evaluated):");
        Console.WriteLine("    llm_faithfulness          Faithfulness to provided context");
        Console.WriteLine("    llm_relevance             Response relevance to input query");
        Console.WriteLine("    llm_context_precision     Precision of retrieved context");
        Console.WriteLine("    llm_context_recall        Recall of retrieved context");
        Console.WriteLine("    llm_answer_correctness    Correctness of the response");

        // RAG metrics (Embedding-based)
        Console.WriteLine("  RAG (Embedding-based):");
        Console.WriteLine("    embed_answer_similarity   Semantic similarity to expected answer");
        Console.WriteLine("    embed_response_context    Semantic similarity: response vs context");
        Console.WriteLine("    embed_query_context       Semantic similarity: query vs context");

        // Agentic metrics
        Console.WriteLine("  Agentic:");
        Console.WriteLine("    code_tool_selection       Correct tool was selected");
        Console.WriteLine("    code_tool_arguments       Tool arguments match expected values");
        Console.WriteLine("    code_tool_success         Tool calls completed without errors");
        Console.WriteLine("    code_tool_efficiency      Optimal tool usage (minimum calls)");
        Console.WriteLine("    llm_task_completion       LLM-judged task completion quality");

        // Safety metrics
        Console.WriteLine("  Safety:");
        Console.WriteLine("    llm_groundedness          Response is grounded in provided facts");
        Console.WriteLine("    llm_coherence             Logical coherence of the response");
        Console.WriteLine("    llm_fluency               Linguistic fluency and naturalness");

        // Responsible AI metrics
        Console.WriteLine("  Responsible AI:");
        Console.WriteLine("    llm_bias                  Detects bias in agent responses");
        Console.WriteLine("    llm_misinformation        Detects misinformation in responses");
        Console.WriteLine("    code_toxicity             Code-based toxicity detection");

        // Retrieval metrics
        Console.WriteLine("  Retrieval:");
        Console.WriteLine("    code_recall_at_k          Recall@K for retrieval evaluation");
        Console.WriteLine("    code_mrr                  Mean Reciprocal Rank");

        // Conversation metrics
        Console.WriteLine("  Conversation:");
        Console.WriteLine("    ConversationCompleteness  Multi-turn conversation completeness");
    }

    internal static void PrintAttacks()
    {
        Console.WriteLine("  Attack Types (Red Team)");
        Console.WriteLine("  ─────────────────────────────────────────────────────────");

        foreach (var attack in Attack.All)
        {
            Console.WriteLine($"    {attack.Name,-30} {attack.DisplayName} ({attack.OwaspLlmId})");
        }

        Console.WriteLine();
        Console.WriteLine($"  Total: {Attack.All.Count} attack types, {Attack.AvailableNames.Count} names");
    }

    internal static void PrintExporters()
    {
        Console.WriteLine("  Export Formats");
        Console.WriteLine("  ─────────────────────────────────────────────────────────");
        Console.WriteLine("    json        JSON (default)");
        Console.WriteLine("    junit / xml JUnit XML for CI/CD integration");
        Console.WriteLine("    markdown / md  Markdown table");
        Console.WriteLine("    trx         Visual Studio Test Results (TRX)");
        Console.WriteLine("    csv         Comma-separated values");
        Console.WriteLine("    directory / dir  ADR-002 structured directory (--output-dir)");
    }

    internal static void PrintDatasets()
    {
        Console.WriteLine("  Dataset Formats");
        Console.WriteLine("  ─────────────────────────────────────────────────────────");
        Console.WriteLine("    .yaml / .yml   YAML dataset (recommended)");
        Console.WriteLine("    .json          JSON array of test cases");
        Console.WriteLine("    .jsonl / .ndjson  JSON Lines (one test per line)");
        Console.WriteLine("    .csv           Comma-separated values");
        Console.WriteLine("    .tsv           Tab-separated values");
    }
}
