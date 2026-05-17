// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.Core.Benchmarks;       // BenchmarkFamilyRegistry (Phase 9 supplementary section)
using AgentEval.DataLoaders;
using AgentEval.Evals;
using AgentEval.Benchmarks;
using System.ComponentModel;

namespace AgentEval.Samples;

/// <summary>
/// Sample F4: Benchmark System — Real Agentic Benchmarking with Preset Factories
///
/// This sample shows how to use AgentEval's agentic preset factory against a real
/// Azure OpenAI-backed agent, loading test prompts from a JSONL file via
/// <see cref="DatasetLoaderFactory"/> (the industry-standard format for AI benchmark
/// datasets — used by BFCL, GAIA, MMLU, GSM8K, etc.).
///
/// It demonstrates:
///   1. Loading test prompts from JSONL via <see cref="DatasetLoaderFactory"/>
///   2. Building a <see cref="CompositeEval"/> via the agentic preset factory
///      (<see cref="AgenticBenchmark.ToolCallAccuracy"/>)
///   3. Running the agent against each prompt and evaluating its response with the preset
///   4. Printing per-scenario + aggregate verdicts from the composite tree
///
/// The agentic suite supersedes AgentEval's prior library-API benchmark surface
/// (removed in v0.9.0-beta). For a full pipeline with audit-chain evidence, PDF reports,
/// and Mission Control integration, prefer the CLI:
///
///     agenteval bench agentic --preset tool-call-accuracy --subject MyAgent
///
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
/// ⏱️ Time to understand: 5 minutes
/// ⏱️ Time to run: ~15–30 seconds (depends on model latency)
/// </summary>
public static class BenchmarkSystem
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            PrintMissingCredentialsBox();
            return;
        }

        Console.WriteLine($"   Endpoint: {AIConfig.Endpoint}");
        Console.WriteLine($"   Model:    {AIConfig.ModelDeployment}\n");

        // ── Build the agent + a judge (the second model that grades responses) ────
        var agent = CreateAgentWithTools();
        var judge = CreateJudgeEvaluator();

        // ── Load test prompts from JSONL ─────────────────────────────────────────
        // DatasetLoaderFactory understands the industry-standard JSONL shape used
        // by BFCL, GAIA, MMLU, GSM8K, ToolBench, etc.
        Console.WriteLine("Loading benchmark prompts from JSONL...\n");
        var prompts = await LoadPromptsFromJsonl("benchmark-tool-accuracy.jsonl");
        Console.WriteLine($"   Loaded {prompts.Count} prompts\n");

        // ── Build the agentic Tool-Call-Accuracy preset ──────────────────────────
        // The preset factory composes 5 sub-evaluators (selection, input accuracy,
        // output utilization, success, efficiency) into one CompositeEval with
        // canonical weights — same as the CLI's `--preset tool-call-accuracy`.
        var preset = AgenticBenchmark.ToolCallAccuracy(judge, judgeModel: AIConfig.ModelDeployment);

        Console.WriteLine($"Running Tool-Call-Accuracy preset against {prompts.Count} prompts\n");

        // ── Run the agent against each prompt, then evaluate the response ────────
        int passed = 0;
        for (int i = 0; i < prompts.Count; i++)
        {
            var prompt = prompts[i];
            var response = await agent.RunAsync(prompt);
            var responseText = response.Text ?? string.Empty;

            // EvalInput carries the user query + agent response into every sub-evaluator.
            // For richer scenarios (multi-turn, tool traces) you would also populate
            // EvalInput.Metadata with the structured trace data — see the CLI for examples.
            var input = new EvalInput(Query: prompt, Response: responseText);
            var result = await preset.EvaluateAsync(input);

            var icon = result.Score.Passed ? "PASS" : "FAIL";
            Console.WriteLine($"   [{icon}] [{i + 1,2}/{prompts.Count}] {Truncate(prompt, 60)}  score={result.Score.Value:F2}");
            if (result.Score.Passed) passed++;
        }

        // ── Summary ─────────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("   +----------------------------------------------------------+");
        Console.WriteLine("   |             TOOL-CALL-ACCURACY PRESET RESULTS            |");
        Console.WriteLine("   +----------------------------------------------------------+");
        Console.WriteLine($"   |  Passed / Total:         {passed,4} / {prompts.Count,-24}|");
        Console.WriteLine($"   |  Overall Accuracy:       {(double)passed / Math.Max(1, prompts.Count),27:P1}   |");
        Console.WriteLine("   +----------------------------------------------------------+");

        // ── Supplementary: enumerate registered benchmark families (Phase 9 / ADR-017 Convention 3)
        EnumerateRegisteredFamilies(judge);

        PrintKeyTakeaways();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Benchmark Family Registry — discovery surface added in v0.10.0-beta
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Demonstrates <see cref="BenchmarkFamilyRegistry"/> — the canonical discovery
    /// mechanism for benchmark families (ADR-017 Convention 3). Every shipped family
    /// auto-registers via <c>[ModuleInitializer]</c> on assembly load; third-party plugins
    /// that publish a NuGet package can drop into the same surface via the same mechanism.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Assembly-anchor pattern</b>: <see cref="BenchmarkFamilyRegistry"/> is populated
    /// lazily by <c>[ModuleInitializer]</c>-attributed methods that run the first time
    /// any code in the owning assembly is touched. In a real CLI / Mission Control we'd
    /// touch one type from each expected assembly to force-load. In this sample, we
    /// reach the agentic family by virtue of <c>AgenticBenchmark.ToolCallAccuracy(...)</c>
    /// being called above. Other families that haven't been touched yet (GDPR, EU AI
    /// Act, OWASP, MITRE, LongMemEval, Memory, Performance) live in assemblies the
    /// umbrella NuGet embeds via <c>PrivateAssets="all"</c> — their <c>[ModuleInitializer]</c>
    /// hooks will fire once we reference any type from them; we don't here, so we get
    /// at least one family in this minimal sample.
    /// </para>
    /// </remarks>
    private static void EnumerateRegisteredFamilies(IEvaluator judge)
    {
        Console.WriteLine();
        Console.WriteLine("   +============================================================+");
        Console.WriteLine("   |   BenchmarkFamilyRegistry — Registered Families (v0.10.0)   |");
        Console.WriteLine("   +============================================================+");

        // ── 1. Enumerate all registered families ────────────────────────────────
        var families = BenchmarkFamilyRegistry.All;
        Console.WriteLine($"\n   {families.Count} families currently registered:");
        foreach (var family in families)
        {
            Console.WriteLine($"     • {family.Name,-14} ({family.DefaultCostTier,-6}) — {family.Description}");
            foreach (var preset in family.Presets)
            {
                var tier = preset.CostTier?.ToString() ?? family.DefaultCostTier.ToString();
                Console.WriteLine($"         └─ {preset.Name,-22} [{tier,-6}] {preset.Description}");
            }
        }
        Console.WriteLine();
        Console.WriteLine("   NOTE: In this minimal sample, only assemblies referenced from this");
        Console.WriteLine("         file are loaded; other families (GDPR / EU AI Act / OWASP /");
        Console.WriteLine("         MITRE / LongMemEval / Memory / Performance) auto-register");
        Console.WriteLine("         when their assembly is touched. The CLI's `bench --list`");
        Console.WriteLine("         force-loads every umbrella sub-assembly before enumeration.");

        // ── 2. Look up a specific family by name ────────────────────────────────
        var agentic = BenchmarkFamilyRegistry.TryGet("agentic");
        if (agentic is not null)
        {
            Console.WriteLine($"\n   Lookup `TryGet(\"agentic\")` returned:");
            Console.WriteLine($"     Name:        {agentic.Name}");
            Console.WriteLine($"     CostTier:    {agentic.DefaultCostTier}");
            Console.WriteLine($"     Presets:     {agentic.Presets.Count} ({string.Join(", ", agentic.Presets.Select(p => p.Name))})");
            Console.WriteLine($"     DocLink:     {agentic.DocLinkUrl ?? "(none)"}");
            Console.WriteLine($"     OwningAsm:   {agentic.OwningAssemblyName ?? "(unknown)"}");

            // ── 3. Construct a CompositeEval from registry metadata (Shape A) ──
            // The CompositeFactory delegate produces a CompositeEval given a preset
            // name + optional judge — the same shape the static factories return,
            // but driven purely by registry metadata (useful for plugins / dynamic CLIs).
            try
            {
                var compositeFromRegistry = agentic.CompositeFactory?.Invoke("agentic-execution", judge);
                Console.WriteLine($"     Composite:   {compositeFromRegistry?.GetType().Name ?? "(no composite factory)"}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"     Composite:   (factory threw — {ex.GetType().Name}: {ex.Message})");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // JSONL Dataset Loading
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Loads prompts from a JSONL file via <see cref="DatasetLoaderFactory"/>.
    /// Each line's <c>input</c> field becomes a prompt string.
    /// </summary>
    private static async Task<List<string>> LoadPromptsFromJsonl(string fileName)
    {
        var path = ResolveDatasetPath(fileName)
            ?? throw new FileNotFoundException(
                $"Benchmark dataset not found: {fileName}. " +
                $"Ensure the file exists in samples/datasets/. " +
                $"See docs/benchmarks.md for JSONL format details.");

        var dataset = await DatasetLoaderFactory.LoadAsync(path);
        return dataset.Select(dc => dc.Input).ToList();
    }

    /// <summary>
    /// Resolves a dataset file path relative to the samples/datasets directory.
    /// </summary>
    private static string? ResolveDatasetPath(string fileName)
    {
        // Try relative to the project output directory
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "datasets", fileName);
        if (File.Exists(path)) return Path.GetFullPath(path);

        // Try relative to working directory (repo root)
        path = Path.Combine("samples", "datasets", fileName);
        if (File.Exists(path)) return Path.GetFullPath(path);

        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Agent + Judge Setup
    // ═══════════════════════════════════════════════════════════════════════════

    private static AIAgent CreateAgentWithTools()
    {
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        return chatClient.AsAIAgent(
            name: "BenchmarkAgent",
            instructions: "You are a helpful assistant. Use the available tools when appropriate.",
            tools:
            [
                AIFunctionFactory.Create(GetWeather),
                AIFunctionFactory.Create(Calculate)
            ]);
    }

    private static IEvaluator CreateJudgeEvaluator()
    {
        // The judge is a second LLM call that grades the agent's response against the
        // preset's rubric. Same Azure deployment is used here for simplicity; in production
        // you would typically use a stronger judge model than the system under test.
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var judgeChatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();
        return new ChatClientEvaluator(judgeChatClient);
    }

    [Description("Get current weather for a city")]
    private static string GetWeather([Description("City name")] string city) =>
        $"The weather in {city} is 18°C and partly cloudy.";

    [Description("Calculate a math expression and return the result")]
    private static string Calculate([Description("Math expression to evaluate")] string expression) =>
        $"Result of {expression} = (calculated)";

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("+============================================================================+");
        Console.WriteLine("|                    Sample F4: Benchmark System                              |");
        Console.WriteLine("|       Real Agentic Benchmarking with Preset Factories (v0.9.0-beta+)        |");
        Console.WriteLine("+============================================================================+");
        Console.WriteLine();
    }

    private static void PrintMissingCredentialsBox()
    {
        Console.WriteLine("+-----------------------------------------------------------------------------+");
        Console.WriteLine("|  SKIPPING SAMPLE F4 - Azure OpenAI Credentials Required                     |");
        Console.WriteLine("|                                                                             |");
        Console.WriteLine("|  This sample runs REAL benchmarks against a live agent.                     |");
        Console.WriteLine("|  Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT  |");
        Console.WriteLine("+-----------------------------------------------------------------------------+");
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine("\n   KEY TAKEAWAYS:");
        Console.WriteLine("   - Test data loaded from JSONL files via DatasetLoaderFactory (industry standard)");
        Console.WriteLine("   - AgenticBenchmark.ToolCallAccuracy(judge) returns a CompositeEval composed of");
        Console.WriteLine("     5 sub-evaluators with canonical weights; same as the CLI preset");
        Console.WriteLine("   - For full pipeline with audit-chain evidence + PDF reports, use the CLI:");
        Console.WriteLine("       agenteval bench agentic --preset tool-call-accuracy --subject MyAgent");
        Console.WriteLine("   - Other presets: agentic-execution, rag-quality, safety, conversational,");
        Console.WriteLine("     reasoning, user-experience, adversarial-direct, telemetry, judge-quality");
        Console.WriteLine("\n   NEXT: Explore Sample B5 for calibrated evaluation, or run `agenteval bench`!\n");
    }
}
