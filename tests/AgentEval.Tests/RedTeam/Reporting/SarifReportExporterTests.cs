// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Reporting/SarifReportExporterTests.cs
using System.Text.Json;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting;

namespace AgentEval.Tests.RedTeam.Reporting;

/// <summary>
/// Tests for SARIF report exporter.
/// </summary>
public class SarifReportExporterTests
{
    [Fact]
    public void Export_ProducesValidSarifJson()
    {
        var result = CreateTestResult();
        var exporter = new SarifReportExporter();

        var sarif = exporter.Export(result);

        Assert.NotNull(sarif);
        var doc = JsonDocument.Parse(sarif);
        Assert.NotNull(doc);
    }

    [Fact]
    public void Export_IncludesSarifVersion()
    {
        var result = CreateTestResult();
        var exporter = new SarifReportExporter();

        var sarif = exporter.Export(result);
        var doc = JsonDocument.Parse(sarif);

        Assert.Equal("2.1.0", doc.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void Export_IncludesSchema()
    {
        var result = CreateTestResult();
        var exporter = new SarifReportExporter();

        var sarif = exporter.Export(result);

        Assert.Contains("$schema", sarif);
        Assert.Contains("sarif-schema-2.1.0.json", sarif);
    }

    [Fact]
    public void Export_IncludesToolInfo()
    {
        var result = CreateTestResult();
        var exporter = new SarifReportExporter();

        var sarif = exporter.Export(result);
        var doc = JsonDocument.Parse(sarif);

        var tool = doc.RootElement.GetProperty("runs")[0].GetProperty("tool").GetProperty("driver");
        Assert.Equal("AgentEval RedTeam", tool.GetProperty("name").GetString());
    }

    [Fact]
    public void Export_IncludesRules()
    {
        var result = CreateTestResult();
        var exporter = new SarifReportExporter();

        var sarif = exporter.Export(result);
        var doc = JsonDocument.Parse(sarif);

        var rules = doc.RootElement.GetProperty("runs")[0].GetProperty("tool").GetProperty("driver").GetProperty("rules");
        Assert.Equal(1, rules.GetArrayLength());
        Assert.Equal("PromptInjection", rules[0].GetProperty("id").GetString());
    }

    [Fact]
    public void Export_IncludesResultsOnlyForSucceeded()
    {
        var result = CreateTestResult();
        var exporter = new SarifReportExporter();

        var sarif = exporter.Export(result);
        var doc = JsonDocument.Parse(sarif);

        var results = doc.RootElement.GetProperty("runs")[0].GetProperty("results");
        // Only succeeded probes become results
        Assert.Equal(1, results.GetArrayLength());
    }

    [Fact]
    public void Export_ResultsHaveCorrectLevel()
    {
        var result = CreateTestResult();
        var exporter = new SarifReportExporter();

        var sarif = exporter.Export(result);
        var doc = JsonDocument.Parse(sarif);

        var resultLevel = doc.RootElement.GetProperty("runs")[0].GetProperty("results")[0].GetProperty("level").GetString();
        Assert.Equal("error", resultLevel); // High severity = error
    }

    [Fact]
    public void Export_IncludesInvocation()
    {
        var result = CreateTestResult();
        var exporter = new SarifReportExporter();

        var sarif = exporter.Export(result);
        var doc = JsonDocument.Parse(sarif);

        var invocations = doc.RootElement.GetProperty("runs")[0].GetProperty("invocations");
        Assert.Equal(1, invocations.GetArrayLength());
        Assert.True(invocations[0].GetProperty("executionSuccessful").GetBoolean());
    }

    [Fact]
    public void FileExtension_ReturnsSarif()
    {
        var exporter = new SarifReportExporter();
        Assert.Equal(".sarif", exporter.FileExtension);
    }

    [Fact]
    public void FormatName_ReturnsSARIF()
    {
        var exporter = new SarifReportExporter();
        Assert.Equal("SARIF", exporter.FormatName);
    }

    [Fact]
    public void MimeType_ReturnsSarifJson()
    {
        var exporter = new SarifReportExporter();
        Assert.Equal("application/sarif+json", exporter.MimeType);
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
        AttackResults =
        [
            new AttackResult
            {
                AttackName = "PromptInjection",
                AttackDisplayName = "Prompt Injection",
                OwaspId = "LLM01",
                Severity = Severity.High,
                ResistedCount = 4,
                SucceededCount = 1,
                ProbeResults =
                [
                    new ProbeResult
                    {
                        ProbeId = "PI-001",
                        Prompt = "Safe prompt",
                        Response = "Safe response",
                        Outcome = EvaluationOutcome.Resisted,
                        Reason = "No markers",
                        Difficulty = Difficulty.Easy
                    },
                    new ProbeResult
                    {
                        ProbeId = "PI-003",
                        Prompt = "Attack prompt",
                        Response = "PWNED",
                        Outcome = EvaluationOutcome.Succeeded,
                        Reason = "Marker found",
                        Difficulty = Difficulty.Moderate,
                        Technique = "injection"
                    }
                ]
            }
        ]
    };
}
