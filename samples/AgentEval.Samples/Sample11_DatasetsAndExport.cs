// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.DataLoaders;
using AgentEval.Exporters;

namespace AgentEval.Samples;

/// <summary>
/// Sample 11: Datasets and Export - Batch evaluation with data files
/// 
/// This demonstrates:
/// - Loading test datasets from YAML using DatasetLoaderFactory
/// - Running batch evaluations against a real agent
/// - Multi-format export (JUnit, Markdown, JSON, TRX)
/// - CI/CD integration patterns
/// 
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
/// ⏱️ Time to understand: 7 minutes
/// </summary>
public static class Sample11_DatasetsAndExport
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            PrintMissingCredentialsBox();
            return;
        }

        Console.WriteLine($"   🔗 Endpoint: {AIConfig.Endpoint}");
        Console.WriteLine($"   🤖 Model: {AIConfig.ModelDeployment}\n");

        var testCases = await LoadDataset();
        var results = await RunBatchEvaluation(testCases);
        await ExportResults(results);
        PrintCIIntegration();
        PrintKeyTakeaways();
    }

    private static async Task<IReadOnlyList<DatasetTestCase>> LoadDataset()
    {
        Console.WriteLine("📝 Step 1: Loading dataset with DatasetLoaderFactory...\n");

        var datasetPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "datasets", "rag-qa.yaml");
        if (!File.Exists(datasetPath))
        {
            datasetPath = Path.Combine("samples", "datasets", "rag-qa.yaml");
        }

        Console.WriteLine($"   Dataset: {datasetPath}");
        var loader = DatasetLoaderFactory.CreateFromExtension(".yaml");
        Console.WriteLine($"   Loader: {loader.GetType().Name} (format: {loader.Format})\n");

        var testCases = await loader.LoadAsync(datasetPath);

        Console.WriteLine($"   Loaded {testCases.Count} test cases:");
        foreach (var tc in testCases)
        {
            var input = tc.Input.Length > 50 ? tc.Input[..50] + "..." : tc.Input;
            var context = tc.Context?.Count > 0 ? $" [{tc.Context.Count} context docs]" : "";
            Console.WriteLine($"      • [{tc.Id}] \"{input}\"{context}");
        }

        return testCases;
    }

    private static async Task<EvaluationReport> RunBatchEvaluation(IReadOnlyList<DatasetTestCase> testCases)
    {
        Console.WriteLine("\n📝 Step 2: Running batch evaluation against real agent...\n");

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        var testResults = new List<TestResultSummary>();
        var startTime = DateTimeOffset.UtcNow;

        foreach (var tc in testCases)
        {
            Console.Write($"   Running: [{tc.Id}] \"{tc.Input[..Math.Min(40, tc.Input.Length)]}\" ... ");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await chatClient.GetResponseAsync(tc.Input);
            sw.Stop();

            var actualOutput = response.Text ?? "";
            var passed = tc.ExpectedOutput == null ||
                         actualOutput.Contains(tc.ExpectedOutput, StringComparison.OrdinalIgnoreCase);

            Console.ForegroundColor = passed ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine(passed ? $"✅ ({sw.ElapsedMilliseconds}ms)" : $"❌ ({sw.ElapsedMilliseconds}ms)");
            Console.ResetColor();

            testResults.Add(new TestResultSummary
            {
                Name = tc.Id,
                Score = passed ? 100 : 0,
                Passed = passed,
                DurationMs = sw.ElapsedMilliseconds,
                Output = actualOutput.Length > 200 ? actualOutput[..200] : actualOutput
            });
        }

        var report = new EvaluationReport
        {
            Name = "Batch Evaluation — rag-qa.yaml",
            StartTime = startTime,
            EndTime = DateTimeOffset.UtcNow,
            TotalTests = testResults.Count,
            PassedTests = testResults.Count(r => r.Passed),
            FailedTests = testResults.Count(r => !r.Passed),
            OverallScore = testResults.Average(r => r.Score),
            Agent = new AgentInfo { Name = "QA Agent", Model = AIConfig.ModelDeployment },
            TestResults = testResults
        };

        Console.WriteLine($"\n   Results: {report.PassedTests}/{report.TotalTests} passed ({report.OverallScore:F0}% avg score)");
        return report;
    }

    private static async Task ExportResults(EvaluationReport report)
    {
        Console.WriteLine("\n📝 Step 3: Multi-Format Export...\n");

        var outputDir = Path.Combine(Path.GetTempPath(), "agenteval-export-demo");
        Directory.CreateDirectory(outputDir);

        // JUnit XML
        var junitPath = Path.Combine(outputDir, "results.xml");
        var junitExporter = new JUnitXmlExporter();
        await using (var junitStream = File.Create(junitPath))
        {
            await junitExporter.ExportAsync(report, junitStream);
        }
        Console.WriteLine($"   ✅ JUnit XML:  {junitPath}");

        // Markdown
        var mdPath = Path.Combine(outputDir, "results.md");
        var mdExporter = new MarkdownExporter();
        await using (var mdStream = File.Create(mdPath))
        {
            await mdExporter.ExportAsync(report, mdStream);
        }
        Console.WriteLine($"   ✅ Markdown:   {mdPath}");

        // JSON
        var jsonPath = Path.Combine(outputDir, "results.json");
        var jsonExporter = new JsonExporter();
        await using (var jsonStream = File.Create(jsonPath))
        {
            await jsonExporter.ExportAsync(report, jsonStream);
        }
        Console.WriteLine($"   ✅ JSON:       {jsonPath}");

        // TRX (Visual Studio)
        var trxPath = Path.Combine(outputDir, "results.trx");
        var trxExporter = new TrxExporter();
        await using (var trxStream = File.Create(trxPath))
        {
            await trxExporter.ExportAsync(report, trxStream);
        }
        Console.WriteLine($"   ✅ TRX:        {trxPath}");

        Console.WriteLine($"\n   📁 All files saved to: {outputDir}");

        // Show Markdown preview
        Console.WriteLine("\n   📋 Markdown preview:\n");
        Console.ForegroundColor = ConsoleColor.Cyan;
        var mdContent = await File.ReadAllTextAsync(mdPath);
        foreach (var line in mdContent.Split('\n').Take(20))
        {
            Console.WriteLine($"   {line}");
        }
        if (mdContent.Split('\n').Length > 20) Console.WriteLine("   ...");
        Console.ResetColor();
    }

    private static void PrintCIIntegration()
    {
        Console.WriteLine("\n\n📝 Step 4: CI/CD integration patterns...\n");

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(@"   # GitHub Actions
   - name: Run AgentEval tests
     run: agenteval eval --dataset tests.yaml --export junit --output results.xml
   
   - name: Publish Test Results
     uses: dorny/test-reporter@v1
     with:
       name: AgentEval Results
       path: results.xml
       reporter: java-junit

   # Azure DevOps
   - task: PublishTestResults@2
     inputs:
       testResultsFormat: 'JUnit'
       testResultsFiles: '**/results.xml'");
        Console.ResetColor();
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   📦 SAMPLE 11: DATASETS AND EXPORT                                          ║
║   Batch evaluation with real datasets + multi-format export                   ║
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
   │  ⚠️  SKIPPING SAMPLE 11 - Azure OpenAI Credentials Required               │
   ├─────────────────────────────────────────────────────────────────────────────┤
   │  This sample loads a real dataset and runs batch evaluations.               │
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
        Console.WriteLine("\n\n💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • DatasetLoaderFactory.CreateFromExtension() auto-selects JSON/JSONL/CSV/YAML loader");
        Console.WriteLine("   • Real exporters: JUnitXmlExporter, MarkdownExporter, JsonExporter, TrxExporter");
        Console.WriteLine("   • Export files integrate directly with CI/CD (GitHub Actions, Azure DevOps)");
        Console.WriteLine("   • Use agenteval CLI for automated batch evaluation in pipelines");
        Console.WriteLine("\n🔗 NEXT: Run Sample 12 for policy and safety evaluation!\n");
    }
}
