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
            Console.Error.WriteLine();
            PrintAttacks();
            Console.Error.WriteLine();
            PrintExporters();
            Console.Error.WriteLine();
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
        Console.Error.WriteLine("  Metrics");
        Console.Error.WriteLine("  ─────────────────────────────────────────────────────────");

        // RAG metrics (LLM-evaluated)
        Console.Error.WriteLine("  RAG (LLM-evaluated):");
        Console.Error.WriteLine("    llm_faithfulness          Faithfulness to provided context");
        Console.Error.WriteLine("    llm_relevance             Response relevance to input query");
        Console.Error.WriteLine("    llm_context_precision     Precision of retrieved context");
        Console.Error.WriteLine("    llm_context_recall        Recall of retrieved context");
        Console.Error.WriteLine("    llm_answer_correctness    Correctness of the response");

        // RAG metrics (Embedding-based)
        Console.Error.WriteLine("  RAG (Embedding-based):");
        Console.Error.WriteLine("    embed_answer_similarity   Semantic similarity to expected answer");
        Console.Error.WriteLine("    embed_response_context    Semantic similarity: response vs context");
        Console.Error.WriteLine("    embed_query_context       Semantic similarity: query vs context");

        // Agentic metrics
        Console.Error.WriteLine("  Agentic:");
        Console.Error.WriteLine("    code_tool_selection       Correct tool was selected");
        Console.Error.WriteLine("    code_tool_arguments       Tool arguments match expected values");
        Console.Error.WriteLine("    code_tool_success         Tool calls completed without errors");
        Console.Error.WriteLine("    code_tool_efficiency      Optimal tool usage (minimum calls)");
        Console.Error.WriteLine("    llm_task_completion       LLM-judged task completion quality");

        // Safety metrics
        Console.Error.WriteLine("  Safety:");
        Console.Error.WriteLine("    llm_groundedness          Response is grounded in provided facts");
        Console.Error.WriteLine("    llm_coherence             Logical coherence of the response");
        Console.Error.WriteLine("    llm_fluency               Linguistic fluency and naturalness");

        // Responsible AI metrics
        Console.Error.WriteLine("  Responsible AI:");
        Console.Error.WriteLine("    llm_bias                  Detects bias in agent responses");
        Console.Error.WriteLine("    llm_misinformation        Detects misinformation in responses");
        Console.Error.WriteLine("    code_toxicity             Code-based toxicity detection");

        // Retrieval metrics
        Console.Error.WriteLine("  Retrieval:");
        Console.Error.WriteLine("    code_recall_at_k          Recall@K for retrieval evaluation");
        Console.Error.WriteLine("    code_mrr                  Mean Reciprocal Rank");

        // Conversation metrics
        Console.Error.WriteLine("  Conversation:");
        Console.Error.WriteLine("    ConversationCompleteness  Multi-turn conversation completeness");
    }

    internal static void PrintAttacks()
    {
        Console.Error.WriteLine("  Attack Types (Red Team)");
        Console.Error.WriteLine("  ─────────────────────────────────────────────────────────");

        foreach (var attack in Attack.All)
        {
            Console.Error.WriteLine($"    {attack.Name,-30} {attack.DisplayName} ({attack.OwaspLlmId})");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"  Total: {Attack.All.Count} attack types, {Attack.AvailableNames.Count} names");
    }

    internal static void PrintExporters()
    {
        Console.Error.WriteLine("  Export Formats");
        Console.Error.WriteLine("  ─────────────────────────────────────────────────────────");
        Console.Error.WriteLine("    json        JSON (default)");
        Console.Error.WriteLine("    junit / xml JUnit XML for CI/CD integration");
        Console.Error.WriteLine("    markdown / md  Markdown table");
        Console.Error.WriteLine("    trx         Visual Studio Test Results (TRX)");
        Console.Error.WriteLine("    csv         Comma-separated values");
        Console.Error.WriteLine("    directory / dir  ADR-002 structured directory (--output-dir)");
    }

    internal static void PrintDatasets()
    {
        Console.Error.WriteLine("  Dataset Formats");
        Console.Error.WriteLine("  ─────────────────────────────────────────────────────────");
        Console.Error.WriteLine("    .yaml / .yml   YAML dataset (recommended)");
        Console.Error.WriteLine("    .json          JSON array of test cases");
        Console.Error.WriteLine("    .jsonl / .ndjson  JSON Lines (one test per line)");
        Console.Error.WriteLine("    .csv           Comma-separated values");
        Console.Error.WriteLine("    .tsv           Tab-separated values");
    }
}
