// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Exporters;

/// <summary>
/// Exports evaluation results as Visual Studio TRX format.
/// Native format for .NET test tooling and Azure DevOps.
/// </summary>
/// <remarks>
/// TRX is the native format for Visual Studio and .NET test runners.
/// Azure DevOps supports TRX directly without format conversion.
/// </remarks>
public class TrxExporter : IResultExporter
{
    /// <inheritdoc />
    public ExportFormat Format => ExportFormat.Trx;
    
    /// <inheritdoc />
    public string FileExtension => ".trx";
    
    /// <inheritdoc />
    public string ContentType => "application/xml";

    private const string TrxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <inheritdoc />
    public async Task ExportAsync(EvaluationReport report, Stream output, CancellationToken ct = default)
    {
        var settings = new XmlWriterSettings
        {
            Async = true,
            Indent = true,
            Encoding = new UTF8Encoding(false)
        };

        await using var writer = XmlWriter.Create(output, settings);
        
        var testRunId = Guid.NewGuid();
        var testListId = Guid.NewGuid();
        
        await writer.WriteStartDocumentAsync();
        
        // <TestRun>
        await writer.WriteStartElementAsync(null, "TestRun", TrxNamespace);
        await writer.WriteAttributeStringAsync(null, "id", null, testRunId.ToString());
        await writer.WriteAttributeStringAsync(null, "name", null, report.Name ?? $"AgentEval_{report.RunId}");
        await writer.WriteAttributeStringAsync(null, "runUser", null, Environment.UserName);
        
        // <Times>
        await writer.WriteStartElementAsync(null, "Times", null);
        await writer.WriteAttributeStringAsync(null, "creation", null, report.StartTime.ToString("O"));
        await writer.WriteAttributeStringAsync(null, "queuing", null, report.StartTime.ToString("O"));
        await writer.WriteAttributeStringAsync(null, "start", null, report.StartTime.ToString("O"));
        await writer.WriteAttributeStringAsync(null, "finish", null, report.EndTime.ToString("O"));
        await writer.WriteEndElementAsync();
        
        // <ResultSummary>
        await writer.WriteStartElementAsync(null, "ResultSummary", null);
        await writer.WriteAttributeStringAsync(null, "outcome", null, 
            report.FailedTests == 0 ? "Passed" : "Failed");
        
        await writer.WriteStartElementAsync(null, "Counters", null);
        await writer.WriteAttributeStringAsync(null, "total", null, report.TotalTests.ToString());
        await writer.WriteAttributeStringAsync(null, "executed", null, (report.TotalTests - report.SkippedTests).ToString());
        await writer.WriteAttributeStringAsync(null, "passed", null, report.PassedTests.ToString());
        await writer.WriteAttributeStringAsync(null, "failed", null, report.FailedTests.ToString());
        await writer.WriteAttributeStringAsync(null, "error", null, "0");
        await writer.WriteAttributeStringAsync(null, "timeout", null, "0");
        await writer.WriteAttributeStringAsync(null, "aborted", null, "0");
        await writer.WriteAttributeStringAsync(null, "inconclusive", null, "0");
        await writer.WriteAttributeStringAsync(null, "notRunnable", null, "0");
        await writer.WriteAttributeStringAsync(null, "notExecuted", null, report.SkippedTests.ToString());
        await writer.WriteEndElementAsync(); // </Counters>
        await writer.WriteEndElementAsync(); // </ResultSummary>
        
        // <TestDefinitions>
        await writer.WriteStartElementAsync(null, "TestDefinitions", null);
        foreach (var test in report.TestResults)
        {
            var testId = CreateDeterministicGuid(test.Name);
            var executionId = CreateDeterministicGuid($"{test.Name}_exec");
            
            await writer.WriteStartElementAsync(null, "UnitTest", null);
            await writer.WriteAttributeStringAsync(null, "name", null, test.Name);
            await writer.WriteAttributeStringAsync(null, "id", null, testId.ToString());
            
            await writer.WriteStartElementAsync(null, "Execution", null);
            await writer.WriteAttributeStringAsync(null, "id", null, executionId.ToString());
            await writer.WriteEndElementAsync();
            
            await writer.WriteStartElementAsync(null, "TestMethod", null);
            await writer.WriteAttributeStringAsync(null, "codeBase", null, "AgentEval.dll");
            await writer.WriteAttributeStringAsync(null, "className", null, $"AgentEval.{test.Category ?? "Evaluations"}");
            await writer.WriteAttributeStringAsync(null, "name", null, test.Name);
            await writer.WriteEndElementAsync();
            
            await writer.WriteEndElementAsync(); // </UnitTest>
        }
        await writer.WriteEndElementAsync(); // </TestDefinitions>
        
        // <TestLists>
        await writer.WriteStartElementAsync(null, "TestLists", null);
        await writer.WriteStartElementAsync(null, "TestList", null);
        await writer.WriteAttributeStringAsync(null, "name", null, "All Tests");
        await writer.WriteAttributeStringAsync(null, "id", null, testListId.ToString());
        await writer.WriteEndElementAsync();
        await writer.WriteEndElementAsync();
        
        // <TestEntries>
        await writer.WriteStartElementAsync(null, "TestEntries", null);
        foreach (var test in report.TestResults)
        {
            var testId = CreateDeterministicGuid(test.Name);
            var executionId = CreateDeterministicGuid($"{test.Name}_exec");
            
            await writer.WriteStartElementAsync(null, "TestEntry", null);
            await writer.WriteAttributeStringAsync(null, "testId", null, testId.ToString());
            await writer.WriteAttributeStringAsync(null, "executionId", null, executionId.ToString());
            await writer.WriteAttributeStringAsync(null, "testListId", null, testListId.ToString());
            await writer.WriteEndElementAsync();
        }
        await writer.WriteEndElementAsync();
        
        // <Results>
        await writer.WriteStartElementAsync(null, "Results", null);
        foreach (var test in report.TestResults)
        {
            var testId = CreateDeterministicGuid(test.Name);
            var executionId = CreateDeterministicGuid($"{test.Name}_exec");
            
            var outcome = test.Skipped ? "NotExecuted" : (test.Passed ? "Passed" : "Failed");
            
            await writer.WriteStartElementAsync(null, "UnitTestResult", null);
            await writer.WriteAttributeStringAsync(null, "testId", null, testId.ToString());
            await writer.WriteAttributeStringAsync(null, "executionId", null, executionId.ToString());
            await writer.WriteAttributeStringAsync(null, "testName", null, test.Name);
            await writer.WriteAttributeStringAsync(null, "computerName", null, Environment.MachineName);
            await writer.WriteAttributeStringAsync(null, "duration", null, 
                TimeSpan.FromMilliseconds(test.DurationMs).ToString(@"hh\:mm\:ss\.fffffff"));
            await writer.WriteAttributeStringAsync(null, "startTime", null, report.StartTime.ToString("O"));
            await writer.WriteAttributeStringAsync(null, "endTime", null, 
                report.StartTime.AddMilliseconds(test.DurationMs).ToString("O"));
            await writer.WriteAttributeStringAsync(null, "outcome", null, outcome);
            await writer.WriteAttributeStringAsync(null, "testListId", null, testListId.ToString());
            
            // Output section
            await writer.WriteStartElementAsync(null, "Output", null);
            
            // StdOut
            if (!string.IsNullOrEmpty(test.Output) || test.MetricScores.Count > 0)
            {
                await writer.WriteStartElementAsync(null, "StdOut", null);
                var stdout = new StringBuilder();
                stdout.AppendLine($"Score: {test.Score:F1}/100");
                foreach (var (metric, score) in test.MetricScores)
                {
                    stdout.AppendLine($"{metric}: {score:F1}");
                }
                if (!string.IsNullOrEmpty(test.Output))
                {
                    stdout.AppendLine(test.Output);
                }
                await writer.WriteStringAsync(stdout.ToString());
                await writer.WriteEndElementAsync();
            }
            
            // ErrorInfo for failures
            if (!test.Passed && !test.Skipped)
            {
                await writer.WriteStartElementAsync(null, "ErrorInfo", null);
                await writer.WriteElementStringAsync(null, "Message", null, 
                    test.Error ?? $"Score {test.Score:F1} below threshold");
                if (!string.IsNullOrEmpty(test.StackTrace))
                {
                    await writer.WriteElementStringAsync(null, "StackTrace", null, test.StackTrace);
                }
                await writer.WriteEndElementAsync();
            }
            
            await writer.WriteEndElementAsync(); // </Output>
            await writer.WriteEndElementAsync(); // </UnitTestResult>
        }
        await writer.WriteEndElementAsync(); // </Results>
        
        await writer.WriteEndElementAsync(); // </TestRun>
        
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }

    private static Guid CreateDeterministicGuid(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }
}
