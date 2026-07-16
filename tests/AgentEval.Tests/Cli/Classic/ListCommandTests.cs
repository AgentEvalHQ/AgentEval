// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Ported from AgentEvalHQ/AgentEval.Cli/tests/AgentEval.Cli.Tests/ListCommandTests.cs
// during the v1.1 CLI consolidation. Only the namespace was edited; the test bodies
// are unchanged so the catalogue surface (`agenteval list metrics|attacks|exporters|datasets`)
// stays exercised verbatim.

using AgentEval.Cli.Commands;
using Xunit;

namespace AgentEval.Tests.Cli.Classic;

/// <summary>
/// Tests for the ListCommand — list available metrics, attacks, exporters, datasets.
/// </summary>
[Collection("ConsoleTests")]
public class ListCommandTests
{
    private (string Output, int ExitCode) CaptureOutput(string? type)
    {
        // GAP-16/GAP-13: catalog data now goes to stdout (so `agenteval list | grep` works); the
        // unknown-type diagnostic stays on stderr. Capture BOTH so these assertions cover the
        // combined surface regardless of which stream each line uses.
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetError(sw);
        try
        {
            var exitCode = ListCommand.Execute(type);
            return (sw.ToString(), exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // COMMAND STRUCTURE TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Create_ReturnsCommandNamedList()
    {
        var command = ListCommand.Create();
        Assert.Equal("list", command.Name);
    }

    [Fact]
    public void Create_HasDescription()
    {
        var command = ListCommand.Create();
        Assert.False(string.IsNullOrWhiteSpace(command.Description));
    }

    [Fact]
    public void Create_Has1Option()
    {
        // --type
        var command = ListCommand.Create();
        Assert.Single(command.Options);
    }

    [Fact]
    public void Create_HasTypeOption()
    {
        var command = ListCommand.Create();
        Assert.Contains(command.Options,
            o => o.Name.Contains("type", StringComparison.OrdinalIgnoreCase));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ALL (DEFAULT)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Execute_Null_ListsAll()
    {
        var (output, exitCode) = CaptureOutput(null);

        Assert.Equal(0, exitCode);
        Assert.Contains("Metrics", output);
        Assert.Contains("Attack Types", output);
        Assert.Contains("Export Formats", output);
        Assert.Contains("Dataset Formats", output);
    }

    [Fact]
    public void Execute_All_ListsAll()
    {
        var (output, exitCode) = CaptureOutput("all");

        Assert.Equal(0, exitCode);
        Assert.Contains("llm_faithfulness", output);
        Assert.Contains("PromptInjection", output);
        Assert.Contains("json", output);
        Assert.Contains(".yaml", output);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // METRICS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Execute_Metrics_WritesCatalogToStdout()
    {
        // GAP-13: the catalog IS the command's data, so it must go to STDOUT (so `agenteval list |
        // grep` and `> file` work) — not stderr. Capture stdout only.
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try { ListCommand.Execute("metrics"); }
        finally { Console.SetOut(originalOut); }

        Assert.Contains("llm_faithfulness", sw.ToString());
    }

    [Fact]
    public void Execute_UnknownType_WritesDiagnosticToStderr()
    {
        // The error diagnostic remains on stderr (reserved for diagnostics), not stdout.
        var originalErr = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);
        try { ListCommand.Execute("bogus"); }
        finally { Console.SetError(originalErr); }

        Assert.Contains("Unknown type", sw.ToString());
    }

    [Fact]
    public void Execute_Metrics_ListsAllMetricCategories()
    {
        var (output, exitCode) = CaptureOutput("metrics");

        Assert.Equal(0, exitCode);

        // Check all categories are present
        Assert.Contains("RAG (LLM-evaluated)", output);
        Assert.Contains("RAG (Embedding-based)", output);
        Assert.Contains("Agentic", output);
        Assert.Contains("Safety", output);
        Assert.Contains("Responsible AI", output);
        Assert.Contains("Retrieval", output);
        Assert.Contains("Conversation", output);
    }

    [Fact]
    public void Execute_Metrics_ContainsSpecificMetricNames()
    {
        var (output, exitCode) = CaptureOutput("metrics");

        Assert.Equal(0, exitCode);

        // Spot check key metrics from each category
        Assert.Contains("llm_faithfulness", output);
        Assert.Contains("llm_relevance", output);
        Assert.Contains("llm_context_precision", output);
        Assert.Contains("llm_context_recall", output);
        Assert.Contains("llm_answer_correctness", output);
        Assert.Contains("embed_answer_similarity", output);
        Assert.Contains("embed_response_context", output);
        Assert.Contains("embed_query_context", output);
        Assert.Contains("code_tool_selection", output);
        Assert.Contains("code_tool_arguments", output);
        Assert.Contains("code_tool_success", output);
        Assert.Contains("code_tool_efficiency", output);
        Assert.Contains("llm_task_completion", output);
        Assert.Contains("llm_groundedness", output);
        Assert.Contains("llm_coherence", output);
        Assert.Contains("llm_fluency", output);
        Assert.Contains("llm_bias", output);
        Assert.Contains("llm_misinformation", output);
        Assert.Contains("code_toxicity", output);
        Assert.Contains("code_recall_at_k", output);
        Assert.Contains("code_mrr", output);
        Assert.Contains("ConversationCompleteness", output);
    }

    [Fact]
    public void Execute_Metrics_ListsAtLeast22Metrics()
    {
        var (output, _) = CaptureOutput("metrics");

        // Count unique metric names by prefix conventions
        var lines = output.Split('\n');
        var metricCount = lines.Count(l =>
        {
            var trimmed = l.Trim();
            return trimmed.StartsWith("llm_") || trimmed.StartsWith("code_") ||
                   trimmed.StartsWith("embed_") || trimmed.StartsWith("ConversationCompleteness");
        });

        Assert.True(metricCount >= 22, $"Expected at least 22 metrics, found {metricCount}");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // METRICS — CASE INSENSITIVITY
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Metrics")]
    [InlineData("METRICS")]
    [InlineData("metrics")]
    public void Execute_Metrics_CaseInsensitive(string type)
    {
        var (output, exitCode) = CaptureOutput(type);
        Assert.Equal(0, exitCode);
        Assert.Contains("llm_faithfulness", output);
    }

    [Theory]
    [InlineData("Attacks")]
    [InlineData("ATTACKS")]
    public void Execute_Attacks_CaseInsensitive(string type)
    {
        var (output, exitCode) = CaptureOutput(type);
        Assert.Equal(0, exitCode);
        Assert.Contains("PromptInjection", output);
    }

    [Theory]
    [InlineData("Exporters")]
    [InlineData("EXPORTERS")]
    public void Execute_Exporters_CaseInsensitive(string type)
    {
        var (output, exitCode) = CaptureOutput(type);
        Assert.Equal(0, exitCode);
        Assert.Contains("json", output);
    }

    [Theory]
    [InlineData("Datasets")]
    [InlineData("DATASETS")]
    public void Execute_Datasets_CaseInsensitive(string type)
    {
        var (output, exitCode) = CaptureOutput(type);
        Assert.Equal(0, exitCode);
        Assert.Contains(".yaml", output);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ATTACKS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Execute_Attacks_Lists9AttackTypes()
    {
        var (output, exitCode) = CaptureOutput("attacks");

        Assert.Equal(0, exitCode);
        Assert.Contains("PromptInjection", output);
        Assert.Contains("Jailbreak", output);
        Assert.Contains("PIILeakage", output);
        Assert.Contains("SystemPromptExtraction", output);
        Assert.Contains("IndirectInjection", output);
        Assert.Contains("InferenceAPIAbuse", output);
        Assert.Contains("ExcessiveAgency", output);
        Assert.Contains("InsecureOutput", output);
        Assert.Contains("EncodingEvasion", output);
    }

    [Fact]
    public void Execute_Attacks_ShowsOwaspIds()
    {
        var (output, exitCode) = CaptureOutput("attacks");

        Assert.Equal(0, exitCode);
        Assert.Contains("LLM01", output); // PromptInjection
        Assert.Contains("LLM02", output); // PIILeakage
    }

    [Fact]
    public void Execute_Attacks_ShowsTotalCount()
    {
        var (output, exitCode) = CaptureOutput("attacks");

        Assert.Equal(0, exitCode);
        Assert.Contains("Total:", output);
        Assert.Contains("14 attack types", output);   // Wave D: 10/10 OWASP; +SkillInjection (Skills Phase 3)
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EXPORTERS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Execute_Exporters_ListsAllFormats()
    {
        var (output, exitCode) = CaptureOutput("exporters");

        Assert.Equal(0, exitCode);
        Assert.Contains("json", output);
        Assert.Contains("junit", output);
        Assert.Contains("markdown", output);
        Assert.Contains("trx", output);
        Assert.Contains("csv", output);
        Assert.Contains("directory", output);
    }

    [Fact]
    public void Execute_Exporters_ShowsAliases()
    {
        var (output, exitCode) = CaptureOutput("exporters");

        Assert.Equal(0, exitCode);
        Assert.Contains("xml", output);    // alias for junit
        Assert.Contains("md", output);     // alias for markdown
        Assert.Contains("dir", output);    // alias for directory
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DATASETS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Execute_Datasets_ListsAllFormats()
    {
        var (output, exitCode) = CaptureOutput("datasets");

        Assert.Equal(0, exitCode);
        Assert.Contains(".yaml", output);
        Assert.Contains(".json", output);
        Assert.Contains(".jsonl", output);
        Assert.Contains(".csv", output);
        Assert.Contains(".tsv", output);
    }

    [Fact]
    public void Execute_Datasets_ShowsYmlAlias()
    {
        var (output, exitCode) = CaptureOutput("datasets");

        Assert.Equal(0, exitCode);
        Assert.Contains(".yml", output);
    }

    [Fact]
    public void Execute_Datasets_ShowsNdjsonAlias()
    {
        var (output, exitCode) = CaptureOutput("datasets");

        Assert.Equal(0, exitCode);
        Assert.Contains(".ndjson", output);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UNKNOWN TYPE
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Execute_UnknownType_ReturnsUsageError()
    {
        var (output, exitCode) = CaptureOutput("invalid");

        Assert.Equal(AgentEval.Cli.ExitCodes.UsageError, exitCode);
        Assert.Contains("Unknown type", output);
    }

    [Theory]
    [InlineData("metric")]     // common typo (singular)
    [InlineData("attack")]     // common typo (singular)
    [InlineData("format")]     // wrong name
    [InlineData("xyz123")]     // random
    public void Execute_VariousInvalidTypes_ReturnUsageError(string type)
    {
        var (_, exitCode) = CaptureOutput(type);
        Assert.Equal(AgentEval.Cli.ExitCodes.UsageError, exitCode);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ALL OUTPUT COMPLETENESS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Execute_All_ContainsAllFourSections()
    {
        var (output, _) = CaptureOutput(null);

        Assert.Contains("Metrics", output);
        Assert.Contains("Attack Types", output);
        Assert.Contains("Export Formats", output);
        Assert.Contains("Dataset Formats", output);
    }

    [Fact]
    public void Execute_All_IsSuperset_OfIndividualSections()
    {
        var (allOutput, _) = CaptureOutput(null);
        var (metricsOutput, _) = CaptureOutput("metrics");
        var (attacksOutput, _) = CaptureOutput("attacks");

        // All output should contain key items from each section
        Assert.Contains("llm_faithfulness", allOutput);
        Assert.Contains("PromptInjection", allOutput);
        Assert.Contains("json", allOutput);
        Assert.Contains(".yaml", allOutput);
    }
}
