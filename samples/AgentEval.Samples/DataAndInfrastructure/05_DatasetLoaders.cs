// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.DataLoaders;
using AgentEval.Models;

namespace AgentEval.Samples;

/// <summary>
/// Sample F5: Dataset Loaders — Comprehensive Multi-Format Pipeline
///
/// Demonstrates the full AgentEval dataset loading pipeline:
/// - Multi-format loading: YAML, JSONL, CSV (auto-detected from extension)
/// - Static facade vs DI factory patterns
/// - DatasetTestCase → TestCase conversion with ToTestCase()
/// - DatasetTestCase → EvaluationContext with ToEvaluationContext()
/// - Streaming vs buffered loading (IsTrulyStreaming)
/// - Field aliases (question/prompt/query, answer/response/expected)
/// - New properties: EvaluationCriteria, Tags, PassingScore
/// - Ground truth tool calls &amp; custom projection
/// - Convenience LoadAsync / LoadStreamingAsync facade methods
/// - Metadata collection for unknown fields
///
/// ⚡ No Azure credentials required — runs fully offline.
/// ⏱️ Time to understand: 5 minutes
/// ⏱️ Time to run: &lt;1 second
/// </summary>
public static class DatasetLoaders
{
    public static async Task RunAsync()
    {
        PrintHeader();

        // ── Step 1: Multi-format loading ──────────────────────────────────────
        Console.WriteLine("📂 STEP 1: Multi-Format Dataset Loading\n");
        await DemoMultiFormatLoading();

        // ── Step 2: Factory patterns ──────────────────────────────────────────
        Console.WriteLine("\n🏭 STEP 2: Factory Patterns (Static vs DI)\n");
        DemoFactoryPatterns();

        // ── Step 3: DatasetTestCase → TestCase bridge ─────────────────────────
        Console.WriteLine("\n🔄 STEP 3: DatasetTestCase → TestCase Conversion\n");
        await DemoToTestCase();

        // ── Step 4: DatasetTestCase → EvaluationContext ───────────────────────
        Console.WriteLine("\n📊 STEP 4: DatasetTestCase → EvaluationContext\n");
        DemoToEvaluationContext();

        // ── Step 5: Streaming vs Buffered ─────────────────────────────────────
        Console.WriteLine("\n🌊 STEP 5: Streaming vs Buffered Loading\n");
        await DemoStreamingVsBuffered();

        // ── Step 6: Ground truth & custom projection ──────────────────────────
        Console.WriteLine("\n🎯 STEP 6: Ground Truth & Custom Projection\n");
        DemoGroundTruthProjection();

        // ── Step 7: Convenience methods ───────────────────────────────────────
        Console.WriteLine("\n⚡ STEP 7: Convenience LoadAsync / LoadStreamingAsync\n");
        await DemoConvenienceMethods();

        PrintKeyTakeaways();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // STEP 1: MULTI-FORMAT LOADING
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task DemoMultiFormatLoading()
    {
        var formats = new[] { ("YAML", "rag-qa.yaml"), ("JSONL", "dataloader-showcase.jsonl"), ("CSV", "dataloader-showcase.csv") };

        foreach (var (name, fileName) in formats)
        {
            var path = ResolveDatasetPath(fileName);
            if (path == null)
            {
                Console.WriteLine($"   ⚠️  {name}: {fileName} not found, skipping.");
                continue;
            }

            var ext = Path.GetExtension(path);
            var loader = DatasetLoaderFactory.CreateFromExtension(ext);

            var testCases = await loader.LoadAsync(path);

            Console.WriteLine($"   📄 {name} ({ext}): {testCases.Count} test cases loaded");
            Console.WriteLine($"      Loader: {loader.GetType().Name} | Format: {loader.Format} | Truly streams: {loader.IsTrulyStreaming}");
            Console.WriteLine($"      Extensions: {string.Join(", ", loader.SupportedExtensions)}");

            foreach (var tc in testCases.Take(2))
            {
                var input = tc.Input.Length > 40 ? tc.Input[..40] + "..." : tc.Input;
                var tags = tc.Tags != null ? $" tags=[{string.Join(",", tc.Tags)}]" : "";
                var score = tc.PassingScore.HasValue ? $" pass≥{tc.PassingScore}" : "";
                var criteria = tc.EvaluationCriteria?.Count > 0 ? $" criteria={tc.EvaluationCriteria.Count}" : "";
                Console.WriteLine($"      • [{tc.Id}] \"{input}\"{tags}{score}{criteria}");
            }

            if (testCases.Count > 2)
                Console.WriteLine($"      ... and {testCases.Count - 2} more");

            Console.WriteLine();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // STEP 2: FACTORY PATTERNS
    // ═══════════════════════════════════════════════════════════════════════

    private static void DemoFactoryPatterns()
    {
        // Static facade (non-DI)
        Console.WriteLine("   1️⃣  Static Facade (DatasetLoaderFactory):");
        var loaderByExt = DatasetLoaderFactory.CreateFromExtension(".jsonl");
        Console.WriteLine($"      CreateFromExtension(\".jsonl\") → {loaderByExt.GetType().Name}");

        var loaderByFmt = DatasetLoaderFactory.Create("yaml");
        Console.WriteLine($"      Create(\"yaml\")               → {loaderByFmt.GetType().Name}");

        // DI factory (IDatasetLoaderFactory → DefaultDatasetLoaderFactory)
        Console.WriteLine("\n   2️⃣  DI Factory (IDatasetLoaderFactory):");
        IDatasetLoaderFactory factory = new DefaultDatasetLoaderFactory();
        Console.WriteLine($"      Instance: {factory.GetType().Name}");

        var allFormats = new[] { "jsonl", "ndjson", "json", "csv", "tsv", "yaml", "yml" };
        foreach (var fmt in allFormats)
        {
            var loader = factory.Create(fmt);
            Console.WriteLine($"      Create(\"{fmt}\") → {loader.GetType().Name} (format: {loader.Format})");
        }

        // Custom registration
        Console.WriteLine("\n   3️⃣  Custom Loader Registration:");
        factory.Register(".parquet", () => new JsonlDatasetLoader());
        var custom = factory.CreateFromExtension(".parquet");
        Console.WriteLine($"      Register(\".parquet\", ...) → {custom.GetType().Name} (demo)");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // STEP 3: DatasetTestCase → TestCase
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task DemoToTestCase()
    {
        var path = ResolveDatasetPath("dataloader-showcase.jsonl");
        if (path == null) { Console.WriteLine("   ⚠️  Dataset not found."); return; }

        var testCases = await DatasetLoaderFactory.LoadAsync(path);

        Console.WriteLine("   DatasetTestCase (persistence)  →  TestCase (execution):\n");

        foreach (var dc in testCases)
        {
            var tc = dc.ToTestCase();
            Console.WriteLine($"   ┌─ DatasetTestCase ─────────────────────────────────────┐");
            Console.WriteLine($"   │ Id:              {dc.Id,-42}│");
            Console.WriteLine($"   │ Input:           {Truncate(dc.Input, 42),-42}│");
            Console.WriteLine($"   │ ExpectedOutput:  {Truncate(dc.ExpectedOutput ?? "(null)", 42),-42}│");
            Console.WriteLine($"   │ PassingScore:    {dc.PassingScore?.ToString() ?? "(null)",-42}│");
            Console.WriteLine($"   │ Tags:            {FormatList(dc.Tags),-42}│");
            Console.WriteLine($"   │ Criteria:        {FormatList(dc.EvaluationCriteria),-42}│");
            Console.WriteLine($"   │ Metadata keys:   {(dc.Metadata.Count > 0 ? string.Join(", ", dc.Metadata.Keys) : "(none)"),-42}│");
            Console.WriteLine($"   └──────────────────────────────────────────────────────┘");
            Console.WriteLine($"       ↓ ToTestCase()");
            Console.WriteLine($"   ┌─ TestCase ────────────────────────────────────────────┐");
            Console.WriteLine($"   │ Name:                  {Truncate(tc.Name, 36),-36}│");
            Console.WriteLine($"   │ Input:                 {Truncate(tc.Input, 36),-36}│");
            Console.WriteLine($"   │ ExpectedOutputContains:{Truncate(tc.ExpectedOutputContains ?? "(null)", 36),-36}│");
            Console.WriteLine($"   │ PassingScore:          {tc.PassingScore,-36}│");
            Console.WriteLine($"   │ GroundTruth:           {Truncate(tc.GroundTruth ?? "(null)", 36),-36}│");
            Console.WriteLine($"   │ Tags:                  {FormatList(tc.Tags),-36}│");
            Console.WriteLine($"   │ Metadata:              {(tc.Metadata?.Count > 0 ? $"{tc.Metadata.Count} keys" : "(null)"),-36}│");
            Console.WriteLine($"   └──────────────────────────────────────────────────────┘\n");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // STEP 4: DatasetTestCase → EvaluationContext
    // ═══════════════════════════════════════════════════════════════════════

    private static void DemoToEvaluationContext()
    {
        var dc = new DatasetTestCase
        {
            Id = "demo-eval",
            Input = "What is the capital of France?",
            ExpectedOutput = "Paris",
            Context = new[] { "France is in Europe.", "Paris is the capital of France." },
        };

        var ctx = dc.ToEvaluationContext("The capital of France is Paris.");

        Console.WriteLine($"   EvaluationContext:");
        Console.WriteLine($"      Input:       {ctx.Input}");
        Console.WriteLine($"      Output:      {ctx.Output}");
        Console.WriteLine($"      Context:     {ctx.Context}");
        Console.WriteLine($"      GroundTruth: {ctx.GroundTruth}");
        Console.WriteLine();

        // Custom separator
        var ctx2 = dc.ToEvaluationContext("Paris", contextSeparator: " | ");
        Console.WriteLine($"   With custom separator (\" | \"):");
        Console.WriteLine($"      Context:     {ctx2.Context}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // STEP 5: STREAMING VS BUFFERED
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task DemoStreamingVsBuffered()
    {
        Console.WriteLine("   ┌─────────────┬────────────────┬───────────────────────────────────────────┐");
        Console.WriteLine("   │   Format    │ IsTrulyStreaming│ Explanation                               │");
        Console.WriteLine("   ├─────────────┼────────────────┼───────────────────────────────────────────┤");

        var loaders = new (string ext, string desc)[]
        {
            (".jsonl", "Line-by-line, true streaming"),
            (".csv",   "Row-by-row, true streaming"),
            (".json",  "Buffers entire doc, then yields"),
            (".yaml",  "Buffers entire doc, then yields"),
        };

        foreach (var (ext, desc) in loaders)
        {
            var loader = DatasetLoaderFactory.CreateFromExtension(ext);
            var streaming = loader.IsTrulyStreaming ? "✅ true " : "❌ false";
            Console.WriteLine($"   │ {ext,-11} │ {streaming,-14} │ {desc,-41} │");
        }

        Console.WriteLine("   └─────────────┴────────────────┴───────────────────────────────────────────┘");

        // Demo streaming enumeration
        var path = ResolveDatasetPath("dataloader-showcase.jsonl");
        if (path != null)
        {
            Console.WriteLine($"\n   Streaming JSONL line-by-line:");
            var count = 0;
            await foreach (var tc in DatasetLoaderFactory.LoadStreamingAsync(path))
            {
                count++;
                Console.WriteLine($"      Streamed item {count}: [{tc.Id}] \"{Truncate(tc.Input, 40)}\"");
            }
            Console.WriteLine($"   Total: {count} items streamed without buffering entire file.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // STEP 6: GROUND TRUTH & CUSTOM PROJECTION
    // ═══════════════════════════════════════════════════════════════════════

    private static void DemoGroundTruthProjection()
    {
        var dc = new DatasetTestCase
        {
            Id = "gt-demo",
            Input = "Book a flight to Paris",
            GroundTruth = new GroundTruthToolCall
            {
                Name = "book_flight",
                Arguments = new Dictionary<string, object?> { ["destination"] = "Paris", ["class"] = "economy" }
            }
        };

        // Default: JSON serialization
        var tcDefault = dc.ToTestCase();
        Console.WriteLine($"   Default projection (JSON):");
        Console.WriteLine($"      GroundTruth = {tcDefault.GroundTruth}\n");

        // Custom: name-only
        var tcNameOnly = dc.ToTestCase(gt => gt?.Name);
        Console.WriteLine($"   Custom projection (name only):");
        Console.WriteLine($"      GroundTruth = {tcNameOnly.GroundTruth}\n");

        // Custom: formatted
        var tcFormatted = dc.ToTestCase(gt => gt is null ? null : $"{gt.Name}({string.Join(", ", gt.Arguments.Select(a => $"{a.Key}={a.Value}"))})");
        Console.WriteLine($"   Custom projection (formatted call):");
        Console.WriteLine($"      GroundTruth = {tcFormatted.GroundTruth}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // STEP 7: CONVENIENCE METHODS
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task DemoConvenienceMethods()
    {
        Console.WriteLine("   DatasetLoaderFactory convenience methods auto-detect format:\n");

        var files = new[] { "rag-qa.yaml", "dataloader-showcase.jsonl", "dataloader-showcase.csv" };

        foreach (var fileName in files)
        {
            var path = ResolveDatasetPath(fileName);
            if (path == null) continue;

            var results = await DatasetLoaderFactory.LoadAsync(path);
            Console.WriteLine($"   LoadAsync(\"{fileName}\") → {results.Count} items");
        }

        var jsonlPath = ResolveDatasetPath("dataloader-showcase.jsonl");
        if (jsonlPath != null)
        {
            var count = 0;
            await foreach (var _ in DatasetLoaderFactory.LoadStreamingAsync(jsonlPath))
                count++;
            Console.WriteLine($"   LoadStreamingAsync(\"dataloader-showcase.jsonl\") → {count} items streamed");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private static string? ResolveDatasetPath(string fileName)
    {
        // Try relative to the project output directory
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "datasets", fileName);
        if (File.Exists(path)) return Path.GetFullPath(path);

        // Try relative to working directory
        path = Path.Combine("samples", "datasets", fileName);
        if (File.Exists(path)) return Path.GetFullPath(path);

        return null;
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        return text.Length <= max ? text : text[..(max - 3)] + "...";
    }

    private static string FormatList(IReadOnlyList<string>? items)
    {
        if (items == null || items.Count == 0) return "(none)";
        var joined = string.Join(", ", items);
        return joined.Length > 40 ? joined[..37] + "..." : joined;
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   📂 SAMPLE F5: DATASET LOADERS — MULTI-FORMAT PIPELINE                      ║
║   Multi-format, streaming, ToTestCase, ToEvaluationContext, factory patterns  ║
║   ⚡ No credentials required — runs fully offline                             ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine("\n\n💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • DatasetLoaderFactory auto-detects format from file extension (.yaml/.jsonl/.csv/.json/.tsv)");
        Console.WriteLine("   • JSONL & CSV provide true streaming; JSON & YAML buffer then yield");
        Console.WriteLine("   • DatasetTestCase (persistence, mutable) converts to TestCase (execution, immutable) via ToTestCase()");
        Console.WriteLine("   • ToEvaluationContext() bridges to IMetric evaluation (GroundTruth = ExpectedOutput text, not tool call)");
        Console.WriteLine("   • Ground truth projection is customizable: default JSON, or pass gt => gt?.Name, etc.");
        Console.WriteLine("   • IDatasetLoaderFactory for DI; DatasetLoaderFactory static facade for quick scripts");
        Console.WriteLine("   • Field aliases auto-recognized: question/prompt/query → Input, answer/response → ExpectedOutput");
        Console.WriteLine("   • New properties: evaluation_criteria, tags, passing_score — parsed by all loaders");
        Console.WriteLine("\n🔗 See also: Sample F2 (Datasets & Export), docs/naming-conventions.md, ADR-014\n");
    }
}
