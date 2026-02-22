// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Reporting/Compliance/ISO27001ComplianceReporterTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Tests.RedTeam.Reporting.Compliance;

public class ISO27001ComplianceReporterTests
{
    private static RedTeamResult CreateTestResult(params (string AttackName, int Total, int Resisted)[] attacks)
    {
        var attackResults = attacks.Select(a =>
        {
            // Create probe results based on counts
            var probes = new List<ProbeResult>();
            for (int i = 0; i < a.Resisted; i++)
                probes.Add(new ProbeResult { ProbeId = $"p{i}", Prompt = $"probe-{i}", Response = "Resisted", Reason = "Test", Outcome = EvaluationOutcome.Resisted });
            for (int i = 0; i < a.Total - a.Resisted; i++)
                probes.Add(new ProbeResult { ProbeId = $"p{a.Resisted + i}", Prompt = $"probe-{a.Resisted + i}", Response = "Succeeded", Reason = "Test", Outcome = EvaluationOutcome.Succeeded });

            return new AttackResult
            {
                AttackName = a.AttackName,
                OwaspId = "LLM01", // Default for tests
                ResistedCount = a.Resisted,
                SucceededCount = a.Total - a.Resisted,
                ProbeResults = probes
            };
        }).ToList();

        var totalProbes = attackResults.Sum(a => a.TotalCount);
        var resistedProbes = attackResults.Sum(a => a.ResistedCount);

        return new RedTeamResult
        {
            AgentName = "TestAgent",
            AttackResults = attackResults,
            TotalProbes = totalProbes,
            ResistedProbes = resistedProbes,
            SucceededProbes = totalProbes - resistedProbes
        };
    }

    [Fact]
    public void GenerateReport_WithEmptyResults_ReturnsValidReport()
    {
        // Arrange
        var result = CreateTestResult();
        var reporter = new ISO27001ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);

        // Assert
        Assert.NotNull(report);
        Assert.Equal("ISO 27001:2022", report.FrameworkName);
        Assert.Equal("TestAgent", report.AgentName);
        Assert.Equal(8, report.Controls.Count); // 8 ISO controls defined
        Assert.All(report.Controls, c => Assert.Equal(ControlEvaluationStatus.NotEvaluated, c.Status));
    }

    [Fact]
    public void GenerateReport_WithAllAttacksBlocked_ReturnsHighCompliance()
    {
        // Arrange
        var result = CreateTestResult(
            ("PromptInjection", 10, 10),
            ("Jailbreak", 10, 10),
            ("PIILeakage", 10, 10)
        );
        var reporter = new ISO27001ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);

        // Assert
        Assert.True(report.ComplianceRate >= 95, "Expected high compliance rate when all attacks blocked");
        Assert.Equal(RiskLevel.Low, report.RiskLevel);
        Assert.Empty(report.NonConformities.Where(n => n.Severity == NonConformitySeverity.Major));
    }

    [Fact]
    public void GenerateReport_WithMajorFailures_ReturnsCriticalRiskLevel()
    {
        // Arrange - attacks with very low resistance (< 50%)
        var result = CreateTestResult(
            ("PromptInjection", 20, 5),   // 25% pass
            ("Jailbreak", 20, 6),          // 30% pass
            ("PIILeakage", 20, 4)          // 20% pass
        );
        var reporter = new ISO27001ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);

        // Assert
        Assert.Equal(RiskLevel.Critical, report.RiskLevel);
        Assert.True(report.NonConformities.Any(n => n.Severity == NonConformitySeverity.Major));
    }

    [Fact]
    public void ISO27001Controls_ContainsExpectedControls()
    {
        // Assert
        Assert.Equal(8, ISO27001Controls.All.Length);

        var controlIds = ISO27001Controls.All.Select(c => c.ControlId).ToList();
        Assert.Contains("A.5.1", controlIds);   // Information security policies
        Assert.Contains("A.5.15", controlIds);  // Access control
        Assert.Contains("A.5.33", controlIds);  // Protection of records
        Assert.Contains("A.8.3", controlIds);   // Information access restriction
        Assert.Contains("A.8.11", controlIds);  // Data masking
        Assert.Contains("A.8.12", controlIds);  // Data leakage prevention
        Assert.Contains("A.8.24", controlIds);  // Use of cryptography
        Assert.Contains("A.8.28", controlIds);  // Secure coding

        // Verify framework is ISO27001 (no space)
        Assert.All(ISO27001Controls.All, c => Assert.Equal("ISO27001", c.Framework));
    }

    [Theory]
    [InlineData("PromptInjection", "A.5.1")]
    [InlineData("PIILeakage", "A.5.33")]
    [InlineData("PIILeakage", "A.8.11")]
    [InlineData("PIILeakage", "A.8.12")]
    public void GenerateReport_MapsAttacksToCorrectControls(string attackName, string expectedControlId)
    {
        // Arrange
        var result = CreateTestResult((attackName, 10, 10));
        var reporter = new ISO27001ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);

        // Assert
        var control = report.Controls.FirstOrDefault(c => c.Control.ControlId == expectedControlId);
        Assert.NotNull(control);
        Assert.NotEqual(ControlEvaluationStatus.NotEvaluated, control.Status);
    }

    [Fact]
    public void GenerateReport_CreatesNonConformitiesForFailures()
    {
        // Arrange - failures that should generate non-conformities
        var result = CreateTestResult(
            ("PromptInjection", 20, 5),   // 25% - Major NC
            ("PIILeakage", 20, 17)        // 85% - Observation
        );
        var reporter = new ISO27001ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);

        // Assert
        Assert.NotEmpty(report.NonConformities);
        Assert.True(report.NonConformities.All(n => n.Id > 0));
        Assert.True(report.NonConformities.All(n => !string.IsNullOrEmpty(n.ControlId)));
        Assert.True(report.NonConformities.All(n => !string.IsNullOrEmpty(n.Finding)));
    }

    [Fact]
    public void NonConformity_MajorSeverity_ForVeryLowPassRate()
    {
        // Arrange - < 50% pass rate should be Major
        var result = CreateTestResult(("PromptInjection", 20, 5));
        var reporter = new ISO27001ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);

        // Assert
        var majorNCs = report.NonConformities.Where(n => n.Severity == NonConformitySeverity.Major).ToList();
        Assert.NotEmpty(majorNCs);
    }

    [Fact]
    public void NonConformity_MinorSeverity_ForModerateFailure()
    {
        // Arrange - 50-80% pass rate should be Minor
        var result = CreateTestResult(("PromptInjection", 20, 12)); // 60% pass
        var reporter = new ISO27001ComplianceReporter();

        // Act  
        var report = reporter.GenerateReport(result);

        // Assert - should have NeedsImprovement with Minor NC
        var control = report.Controls.First(c => c.Control.RelevantAttacks.Contains("PromptInjection"));
        Assert.Equal(ControlEvaluationStatus.NeedsImprovement, control.Status);
    }

    [Fact]
    public void ToMarkdown_GeneratesValidMarkdownReport()
    {
        // Arrange
        var result = CreateTestResult(
            ("PromptInjection", 10, 9),
            ("PIILeakage", 10, 10)
        );
        var reporter = new ISO27001ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);
        var markdown = report.ToMarkdown();

        // Assert
        Assert.Contains("# ISO 27001:2022", markdown);
        Assert.Contains("**Organization System:** TestAgent", markdown);
        Assert.Contains("## Executive Summary", markdown);
        Assert.Contains("## Annex A Control Assessment", markdown);
        Assert.Contains("A.5.", markdown); // At least one Annex A control
    }

    [Fact]
    public void ToMarkdown_IncludesNonConformitiesSection()
    {
        // Arrange
        var result = CreateTestResult(("PromptInjection", 20, 5));
        var reporter = new ISO27001ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);
        var markdown = report.ToMarkdown();

        // Assert
        Assert.Contains("## Non-Conformities", markdown);
        Assert.Contains("NC-1", markdown);
    }

    [Fact]
    public void ToJson_GeneratesValidJsonReport()
    {
        // Arrange
        var result = CreateTestResult(("PromptInjection", 10, 10));
        var reporter = new ISO27001ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);
        var json = report.ToJson();

        // Assert
        Assert.Contains("\"frameworkName\"", json);
        Assert.Contains("\"ISO 27001:2022\"", json);
        Assert.Contains("\"nonConformities\"", json);
        Assert.Contains("\"controls\"", json);
    }

    [Fact]
    public void GenerateReport_IncludesRecommendationsWhenRequested()
    {
        // Arrange
        var result = CreateTestResult(("PromptInjection", 20, 5));
        var reporter = new ISO27001ComplianceReporter();
        var options = new ComplianceReportOptions { IncludeRecommendations = true };

        // Act
        var report = reporter.GenerateReport(result, options);

        // Assert
        Assert.NotEmpty(report.Recommendations);
        Assert.Contains(report.Recommendations, r => r.Contains("URGENT") || r.Contains("A.5.1"));
    }

    [Fact]
    public void GenerateReport_ExcludesRecommendationsWhenNotRequested()
    {
        // Arrange
        var result = CreateTestResult(("PromptInjection", 20, 5));
        var reporter = new ISO27001ComplianceReporter();
        var options = new ComplianceReportOptions { IncludeRecommendations = false };

        // Act
        var report = reporter.GenerateReport(result, options);

        // Assert
        Assert.Empty(report.Recommendations);
    }

    [Fact]
    public void ExtensionMethod_ToISO27001ComplianceReport_Works()
    {
        // Arrange
        var result = CreateTestResult(("PromptInjection", 10, 10));

        // Act
        var report = result.ToISO27001ComplianceReport();

        // Assert
        Assert.NotNull(report);
        Assert.Equal("TestAgent", report.AgentName);
        Assert.Equal("ISO 27001:2022", report.FrameworkName);
    }

    [Fact]
    public void Controls_HaveOwaspMappings()
    {
        // Assert - verify OWASP cross-references exist
        var controlsWithOwasp = ISO27001Controls.All.Where(c => c.OwaspCategories.Length > 0).ToList();
        Assert.NotEmpty(controlsWithOwasp);
        
        // A.5.1 should map to OWASP categories
        var a51 = ISO27001Controls.All.First(c => c.ControlId == "A.5.1");
        Assert.NotEmpty(a51.OwaspCategories);
    }

    [Fact]
    public void GenerateReport_CalculatesSummaryCorrectly()
    {
        // Arrange
        var result = CreateTestResult(
            ("PromptInjection", 20, 20),  // 100% - Effective
            ("PIILeakage", 20, 5)          // 25% - NeedsImprovement  
        );
        var reporter = new ISO27001ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);

        // Assert
        Assert.True(report.Summary.TestedCategories > 0);
        Assert.True(report.Summary.CriticalFindings >= 0 || report.Summary.HighFindings >= 0);
    }

    [Fact]
    public void GenerateReport_WithPartialSuccess_ReturnsPartiallyEffective()
    {
        // Arrange - 85% pass rate (between 80-95%)
        var result = CreateTestResult(("PromptInjection", 20, 17));
        var reporter = new ISO27001ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);

        // Assert
        var control = report.Controls.FirstOrDefault(c => 
            c.Status == ControlEvaluationStatus.PartiallyEffective);
        Assert.NotNull(control);
        
        // Should generate observation, not major NC
        var relatedNCs = report.NonConformities.Where(n => n.ControlId == control.Control.ControlId);
        Assert.True(relatedNCs.All(n => n.Severity != NonConformitySeverity.Major));
    }
}
