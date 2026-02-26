// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.DataLoaders;
using AgentEval.Core;
using AgentEval.Exporters;
using AgentEval.Models;

namespace AgentEval.Samples;

/// <summary>
/// Sample 11: Datasets and Export - Rich Output Formats &amp; Visual Reports
/// 
/// This demonstrates AgentEval's comprehensive output format ecosystem:
/// - Loading test datasets from YAML using DatasetLoaderFactory
/// - Running batch evaluations against a real agent
/// - Rich format export: JUnit XML, Markdown, JSON, TRX, HTML, CSV
/// - Visual console output with progress bars and charts
/// - Interactive HTML reports with charts and filtering
/// - CI/CD integration patterns for all formats
/// 
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
/// ⏱️ Time to understand: 8 minutes
/// ⏱️ Time to run: ~1–2 minutes (depends on dataset size)
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
        await ExportAllFormats(results);
        await GenerateVisualReports(results);
        PrintCIIntegration();
        PrintRichOutputShowcase();
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
                Output = actualOutput.Length > 200 ? actualOutput[..200] : actualOutput,
                MetricScores = new Dictionary<string, double>
                {
                    ["relevance"] = passed ? 95.0 : 30.0,
                    ["correctness"] = passed ? 90.0 : 20.0
                }
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

    private static async Task ExportAllFormats(EvaluationReport report)
    {
        Console.WriteLine("\n📝 Step 3: Rich Output Format Showcase...\n");

        var outputDir = Path.Combine(Path.GetTempPath(), "agenteval-rich-export");
        Directory.CreateDirectory(outputDir);
        
        Console.WriteLine("   🔄 Exporting to all supported formats:");

        // Core CI/CD Formats
        await ExportJUnit(report, outputDir);
        await ExportMarkdown(report, outputDir);
        await ExportJson(report, outputDir);
        await ExportTrx(report, outputDir);

        // Rich Visual Formats  
        await ExportHtml(report, outputDir);
        await ExportCsv(report, outputDir);
        
        Console.WriteLine($"\n   📁 All files exported to: {outputDir}");
        Console.WriteLine("   🌐 Open the HTML report for interactive charts and filtering!\n");
    }
    
    private static async Task ExportJUnit(EvaluationReport report, string outputDir)
    {
        var junitPath = Path.Combine(outputDir, "results.xml");
        var exporter = ResultExporterFactory.Create(ExportFormat.Junit);
        await using var junitStream = File.Create(junitPath);
        await exporter.ExportAsync(report, junitStream);
        Console.WriteLine($"      ✅ JUnit XML:     {Path.GetFileName(junitPath)} (CI/CD integration)");
    }
    
    private static async Task ExportMarkdown(EvaluationReport report, string outputDir)
    {
        var mdPath = Path.Combine(outputDir, "results.md");
        var exporter = ResultExporterFactory.Create(ExportFormat.Markdown);
        await using var mdStream = File.Create(mdPath);
        await exporter.ExportAsync(report, mdStream);
        Console.WriteLine($"      ✅ Markdown:      {Path.GetFileName(mdPath)} (GitHub/docs)");
        
        // Demonstrate MarkdownExportOptions for customized output
        var customMdPath = Path.Combine(outputDir, "results-failures-first.md");
        var customExporter = new MarkdownExporter
        {
            Options = new MarkdownExportOptions
            {
                FailuresFirst = true,
                IncludeMetricBreakdown = true,
                IncludeFailureDetails = true
            }
        };
        await using var customStream = File.Create(customMdPath);
        await customExporter.ExportAsync(report, customStream);
        Console.WriteLine($"      ✅ Markdown:      {Path.GetFileName(customMdPath)} (failures-first, metrics)");
    }
    
    private static async Task ExportJson(EvaluationReport report, string outputDir)
    {
        var jsonPath = Path.Combine(outputDir, "results.json");
        var exporter = ResultExporterFactory.Create(ExportFormat.Json);
        await using var jsonStream = File.Create(jsonPath);
        await exporter.ExportAsync(report, jsonStream);
        Console.WriteLine($"      ✅ JSON:          {Path.GetFileName(jsonPath)} (API/programmatic)");
    }
    
    private static async Task ExportTrx(EvaluationReport report, string outputDir)
    {
        var trxPath = Path.Combine(outputDir, "results.trx");
        var exporter = ResultExporterFactory.Create(ExportFormat.Trx);
        await using var trxStream = File.Create(trxPath);
        await exporter.ExportAsync(report, trxStream);
        Console.WriteLine($"      ✅ TRX:           {Path.GetFileName(trxPath)} (Visual Studio)");
    }
    
    private static async Task ExportHtml(EvaluationReport report, string outputDir)
    {
        var htmlPath = Path.Combine(outputDir, "results.html");
        var htmlContent = GenerateInteractiveHtmlReport(report);
        await File.WriteAllTextAsync(htmlPath, htmlContent);
        Console.WriteLine($"      ✅ HTML Report:   {Path.GetFileName(htmlPath)} (interactive charts)");
    }
    
    private static async Task ExportCsv(EvaluationReport report, string outputDir)
    {
        var csvPath = Path.Combine(outputDir, "results.csv");
        var exporter = ResultExporterFactory.Create(ExportFormat.Csv);
        await using var csvStream = File.Create(csvPath);
        await exporter.ExportAsync(report, csvStream);
        Console.WriteLine($"      ✅ CSV:           {Path.GetFileName(csvPath)} (Excel/analysis)");
    }

    private static async Task GenerateVisualReports(EvaluationReport report)
    {
        Console.WriteLine("📝 Step 4: Visual Report Generation...\n");
        
        // ASCII Chart in Console
        PrintResultsChart(report);
        
        // Performance Metrics Table  
        PrintPerformanceTable(report);
        
        // Rich Console Summary
        PrintRichSummary(report);
    }
    
    private static void PrintResultsChart(EvaluationReport report)
    {
        Console.WriteLine("   📊 Test Results Chart:");
        Console.WriteLine("   ┌─────────────────────────────────────────────────────┐");
        
        var passedCount = report.PassedTests;
        var failedCount = report.FailedTests;
        var totalWidth = 50;
        var passedWidth = totalWidth * passedCount / report.TotalTests;
        var failedWidth = totalWidth - passedWidth;
        
        Console.Write("   │ ");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write(new string('█', passedWidth));
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write(new string('█', failedWidth));
        Console.ResetColor();
        Console.WriteLine(" │");
        Console.WriteLine("   └─────────────────────────────────────────────────────┘");
        Console.WriteLine($"   Green: {passedCount} passed | Red: {failedCount} failed | Success Rate: {(double)passedCount/report.TotalTests:P1}\n");
    }
    
    private static void PrintPerformanceTable(EvaluationReport report)
    {
        Console.WriteLine("   ⚡ Performance Metrics:");
        Console.WriteLine("   ┌─────────────────┬─────────────────┬─────────────────┐");
        Console.WriteLine("   │     Metric      │      Value      │   Threshold     │");
        Console.WriteLine("   ├─────────────────┼─────────────────┼─────────────────┤");
        
        var avgDuration = report.TestResults.Average(r => r.DurationMs);
        var maxDuration = report.TestResults.Max(r => r.DurationMs);
        var totalDuration = report.TestResults.Sum(r => r.DurationMs);
        
        Console.WriteLine($"   │ Avg Response    │ {avgDuration:F0}ms           │ <2000ms         │");
        Console.WriteLine($"   │ Max Response    │ {maxDuration:F0}ms           │ <5000ms         │");
        Console.WriteLine($"   │ Total Runtime   │ {totalDuration:F0}ms          │ N/A             │"); 
        Console.WriteLine($"   │ Tests/sec       │ {1000.0 * report.TotalTests / totalDuration:F1}           │ >5.0            │");
        Console.WriteLine("   └─────────────────┴─────────────────┴─────────────────┘\n");
    }
    
    private static void PrintRichSummary(EvaluationReport report)
    {
        Console.WriteLine("   📋 Executive Summary:");
        Console.WriteLine("   ┌───────────────────────────────────────────────────────────────┐");
        Console.WriteLine($"   │ Dataset: {report.Name}     │");
        Console.WriteLine($"   │ Agent Model: {report.Agent?.Model}                    │");
        Console.WriteLine($"   │ Total Tests: {report.TotalTests}                             │");
        Console.WriteLine($"   │ Success Rate: {(double)report.PassedTests / report.TotalTests:P1}                       │");
        Console.WriteLine($"   │ Average Score: {report.OverallScore:F1}%                     │");
        Console.WriteLine($"   │ Time Taken: {(report.EndTime - report.StartTime).TotalSeconds:F1}s                     │");
        Console.WriteLine("   └───────────────────────────────────────────────────────────────┘\n");
    }
    
    private static string GenerateInteractiveHtmlReport(EvaluationReport report)
    {
        var passedCount = report.PassedTests;
        var failedCount = report.FailedTests;
        var avgScore = report.OverallScore;
        
        var testRowsHtml = string.Join("\n", report.TestResults.Select(t =>
        {
            var status = t.Passed ? "passed" : "failed";
            var statusClass = t.Passed ? "status-pass" : "status-fail";
            var statusText = t.Passed ? "&#x2705; PASS" : "&#x274C; FAIL";
            var outputPreview = t.Output?[..Math.Min(100, t.Output?.Length ?? 0)] ?? "";
            var ellipsis = (t.Output?.Length > 100) ? "..." : "";
            return $"        <div class=\"test-row test-item\" data-status=\"{status}\">" +
                   $"<div>{t.Name}</div>" +
                   $"<div class=\"{statusClass}\">{statusText}</div>" +
                   $"<div>{t.Score:F0}%</div>" +
                   $"<div style=\"font-family: monospace; font-size: 0.9em;\">{outputPreview}{ellipsis}</div></div>";
        }));
        
        var durationsJs = string.Join(", ", report.TestResults.Select(r => r.DurationMs.ToString()));
        
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("    <meta charset=\"UTF-8\">");
        sb.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine($"    <title>AgentEval Report - {report.Name}</title>");
        sb.AppendLine("    <script src=\"https://cdn.jsdelivr.net/npm/chart.js\"></script>");
        sb.AppendLine("    <style>");
        sb.AppendLine("        body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; margin: 20px; background-color: #f5f5f5; }");
        sb.AppendLine("        .header { background: linear-gradient(135deg, #667eea 0%, #764ba2 100%); color: white; padding: 20px; border-radius: 10px; text-align: center; }");
        sb.AppendLine("        .summary { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 20px; margin: 20px 0; }");
        sb.AppendLine("        .metric-card { background: white; padding: 20px; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); text-align: center; }");
        sb.AppendLine("        .metric-value { font-size: 2em; font-weight: bold; color: #667eea; }");
        sb.AppendLine("        .chart-container { background: white; padding: 20px; border-radius: 8px; margin: 20px 0; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }");
        sb.AppendLine("        .test-details { background: white; padding: 20px; border-radius: 8px; margin: 20px 0; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }");
        sb.AppendLine("        .test-row { display: grid; grid-template-columns: 200px 80px 100px 1fr; gap: 10px; padding: 10px; border-bottom: 1px solid #eee; align-items: center; }");
        sb.AppendLine("        .test-header { font-weight: bold; background-color: #f8f9fa; }");
        sb.AppendLine("        .status-pass { color: #28a745; }");
        sb.AppendLine("        .status-fail { color: #dc3545; }");
        sb.AppendLine("        .filter-controls { margin: 20px 0; }");
        sb.AppendLine("        .filter-button { padding: 8px 16px; margin: 5px; border: none; border-radius: 4px; cursor: pointer; }");
        sb.AppendLine("        .filter-all { background-color: #6c757d; color: white; }");
        sb.AppendLine("        .filter-passed { background-color: #28a745; color: white; }");
        sb.AppendLine("        .filter-failed { background-color: #dc3545; color: white; }");
        sb.AppendLine("    </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("    <div class=\"header\">");
        sb.AppendLine($"        <h1>AgentEval Report</h1>");
        sb.AppendLine($"        <h2>{report.Name}</h2>");
        sb.AppendLine($"        <p>Generated on {report.EndTime:yyyy-MM-dd HH:mm:ss} UTC</p>");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"summary\">");
        sb.AppendLine($"        <div class=\"metric-card\"><div class=\"metric-value\">{report.TotalTests}</div><div>Total Tests</div></div>");
        sb.AppendLine($"        <div class=\"metric-card\"><div class=\"metric-value\">{passedCount}</div><div>Passed</div></div>");
        sb.AppendLine($"        <div class=\"metric-card\"><div class=\"metric-value\">{failedCount}</div><div>Failed</div></div>");
        sb.AppendLine($"        <div class=\"metric-card\"><div class=\"metric-value\">{avgScore:F1}%</div><div>Avg Score</div></div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"chart-container\"><h3>Test Results Overview</h3><canvas id=\"resultsChart\" width=\"400\" height=\"200\"></canvas></div>");
        sb.AppendLine("    <div class=\"chart-container\"><h3>Performance Distribution</h3><canvas id=\"performanceChart\" width=\"400\" height=\"200\"></canvas></div>");
        sb.AppendLine("    <div class=\"test-details\">");
        sb.AppendLine("        <h3>Test Details</h3>");
        sb.AppendLine("        <div class=\"filter-controls\">");
        sb.AppendLine("            <button class=\"filter-button filter-all\" onclick=\"filterTests('all')\">All Tests</button>");
        sb.AppendLine("            <button class=\"filter-button filter-passed\" onclick=\"filterTests('passed')\">Passed Only</button>");
        sb.AppendLine("            <button class=\"filter-button filter-failed\" onclick=\"filterTests('failed')\">Failed Only</button>");
        sb.AppendLine("        </div>");
        sb.AppendLine("        <div class=\"test-row test-header\"><div>Test Name</div><div>Status</div><div>Score</div><div>Output Preview</div></div>");
        sb.AppendLine(testRowsHtml);
        sb.AppendLine("    </div>");
        sb.AppendLine("    <script>");
        sb.AppendLine("        const ctx1 = document.getElementById('resultsChart').getContext('2d');");
        sb.AppendLine($"        new Chart(ctx1, {{ type: 'doughnut', data: {{ labels: ['Passed','Failed'], datasets: [{{ data: [{passedCount},{failedCount}], backgroundColor: ['#28a745','#dc3545'], borderWidth: 2, borderColor: '#fff' }}] }}, options: {{ responsive: true, plugins: {{ legend: {{ position: 'bottom' }} }} }} }});");
        sb.AppendLine($"        const durations = [{durationsJs}];");
        sb.AppendLine("        const ctx2 = document.getElementById('performanceChart').getContext('2d');");
        sb.AppendLine("        new Chart(ctx2, { type: 'bar', data: { labels: durations.map((d,i) => 'T'+(i+1)), datasets: [{ label: 'Response Time (ms)', data: durations, backgroundColor: 'rgba(102,126,234,0.6)', borderColor: 'rgba(102,126,234,1)', borderWidth: 1 }] }, options: { responsive: true, scales: { y: { title: { display: true, text: 'ms' } } } } });");
        sb.AppendLine("        function filterTests(status) { document.querySelectorAll('.test-item').forEach(item => { item.style.display = (status === 'all' || item.dataset.status === status) ? 'grid' : 'none'; }); }");
        sb.AppendLine("    </script>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        
        return sb.ToString();
    }
    
    private static void PrintCIIntegration()
    {
        Console.WriteLine("📝 Step 5: CI/CD Integration Showcase...\n");

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(@"   # GitHub Actions (using dotnet test)
   - name: Run AgentEval Tests
     run: dotnet test --logger trx --logger ""junit;LogFilePath=results.xml""

   - name: Publish Test Results  
     uses: dorny/test-reporter@v1
     with:
       name: AgentEval Results
       path: results.xml
       reporter: java-junit

   # Azure DevOps (Visual Studio Integration)
   - task: PublishTestResults@2
     inputs:
       testResultsFormat: 'JUnit'
       testResultsFiles: '**/results.xml'
       searchFolder: '$(System.DefaultWorkingDirectory)'
       
   - task: PublishHtmlReport@1
     inputs:
       reportDir: '$(System.DefaultWorkingDirectory)/results.html'");
        Console.ResetColor();
        Console.WriteLine();
    }
    
    private static void PrintRichOutputShowcase()
    {
        Console.WriteLine("\n📝 Step 6: Rich Output Format Capabilities...\n");
        
        Console.WriteLine("   📊 Format Comparison:");
        Console.WriteLine("   ┌─────────────┬─────────────────┬─────────────────────┬─────────────────┐");
        Console.WriteLine("   │   Format    │   Primary Use   │      Features       │   File Size     │");
        Console.WriteLine("   ├─────────────┼─────────────────┼─────────────────────┼─────────────────┤");
        Console.WriteLine("   │ JUnit XML   │ CI/CD           │ Standards compliant │ Small           │");
        Console.WriteLine("   │ Markdown    │ Documentation   │ GitHub render       │ Medium          │");
        Console.WriteLine("   │ JSON        │ APIs/Scripts    │ Machine readable    │ Medium          │");
        Console.WriteLine("   │ TRX         │ Visual Studio   │ IDE integration     │ Medium          │");
        Console.WriteLine("   │ HTML        │ Interactive     │ Charts, filtering   │ Large           │");
        Console.WriteLine("   │ CSV         │ Analysis        │ Excel/BI tools      │ Small           │");
        Console.WriteLine("   └─────────────┴─────────────────┴─────────────────────┴─────────────────┘\n");
        
        Console.WriteLine("   🎯 Advanced Features:");
        Console.WriteLine("      • HTML: Interactive charts with Chart.js, filtering, responsive design");
        Console.WriteLine("      • Markdown: GitHub-flavored with tables, charts, and emoji");
        Console.WriteLine("      • JSON: Structured metadata, performance metrics, extensible schema");
        Console.WriteLine("      • CSV: Pivot-ready data for business intelligence and trend analysis");
        Console.WriteLine("      • JUnit: Full compatibility with CI/CD tools (Jenkins, GitHub, Azure DevOps)");
        Console.WriteLine("      • TRX: Deep Visual Studio integration with test explorer");
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   📦 SAMPLE 11: RICH OUTPUT FORMATS & VISUAL REPORTS                         ║
║   Interactive HTML, charts, CI/CD integration + 6 export formats             ║
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
        Console.WriteLine("   • DatasetLoaderFactory auto-selects optimal loader (JSON/JSONL/CSV/YAML)");
        Console.WriteLine("   • 6 Rich Export Formats: JUnit, Markdown, JSON, TRX, HTML, CSV");
        Console.WriteLine("   • Interactive HTML with Chart.js: filtering, charts, responsive design");
        Console.WriteLine("   • CI/CD Ready: GitHub Actions, Azure DevOps, Jenkins integration");
        Console.WriteLine("   • Visual Console Output: ASCII charts, performance tables, rich formatting");
        Console.WriteLine("   • Business Intelligence: CSV export for Excel/BI tool analysis");
        Console.WriteLine("\n🔗 NEXT: Run Sample 12 for policy and safety evaluation!");
        Console.WriteLine("📊 TIP: Open the HTML report (results.html) for interactive experience!\n");
    }
}
