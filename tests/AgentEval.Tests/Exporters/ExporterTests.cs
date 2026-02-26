// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;
using AgentEval.Core;
using AgentEval.Exporters;
using AgentEval.Models;

namespace AgentEval.Tests;

/// <summary>
/// Tests for result exporters.
/// </summary>
public class ExporterTests
{
    private static EvaluationReport CreateSampleReport() => new()
    {
        RunId = "abc12345",
        Name = "Test Suite",
        StartTime = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero),
        EndTime = new DateTimeOffset(2026, 1, 5, 10, 0, 5, TimeSpan.Zero),
        TotalTests = 3,
        PassedTests = 2,
        FailedTests = 1,
        OverallScore = 78.3,
        Agent = new AgentInfo { Name = "TestAgent", Model = "gpt-4o" },
        TestResults = new List<TestResultSummary>
        {
            new() { Name = "code_tool_selection", Score = 95.0, Passed = true, DurationMs = 1200, Category = "Agentic" },
            new() { Name = "llm_faithfulness", Score = 88.0, Passed = true, DurationMs = 2300, Category = "RAG" },
            new() { Name = "llm_task_completion", Score = 45.0, Passed = false, DurationMs = 3100, Category = "Agentic", Error = "Incomplete reasoning" }
        }
    };

    #region Factory Tests

    [Fact]
    public void Factory_Creates_JsonExporter()
    {
        var exporter = ResultExporterFactory.Create(ExportFormat.Json);
        
        Assert.IsType<JsonExporter>(exporter);
        Assert.Equal(ExportFormat.Json, exporter.Format);
        Assert.Equal(".json", exporter.FileExtension);
    }

    [Fact]
    public void Factory_Creates_JUnitXmlExporter()
    {
        var exporter = ResultExporterFactory.Create(ExportFormat.Junit);
        
        Assert.IsType<JUnitXmlExporter>(exporter);
        Assert.Equal(ExportFormat.Junit, exporter.Format);
        Assert.Equal(".xml", exporter.FileExtension);
    }

    [Fact]
    public void Factory_Creates_MarkdownExporter()
    {
        var exporter = ResultExporterFactory.Create(ExportFormat.Markdown);
        
        Assert.IsType<MarkdownExporter>(exporter);
        Assert.Equal(ExportFormat.Markdown, exporter.Format);
        Assert.Equal(".md", exporter.FileExtension);
    }

    [Fact]
    public void Factory_Creates_TrxExporter()
    {
        var exporter = ResultExporterFactory.Create(ExportFormat.Trx);
        
        Assert.IsType<TrxExporter>(exporter);
        Assert.Equal(ExportFormat.Trx, exporter.Format);
        Assert.Equal(".trx", exporter.FileExtension);
    }

    [Fact]
    public void Factory_Creates_CsvExporter()
    {
        var exporter = ResultExporterFactory.Create(ExportFormat.Csv);
        
        Assert.IsType<CsvExporter>(exporter);
        Assert.Equal(ExportFormat.Csv, exporter.Format);
        Assert.Equal(".csv", exporter.FileExtension);
    }

    [Fact]
    public void Factory_CreateFromExtension_Works()
    {
        Assert.IsType<JsonExporter>(ResultExporterFactory.CreateFromExtension(".json"));
        Assert.IsType<JUnitXmlExporter>(ResultExporterFactory.CreateFromExtension(".xml"));
        Assert.IsType<MarkdownExporter>(ResultExporterFactory.CreateFromExtension(".md"));
        Assert.IsType<MarkdownExporter>(ResultExporterFactory.CreateFromExtension(".markdown"));
        Assert.IsType<TrxExporter>(ResultExporterFactory.CreateFromExtension(".trx"));
        Assert.IsType<CsvExporter>(ResultExporterFactory.CreateFromExtension(".csv"));
    }

    [Fact]
    public void Factory_Create_ThrowsOnUnknownFormat()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ResultExporterFactory.Create((ExportFormat)999));
    }

    [Fact]
    public void Factory_CreateFromExtension_ThrowsOnUnknownExtension()
    {
        Assert.Throws<ArgumentException>(() =>
            ResultExporterFactory.CreateFromExtension(".xyz"));
    }

    #endregion

    #region JSON Exporter Tests

    [Fact]
    public async Task JsonExporter_Produces_ValidJson()
    {
        var exporter = new JsonExporter();
        var report = CreateSampleReport();

        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);
        
        stream.Position = 0;
        var json = await new StreamReader(stream).ReadToEndAsync();
        
        Assert.NotEmpty(json);
        Assert.Contains("\"runId\"", json);
        Assert.Contains("abc12345", json);
        Assert.Contains("\"overallScore\"", json);
        Assert.Contains("78.3", json);
    }

    [Fact]
    public async Task JsonExporter_ExportToString_Works()
    {
        var exporter = new JsonExporter();
        var report = CreateSampleReport();

        var json = await exporter.ExportToStringAsync(report);
        
        Assert.Contains("\"passRate\"", json);
        Assert.Contains("\"stats\"", json);
        Assert.Contains("\"results\"", json);
    }

    [Fact]
    public async Task JsonExporter_Includes_TestResults()
    {
        var exporter = new JsonExporter();
        var report = CreateSampleReport();

        var json = await exporter.ExportToStringAsync(report);
        
        Assert.Contains("code_tool_selection", json);
        Assert.Contains("llm_faithfulness", json);
        Assert.Contains("llm_task_completion", json);
        Assert.Contains("Incomplete reasoning", json);
    }

    [Fact]
    public async Task JsonExporter_Includes_MetricScores()
    {
        var exporter = new JsonExporter();
        var report = CreateSampleReport();
        report.TestResults[0].MetricScores = new Dictionary<string, double>
        {
            ["relevance"] = 92.5,
            ["correctness"] = 88.0
        };

        var json = await exporter.ExportToStringAsync(report);

        Assert.Contains("\"metricScores\"", json);
        Assert.Contains("relevance", json);
        Assert.Contains("92.5", json);
    }

    [Fact]
    public async Task JsonExporter_Omits_EmptyMetricScores()
    {
        var exporter = new JsonExporter();
        var report = CreateSampleReport();
        // Default CreateSampleReport has no MetricScores

        var json = await exporter.ExportToStringAsync(report);

        // Empty MetricScores should be omitted (WhenWritingNull)
        Assert.DoesNotContain("\"metricScores\"", json);
    }

    #endregion

    #region JUnit XML Exporter Tests

    [Fact]
    public async Task JUnitXmlExporter_Produces_ValidXml()
    {
        var exporter = new JUnitXmlExporter();
        var report = CreateSampleReport();

        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);
        
        stream.Position = 0;
        var xml = await new StreamReader(stream).ReadToEndAsync();
        
        Assert.StartsWith("<?xml", xml);
        Assert.Contains("<testsuites", xml);
        Assert.Contains("<testsuite", xml);
        Assert.Contains("<testcase", xml);
    }

    [Fact]
    public async Task JUnitXmlExporter_Includes_Failures()
    {
        var exporter = new JUnitXmlExporter();
        var report = CreateSampleReport();

        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);
        
        stream.Position = 0;
        var xml = await new StreamReader(stream).ReadToEndAsync();
        
        Assert.Contains("<failure", xml);
        Assert.Contains("Incomplete reasoning", xml);
    }

    [Fact]
    public async Task JUnitXmlExporter_Has_Correct_Counts()
    {
        var exporter = new JUnitXmlExporter();
        var report = CreateSampleReport();

        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);
        
        stream.Position = 0;
        var xml = await new StreamReader(stream).ReadToEndAsync();
        
        Assert.Contains("tests=\"3\"", xml);
        Assert.Contains("failures=\"1\"", xml);
    }

    [Fact]
    public async Task JUnitXmlExporter_Groups_By_Category()
    {
        var exporter = new JUnitXmlExporter();
        var report = CreateSampleReport();

        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);
        
        stream.Position = 0;
        var xml = await new StreamReader(stream).ReadToEndAsync();
        
        Assert.Contains("AgentEval.Agentic", xml);
        Assert.Contains("AgentEval.RAG", xml);
    }

    [Fact]
    public async Task JUnitXmlExporter_Handles_SkippedTests()
    {
        var exporter = new JUnitXmlExporter();
        var report = CreateSampleReport();
        report.TestResults[0].Skipped = true;
        report.TestResults[0].Passed = false;
        report.SkippedTests = 1;

        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);
        
        stream.Position = 0;
        var xml = await new StreamReader(stream).ReadToEndAsync();
        
        Assert.Contains("<skipped", xml);
    }

    [Fact]
    public async Task JUnitXmlExporter_Includes_MetricScores_In_SystemOut()
    {
        var exporter = new JUnitXmlExporter();
        var report = CreateSampleReport();
        report.TestResults[0].MetricScores = new Dictionary<string, double>
        {
            ["relevance"] = 92.5
        };

        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);
        
        stream.Position = 0;
        var xml = await new StreamReader(stream).ReadToEndAsync();
        
        Assert.Contains("<system-out>", xml);
        Assert.Contains("relevance: 92.5", xml);
    }

    #endregion

    #region Markdown Exporter Tests

    [Fact]
    public async Task MarkdownExporter_Produces_Markdown()
    {
        var exporter = new MarkdownExporter();
        var report = CreateSampleReport();

        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);
        
        stream.Position = 0;
        var md = await new StreamReader(stream).ReadToEndAsync();
        
        Assert.Contains("## 🤖 AgentEval Results", md);
        Assert.Contains("| Test | Score | Status | Time |", md);
    }

    [Fact]
    public void MarkdownExporter_ExportToString_Works()
    {
        var exporter = new MarkdownExporter();
        var report = CreateSampleReport();

        var md = exporter.ExportToString(report);
        
        Assert.Contains("**Score:** 78.3/100", md);
        Assert.Contains("**Tests:** 2/3 passed", md);
    }

    [Fact]
    public void MarkdownExporter_Shows_Status_Emoji()
    {
        var exporter = new MarkdownExporter();
        
        // Failed report
        var failedReport = CreateSampleReport();
        var md = exporter.ExportToString(failedReport);
        Assert.Contains("❌ FAILED", md);
        
        // Passing report
        var passedReport = new EvaluationReport
        {
            TotalTests = 2,
            PassedTests = 2,
            FailedTests = 0,
            OverallScore = 95.0,
            TestResults = new List<TestResultSummary>
            {
                new() { Name = "Test1", Passed = true, Score = 95.0 },
                new() { Name = "Test2", Passed = true, Score = 95.0 }
            }
        };
        md = exporter.ExportToString(passedReport);
        Assert.Contains("✅ PASSED", md);
    }

    [Fact]
    public void MarkdownExporter_Includes_Failure_Details()
    {
        var exporter = new MarkdownExporter { Options = new() { IncludeFailureDetails = true } };
        var report = CreateSampleReport();

        var md = exporter.ExportToString(report);
        
        Assert.Contains("### ❌ Failures", md);
        Assert.Contains("**llm\\_task\\_completion**", md);  // Underscores escaped in markdown
        Assert.Contains("Incomplete reasoning", md);
    }

    [Fact]
    public void MarkdownExporter_Can_Hide_Footer()
    {
        var exporter = new MarkdownExporter { Options = new() { IncludeFooter = false } };
        var report = CreateSampleReport();

        var md = exporter.ExportToString(report);
        
        Assert.DoesNotContain("Run ID:", md);
    }

    [Fact]
    public void MarkdownExporter_FailuresFirst_OrdersCorrectly()
    {
        var exporter = new MarkdownExporter { Options = new() { FailuresFirst = true } };
        var report = CreateSampleReport();

        var md = exporter.ExportToString(report);
        var lines = md.Split('\n');

        // Find first test row (after header + separator)
        var testRows = lines.Where(l => l.StartsWith("| ") && !l.StartsWith("| Test") && !l.StartsWith("|--")).ToList();
        Assert.True(testRows.Count > 0);
        // First test row should be the failed test
        Assert.Contains("llm\\_task\\_completion", testRows[0]);
    }

    [Fact]
    public void MarkdownExporter_Includes_MetricBreakdown()
    {
        var exporter = new MarkdownExporter { Options = new() { IncludeMetricBreakdown = true } };
        var report = CreateSampleReport();
        report.TestResults[0].MetricScores = new Dictionary<string, double>
        {
            ["relevance"] = 92.5,
            ["correctness"] = 88.0
        };

        var md = exporter.ExportToString(report);

        Assert.Contains("### 📊 Metric Breakdown", md);
        Assert.Contains("relevance", md);
        Assert.Contains("correctness", md);
        Assert.Contains("92.5", md);
    }

    [Fact]
    public void MarkdownExporter_Hides_MetricBreakdown_WhenDisabled()
    {
        var exporter = new MarkdownExporter { Options = new() { IncludeMetricBreakdown = false } };
        var report = CreateSampleReport();
        report.TestResults[0].MetricScores = new Dictionary<string, double>
        {
            ["relevance"] = 92.5
        };

        var md = exporter.ExportToString(report);

        Assert.DoesNotContain("Metric Breakdown", md);
    }

    [Fact]
    public void MarkdownExporter_Shows_Skipped_Status()
    {
        var exporter = new MarkdownExporter();
        var report = CreateSampleReport();
        report.TestResults[0].Skipped = true;
        report.TestResults[0].Passed = false;

        var md = exporter.ExportToString(report);

        Assert.Contains("⏭️", md);
    }

    #endregion

    #region TRX Exporter Tests

    [Fact]
    public async Task TrxExporter_Produces_ValidXml()
    {
        var exporter = new TrxExporter();
        var report = CreateSampleReport();

        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);
        
        stream.Position = 0;
        var xml = await new StreamReader(stream).ReadToEndAsync();
        
        Assert.StartsWith("<?xml", xml);
        Assert.Contains("<TestRun", xml);
        Assert.Contains("<ResultSummary", xml);
        Assert.Contains("<Results", xml);
    }

    [Fact]
    public async Task TrxExporter_Has_Correct_Counters()
    {
        var exporter = new TrxExporter();
        var report = CreateSampleReport();

        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);
        
        stream.Position = 0;
        var xml = await new StreamReader(stream).ReadToEndAsync();
        
        Assert.Contains("total=\"3\"", xml);
        Assert.Contains("passed=\"2\"", xml);
        Assert.Contains("failed=\"1\"", xml);
    }

    [Fact]
    public async Task TrxExporter_Includes_ErrorInfo()
    {
        var exporter = new TrxExporter();
        var report = CreateSampleReport();

        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);
        
        stream.Position = 0;
        var xml = await new StreamReader(stream).ReadToEndAsync();
        
        Assert.Contains("<ErrorInfo>", xml);
        Assert.Contains("Incomplete reasoning", xml);
    }

    [Fact]
    public async Task TrxExporter_Produces_DeterministicGuids()
    {
        var exporter = new TrxExporter();
        var report = CreateSampleReport();

        using var stream1 = new MemoryStream();
        await exporter.ExportAsync(report, stream1);
        stream1.Position = 0;
        var xml1 = await new StreamReader(stream1).ReadToEndAsync();

        using var stream2 = new MemoryStream();
        await exporter.ExportAsync(report, stream2);
        stream2.Position = 0;
        var xml2 = await new StreamReader(stream2).ReadToEndAsync();

        // TestDefinitions and TestEntries should use deterministic GUIDs
        // (testRunId and testListId are random, so compare testId attributes)
        var testIdPattern = new System.Text.RegularExpressions.Regex("testId=\"([^\"]+)\"");
        var ids1 = testIdPattern.Matches(xml1).Select(m => m.Groups[1].Value).ToList();
        var ids2 = testIdPattern.Matches(xml2).Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(ids1, ids2);
    }

    [Fact]
    public async Task TrxExporter_Handles_SkippedTests()
    {
        var exporter = new TrxExporter();
        var report = CreateSampleReport();
        report.TestResults[0].Skipped = true;
        report.TestResults[0].Passed = false;
        report.SkippedTests = 1;

        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);
        
        stream.Position = 0;
        var xml = await new StreamReader(stream).ReadToEndAsync();
        
        Assert.Contains("outcome=\"NotExecuted\"", xml);
    }

    #endregion

    #region EvaluationReport Tests

    [Fact]
    public void EvaluationReport_Calculates_Duration()
    {
        var report = new EvaluationReport
        {
            StartTime = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero),
            EndTime = new DateTimeOffset(2026, 1, 5, 10, 0, 30, TimeSpan.Zero)
        };
        
        Assert.Equal(TimeSpan.FromSeconds(30), report.Duration);
    }

    [Fact]
    public void EvaluationReport_Calculates_PassRate()
    {
        var report = new EvaluationReport
        {
            TotalTests = 10,
            PassedTests = 7
        };
        
        Assert.Equal(70.0, report.PassRate);
    }

    [Fact]
    public void EvaluationReport_PassRate_HandlesZeroTests()
    {
        var report = new EvaluationReport { TotalTests = 0, PassedTests = 0 };
        
        Assert.Equal(0.0, report.PassRate);
    }

    [Fact]
    public void EvaluationReport_RunId_IsAutoGenerated()
    {
        var report = new EvaluationReport();
        
        Assert.NotNull(report.RunId);
        Assert.Equal(8, report.RunId.Length);
        Assert.True(report.RunId.All(c => "0123456789abcdef".Contains(c)));
    }

    #endregion

    #region CSV Exporter Tests

    [Fact]
    public async Task CsvExporter_Produces_ValidCsv()
    {
        var exporter = new CsvExporter();
        var report = CreateSampleReport();

        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);
        
        stream.Position = 0;
        var csv = await new StreamReader(stream).ReadToEndAsync();
        
        Assert.Contains("RunId,TestName", csv);
        Assert.Contains("code_tool_selection", csv);
        Assert.Contains("llm_faithfulness", csv);
        Assert.Contains("llm_task_completion", csv);
    }

    [Fact]
    public async Task CsvExporter_EscapesSpecialCharacters()
    {
        var exporter = new CsvExporter();
        var report = CreateSampleReport();
        report.TestResults[2].Error = "Error with, commas and \"quotes\"";

        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);
        
        stream.Position = 0;
        var csv = await new StreamReader(stream).ReadToEndAsync();
        
        Assert.Contains("\"Error with, commas and \"\"quotes\"\"\"", csv);
    }

    [Fact]
    public async Task CsvExporter_IncludesMetricScoreColumns()
    {
        var exporter = new CsvExporter();
        var report = CreateSampleReport();
        report.TestResults[0].MetricScores = new Dictionary<string, double>
        {
            ["relevance"] = 92.5,
            ["correctness"] = 88.0
        };

        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);
        
        stream.Position = 0;
        var csv = await new StreamReader(stream).ReadToEndAsync();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        // Header should include metric columns
        Assert.Contains("correctness", lines[0]);
        Assert.Contains("relevance", lines[0]);
        
        // First data row should have metric values
        Assert.Contains("92.50", lines[1]);
        Assert.Contains("88.00", lines[1]);
    }

    [Fact]
    public async Task CsvExporter_ExportToString_Works()
    {
        var exporter = new CsvExporter();
        var report = CreateSampleReport();

        var csv = await exporter.ExportToStringAsync(report);
        
        Assert.Contains("RunId,TestName", csv);
        Assert.Contains("abc12345", csv);
    }

    [Fact]
    public async Task CsvExporter_WithoutMetricScores_HasBaseColumnsOnly()
    {
        var exporter = new CsvExporter();
        var report = CreateSampleReport();
        // Default report has no MetricScores

        var csv = await exporter.ExportToStringAsync(report);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Header should end with AgentModel (no extra metric columns)
        Assert.EndsWith("AgentModel", lines[0].TrimEnd());
        // Should have header + 3 data rows
        Assert.Equal(4, lines.Length);
    }

    #endregion
}
