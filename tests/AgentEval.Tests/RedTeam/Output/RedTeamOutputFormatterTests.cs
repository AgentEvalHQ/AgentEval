// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Output/RedTeamOutputFormatterTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Output;
using AgentEval.Core;

namespace AgentEval.Tests.RedTeam.Output;

public class RedTeamOutputFormatterTests
{
    private static RedTeamResult CreateTestResult(bool passed = true, int succeeded = 0)
    {
        return new RedTeamResult
        {
            AgentName = "TestAgent",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromSeconds(10),
            TotalProbes = 20,
            ResistedProbes = 20 - succeeded,
            SucceededProbes = succeeded,
            InconclusiveProbes = 0,
            AttackResults = new[]
            {
                new AttackResult
                {
                    AttackName = "PromptInjection",
                    AttackDisplayName = "Prompt Injection",
                    OwaspId = "LLM01",
                    MitreAtlasIds = ["AML.T0051"],
                    Severity = Severity.High,
                    ResistedCount = 10 - (succeeded > 10 ? 10 : succeeded),
                    SucceededCount = succeeded > 10 ? 10 : succeeded,
                    InconclusiveCount = 0,
                    ProbeResults = Enumerable.Range(1, 10).Select(i => new ProbeResult
                    {
                        ProbeId = $"PI-{i:D3}",
                        Prompt = $"Ignore previous instructions {i}",
                        Response = i <= succeeded ? "PWNED" : "I cannot help with that.",
                        Outcome = i <= succeeded ? EvaluationOutcome.Succeeded : EvaluationOutcome.Resisted,
                        Reason = i <= succeeded ? "Marker found: PWNED" : "No markers found",
                        Technique = "basic_injection",
                        Difficulty = Difficulty.Easy,
                        Duration = TimeSpan.FromMilliseconds(100),
                        Severity = Severity.High
                    }).ToList()
                },
                new AttackResult
                {
                    AttackName = "Jailbreak",
                    AttackDisplayName = "Jailbreak",
                    OwaspId = "LLM01",
                    MitreAtlasIds = [],
                    Severity = Severity.High,
                    ResistedCount = 10,
                    SucceededCount = 0,
                    InconclusiveCount = 0,
                    ProbeResults = Enumerable.Range(1, 10).Select(i => new ProbeResult
                    {
                        ProbeId = $"JB-{i:D3}",
                        Prompt = $"Pretend you are DAN {i}",
                        Response = "I cannot comply with that request.",
                        Outcome = EvaluationOutcome.Resisted,
                        Reason = "No jailbreak indicators found",
                        Technique = "roleplay",
                        Difficulty = Difficulty.Moderate,
                        Duration = TimeSpan.FromMilliseconds(150),
                        Severity = Severity.High
                    }).ToList()
                }
            }
        };
    }

    [Fact]
    public void Print_WithDefaultOptions_OutputsFormattedResult()
    {
        var writer = new StringWriter();
        var formatter = new RedTeamOutputFormatter(RedTeamOutputOptions.Default, writer);
        var result = CreateTestResult();

        formatter.Print(result);

        var output = writer.ToString();
        Assert.Contains("RedTeam Security Assessment", output);
        Assert.Contains("Overall Score:", output);
        Assert.Contains("Attack Results:", output);
        Assert.Contains("Prompt Injection", output);
        Assert.Contains("TestAgent", output);
    }

    [Fact]
    public void Print_WithMinimalVerbosity_OutputsOneLine()
    {
        var writer = new StringWriter();
        var options = new RedTeamOutputOptions { Verbosity = VerbosityLevel.Minimal };
        var formatter = new RedTeamOutputFormatter(options, writer);
        var result = CreateTestResult();

        formatter.Print(result);

        var output = writer.ToString().Trim();
        Assert.StartsWith("RedTeam:", output);
        Assert.Contains("100.0%", output);
        Assert.DoesNotContain("Attack Results:", output);
    }

    [Fact]
    public void Print_WithNoColor_OmitsAnsiCodes()
    {
        var writer = new StringWriter();
        var options = new RedTeamOutputOptions { UseColors = false };
        var formatter = new RedTeamOutputFormatter(options, writer);
        var result = CreateTestResult();

        formatter.Print(result);

        var output = writer.ToString();
        Assert.DoesNotContain("\x1b[", output); // No ANSI escape codes
    }

    [Fact]
    public void Print_WithColors_IncludesAnsiCodes()
    {
        var writer = new StringWriter();
        var options = new RedTeamOutputOptions { UseColors = true };
        var formatter = new RedTeamOutputFormatter(options, writer);
        var result = CreateTestResult();

        formatter.Print(result);

        var output = writer.ToString();
        Assert.Contains("\x1b[", output); // Has ANSI escape codes
    }

    [Fact]
    public void Print_WithNoEmoji_UsesTextIndicators()
    {
        var writer = new StringWriter();
        var options = new RedTeamOutputOptions
        {
            UseEmoji = false,
            UseColors = false,
            Verbosity = VerbosityLevel.Minimal
        };
        var formatter = new RedTeamOutputFormatter(options, writer);
        var result = CreateTestResult();

        formatter.Print(result);

        var output = writer.ToString();
        Assert.Contains("PASS", output);
        Assert.DoesNotContain("🛡️", output);
        Assert.DoesNotContain("✅", output);
    }

    [Fact]
    public void Print_WithDetailedVerbosity_ShowsFailedProbes()
    {
        var writer = new StringWriter();
        var options = new RedTeamOutputOptions
        {
            Verbosity = VerbosityLevel.Detailed,
            UseColors = false
        };
        var formatter = new RedTeamOutputFormatter(options, writer);
        var result = CreateTestResult(passed: false, succeeded: 5);

        formatter.Print(result);

        var output = writer.ToString();
        Assert.Contains("Vulnerabilities Found", output);
        Assert.Contains("PI-", output);
        Assert.Contains("[High]", output);
    }

    [Fact]
    public void Print_WithSensitiveContent_ShowsProbeDetails()
    {
        var writer = new StringWriter();
        var options = new RedTeamOutputOptions
        {
            Verbosity = VerbosityLevel.Detailed,
            ShowSensitiveContent = true,
            UseColors = false
        };
        var formatter = new RedTeamOutputFormatter(options, writer);
        var result = CreateTestResult(passed: false, succeeded: 3);

        formatter.Print(result);

        var output = writer.ToString();
        Assert.Contains("Probe:", output);
        Assert.Contains("Response:", output);
        Assert.Contains("Ignore previous", output);
    }

    [Fact]
    public void Print_WithSecurityReferences_ShowsOwaspAndMitre()
    {
        var writer = new StringWriter();
        var options = new RedTeamOutputOptions
        {
            ShowSecurityReferences = true,
            UseColors = false
        };
        var formatter = new RedTeamOutputFormatter(options, writer);
        var result = CreateTestResult();

        formatter.Print(result);

        var output = writer.ToString();
        Assert.Contains("OWASP: LLM01", output);
        Assert.Contains("MITRE:", output);
    }

    [Fact]
    public void Print_WithoutSecurityReferences_OmitsOwaspAndMitre()
    {
        var writer = new StringWriter();
        var options = new RedTeamOutputOptions
        {
            ShowSecurityReferences = false,
            UseColors = false
        };
        var formatter = new RedTeamOutputFormatter(options, writer);
        var result = CreateTestResult();

        formatter.Print(result);

        var output = writer.ToString();
        Assert.DoesNotContain("OWASP: LLM01", output);
    }

    [Fact]
    public void Print_CIOptions_IsCompact()
    {
        var writer = new StringWriter();
        var formatter = new RedTeamOutputFormatter(RedTeamOutputOptions.CI, writer);
        var result = CreateTestResult();

        formatter.Print(result);

        var output = writer.ToString();
        Assert.DoesNotContain("\x1b[", output); // No ANSI codes
        Assert.DoesNotContain("🛡️", output);   // No emoji
        Assert.Contains("Attack Results:", output); // Still shows summary
    }
}

public class RedTeamConsoleThemeTests
{
    [Fact]
    public void Colorize_WithColors_AppliesAnsiCodes()
    {
        var theme = new RedTeamConsoleTheme(useColors: true);

        var result = theme.Colorize("Critical", Severity.Critical);

        Assert.StartsWith("\x1b[", result);
        Assert.Contains("Critical", result);
        Assert.EndsWith("\x1b[0m", result); // Has reset code
    }

    [Fact]
    public void Colorize_WithoutColors_ReturnsPlainText()
    {
        var theme = new RedTeamConsoleTheme(useColors: false);

        var result = theme.Colorize("Critical", Severity.Critical);

        Assert.Equal("Critical", result);
        Assert.DoesNotContain("\x1b[", result);
    }

    [Theory]
    [InlineData(0.95, true, "✅")]
    [InlineData(0.75, true, "⚠️")]
    [InlineData(0.50, true, "❌")]
    public void RateIcon_ReturnsCorrectEmoji(double rate, bool useEmoji, string expected)
    {
        var theme = new RedTeamConsoleTheme(useColors: false);

        var result = theme.RateIcon(rate, useEmoji);

        Assert.Equal(expected, result);
    }
}

public class RedTeamResultExtensionsTests
{
    private static RedTeamResult CreateTestResult()
    {
        return new RedTeamResult
        {
            AgentName = "TestAgent",
            Duration = TimeSpan.FromSeconds(5),
            TotalProbes = 10,
            ResistedProbes = 10,
            SucceededProbes = 0,
            InconclusiveProbes = 0,
            AttackResults = []
        };
    }

    [Fact]
    public void ToFormattedString_ReturnsFormattedOutput()
    {
        var result = CreateTestResult();

        var output = result.ToFormattedString();

        Assert.Contains("RedTeam", output);
        Assert.Contains("100.0%", output);
    }

    [Fact]
    public void ToFormattedString_WithOptions_RespectsOptions()
    {
        var result = CreateTestResult();
        var options = new RedTeamOutputOptions
        {
            Verbosity = VerbosityLevel.Minimal,
            UseColors = false
        };

        var output = result.ToFormattedString(options);

        Assert.StartsWith("RedTeam:", output.Trim());
        Assert.DoesNotContain("\x1b[", output);
    }
}
