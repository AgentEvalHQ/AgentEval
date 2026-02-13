// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Exporters;

namespace AgentEval.Samples;

/// <summary>
/// Sample 10: Datasets and Export - Batch evaluation with data files
/// 
/// This demonstrates:
/// - Loading test datasets from JSON, JSONL, CSV, YAML
/// - Field aliasing for different data formats
/// - Running batch evaluations
/// - Multi-format export (JUnit, Markdown, JSON, TRX)
/// - GitHub PR comment format
/// - CI/CD integration patterns
/// 
/// ⏱️ Time to understand: 7 minutes
/// 
/// Related samples:
/// - Sample15_ModelComparison: Uses ToGitHubComment() for model results
/// </summary>
public static class Sample10_DatasetsAndExport
{
    public static async Task RunAsync()
    {
        PrintHeader();

        // ═══════════════════════════════════════════════════════════════
        // STEP 1: Supported dataset formats
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 1: Supported dataset formats...\n");
        
        Console.WriteLine("   AgentEval supports multiple data formats:\n");
        
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("   📄 JSON (TestCases.json):");
        Console.WriteLine(@"   [
     { ""input"": ""What is 2+2?"", ""expectedOutput"": ""4"" },
     { ""input"": ""Capital of France?"", ""expectedOutput"": ""Paris"" }
   ]");
        Console.ResetColor();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("   📄 JSONL (TestCases.jsonl) - one per line:");
        Console.WriteLine(@"   {""input"": ""What is 2+2?"", ""expectedOutput"": ""4""}
   {""input"": ""Capital of France?"", ""expectedOutput"": ""Paris""}");
        Console.ResetColor();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("   📄 CSV (TestCases.csv):");
        Console.WriteLine(@"   input,expectedOutput,tags
   What is 2+2?,4,math
   Capital of France?,Paris,geography");
        Console.ResetColor();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("   📄 YAML (TestCases.yaml):");
        Console.WriteLine(@"   - input: What is 2+2?
     expectedOutput: ""4""
   - input: Capital of France?
     expectedOutput: Paris");
        Console.ResetColor();

        // ═══════════════════════════════════════════════════════════════
        // STEP 2: Loading datasets with aliasing
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 2: Loading datasets with field aliasing...\n");
        
        Console.WriteLine("   When your data uses different field names, use aliases:\n");
        
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"   // Your data has ""question"" and ""answer"" but AgentEval expects ""input"" and ""expectedOutput""
   var loader = new JsonDatasetLoader(new DatasetOptions
   {
       FieldAliases = new Dictionary<string, string>
       {
           [""question""] = ""input"",
           [""answer""] = ""expectedOutput"",
           [""category""] = ""tags""
       }
   });
   
   var testCases = await loader.LoadAsync(""my-dataset.json"");");
        Console.ResetColor();
        Console.WriteLine();

        // Simulate loading
        var testCases = new List<TestCaseData>
        {
            new() { Input = "What is 2+2?", ExpectedOutput = "4", Tags = ["math"] },
            new() { Input = "Capital of France?", ExpectedOutput = "Paris", Tags = ["geography"] },
            new() { Input = "Who wrote Hamlet?", ExpectedOutput = "Shakespeare", Tags = ["literature"] }
        };
        
        Console.WriteLine($"   Loaded {testCases.Count} test cases:");
        foreach (var tc in testCases)
        {
            Console.WriteLine($"      • \"{tc.Input}\" → \"{tc.ExpectedOutput}\" [{string.Join(", ", tc.Tags)}]");
        }

        // ═══════════════════════════════════════════════════════════════
        // STEP 3: Running batch evaluation
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 3: Running batch evaluation...\n");
        
        var results = new List<TestResultData>();
        
        foreach (var testCase in testCases)
        {
            await Task.Delay(50); // Simulate agent call
            
            // Simulate agent response
            var passed = testCase.ExpectedOutput.Length > 0;
            results.Add(new TestResultData
            {
                TestName = testCase.Input,
                Passed = passed,
                Score = passed ? 100 : 0,
                ActualOutput = testCase.ExpectedOutput,
                Duration = TimeSpan.FromMilliseconds(Random.Shared.Next(50, 200))
            });
            
            Console.Write($"   Running: \"{testCase.Input[..Math.Min(30, testCase.Input.Length)]}...\" ");
            Console.ForegroundColor = passed ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine(passed ? "✅" : "❌");
            Console.ResetColor();
        }

        // ═══════════════════════════════════════════════════════════════
        // STEP 4: JUnit XML export for CI/CD
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 4: JUnit XML export for CI/CD...\n");
        
        var junitXml = GenerateJUnitXml(results);
        
        Console.WriteLine("   Generated JUnit XML for CI integration:\n");
        Console.ForegroundColor = ConsoleColor.Cyan;
        foreach (var line in junitXml.Split('\n').Take(15))
        {
            Console.WriteLine($"   {line}");
        }
        Console.ResetColor();
        Console.WriteLine("   ...\n");
        
        Console.WriteLine("   💡 CI/CD Integration Examples:");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(@"
   # GitHub Actions
   - name: Run AgentEval tests
     run: dotnet run -- eval --export junit --output results.xml
   
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

        // ═══════════════════════════════════════════════════════════════
        // STEP 5: Multi-Format Export Demo
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 5: Multi-Format Export Demo...\n");
        
        // Build an EvaluationReport from our results
        var report = new EvaluationReport
        {
            Name = "Batch Evaluation Demo",
            StartTime = DateTimeOffset.UtcNow.AddSeconds(-results.Sum(r => r.Duration.TotalSeconds)),
            EndTime = DateTimeOffset.UtcNow,
            TotalTests = results.Count,
            PassedTests = results.Count(r => r.Passed),
            FailedTests = results.Count(r => !r.Passed),
            OverallScore = results.Average(r => r.Score),
            Agent = new AgentInfo { Name = "Demo Agent", Model = "gpt-4o-mini" },
            TestResults = results.Select(r => new TestResultSummary
            {
                Name = r.TestName,
                Score = r.Score,
                Passed = r.Passed,
                DurationMs = (long)r.Duration.TotalMilliseconds,
                Output = r.ActualOutput
            }).ToList()
        };
        
        // Export to multiple formats using real exporters
        var outputDir = Path.Combine(Path.GetTempPath(), "agenteval-export-demo");
        Directory.CreateDirectory(outputDir);
        
        Console.WriteLine("   Exporting to multiple formats:\n");
        
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
        
        Console.WriteLine($"\n   📁 All files saved to: {outputDir}\n");
        
        // Show Markdown preview
        Console.WriteLine("   📋 Markdown output preview:\n");
        Console.ForegroundColor = ConsoleColor.Cyan;
        var mdContent = await File.ReadAllTextAsync(mdPath);
        foreach (var line in mdContent.Split('\n').Take(20))
        {
            Console.WriteLine($"   {line}");
        }
        if (mdContent.Split('\n').Length > 20) Console.WriteLine("   ...");
        Console.ResetColor();
        Console.WriteLine();
        
        // ═══════════════════════════════════════════════════════════════
        // STEP 6: GitHub PR Comment Format
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 6: GitHub PR Comment Format...\n");
        
        Console.WriteLine("   For model comparisons, use result.ToGitHubComment() for collapsible PR comments.");
        Console.WriteLine("   (See Sample15_ModelComparison for full demo)\n");
        
        Console.WriteLine("   For batch results, Markdown export can be posted as PR comments:\n");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(@"   # GitHub Actions workflow snippet:
   - name: Run AgentEval
     run: agenteval eval --dataset tests.jsonl --export markdown --output evaluation.md
   
   - name: Post PR Comment
     uses: marocchino/sticky-pull-request-comment@v2
     with:
       path: evaluation.md");
        Console.ResetColor();
        Console.WriteLine();

        // ═══════════════════════════════════════════════════════════════
        // STEP 7: CLI usage
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 7: CLI usage for batch evaluation...\n");
        
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"   # Initialize a test project
   agenteval init
   
   # Run evaluation on a dataset
   agenteval eval --dataset tests.jsonl --model gpt-4o-mini
   
   # Export to JUnit for CI
   agenteval eval --dataset tests.jsonl --export junit --output results.xml
   
   # Run with multiple formats
   agenteval eval --dataset tests.jsonl --export json,junit,markdown
   
   # List available metrics
   agenteval list metrics
   
   # Run specific metrics
   agenteval eval --dataset tests.jsonl --metrics relevance,faithfulness");
        Console.ResetColor();

        await Task.Delay(1); // Keep async signature
        
        // ═══════════════════════════════════════════════════════════════
        // KEY TAKEAWAYS
        // ═══════════════════════════════════════════════════════════════
        PrintKeyTakeaways();
    }

    private static string GenerateJUnitXml(List<TestResultData> results)
    {
        var passed = results.Count(r => r.Passed);
        var failed = results.Count - passed;
        var totalTime = results.Sum(r => r.Duration.TotalSeconds);
        
        var xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<testsuites tests=""{results.Count}"" failures=""{failed}"" time=""{totalTime:F3}"">
  <testsuite name=""AgentEval"" tests=""{results.Count}"" failures=""{failed}"" time=""{totalTime:F3}"">";

        foreach (var result in results)
        {
            xml += $@"
    <testcase name=""{EscapeXml(result.TestName)}"" time=""{result.Duration.TotalSeconds:F3}"" classname=""AgentEval.Tests"">";
            
            if (!result.Passed)
            {
                xml += $@"
      <failure message=""Test failed"">Score: {result.Score}/100</failure>";
            }
            
            xml += @"
    </testcase>";
        }

        xml += @"
  </testsuite>
</testsuites>";
        
        return xml;
    }

    private static string EscapeXml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║                   Sample 10: Datasets and Export                              ║
║                                                                               ║
║   Learn how to:                                                               ║
║   • Load test datasets from JSON, JSONL, CSV, YAML                            ║
║   • Use field aliasing for different data formats                             ║
║   • Run batch evaluations on datasets                                         ║
║   • Export to multiple formats (JUnit, Markdown, JSON, TRX)                   ║
║   • Integrate with CI/CD pipelines and GitHub PR comments                     ║
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
│  1. Multiple dataset formats supported:                                         │
│     - JSON, JSONL (line-delimited), CSV, YAML                                   │
│     - Use IDatasetLoader interface for custom formats                           │
│                                                                                 │
│  2. Field aliasing for flexibility:                                             │
│     new DatasetOptions { FieldAliases = { [""question""] = ""input"" } }           │
│                                                                                 │
│  3. Multi-format export (use real exporters):                                   │
│     - JUnitXmlExporter: CI/CD (GitHub Actions, Azure DevOps, Jenkins)           │
│     - MarkdownExporter: PR comments and documentation                           │
│     - JsonExporter:     Custom dashboards and tooling                           │
│     - TrxExporter:      Visual Studio Test Results                              │
│                                                                                 │
│  4. CLI for automation:                                                         │
│     agenteval eval --dataset tests.jsonl --export junit,markdown,json           │
│                                                                                 │
│  5. GitHub PR Comments:                                                         │
│     - Use MarkdownExporter output with sticky-pull-request-comment action       │
│     - For model comparison: result.ToGitHubComment() (see Sample15)             │
│                                                                                 │
└─────────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }

    // Local types for this sample
    private record TestCaseData
    {
        public required string Input { get; init; }
        public required string ExpectedOutput { get; init; }
        public List<string> Tags { get; init; } = [];
    }

    private record TestResultData
    {
        public required string TestName { get; init; }
        public bool Passed { get; init; }
        public int Score { get; init; }
        public string? ActualOutput { get; init; }
        public TimeSpan Duration { get; init; }
    }
}
