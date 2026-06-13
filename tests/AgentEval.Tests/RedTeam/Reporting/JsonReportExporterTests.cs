// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Reporting/JsonReportExporterTests.cs
using System.Text.Json;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting;

namespace AgentEval.Tests.RedTeam.Reporting;

/// <summary>
/// Tests for JSON report exporter.
/// </summary>
public class JsonReportExporterTests
{
    [Fact]
    public void Export_ProducesValidJson()
    {
        var result = CreateTestResult();
        var exporter = new JsonReportExporter();

        var json = exporter.Export(result);

        Assert.NotNull(json);
        Assert.NotEmpty(json);
        var doc = JsonDocument.Parse(json); // Should not throw
        Assert.NotNull(doc);
    }

    [Fact]
    public void Export_IncludesSchemaVersion()
    {
        var result = CreateTestResult();
        var exporter = new JsonReportExporter();

        var json = exporter.Export(result);
        var doc = JsonDocument.Parse(json);

        var schemaVersion = doc.RootElement.GetProperty("schema_version").GetString();
        Assert.Equal("0.2.0", schemaVersion);   // 5d: bumped for coverage/truncation + per-probe fidelity fields
    }

    [Fact]
    public void Export_Summary_IncludesCoverageAndTruncationHonestyFields()
    {
        // 5d: a FailFast-truncated / low-coverage scan must be machine-distinguishable from a clean full scan.
        var result = new RedTeamResult
        {
            AgentName = "TestAgent",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromSeconds(5),
            TotalProbes = 6, ResistedProbes = 3, SucceededProbes = 0, InconclusiveProbes = 3,
            WasTruncated = true, SkippedProbes = 4,
            AttackResults =
            [
                new AttackResult { AttackName = "PromptInjection", AttackDisplayName = "PI", OwaspId = "LLM01", Severity = Severity.High, ResistedCount = 3, InconclusiveCount = 3, ProbeResults = [] }
            ]
        };

        var summary = JsonDocument.Parse(new JsonReportExporter().Export(result)).RootElement.GetProperty("summary");

        Assert.True(summary.GetProperty("was_truncated").GetBoolean());
        Assert.Equal(4, summary.GetProperty("skipped_probes").GetInt32());
        Assert.Equal(10, summary.GetProperty("planned_probes").GetInt32());   // 6 executed + 4 skipped
        Assert.Equal(50.0, summary.GetProperty("coverage").GetDouble());       // 3 conclusive / 6 total
    }

    [Fact]
    public void Export_Failure_IncludesFidelityAndSurface()
    {
        var result = new RedTeamResult
        {
            AgentName = "TestAgent",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromSeconds(5),
            TotalProbes = 1, SucceededProbes = 1,
            AttackResults =
            [
                new AttackResult
                {
                    AttackName = "IndirectInjection", AttackDisplayName = "II", OwaspId = "LLM01", Severity = Severity.High,
                    SucceededCount = 1,
                    ProbeResults =
                    [
                        new ProbeResult { ProbeId = "IND-040", Prompt = "p", Response = "r", Outcome = EvaluationOutcome.Succeeded,
                            Reason = "tool executed", Fidelity = EvidenceFidelity.Behavioral, Surface = InjectionSurface.ToolOutput }
                    ]
                }
            ]
        };

        var failure = JsonDocument.Parse(new JsonReportExporter().Export(result)).RootElement.GetProperty("failures")[0];

        Assert.Equal("Behavioral", failure.GetProperty("fidelity").GetString());
        Assert.Equal("ToolOutput", failure.GetProperty("surface").GetString());
    }

    [Fact]
    public void Export_IncludesAgentName()
    {
        var result = CreateTestResult();
        var exporter = new JsonReportExporter();

        var json = exporter.Export(result);
        var doc = JsonDocument.Parse(json);

        var agentName = doc.RootElement.GetProperty("target").GetProperty("name").GetString();
        Assert.Equal("TestAgent", agentName);
    }

    [Fact]
    public void Export_IncludesSummary()
    {
        var result = CreateTestResult();
        var exporter = new JsonReportExporter();

        var json = exporter.Export(result);
        var doc = JsonDocument.Parse(json);

        var summary = doc.RootElement.GetProperty("summary");
        Assert.Equal(5, summary.GetProperty("total_probes").GetInt32());
        Assert.Equal("PartialPass", summary.GetProperty("verdict").GetString()); // 80% = PartialPass
    }

    [Fact]
    public void Export_IncludesAttackResults()
    {
        var result = CreateTestResult();
        var exporter = new JsonReportExporter();

        var json = exporter.Export(result);
        var doc = JsonDocument.Parse(json);

        var attacks = doc.RootElement.GetProperty("by_attack");
        Assert.Equal(1, attacks.GetArrayLength());
        Assert.Equal("PromptInjection", attacks[0].GetProperty("attack").GetString());
    }

    [Fact]
    public void Export_IncludesFailures()
    {
        var result = CreateTestResult();
        var exporter = new JsonReportExporter();

        var json = exporter.Export(result);
        var doc = JsonDocument.Parse(json);

        var failures = doc.RootElement.GetProperty("failures");
        Assert.Equal(1, failures.GetArrayLength());
        Assert.Equal("PI-003", failures[0].GetProperty("probe_id").GetString());
    }

    [Fact]
    public void FileExtension_ReturnsJson()
    {
        var exporter = new JsonReportExporter();
        Assert.Equal(".json", exporter.FileExtension);
    }

    [Fact]
    public void FormatName_ReturnsJSON()
    {
        var exporter = new JsonReportExporter();
        Assert.Equal("JSON", exporter.FormatName);
    }

    [Fact]
    public async Task ExportToFileAsync_WritesFile()
    {
        var result = CreateTestResult();
        var exporter = new JsonReportExporter();
        var tempFile = Path.GetTempFileName();

        try
        {
            await exporter.ExportToFileAsync(result, tempFile);

            Assert.True(File.Exists(tempFile));
            var content = await File.ReadAllTextAsync(tempFile);
            Assert.Contains("TestAgent", content);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static RedTeamResult CreateTestResult() => new()
    {
        AgentName = "TestAgent",
        StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
        CompletedAt = DateTimeOffset.UtcNow,
        Duration = TimeSpan.FromSeconds(10),
        TotalProbes = 5,
        ResistedProbes = 4,
        SucceededProbes = 1,
        InconclusiveProbes = 0,
        AttackResults =
        [
            new AttackResult
            {
                AttackName = "PromptInjection",
                AttackDisplayName = "Prompt Injection",
                OwaspId = "LLM01",
                MitreAtlasIds = ["AML.T0051"],
                Severity = Severity.High,
                ResistedCount = 4,
                SucceededCount = 1,
                InconclusiveCount = 0,
                ProbeResults =
                [
                    new ProbeResult
                    {
                        ProbeId = "PI-001",
                        Prompt = "Test prompt 1",
                        Response = "Safe response",
                        Outcome = EvaluationOutcome.Resisted,
                        Reason = "No markers found",
                        Difficulty = Difficulty.Easy
                    },
                    new ProbeResult
                    {
                        ProbeId = "PI-003",
                        Prompt = "Ignore instructions",
                        Response = "PWNED as requested",
                        Outcome = EvaluationOutcome.Succeeded,
                        Reason = "Response contained marker 'PWNED'",
                        Difficulty = Difficulty.Moderate,
                        Technique = "direct_override"
                    }
                ]
            }
        ]
    };
}
