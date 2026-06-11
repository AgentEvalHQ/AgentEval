// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Ported from AgentEvalHQ/AgentEval.Cli/tests/AgentEval.Cli.Tests/RedTeamCommandTests.cs
// during the v1.1 CLI consolidation. Only the namespace was edited.

using AgentEval.Cli.Commands;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting;
using Xunit;

namespace AgentEval.Tests.Cli.Classic;

/// <summary>
/// Tests for the 'agenteval redteam' command (Item 6).
/// </summary>
public class RedTeamCommandTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // COMMAND STRUCTURE
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        var command = RedTeamCommand.Create();
        Assert.Equal("redteam", command.Name);
    }

    [Fact]
    public void Create_HasDescription()
    {
        var command = RedTeamCommand.Create();
        Assert.False(string.IsNullOrWhiteSpace(command.Description));
        Assert.Contains("red team", command.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_Has16Options()
    {
        // endpoint, azure, model, deployment-name, api-key, system-prompt, attacks, intensity,
        // fail-fast, max-probes, judge, judge-model, format, output, verbose, quiet = 16
        var command = RedTeamCommand.Create();
        Assert.Equal(16, command.Options.Count);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("deployment-name")]
    [InlineData("attack")]
    [InlineData("intensity")]
    [InlineData("format")]
    [InlineData("endpoint")]
    [InlineData("azure")]
    [InlineData("api-key")]
    [InlineData("system-prompt")]
    [InlineData("fail")]
    [InlineData("max-probes")]
    [InlineData("judge")]
    [InlineData("verbose")]
    [InlineData("quiet")]
    public void Create_ContainsExpectedOption(string optionName)
    {
        var command = RedTeamCommand.Create();
        Assert.Contains(command.Options,
            o => o.Name.Contains(optionName, StringComparison.OrdinalIgnoreCase));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // VALIDATION
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_NoEndpoint_Throws()
    {
        var opts = new RedTeamOptions
        {
            Model = "gpt-4o",
            Intensity = "moderate",
            Format = "json",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RedTeamCommand.ExecuteAsync(opts, CancellationToken.None));
        Assert.Contains("--endpoint", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_NoEndpointAndNoAzure_Throws()
    {
        var opts = new RedTeamOptions
        {
            Azure = false,
            Model = "gpt-4o",
            Intensity = "moderate",
            Format = "json",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RedTeamCommand.ExecuteAsync(opts, CancellationToken.None));
        Assert.Contains("--endpoint", ex.Message);
        Assert.Contains("--azure", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_AzureWithoutDeploymentName_Throws()
    {
        var opts = new RedTeamOptions
        {
            Azure = true,
            Endpoint = "https://test.openai.azure.com/",
            Intensity = "moderate",
            Format = "json",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RedTeamCommand.ExecuteAsync(opts, CancellationToken.None));
        Assert.Contains("--deployment-name", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_NonAzureWithoutModel_Throws()
    {
        var opts = new RedTeamOptions
        {
            Endpoint = "http://localhost:11434/v1",
            Intensity = "moderate",
            Format = "json",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RedTeamCommand.ExecuteAsync(opts, CancellationToken.None));
        Assert.Contains("--model", ex.Message);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EXPORTER RESOLUTION
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("json", typeof(JsonReportExporter))]
    [InlineData("sarif", typeof(SarifReportExporter))]
    [InlineData("markdown", typeof(MarkdownReportExporter))]
    [InlineData("md", typeof(MarkdownReportExporter))]
    [InlineData("junit", typeof(JUnitReportExporter))]
    [InlineData("xml", typeof(JUnitReportExporter))]
    public void ResolveExporter_ReturnsCorrectType(string format, Type expectedType)
    {
        var exporter = RedTeamCommand.ResolveExporter(format);
        Assert.IsType(expectedType, exporter);
    }

    [Theory]
    [InlineData("JSON")]
    [InlineData("Sarif")]
    [InlineData("MARKDOWN")]
    [InlineData("Md")]
    [InlineData("JUnit")]
    [InlineData("XML")]
    public void ResolveExporter_CaseInsensitive(string format)
    {
        var exporter = RedTeamCommand.ResolveExporter(format);
        Assert.NotNull(exporter);
    }

    [Fact]
    public void ResolveExporter_UnknownFormat_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => RedTeamCommand.ResolveExporter("invalid-format"));
        Assert.Contains("invalid-format", ex.Message);
        Assert.Contains("Valid", ex.Message);
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("trx")]
    [InlineData("html")]
    public void ResolveExporter_UnsupportedFormats_Throw(string format)
    {
        Assert.Throws<ArgumentException>(() => RedTeamCommand.ResolveExporter(format));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ATTACK RESOLUTION (via Attack.ByName)
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("PromptInjection")]
    [InlineData("Jailbreak")]
    [InlineData("PIILeakage")]
    [InlineData("SystemPromptExtraction")]
    [InlineData("IndirectInjection")]
    [InlineData("InferenceAPIAbuse")]
    [InlineData("ExcessiveAgency")]
    [InlineData("InsecureOutput")]
    [InlineData("EncodingEvasion")]
    public void Attack_ByName_AllNineTypes_Resolve(string name)
    {
        var attack = Attack.ByName(name);
        Assert.NotNull(attack);
        Assert.Equal(name, attack.Name);
    }

    [Fact]
    public void Attack_All_Returns13Types()
    {
        Assert.Equal(13, Attack.All.Count);   // Wave D: 10/10 OWASP
    }

    [Fact]
    public void Attack_ByName_UnknownAttack_ReturnsNull()
    {
        var attack = Attack.ByName("UnknownAttack");
        Assert.Null(attack);
    }

    [Fact]
    public void Attack_AvailableNames_Has9OrMore()
    {
        Assert.True(Attack.AvailableNames.Count >= 9,
            $"Expected at least 9 available attack names, got {Attack.AvailableNames.Count}");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // INTENSITY RESOLUTION (mirroring ExecuteAsync logic)
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("quick", Intensity.Quick)]
    [InlineData("moderate", Intensity.Moderate)]
    [InlineData("comprehensive", Intensity.Comprehensive)]
    public void Intensity_StringResolution_Succeeds(string input, Intensity expected)
    {
        var result = input.ToLowerInvariant() switch
        {
            "quick" => Intensity.Quick,
            "moderate" => Intensity.Moderate,
            "comprehensive" => Intensity.Comprehensive,
            _ => throw new ArgumentException($"Unknown intensity: '{input}'"),
        };
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Quick")]
    [InlineData("MODERATE")]
    [InlineData("Comprehensive")]
    public void Intensity_CaseInsensitive_Succeeds(string input)
    {
        var result = input.ToLowerInvariant() switch
        {
            "quick" => Intensity.Quick,
            "moderate" => Intensity.Moderate,
            "comprehensive" => Intensity.Comprehensive,
            _ => throw new ArgumentException($"Unknown intensity: '{input}'"),
        };
        Assert.True(Enum.IsDefined(result), $"Expected valid Intensity enum for '{input}'");
    }

    [Theory]
    [InlineData("fast")]
    [InlineData("extreme")]
    [InlineData("normal")]
    public void Intensity_InvalidValues_Throw(string input)
    {
        Assert.Throws<ArgumentException>(() => input.ToLowerInvariant() switch
        {
            "quick" => Intensity.Quick,
            "moderate" => Intensity.Moderate,
            "comprehensive" => Intensity.Comprehensive,
            _ => throw new ArgumentException($"Unknown intensity: '{input}'"),
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // OPTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void RedTeamOptions_DefaultValues()
    {
        var opts = new RedTeamOptions
        {
            Intensity = "moderate",
            Format = "markdown",
        };

        Assert.False(opts.Azure);
        Assert.False(opts.FailFast);
        Assert.Equal(0, opts.MaxProbes);
        Assert.Null(opts.Model);
        Assert.Null(opts.DeploymentName);
        Assert.Null(opts.Attacks);
        Assert.Null(opts.SystemPrompt);
        Assert.Null(opts.Endpoint);
        Assert.Null(opts.ApiKey);
        Assert.Null(opts.JudgeEndpoint);
        Assert.Null(opts.JudgeModel);
        Assert.Null(opts.Output);
        Assert.False(opts.Verbose);
        Assert.False(opts.Quiet);
    }

    [Fact]
    public void RedTeamOptions_AllFieldsSettable()
    {
        var opts = new RedTeamOptions
        {
            Model = "gpt-4o",
            Intensity = "comprehensive",
            Format = "sarif",
            Endpoint = "http://localhost:11434/v1",
            Azure = false,
            ApiKey = "test-key",
            SystemPrompt = "You are a helpful assistant.",
            Attacks = "PromptInjection,Jailbreak",
            FailFast = true,
            MaxProbes = 5,
            JudgeEndpoint = "http://judge:8080",
            JudgeModel = "judge-model",
            Output = new FileInfo("report.sarif"),
            Verbose = true,
            Quiet = false,
        };

        Assert.Equal("gpt-4o", opts.Model);
        Assert.Equal("comprehensive", opts.Intensity);
        Assert.Equal("sarif", opts.Format);
        Assert.Equal("http://localhost:11434/v1", opts.Endpoint);
        Assert.Equal("test-key", opts.ApiKey);
        Assert.Equal("You are a helpful assistant.", opts.SystemPrompt);
        Assert.Equal("PromptInjection,Jailbreak", opts.Attacks);
        Assert.True(opts.FailFast);
        Assert.Equal(5, opts.MaxProbes);
        Assert.Equal("http://judge:8080", opts.JudgeEndpoint);
        Assert.Equal("judge-model", opts.JudgeModel);
        Assert.True(opts.Verbose);
    }

    [Fact]
    public void RedTeamOptions_VerboseAndQuiet_CanBothBeSet()
    {
        // While semantically conflicting, both should be settable;
        // the command logic decides priority (quiet wins)
        var opts = new RedTeamOptions
        {
            Model = "gpt-4o",
            Intensity = "moderate",
            Format = "json",
            Verbose = true,
            Quiet = true,
        };

        Assert.True(opts.Verbose);
        Assert.True(opts.Quiet);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ATTACK PARSING (simulating ExecuteAsync logic)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AttackParsing_CommaSeparated_ResolvesAll()
    {
        var attacksArg = "PromptInjection,Jailbreak,PIILeakage";
        var names = attacksArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var resolved = new List<IAttackType>();
        foreach (var name in names)
        {
            var attack = Attack.ByName(name);
            Assert.NotNull(attack);
            resolved.Add(attack);
        }

        Assert.Equal(3, resolved.Count);
    }

    [Fact]
    public void AttackParsing_WithSpaces_ResolvesCorrectly()
    {
        var attacksArg = " PromptInjection , Jailbreak ";
        var names = attacksArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var resolved = new List<IAttackType>();
        foreach (var name in names)
        {
            var attack = Attack.ByName(name);
            Assert.NotNull(attack);
            resolved.Add(attack);
        }

        Assert.Equal(2, resolved.Count);
    }

    [Fact]
    public void AttackParsing_UnknownName_ThrowsInLoop()
    {
        var attacksArg = "PromptInjection,NonExistentAttack";
        var names = attacksArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Throws<ArgumentException>(() =>
        {
            foreach (var name in names)
            {
                var attack = Attack.ByName(name)
                    ?? throw new ArgumentException(
                        $"Unknown attack type: '{name}'. Available: {string.Join(", ", Attack.AvailableNames)}");
            }
        });
    }

    [Fact]
    public void AttackParsing_SingleAttack_Resolves()
    {
        var attacksArg = "EncodingEvasion";
        var names = attacksArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Single(names);
        var attack = Attack.ByName(names[0]);
        Assert.NotNull(attack);
        Assert.Equal("EncodingEvasion", attack.Name);
    }
}
