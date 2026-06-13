// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Reporting/JUnitReportExporterTests.cs
using System.Xml.Linq;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting;

namespace AgentEval.Tests.RedTeam.Reporting;

/// <summary>
/// Tests for JUnit XML report exporter.
/// </summary>
public class JUnitReportExporterTests
{
    [Fact]
    public void Export_ProducesValidXml()
    {
        var result = CreateTestResult();
        var exporter = new JUnitReportExporter();

        var xml = exporter.Export(result);

        Assert.NotNull(xml);
        var doc = XDocument.Parse(xml); // Should not throw
        Assert.NotNull(doc);
    }

    [Fact]
    public void Export_HasXmlDeclaration()
    {
        var result = CreateTestResult();
        var exporter = new JUnitReportExporter();

        var xml = exporter.Export(result);

        Assert.StartsWith("<?xml", xml);
    }

    [Fact]
    public void Export_HasTestSuitesRoot()
    {
        var result = CreateTestResult();
        var exporter = new JUnitReportExporter();

        var xml = exporter.Export(result);
        var doc = XDocument.Parse(xml);

        Assert.Equal("testsuites", doc.Root!.Name.LocalName);
    }

    [Fact]
    public void Export_TestSuitesHasCorrectCounts()
    {
        var result = CreateTestResult();
        var exporter = new JUnitReportExporter();

        var xml = exporter.Export(result);
        var doc = XDocument.Parse(xml);

        Assert.Equal("5", doc.Root!.Attribute("tests")?.Value);
        Assert.Equal("1", doc.Root!.Attribute("failures")?.Value); // Successful attacks = failures
        Assert.Equal("0", doc.Root!.Attribute("errors")?.Value);
    }

    [Fact]
    public void Export_IncludesTestSuitePerAttack()
    {
        var result = CreateTestResult();
        var exporter = new JUnitReportExporter();

        var xml = exporter.Export(result);
        var doc = XDocument.Parse(xml);

        var testSuites = doc.Root!.Elements("testsuite").ToList();
        Assert.Single(testSuites);
        Assert.Equal("RedTeam.PromptInjection", testSuites[0].Attribute("name")?.Value);
    }

    [Fact]
    public void Export_TestSuiteHasProperties()
    {
        var result = CreateTestResult();
        var exporter = new JUnitReportExporter();

        var xml = exporter.Export(result);
        var doc = XDocument.Parse(xml);

        var properties = doc.Root!.Element("testsuite")!.Element("properties");
        Assert.NotNull(properties);

        var owaspProp = properties.Elements("property")
            .FirstOrDefault(p => p.Attribute("name")?.Value == "owasp_id");
        Assert.NotNull(owaspProp);
        Assert.Equal("LLM01", owaspProp.Attribute("value")?.Value);
    }

    [Fact]
    public void Export_IncludesTestCasePerProbe()
    {
        var result = CreateTestResult();
        var exporter = new JUnitReportExporter();

        var xml = exporter.Export(result);
        var doc = XDocument.Parse(xml);

        var testCases = doc.Root!.Element("testsuite")!.Elements("testcase").ToList();
        Assert.Equal(2, testCases.Count);
    }

    [Fact]
    public void Export_SucceededProbe_HasFailureElement()
    {
        var result = CreateTestResult();
        var exporter = new JUnitReportExporter();

        var xml = exporter.Export(result);
        var doc = XDocument.Parse(xml);

        var testCases = doc.Root!.Element("testsuite")!.Elements("testcase").ToList();
        var failedCase = testCases.First(tc => tc.Attribute("name")?.Value?.Contains("PI-003") == true);

        var failure = failedCase.Element("failure");
        Assert.NotNull(failure);
        Assert.Equal("VulnerabilityFound", failure.Attribute("type")?.Value);
    }

    [Fact]
    public void Export_ResistedProbe_HasSystemOut()
    {
        var result = CreateTestResult();
        var exporter = new JUnitReportExporter();

        var xml = exporter.Export(result);
        var doc = XDocument.Parse(xml);

        var testCases = doc.Root!.Element("testsuite")!.Elements("testcase").ToList();
        var passedCase = testCases.First(tc => tc.Attribute("name")?.Value?.Contains("PI-001") == true);

        var systemOut = passedCase.Element("system-out");
        Assert.NotNull(systemOut);
        Assert.Contains("resisted", systemOut.Value);
    }

    [Fact]
    public void Export_IncludesTimestamp()
    {
        var result = CreateTestResult();
        var exporter = new JUnitReportExporter();

        var xml = exporter.Export(result);
        var doc = XDocument.Parse(xml);

        var timestamp = doc.Root!.Attribute("timestamp")?.Value;
        Assert.NotNull(timestamp);
    }

    [Fact]
    public void FileExtension_ReturnsXml()
    {
        var exporter = new JUnitReportExporter();
        Assert.Equal(".xml", exporter.FileExtension);
    }

    [Fact]
    public void FormatName_ReturnsJUnitXml()
    {
        var exporter = new JUnitReportExporter();
        Assert.Equal("JUnit XML", exporter.FormatName);
    }

    [Fact]
    public void MimeType_ReturnsXml()
    {
        var exporter = new JUnitReportExporter();
        Assert.Equal("application/xml", exporter.MimeType);
    }

    [Fact]
    public async Task ExportToFileAsync_WritesValidXml()
    {
        var result = CreateTestResult();
        var exporter = new JUnitReportExporter();
        var tempFile = Path.GetTempFileName();

        try
        {
            await exporter.ExportToFileAsync(result, tempFile);

            Assert.True(File.Exists(tempFile));
            var content = await File.ReadAllTextAsync(tempFile);
            var doc = XDocument.Parse(content); // Should not throw
            Assert.NotNull(doc);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Export_InconclusiveProbe_HasErrorElement()
    {
        var result = new RedTeamResult
        {
            AgentName = "TestAgent",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromSeconds(5),
            TotalProbes = 1,
            InconclusiveProbes = 1,
            AttackResults =
            [
                new AttackResult
                {
                    AttackName = "Test",
                    AttackDisplayName = "Test",
                    OwaspId = "LLM01",
                    Severity = Severity.Medium,
                    InconclusiveCount = 1,
                    ProbeResults =
                    [
                        new ProbeResult
                        {
                            ProbeId = "T-001",
                            Prompt = "Test",
                            Response = "[TIMEOUT]",
                            Outcome = EvaluationOutcome.Inconclusive,
                            Reason = "Timed out",
                            Difficulty = Difficulty.Easy,
                            Error = "Timeout"
                        }
                    ]
                }
            ]
        };

        var exporter = new JUnitReportExporter();
        var xml = exporter.Export(result);
        var doc = XDocument.Parse(xml);

        var testCase = doc.Root!.Element("testsuite")!.Element("testcase");
        var error = testCase!.Element("error");
        Assert.NotNull(error);
        Assert.Equal("Inconclusive", error.Attribute("type")?.Value);
    }

    [Fact]
    public void Export_ProbeContentWithXmlInvalidControlChars_DoesNotThrowAndRoundTrips()
    {
        // A red-team SUT (encoding-evasion / insecure-output attacks) can echo ANSI escapes
        // (0x1B) and NUL (0x00) - characters illegal in XML 1.0. Before sanitisation the
        // XmlWriter threw ArgumentException and the whole report was lost exactly when a
        // vulnerability was found. Every SUT-derived field must survive export. Control chars
        // are built via (char) casts so no escape sequence appears in source.
        var esc = ((char)0x1b).ToString(); // ESC - illegal in XML 1.0
        var nul = ((char)0x00).ToString(); // NUL - illegal in XML 1.0
        var result = new RedTeamResult
        {
            AgentName = "Agent" + esc + "[0m",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromSeconds(5),
            TotalProbes = 2,
            SucceededProbes = 1,
            InconclusiveProbes = 1,
            AttackResults =
            [
                new AttackResult
                {
                    AttackName = "InsecureOutput",
                    AttackDisplayName = "Insecure Output",
                    OwaspId = "LLM05",
                    Severity = Severity.High,
                    SucceededCount = 1,
                    InconclusiveCount = 1,
                    ProbeResults =
                    [
                        new ProbeResult
                        {
                            ProbeId = "IO-001",
                            Prompt = "Echo " + esc + "[31m red " + nul + " nul",
                            Response = esc + "[31mPWNED" + esc + "[0m" + nul,
                            Outcome = EvaluationOutcome.Succeeded,
                            Reason = "Marker leaked " + esc + "]8;;evil",
                            Technique = "ansiescape",
                            Difficulty = Difficulty.Moderate
                        },
                        new ProbeResult
                        {
                            ProbeId = "IO-002",
                            Prompt = "p",
                            Response = "r",
                            Outcome = EvaluationOutcome.Inconclusive,
                            Reason = "weird " + nul + " reason",
                            Error = "boom " + esc + "[1m",
                            Difficulty = Difficulty.Easy
                        }
                    ]
                }
            ]
        };

        var exporter = new JUnitReportExporter();

        var xml = exporter.Export(result); // must not throw
        var doc = XDocument.Parse(xml);     // must round-trip
        Assert.NotNull(doc);
        // The raw escape / NUL bytes must not survive into the output. Use ordinal char search:
        // xUnit's string DoesNotContain is culture-sensitive and treats control chars as ignorable
        // (spurious match at pos 0), whereas string.Contains(char) is ordinal.
        Assert.False(xml.Contains((char)0x1b), "ESC (0x1B) must be sanitized out of the XML");
        Assert.False(xml.Contains((char)0x00), "NUL (0x00) must be sanitized out of the XML");
    }

    [Fact]
    public void Export_TruncatedScan_SurfacesRootSkipped_AndTruncationNotice()
    {
        // 5d: a FailFast-truncated scan must NOT render as a complete green run — surface a root skipped count
        // and a visible TruncationNotice testsuite/testcase.
        var result = new RedTeamResult
        {
            AgentName = "TestAgent",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromSeconds(5),
            TotalProbes = 2, ResistedProbes = 2, WasTruncated = true, SkippedProbes = 8,
            AttackResults =
            [
                new AttackResult { AttackName = "PromptInjection", AttackDisplayName = "PI", OwaspId = "LLM01", Severity = Severity.High, ResistedCount = 2, ProbeResults =
                [
                    new ProbeResult { ProbeId = "PI-001", Prompt = "p", Response = "r", Outcome = EvaluationOutcome.Resisted, Reason = "ok", Difficulty = Difficulty.Easy }
                ] }
            ]
        };

        var doc = XDocument.Parse(new JUnitReportExporter().Export(result));

        Assert.Equal("8", doc.Root!.Attribute("skipped")?.Value);
        Assert.Equal("10", doc.Root!.Attribute("tests")?.Value);   // L27: 2 executed + 8 skipped = full planned scope
        var notice = doc.Root!.Elements("testsuite").FirstOrDefault(s => s.Attribute("name")?.Value == "RedTeam.TruncationNotice");
        Assert.NotNull(notice);
        Assert.NotNull(notice!.Element("testcase")!.Element("skipped"));
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
                Severity = Severity.High,
                ResistedCount = 4,
                SucceededCount = 1,
                InconclusiveCount = 0,
                ProbeResults =
                [
                    new ProbeResult
                    {
                        ProbeId = "PI-001",
                        Prompt = "Safe prompt",
                        Response = "Safe response",
                        Outcome = EvaluationOutcome.Resisted,
                        Reason = "No markers",
                        Difficulty = Difficulty.Easy,
                        Duration = TimeSpan.FromMilliseconds(100)
                    },
                    new ProbeResult
                    {
                        ProbeId = "PI-003",
                        Prompt = "Attack prompt",
                        Response = "PWNED",
                        Outcome = EvaluationOutcome.Succeeded,
                        Reason = "Marker found",
                        Difficulty = Difficulty.Moderate,
                        Duration = TimeSpan.FromMilliseconds(150)
                    }
                ]
            }
        ]
    };
}
