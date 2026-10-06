// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Core;
using AgentEval.Exporters;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Exporters;

/// <summary>
/// #203 review round 16 (B12m, M3): <c>agenteval eval --metrics</c> exports dropped a not-measured metric with no marker,
/// so "not measured" could not be told from "not requested". Every exporter now says it.
/// </summary>
public sealed class MetricNotMeasuredExportTests
{
    private static EvaluationReport Report() => new TestSummary("suite",
    [
        new TestResult
        {
            TestName = "t",
            Passed = true,
            MetricResults =
            [
                MetricResult.Pass("llm_relevance", 90),
                MetricResult.NotMeasured("llm_faithfulness", "Faithfulness requires a retrieved context, and none was supplied: not measured."),
            ],
        },
    ]).ToEvaluationReport();

    [Fact]
    public void TheReportModel_NamesTheMetric_WithItsReason()
    {
        var test = Report().TestResults[0];

        Assert.DoesNotContain("llm_faithfulness", test.MetricScores.Keys);
        Assert.Contains("retrieved context", test.MetricsNotMeasured["llm_faithfulness"], StringComparison.Ordinal);
    }

    public static TheoryData<string, IResultExporter> StreamExporters() => new()
    {
        { "json", new JsonExporter() },
        { "csv", new CsvExporter() },
        { "markdown", new MarkdownExporter() },
        { "junit", new JUnitXmlExporter() },
        { "trx", new TrxExporter() },
        { "directory summary", new DirectoryExporter() },
    };

    [Theory]
    [MemberData(nameof(StreamExporters))]
    public async Task EveryExporter_SaysTheMetricWasNotMeasured(string because, IResultExporter exporter)
    {
        using var stream = new MemoryStream();
        await exporter.ExportAsync(Report(), stream);
        var text = Encoding.UTF8.GetString(stream.ToArray());

        Assert.Contains("llm_faithfulness", text, StringComparison.Ordinal);
        Assert.True(text.Contains("not measured", StringComparison.Ordinal) || text.Contains("metricsNotMeasured", StringComparison.Ordinal),
            because);
    }

    [Fact]
    public async Task TheDirectoryResults_CarryTheMetricWithItsReason()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agenteval-b12m-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new DirectoryExporter().ExportToDirectoryAsync(Report(), dir);
            var line = (await File.ReadAllLinesAsync(Path.Combine(dir, DirectoryExporter.ResultsFileName)))[0];

            Assert.Contains("\"metricsNotMeasured\":{\"llm_faithfulness\":", line, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
