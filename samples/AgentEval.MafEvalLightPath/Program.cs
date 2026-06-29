// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// ──────────────────────────────────────────────────────────────────────────────
//  AgentEval × MAF 1.11.1 — Evaluation "light path", end to end
//
//  Demonstrates the evaluation feature shipped in Microsoft.Agents.AI 1.11.1
//  (Microsoft.Agents.AI.AgentEvaluationExtensions.EvaluateAsync) using MAF's OWN
//  native IAgentEvaluator contract — AgentEval metrics/composites are wrapped as an
//  IAgentEvaluator (AgentEvalAgentEvaluator) and run via agent.EvaluateAsync(queries,
//  evaluator), then rendered as a self-contained AgentEval HTML report.
//
//  It runs TWO things through the same MAF API to show the full range:
//    [1] FLAT metrics    — AgentEvalEvaluators.Custom(tool + quality metrics)
//    [2] COMPOSITE / FULL BENCHMARK — a whole AgenticBenchmark preset (weighted
//        composite tree with thresholds) wrapped as one IEvaluator.
//
//  Run (from repo root):
//    dotnet run --project samples/AgentEval.MafEvalLightPath
//    dotnet run --project samples/AgentEval.MafEvalLightPath -- --no-open
//    dotnet run --project samples/AgentEval.MafEvalLightPath -- --flat-only
//    dotnet run --project samples/AgentEval.MafEvalLightPath -- --composite-only
//
//  Requires:  AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
//             (skips cleanly with a friendly box when unset — CI-safe).
// ──────────────────────────────────────────────────────────────────────────────

using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.MAF.Evaluators;
using AgentEval.Metrics.Agentic;
using AgentEval.Metrics.RAG;
using AgentEval.Metrics.Safety;
using AgentEval.Core.Evals.Rendering;
using AgentEval.Evals;
using AgentEval.Output;

using MeaiIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;

namespace AgentEval.MafEvalLightPath;

internal static class Program
{
    private const string Query =
        "Find flights from Seattle to Paris for next Friday and a hotel near the Eiffel Tower.";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var autoOpen      = !args.Contains("--no-open", StringComparer.OrdinalIgnoreCase);
        var runFlat       = !args.Contains("--composite-only", StringComparer.OrdinalIgnoreCase);
        var runComposite  = !args.Contains("--flat-only", StringComparer.OrdinalIgnoreCase);
        PrintHeader();

        var endpoint   = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var apiKey     = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
        {
            PrintMissingCredentials();
            return 0; // graceful, CI-safe skip
        }

        var azure = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

        // A real MAF agent (Azure-backed IChatClient -> AIAgent) + a judge IChatClient.
        AIAgent agent = azure.GetChatClient(deployment).AsIChatClient().AsAIAgent(
            name: "TravelLightPathAgent",
            instructions: """
                You are a travel booking assistant. When asked to find flights or hotels,
                ALWAYS call the provided tools, then summarise the best option concisely.
                """,
            tools: [AIFunctionFactory.Create(SearchFlights), AIFunctionFactory.Create(SearchHotels)]);

        IChatClient judge = azure.GetChatClient(deployment).AsIChatClient();
        var chatConfig = new ChatConfiguration(judge);

        var subject = new SubjectIdentity(
            Kind: SubjectKind.Agent,
            Name: agent.Name ?? "TravelLightPathAgent",
            ModelId: deployment,
            Framework: $"MAF {s_maf} (agent.EvaluateAsync)");

        var outputDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "output"));
        Directory.CreateDirectory(outputDir);

        var produced = new List<string>();
        if (runFlat)
            produced.Add(await RunFlatLightPathAsync(agent, judge, chatConfig, subject, outputDir));
        if (runComposite)
            produced.Add(await RunCompositeLightPathAsync(agent, judge, chatConfig, subject, deployment, outputDir));

        Console.WriteLine();
        Console.WriteLine("KEY TAKEAWAYS:");
        Console.WriteLine("  • Both ran via MAF's NATIVE IAgentEvaluator overload: agent.EvaluateAsync(queries, evaluator).");
        Console.WriteLine("  • [1] flat metrics and [2] a full AgenticBenchmark COMPOSITE both plug in as one evaluator.");
        Console.WriteLine("  • The native adapter forwards EvalItem.Conversation (full, with tool calls), so even");
        Console.WriteLine("    code-based tool metrics now see the calls (MAF's MEAI-only adapter drops them).");

        if (autoOpen)
            foreach (var p in produced)
                TryOpen(p);
        else
            Console.WriteLine("\n(--no-open specified; not launching the browser)");

        return 0;
    }

    // ── [1] FLAT metrics as one MEAI IEvaluator ───────────────────────────────────
    private static async Task<string> RunFlatLightPathAsync(
        AIAgent agent, IChatClient judge, ChatConfiguration chatConfig,
        SubjectIdentity subject, string outputDir)
    {
        Console.WriteLine("\n══ [1] FLAT metrics via agent.EvaluateAsync() ════════════════════════════\n");

        MeaiIEvaluator metrics = AgentEvalEvaluators.Custom(
            new ToolSuccessMetric(),
            new ToolSelectionMetric(["SearchFlights", "SearchHotels"]),
            new TaskCompletionMetric(judge),
            new RelevanceMetric(judge),
            new CoherenceMetric(judge));

        // Wrap as MAF's native IAgentEvaluator (AgentEval.MAF) and use the native overload — no
        // ChatConfiguration at the call site. It forwards the full EvalItem.Conversation, so the
        // code-based tool metrics see the real tool calls.
        var evaluator = metrics.AsAgentEvaluator(chatConfig, "AgentEval-Flat");
        AgentEvaluationResults results = await agent.EvaluateAsync([Query], evaluator);

        Console.WriteLine($"MAF verdict: {results.Passed}/{results.Total} passed (AllPassed = {results.AllPassed})");

        var tree = MeaiToEvalResultBridge.Build("MAF Light-Path — Flat Metrics", [Query], results, subject.ModelId);
        PrintTree(tree, 0);

        return await RenderAsync(
            tree, subject,
            title: "MAF agent.EvaluateAsync() — Flat Metrics (light path)",
            regulation: $"AgentEval flat metrics via MEAI IEvaluator (MAF {s_maf})",
            runId: results.RunId, fileTag: "flat", outputDir);
    }

    // ── [2] A full AgenticBenchmark COMPOSITE as one MEAI IEvaluator ───────────────
    private static async Task<string> RunCompositeLightPathAsync(
        AIAgent agent, IChatClient judge, ChatConfiguration chatConfig,
        SubjectIdentity subject, string deployment, string outputDir)
    {
        Console.WriteLine("\n══ [2] FULL AgenticBenchmark COMPOSITE via agent.EvaluateAsync() ══════════\n");

        // The AgenticBenchmark composite is an AgentEval IEval (weighted tree + threshold). Its judge
        // is an AgentEval IEvaluator (ChatClientEvaluator); we then wrap the whole composite as a single
        // MEAI IEvaluator so MAF can run it.
        var benchmarkJudge = new ChatClientEvaluator(judge);
        var composite = AgenticBenchmark.ToolCallAccuracy(benchmarkJudge, judgeModel: deployment);
        var compositeEvaluator = composite.AsMeaiEvaluator();   // AgentEval.MAF: IEval -> MEAI IEvaluator

        // Same MAF-native path: wrap the composite as an IAgentEvaluator and run the native overload.
        var evaluator = compositeEvaluator.AsAgentEvaluator(chatConfig, "AgentEval-Composite");
        AgentEvaluationResults results = await agent.EvaluateAsync([Query], evaluator);

        Console.WriteLine($"MAF verdict: {results.Passed}/{results.Total} passed (AllPassed = {results.AllPassed})");

        // The rich composite tree captured by the adapter (full hierarchy, not the flattened rollup).
        var tree = compositeEvaluator.CapturedResults[0];
        PrintTree(tree, 0);

        return await RenderAsync(
            tree, subject,
            title: $"MAF agent.EvaluateAsync() — {composite.Name} (composite light path)",
            regulation: $"AgentEval AgenticBenchmark composite via MEAI IEvaluator (MAF {s_maf})",
            runId: results.RunId, fileTag: "composite", outputDir);
    }

    private static async Task<string> RenderAsync(
        EvalResult tree, SubjectIdentity subject, string title, string regulation,
        string? runId, string fileTag, string outputDir)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(outputDir, $"maf-lightpath-{fileTag}-{stamp}.html");
        var options = new EvalResultRenderOptions(
            Subject: subject,
            Title: title,
            RegulationOrBenchmark: regulation,
            RunId: runId,
            GeneratedAt: DateTimeOffset.UtcNow,
            IncludeProvenance: true,
            AuditHash: null,
            AgentEvalVersion: ResolveAgentEvalVersion());

        var bytes = await new HtmlEvalResultRenderer().RenderAsync(tree, options);
        await File.WriteAllBytesAsync(path, bytes);
        Console.WriteLine($"\nHTML report saved: {path}");
        return path;
    }

    // ── Tools ────────────────────────────────────────────────────────────────────

    [Description("Search for available flights between two cities on a given date.")]
    private static string SearchFlights(
        [Description("Departure city")] string origin,
        [Description("Arrival city")] string destination,
        [Description("Travel date")] string date)
    {
        Console.WriteLine($"  🔧 SearchFlights({origin} → {destination}, {date})");
        return $"Found 3 flights {origin}→{destination} on {date}: " +
               "AA101 ($450, 10h), DL205 ($520, 9h), UA309 ($480, 11h).";
    }

    [Description("Search for available hotels in a city for given dates.")]
    private static string SearchHotels(
        [Description("City to search")] string city,
        [Description("Check-in date")] string checkIn,
        [Description("Check-out date")] string checkOut)
    {
        Console.WriteLine($"  🔧 SearchHotels({city}, {checkIn}–{checkOut})");
        return $"Found 3 hotels in {city}: Hotel Le Marais ($180/night, 4★), " +
               "Ibis Paris ($95/night, 3★), Ritz Paris ($650/night, 5★).";
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static void PrintTree(EvalResult node, int indent)
    {
        var pad = new string(' ', indent * 2);
        var icon = node.Score.Passed ? "✅" : "❌";
        Console.WriteLine($"  {pad}{icon} {node.Metric.Name,-40} {node.Score.Value * 100,6:F1}%  [{node.Score.Label}]");
        foreach (var child in node.Details.SubResults ?? [])
            PrintTree(child, indent + 1);
    }

    private static void TryOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Console.WriteLine($"(opened {Path.GetFileName(path)} in your default browser)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(could not auto-open — open manually: {path})");
            Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string ResolveAgentEvalVersion() => InfoVersionOf(typeof(EvalResult).Assembly);

    // The actual Microsoft Agent Framework version, resolved at runtime so report metadata never goes
    // stale against the pinned package (mirrors ResolveAgentEvalVersion).
    private static readonly string s_maf = InfoVersionOf(typeof(AgentEvaluationResults).Assembly);

    private static string InfoVersionOf(System.Reflection.Assembly asm)
    {
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString() ?? "unknown";
    }

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine($"║  AgentEval × MAF {s_maf} — Evaluation light path (agent.EvaluateAsync)      ║");
        Console.WriteLine("║  Flat metrics AND a full AgenticBenchmark composite, as MEAI IEvaluators   ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
    }

    private static void PrintMissingCredentials()
    {
        Console.WriteLine("+---------------------------------------------------------------------------+");
        Console.WriteLine("|  SKIPPING — Azure OpenAI credentials required. Set:                       |");
        Console.WriteLine("|    AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT    |");
        Console.WriteLine("+---------------------------------------------------------------------------+");
    }
}
