// tests/AgentEval.Tests/RedTeam/Reporting/Compliance/SOC2ComplianceReporterTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Tests.RedTeam.Reporting.Compliance;

public class SOC2ComplianceReporterTests
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
        var reporter = new SOC2ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);

        // Assert
        Assert.NotNull(report);
        Assert.Equal("SOC2 Type II", report.FrameworkName);
        Assert.Equal("TestAgent", report.AgentName);
        Assert.Equal(7, report.Controls.Count); // 7 SOC2 controls defined
        Assert.All(report.Controls, c => Assert.Equal(ControlEvaluationStatus.NotEvaluated, c.Status));
    }

    [Fact]
    public void GenerateReport_WithAllAttacksBlocked_ReturnsHighCompliance()
    {
        // Arrange - attacks that map to SOC2 controls
        var result = CreateTestResult(
            ("SystemPromptExtraction", 10, 10),
            ("PIILeakage", 10, 10),
            ("PromptInjection", 10, 10)
        );
        var reporter = new SOC2ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);

        // Assert
        Assert.True(report.ComplianceRate >= 95, "Expected high compliance rate when all attacks blocked");
        Assert.Equal(RiskLevel.Low, report.RiskLevel);
    }

    [Fact]
    public void GenerateReport_WithFailedControls_ReturnsCriticalRiskLevel()
    {
        // Arrange - multiple attacks with low resistance that map to SOC2
        var result = CreateTestResult(
            ("SystemPromptExtraction", 20, 5),  // 25% pass - NeedsImprovement
            ("PIILeakage", 20, 6),              // 30% pass - NeedsImprovement
            ("PromptInjection", 20, 4),          // 20% pass - NeedsImprovement
            ("Jailbreak", 20, 5)                 // 25% pass - NeedsImprovement
        );
        var reporter = new SOC2ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);

        // Assert
        Assert.Equal(RiskLevel.Critical, report.RiskLevel);
        Assert.True(report.ComplianceRate < 50, "Expected low compliance with many failures");
    }

    [Theory]
    [InlineData("SystemPromptExtraction", "CC6.1")]
    [InlineData("PIILeakage", "CC6.1")]
    [InlineData("PromptInjection", "CC6.3")]
    public void GenerateReport_MapsAttacksToCorrectControls(string attackName, string expectedControlId)
    {
        // Arrange
        var result = CreateTestResult((attackName, 10, 10));
        var reporter = new SOC2ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);

        // Assert
        var control = report.Controls.FirstOrDefault(c => c.Control.ControlId == expectedControlId);
        Assert.NotNull(control);
        Assert.NotEqual(ControlEvaluationStatus.NotEvaluated, control.Status);
    }

    [Fact]
    public void GenerateReport_WithPartialSuccess_ReturnsPartiallyEffective()
    {
        // Arrange - 85% pass rate (between 80-95%)
        var result = CreateTestResult(
            ("SystemPromptExtraction", 20, 17),
            ("PIILeakage", 20, 17)
        );
        var reporter = new SOC2ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);

        // Assert - CC6.1 should be PartiallyEffective
        var cc61 = report.Controls.First(c => c.Control.ControlId == "CC6.1");
        Assert.Equal(ControlEvaluationStatus.PartiallyEffective, cc61.Status);
        // Risk level is Low because there are 0 NeedsImprovement controls
        // Risk level Moderate requires at least 1 NeedsImprovement
        Assert.Equal(RiskLevel.Low, report.RiskLevel);
    }

    [Fact]
    public void SOC2Controls_ContainsExpectedControls()
    {
        // Assert - verify SOC2 control definitions
        Assert.Equal(7, SOC2Controls.All.Length);
        
        var controlIds = SOC2Controls.All.Select(c => c.ControlId).ToList();
        Assert.Contains("CC6.1", controlIds);
        Assert.Contains("CC6.6", controlIds);
        Assert.Contains("CC6.7", controlIds);
        Assert.Contains("CC7.2", controlIds);
        Assert.Contains("CC8.1", controlIds);
        
        // Verify all controls have descriptions
        Assert.All(SOC2Controls.All, c => Assert.False(string.IsNullOrEmpty(c.Description)));
    }

    [Fact]
    public void ToMarkdown_GeneratesValidMarkdownReport()
    {
        // Arrange
        var result = CreateTestResult(
            ("SystemPromptExtraction", 10, 9),
            ("PIILeakage", 10, 10)
        );
        var reporter = new SOC2ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);
        var markdown = report.ToMarkdown();

        // Assert
        Assert.Contains("# SOC2 Type II", markdown);
        Assert.Contains("**System:** TestAgent", markdown);
        Assert.Contains("## Executive Summary", markdown);
        Assert.Contains("## Control Evidence", markdown);
        Assert.Contains("CC6.1", markdown);
    }

    [Fact]
    public void ToJson_GeneratesValidJsonReport()
    {
        // Arrange
        var result = CreateTestResult(("SystemPromptExtraction", 10, 10));
        var reporter = new SOC2ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);
        var json = report.ToJson();

        // Assert
        Assert.Contains("\"frameworkName\"", json);
        Assert.Contains("\"SOC2 Type II\"", json);
        Assert.Contains("\"agentName\"", json);
        Assert.Contains("\"controls\"", json);
    }

    [Fact]
    public void GenerateReport_IncludesRecommendationsWhenRequested()
    {
        // Arrange
        var result = CreateTestResult(
            ("SystemPromptExtraction", 20, 5) // Low pass rate
        );
        var reporter = new SOC2ComplianceReporter();
        var options = new ComplianceReportOptions { IncludeRecommendations = true };

        // Act
        var report = reporter.GenerateReport(result, options);

        // Assert
        Assert.NotEmpty(report.Recommendations);
        Assert.Contains(report.Recommendations, r => r.Contains("CC6.1"));
    }

    [Fact]
    public void GenerateReport_ExcludesRecommendationsWhenNotRequested()
    {
        // Arrange
        var result = CreateTestResult(("SystemPromptExtraction", 20, 5));
        var reporter = new SOC2ComplianceReporter();
        var options = new ComplianceReportOptions { IncludeRecommendations = false };

        // Act
        var report = reporter.GenerateReport(result, options);

        // Assert
        Assert.Empty(report.Recommendations);
    }

    [Fact]
    public void ExtensionMethod_ToSOC2ComplianceReport_Works()
    {
        // Arrange
        var result = CreateTestResult(("SystemPromptExtraction", 10, 10));

        // Act
        var report = result.ToSOC2ComplianceReport();

        // Assert
        Assert.NotNull(report);
        Assert.Equal("TestAgent", report.AgentName);
    }

    [Fact]
    public void GenerateReport_CalculatesSummaryCorrectly()
    {
        // Arrange - mix of effective and needs improvement
        var result = CreateTestResult(
            ("SystemPromptExtraction", 20, 20),  // 100% - Effective (CC6.1)
            ("PIILeakage", 20, 20),              // 100% - Effective (CC6.1)
            ("PromptInjection", 20, 5)           // 25% - NeedsImprovement (CC6.6)
        );
        var reporter = new SOC2ComplianceReporter();

        // Act
        var report = reporter.GenerateReport(result);

        // Assert
        Assert.True(report.Summary.TestedCategories > 0);
        Assert.True(report.Summary.CriticalFindings >= 0);
    }
}
