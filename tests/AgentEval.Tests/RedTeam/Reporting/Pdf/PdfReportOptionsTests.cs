// tests/AgentEval.Tests/RedTeam/Reporting/Pdf/PdfReportOptionsTests.cs
using AgentEval.RedTeam.Reporting.Pdf;

namespace AgentEval.Tests.RedTeam.Reporting.Pdf;

/// <summary>
/// Tests for PdfReportOptions and BrandingOptions.
/// </summary>
public class PdfReportOptionsTests
{
    [Fact]
    public void DefaultOptions_HasExpectedDefaults()
    {
        var options = new PdfReportOptions();

        Assert.Null(options.CompanyName);
        Assert.Null(options.CompanyLogoPath);
        Assert.Null(options.AgentName);
        Assert.Null(options.AgentVersion);
        Assert.Equal(PageSize.A4, options.PageSize);
        Assert.False(options.IncludeTrends);
        Assert.False(options.IncludeDetailedResults);
        Assert.Null(options.BaselineResults);
        Assert.Equal("AI Agent Security Assessment", options.Subject);
    }

    [Fact]
    public void BrandingOptions_HasExpectedDefaults()
    {
        var branding = new BrandingOptions();

        Assert.Equal("#0078D4", branding.PrimaryColor);
        Assert.Equal("#2B579A", branding.SecondaryColor);
        Assert.Equal("Arial", branding.FontFamily);
    }

    [Fact]
    public void BrandingOptions_CanSetCustomColors()
    {
        var branding = new BrandingOptions
        {
            PrimaryColor = "#FF5733",
            SecondaryColor = "#33FF57",
            FontFamily = "Segoe UI"
        };

        Assert.Equal("#FF5733", branding.PrimaryColor);
        Assert.Equal("#33FF57", branding.SecondaryColor);
        Assert.Equal("Segoe UI", branding.FontFamily);
    }

    [Fact]
    public void Options_WithAllFields_CanBeCreated()
    {
        var options = new PdfReportOptions
        {
            CompanyName = "Acme Corp",
            CompanyLogoPath = "/path/to/logo.png",
            AgentName = "Customer Bot",
            AgentVersion = "2.1.0",
            PageSize = PageSize.Letter,
            IncludeTrends = true,
            IncludeDetailedResults = true,
            Author = "Security Team",
            Subject = "Q4 Security Review",
            Branding = new BrandingOptions
            {
                PrimaryColor = "#123456",
                SecondaryColor = "#654321",
                FontFamily = "Segoe UI"
            }
        };

        Assert.Equal("Acme Corp", options.CompanyName);
        Assert.Equal("/path/to/logo.png", options.CompanyLogoPath);
        Assert.Equal("Customer Bot", options.AgentName);
        Assert.Equal("2.1.0", options.AgentVersion);
        Assert.Equal(PageSize.Letter, options.PageSize);
        Assert.True(options.IncludeTrends);
        Assert.True(options.IncludeDetailedResults);
        Assert.Equal("Security Team", options.Author);
        Assert.Equal("Q4 Security Review", options.Subject);
        Assert.Equal("#123456", options.Branding.PrimaryColor);
        Assert.Equal("#654321", options.Branding.SecondaryColor);
        Assert.Equal("Segoe UI", options.Branding.FontFamily);
    }

    [Theory]
    [InlineData(PageSize.A4)]
    [InlineData(PageSize.Letter)]
    [InlineData(PageSize.Legal)]
    public void PageSize_AllValuesAreValid(PageSize pageSize)
    {
        var options = new PdfReportOptions { PageSize = pageSize };
        
        Assert.Equal(pageSize, options.PageSize);
    }
}
