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
    public void Export_SucceededProbe_BecomesResult()
    {
        var result = CreateTestResult();
        var exporter = new SarifReportExporter();

        var sarif = exporter.Export(result);
        var doc = JsonDocument.Parse(sarif);

        var results = doc.RootElement.GetProperty("runs")[0].GetProperty("results");
        // This fixture has 1 succeeded + 0 inconclusive probes → exactly 1 result (the vulnerability).
        Assert.Equal(1, results.GetArrayLength());
    }

    [Fact]
    public void Export_InconclusiveProbe_BecomesOpenKindResult_NotDropped()
    {
        // W-E5 honesty: an inconclusive probe is a coverage gap, not a pass — SARIF must surface it, not drop it.
        //
        // S0: it is surfaced on the EVALUATION-STATE axis (`kind`), not the SEVERITY axis (`level`).
        // SARIF 2.1.0 §3.27.9 defines "open" as: "The specified rule was evaluated, and the tool concluded that
        // there was insufficient information to decide whether a problem exists." — a verbatim description of
        // EvaluationOutcome.Inconclusive.
        // §3.27.10 then requires: if `kind` "has any value other than \"fail\", then if level is absent, it SHALL
        // default to \"none\", and if it is present, it SHALL have the value \"none\"."
        // The previous encoding (level="note", no kind) conflated "we could not measure this" with "a low-severity
        // problem", which is exactly the coverage-gap-as-pass failure this exporter exists to prevent.
        var result = new RedTeamResult
        {
            AgentName = "TestAgent",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1), CompletedAt = DateTimeOffset.UtcNow, Duration = TimeSpan.FromSeconds(1),
            TotalProbes = 2, ResistedProbes = 1, SucceededProbes = 0, InconclusiveProbes = 1,
            AttackResults =
            [
                new AttackResult
                {
                    AttackName = "SystemPromptExtraction", AttackDisplayName = "System Prompt Extraction",
                    OwaspId = "LLM07", Severity = Severity.High, ResistedCount = 1, SucceededCount = 0,
                    ProbeResults =
                    [
                        new ProbeResult { ProbeId = "SPE-001", Prompt = "x", Response = "no", Outcome = EvaluationOutcome.Resisted, Reason = "refused", Difficulty = Difficulty.Easy },
                        new ProbeResult { ProbeId = "SPE-002", Prompt = "x", Response = "?", Outcome = EvaluationOutcome.Inconclusive, Reason = "no canary planted", Difficulty = Difficulty.Easy, Technique = "leak" },
                    ],
                }
            ],
        };

        var doc = JsonDocument.Parse(new SarifReportExporter().Export(result));
        var results = doc.RootElement.GetProperty("runs")[0].GetProperty("results");

        Assert.Equal(1, results.GetArrayLength());
        var gap = results[0];
        Assert.Equal("open", gap.GetProperty("kind").GetString());     // evaluation state: insufficient information
        Assert.Equal("none", gap.GetProperty("level").GetString());    // §3.27.10 SHALL, given kind != "fail"
        Assert.Equal("SystemPromptExtraction", gap.GetProperty("ruleId").GetString());
        Assert.Equal(0, gap.GetProperty("ruleIndex").GetInt32());      // references the (only) rule in tool.driver.rules
        // The human-readable coverage-gap statement must survive on `message.text`, which is a GitHub
        // code-scanning REQUIRED property — GitHub's SARIF docs do not mention `kind` at all, so its handling of
        // non-"fail" results is undocumented and must not be relied on to carry this meaning.
        Assert.Contains("INCONCLUSIVE", gap.GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Export_SucceededProbe_IsExplicitlyKindFail_WithSeverityOnLevel()
    {
        // S0: `kind` is emitted explicitly rather than relying on the SARIF default of "fail". The point of the
        // fix is that evaluation state is a first-class axis; leaving it implicit on the vulnerability path would
        // reproduce the ambiguity being removed. Severity stays on `level`, which is only meaningful when
        // kind == "fail".
        var result = new RedTeamResult
        {
            AgentName = "TestAgent",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1), CompletedAt = DateTimeOffset.UtcNow, Duration = TimeSpan.FromSeconds(1),
            TotalProbes = 1, ResistedProbes = 0, SucceededProbes = 1, InconclusiveProbes = 0,
            AttackResults =
            [
                new AttackResult
                {
                    AttackName = "PromptInjection", AttackDisplayName = "Prompt Injection",
                    OwaspId = "LLM01", Severity = Severity.Critical, ResistedCount = 0, SucceededCount = 1,
                    ProbeResults =
                    [
                        new ProbeResult { ProbeId = "PI-001", Prompt = "x", Response = "leaked", Outcome = EvaluationOutcome.Succeeded, Reason = "leaked the system prompt", Difficulty = Difficulty.Easy, Technique = "direct" },
                    ],
                }
            ],
        };

        var doc = JsonDocument.Parse(new SarifReportExporter().Export(result));
        var results = doc.RootElement.GetProperty("runs")[0].GetProperty("results");

        Assert.Equal(1, results.GetArrayLength());
        var finding = results[0];
        Assert.Equal("fail", finding.GetProperty("kind").GetString());
        Assert.Equal("error", finding.GetProperty("level").GetString());   // Severity.Critical → "error"
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
    public void Export_ExecutionErrors_SetExecutionSuccessfulFalse_AndSurfaceTruncation()
    {
        // 5d: a genuine execution fault makes executionSuccessful=false; a FailFast truncation is surfaced in the
        // invocation property bag (a by-design stop is NOT an abnormal termination).
        var result = new RedTeamResult
        {
            AgentName = "TestAgent",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromSeconds(5),
            TotalProbes = 4, ResistedProbes = 2, InconclusiveProbes = 2, ErroredProbes = 2,
            WasTruncated = true, SkippedProbes = 3,
            AttackResults =
            [
                new AttackResult { AttackName = "PromptInjection", AttackDisplayName = "PI", OwaspId = "LLM01", Severity = Severity.High, ResistedCount = 2, InconclusiveCount = 2, ProbeResults = [] }
            ]
        };

        var inv = JsonDocument.Parse(new SarifReportExporter().Export(result)).RootElement
            .GetProperty("runs")[0].GetProperty("invocations")[0];

        Assert.False(inv.GetProperty("executionSuccessful").GetBoolean());
        var props = inv.GetProperty("properties");
        Assert.True(props.GetProperty("wasTruncated").GetBoolean());
        Assert.Equal(3, props.GetProperty("skippedProbes").GetInt32());
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
