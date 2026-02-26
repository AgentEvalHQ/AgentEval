// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Xml;
using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Exporters;

/// <summary>
/// Exports evaluation results as JUnit XML format.
/// Compatible with GitHub Actions, Azure DevOps, Jenkins, GitLab CI, and CircleCI.
/// </summary>
/// <remarks>
/// <para>
/// JUnit XML is the de facto standard for test result reporting in CI/CD systems.
/// This exporter produces output compatible with:
/// </para>
/// <list type="bullet">
/// <item><description>GitHub Actions: dorny/test-reporter@v1, EnricoMi/publish-unit-test-result-action</description></item>
/// <item><description>Azure DevOps: PublishTestResults@2 task with testResultsFormat: 'JUnit'</description></item>
/// <item><description>Jenkins: Built-in JUnit plugin</description></item>
/// <item><description>GitLab CI: artifacts:reports:junit directive</description></item>
/// <item><description>CircleCI: store_test_results step</description></item>
/// </list>
/// </remarks>
public class JUnitXmlExporter : IResultExporter
{
    /// <inheritdoc />
    public ExportFormat Format => ExportFormat.Junit;
    
    /// <inheritdoc />
    public string FileExtension => ".xml";
    
    /// <inheritdoc />
    public string ContentType => "application/xml";

    /// <inheritdoc />
    public async Task ExportAsync(EvaluationReport report, Stream output, CancellationToken ct = default)
    {
        var settings = new XmlWriterSettings
        {
            Async = true,
            Indent = true,
            Encoding = new UTF8Encoding(false), // No BOM
            OmitXmlDeclaration = false
        };

        await using var writer = XmlWriter.Create(output, settings);
        
        await writer.WriteStartDocumentAsync();
        
        // <testsuites>
        await writer.WriteStartElementAsync(null, "testsuites", null);
        await writer.WriteAttributeStringAsync(null, "name", null, report.Name ?? "AgentEval");
        await writer.WriteAttributeStringAsync(null, "tests", null, report.TotalTests.ToString());
        await writer.WriteAttributeStringAsync(null, "failures", null, report.FailedTests.ToString());
        await writer.WriteAttributeStringAsync(null, "errors", null, "0");
        await writer.WriteAttributeStringAsync(null, "skipped", null, report.SkippedTests.ToString());
        await writer.WriteAttributeStringAsync(null, "time", null, 
            report.Duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
        
        // Group tests by category
        var testsByCategory = report.TestResults
            .GroupBy(t => t.Category ?? "default")
            .ToList();
        
        foreach (var categoryGroup in testsByCategory)
        {
            var categoryTests = categoryGroup.ToList();
            var categoryFailed = categoryTests.Count(t => !t.Passed && !t.Skipped);
            var categorySkipped = categoryTests.Count(t => t.Skipped);
            var categoryDuration = categoryTests.Sum(t => t.DurationMs) / 1000.0;
            
            // <testsuite>
            await writer.WriteStartElementAsync(null, "testsuite", null);
            await writer.WriteAttributeStringAsync(null, "name", null, $"AgentEval.{categoryGroup.Key}");
            await writer.WriteAttributeStringAsync(null, "tests", null, categoryTests.Count.ToString());
            await writer.WriteAttributeStringAsync(null, "failures", null, categoryFailed.ToString());
            await writer.WriteAttributeStringAsync(null, "errors", null, "0");
            await writer.WriteAttributeStringAsync(null, "skipped", null, categorySkipped.ToString());
            await writer.WriteAttributeStringAsync(null, "time", null,
                categoryDuration.ToString("F3", CultureInfo.InvariantCulture));
            await writer.WriteAttributeStringAsync(null, "timestamp", null,
                report.StartTime.ToString("O", CultureInfo.InvariantCulture));
            
            // <properties>
            await writer.WriteStartElementAsync(null, "properties", null);
            await WritePropertyAsync(writer, "runId", report.RunId);
            await WritePropertyAsync(writer, "overallScore", report.OverallScore.ToString("F1", CultureInfo.InvariantCulture));
            if (report.Agent?.Model != null)
            {
                await WritePropertyAsync(writer, "model", report.Agent.Model);
            }
            await writer.WriteEndElementAsync(); // </properties>
            
            // Test cases
            foreach (var test in categoryTests)
            {
                await writer.WriteStartElementAsync(null, "testcase", null);
                await writer.WriteAttributeStringAsync(null, "name", null, test.Name);
                await writer.WriteAttributeStringAsync(null, "classname", null, $"AgentEval.{categoryGroup.Key}");
                await writer.WriteAttributeStringAsync(null, "time", null,
                    (test.DurationMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture));
                
                if (test.Skipped)
                {
                    await writer.WriteStartElementAsync(null, "skipped", null);
                    if (test.Error != null)
                    {
                        await writer.WriteAttributeStringAsync(null, "message", null, test.Error);
                    }
                    await writer.WriteEndElementAsync();
                }
                else if (!test.Passed)
                {
                    await writer.WriteStartElementAsync(null, "failure", null);
                    await writer.WriteAttributeStringAsync(null, "message", null, 
                        test.Error ?? $"Score {test.Score:F1} below threshold");
                    await writer.WriteAttributeStringAsync(null, "type", null, "AssertionError");
                    
                    var failureContent = new StringBuilder();
                    failureContent.AppendLine($"Score: {test.Score:F1}/100");
                    if (!string.IsNullOrEmpty(test.Error))
                    {
                        failureContent.AppendLine(test.Error);
                    }
                    if (!string.IsNullOrEmpty(test.StackTrace))
                    {
                        failureContent.AppendLine(test.StackTrace);
                    }
                    await writer.WriteStringAsync(failureContent.ToString());
                    await writer.WriteEndElementAsync(); // </failure>
                }
                
                // System output
                if (!string.IsNullOrEmpty(test.Output) || test.MetricScores.Count > 0)
                {
                    await writer.WriteStartElementAsync(null, "system-out", null);
                    var outputContent = new StringBuilder();
                    outputContent.AppendLine($"Score: {test.Score:F1}/100");
                    foreach (var (metric, score) in test.MetricScores)
                    {
                        outputContent.AppendLine($"{metric}: {score:F1}");
                    }
                    if (!string.IsNullOrEmpty(test.Output))
                    {
                        outputContent.AppendLine(test.Output);
                    }
                    await writer.WriteStringAsync(outputContent.ToString());
                    await writer.WriteEndElementAsync();
                }
                
                await writer.WriteEndElementAsync(); // </testcase>
            }
            
            await writer.WriteEndElementAsync(); // </testsuite>
        }
        
        await writer.WriteEndElementAsync(); // </testsuites>
        
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }

    private static async Task WritePropertyAsync(XmlWriter writer, string name, string value)
    {
        await writer.WriteStartElementAsync(null, "property", null);
        await writer.WriteAttributeStringAsync(null, "name", null, name);
        await writer.WriteAttributeStringAsync(null, "value", null, value);
        await writer.WriteEndElementAsync();
    }
}
