// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Reporting/Pdf/PdfReportGeneratorTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Pdf;

namespace AgentEval.Tests.RedTeam.Reporting.Pdf;

/// <summary>
/// Tests for PdfReportGenerator.
/// </summary>
public class PdfReportGeneratorTests
{
    private readonly PdfReportGenerator _generator = new();

    [Fact]
    public void FormatName_ReturnsPDF()
    {
        Assert.Equal("PDF", _generator.FormatName);
    }

    [Fact]
    public void FileExtension_ReturnsPdf()
    {
        Assert.Equal(".pdf", _generator.FileExtension);
    }

    [Fact]
    public void MimeType_ReturnsApplicationPdf()
    {
        Assert.Equal("application/pdf", _generator.MimeType);
    }

    [Fact]
    public void GenerateExecutiveReport_ProducesValidPdf()
    {
        var result = CreateTestResult();

        var bytes = _generator.GenerateExecutiveReport(result);

        // PDF files start with "%PDF-"
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 100, "PDF should have substantial content");
        Assert.Equal(0x25, bytes[0]); // '%'
        Assert.Equal(0x50, bytes[1]); // 'P'
        Assert.Equal(0x44, bytes[2]); // 'D'
        Assert.Equal(0x46, bytes[3]); // 'F'
    }

    [Fact]
    public void GenerateExecutiveReport_WithOptions_IncludesBranding()
    {
        var result = CreateTestResult();
        var options = new PdfReportOptions
        {
            CompanyName = "Acme Corp",
            AgentName = "Customer Bot",
            AgentVersion = "2.1",
            Branding = new BrandingOptions
            {
                PrimaryColor = "#FF5733",
                SecondaryColor = "#33FF57"
            }
        };

        var bytes = _generator.GenerateExecutiveReport(result, options);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 100);
    }

    [Fact]
    public void GenerateExecutiveReport_WithBaseline_IncludesTrends()
    {
        var result = CreateTestResult();
        var baseline = CreateTestResult(withFailures: true);
        var options = new PdfReportOptions
        {
            IncludeTrends = true,
            BaselineResults = baseline
        };

        var bytes = _generator.GenerateExecutiveReport(result, options);

        Assert.NotNull(bytes);
        // PDF should be larger with trend page
        Assert.True(bytes.Length > 1000);
    }

    [Fact]
    public void GenerateExecutiveReport_WithDetailedResults_AddsExtraContent()
    {
        var result = CreateTestResult(withFailures: true);
        var options = new PdfReportOptions
        {
            IncludeDetailedResults = true
        };

        var bytes = _generator.GenerateExecutiveReport(result, options);
        var bytesWithoutDetails = _generator.GenerateExecutiveReport(result, new PdfReportOptions());

        Assert.True(bytes.Length > bytesWithoutDetails.Length, "Detailed results should add content");
    }

    [Fact]
    public void Export_ReturnsBase64String()
    {
        var result = CreateTestResult();

        var base64 = _generator.Export(result);

        Assert.NotNull(base64);
        Assert.NotEmpty(base64);
        // Verify it's valid Base64
        var bytes = Convert.FromBase64String(base64);
        Assert.Equal(0x25, bytes[0]); // '%' (start of PDF)
    }

    [Fact]
    public async Task ExportToFileAsync_WritesValidPdf()
    {
        var result = CreateTestResult();
        var tempFile = Path.Combine(Path.GetTempPath(), $"test-report-{Guid.NewGuid()}.pdf");

        try
        {
            await _generator.ExportToFileAsync(result, tempFile);

            Assert.True(File.Exists(tempFile));
            var content = await File.ReadAllBytesAsync(tempFile);
            Assert.Equal(0x25, content[0]); // '%'
            Assert.Equal(0x50, content[1]); // 'P'
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SaveAsync_WritesValidPdfWithOptions()
    {
        var result = CreateTestResult();
        var options = new PdfReportOptions
        {
            CompanyName = "Test Corp",
            PageSize = PageSize.Letter
        };
        var tempFile = Path.Combine(Path.GetTempPath(), $"test-report-{Guid.NewGuid()}.pdf");

        try
        {
            await _generator.SaveAsync(result, tempFile, options);

            Assert.True(File.Exists(tempFile));
            var content = await File.ReadAllBytesAsync(tempFile);
            Assert.True(content.Length > 1000);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void GenerateExecutiveReport_NullResult_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _generator.GenerateExecutiveReport(null!));
    }

    [Theory]
    [InlineData(PageSize.A4)]
    [InlineData(PageSize.Letter)]
    [InlineData(PageSize.Legal)]
    public void GenerateExecutiveReport_DifferentPageSizes_Succeeds(PageSize pageSize)
    {
        var result = CreateTestResult();
        var options = new PdfReportOptions { PageSize = pageSize };

        var bytes = _generator.GenerateExecutiveReport(result, options);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 100);
    }

    [Fact]
    public void GenerateExecutiveReport_MultipleOwaspCategories_ShowsCoverage()
    {
        var result = CreateMultiCategoryResult();

        var bytes = _generator.GenerateExecutiveReport(result);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 100);
    }

    [Fact]
    public void GenerateExecutiveReport_AllSeverityLevels_HandlesCorrectly()
    {
        var result = CreateAllSeveritiesResult();

        var bytes = _generator.GenerateExecutiveReport(result);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 100);
    }

    private static RedTeamResult CreateTestResult(bool withFailures = false)
    {
        var resisted = withFailures ? 8 : 10;
        var succeeded = withFailures ? 2 : 0;

        return new RedTeamResult
        {
            AgentName = "TestAgent",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromMinutes(5),
            TotalProbes = 10,
            ResistedProbes = resisted,
            SucceededProbes = succeeded,
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
                    ResistedCount = resisted,
                    SucceededCount = succeeded,
                    InconclusiveCount = 0,
                    ProbeResults = CreateProbeResults(resisted, succeeded)
                }
            ]
        };
    }

    private static RedTeamResult CreateMultiCategoryResult()
    {
        return new RedTeamResult
        {
            AgentName = "MultiCategoryAgent",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromMinutes(10),
            TotalProbes = 30,
            ResistedProbes = 25,
            SucceededProbes = 5,
            InconclusiveProbes = 0,
            AttackResults =
            [
                new AttackResult
                {
                    AttackName = "PromptInjection",
                    AttackDisplayName = "Prompt Injection",
                    OwaspId = "LLM01",
                    Severity = Severity.High,
                    ResistedCount = 8,
                    SucceededCount = 2,
                    ProbeResults = CreateProbeResults(8, 2)
                },
                new AttackResult
                {
                    AttackName = "InsecureOutput",
                    AttackDisplayName = "Insecure Output Handling",
                    OwaspId = "LLM05",
                    Severity = Severity.High,
                    ResistedCount = 9,
                    SucceededCount = 1,
                    ProbeResults = CreateProbeResults(9, 1)
                },
                new AttackResult
                {
                    AttackName = "PIILeakage",
                    AttackDisplayName = "PII Leakage",
                    OwaspId = "LLM02",
                    Severity = Severity.Critical,
                    ResistedCount = 8,
                    SucceededCount = 2,
                    ProbeResults = CreateProbeResults(8, 2)
                }
            ]
        };
    }

    private static RedTeamResult CreateAllSeveritiesResult()
    {
        return new RedTeamResult
        {
            AgentName = "AllSeveritiesAgent",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-15),
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromMinutes(15),
            TotalProbes = 20,
            ResistedProbes = 16,
            SucceededProbes = 4,
            InconclusiveProbes = 0,
            AttackResults =
            [
                new AttackResult
                {
                    AttackName = "CriticalAttack",
                    OwaspId = "LLM01",
                    Severity = Severity.Critical,
                    ResistedCount = 4,
                    SucceededCount = 1,
                    ProbeResults = CreateProbeResults(4, 1)
                },
                new AttackResult
                {
                    AttackName = "HighAttack",
                    OwaspId = "LLM05",
                    Severity = Severity.High,
                    ResistedCount = 4,
                    SucceededCount = 1,
                    ProbeResults = CreateProbeResults(4, 1)
                },
                new AttackResult
                {
                    AttackName = "MediumAttack",
                    OwaspId = "LLM02",
                    Severity = Severity.Medium,
                    ResistedCount = 4,
                    SucceededCount = 1,
                    ProbeResults = CreateProbeResults(4, 1)
                },
                new AttackResult
                {
                    AttackName = "LowAttack",
                    OwaspId = "LLM07",
                    Severity = Severity.Low,
                    ResistedCount = 4,
                    SucceededCount = 1,
                    ProbeResults = CreateProbeResults(4, 1)
                }
            ]
        };
    }

    private static List<ProbeResult> CreateProbeResults(int resisted, int succeeded)
    {
        var results = new List<ProbeResult>();
        
        for (int i = 0; i < resisted; i++)
        {
            results.Add(new ProbeResult
            {
                ProbeId = $"R-{i:D3}",
                Prompt = "Safe test prompt",
                Response = "Safe response",
                Outcome = EvaluationOutcome.Resisted,
                Reason = "No attack indicators found",
                Difficulty = Difficulty.Easy
            });
        }
        
        for (int i = 0; i < succeeded; i++)
        {
            results.Add(new ProbeResult
            {
                ProbeId = $"S-{i:D3}",
                Prompt = "Malicious test prompt",
                Response = "Compromised response",
                Outcome = EvaluationOutcome.Succeeded,
                Reason = "Attack succeeded",
                Difficulty = Difficulty.Moderate
            });
        }

        return results;
    }
}
