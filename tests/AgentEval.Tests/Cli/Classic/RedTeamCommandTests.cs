// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Ported from AgentEvalHQ/AgentEval.Cli/tests/AgentEval.Cli.Tests/RedTeamCommandTests.cs
// during the v1.1 CLI consolidation. Only the namespace was edited.

using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Baseline;
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
    public void Create_Has42Options()
    {
        // 16 base + Wave E (save-baseline, baseline, fail-on) = 19
        // + Wave C′ (attacker, attacker-model) = 21
        // + baseline metadata (baseline-version, baseline-note) = 23
        // + per-role API keys (judge-api-key, attacker-api-key) = 25
        // + real-surface harness (sut-tier, system-prompt-canary) = 27
        // + dataset import (import-probes) = 28
        // + --explain rationale = 29
        // + --package-registry (LLM03 live registry) = 30
        // + --calibration (garak-inspired relative z-score scoring) = 31
        // + benchmark packs (--pack, --accept-license) = 33
        // + import dispatch fields (import-prompt-field, import-id-column) = 35 (M10)
        // + throttle/timeout knobs (delay, parallelism, timeout-per-probe, max-turn-timeout) = 39 (L21)
        // + judge grading mode/rubric/timeout (--judge-mode, --judge-rubric, --judge-timeout) = 42 (ADR-021 B.1)
        var command = RedTeamCommand.Create();
        Assert.Equal(42, command.Options.Count);
    }

    [Theory] // ADR-021: --judge-rubric maps strict | lenient | evidence-anchored (case- and alias-tolerant).
    [InlineData("strict", JudgeRubric.Strict)]
    [InlineData("lenient", JudgeRubric.Lenient)]
    [InlineData("evidence-anchored", JudgeRubric.EvidenceAnchored)]
    [InlineData("anchored", JudgeRubric.EvidenceAnchored)]
    [InlineData("EVIDENCE-ANCHORED", JudgeRubric.EvidenceAnchored)]
    [InlineData(null, JudgeRubric.Strict)]
    public void ParseJudgeRubric_MapsAllRubrics(string? input, JudgeRubric expected)
        => Assert.Equal(expected, RedTeamCommand.ParseJudgeRubric(input));

    [Fact]
    public void ParseJudgeRubric_Unknown_Throws()
        => Assert.Throws<ArgumentException>(() => RedTeamCommand.ParseJudgeRubric("bogus"));

    [Fact] // The evidence-anchored rubric supplies a quote-grounded custom prompt; strict keeps the default (none).
    public void GraderFactory_EvidenceAnchored_SuppliesQuoteGroundedPrompt()
    {
        Assert.Null(GraderFactory.OptionsFor(JudgeRubric.Strict).CustomSystemPrompt);
        var anchored = GraderFactory.OptionsFor(JudgeRubric.EvidenceAnchored).CustomSystemPrompt;
        Assert.NotNull(anchored);
        Assert.Contains("verbatim quote", anchored!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EVIDENCE:", anchored, StringComparison.Ordinal);
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
    [InlineData("save-baseline")]   // Wave E
    [InlineData("baseline")]        // Wave E
    [InlineData("baseline-version")] // baseline metadata
    [InlineData("baseline-note")]    // baseline metadata
    [InlineData("fail-on")]         // Wave E
    [InlineData("attacker")]        // Wave C′
    [InlineData("attacker-model")]  // Wave C′
    [InlineData("judge-api-key")]   // per-role keys
    [InlineData("attacker-api-key")] // per-role keys
    [InlineData("sut-tier")]        // real-surface harness
    [InlineData("system-prompt-canary")] // real-surface harness
    [InlineData("import-probes")]   // dataset import (Wave-F seam)
    [InlineData("import-prompt-field")] // CSV/JSON prompt-field dispatch (M10)
    [InlineData("import-id-column")]    // optional upstream id (M10)
    [InlineData("delay")]               // throttle (L21)
    [InlineData("parallelism")]         // within-attack concurrency (L21)
    [InlineData("timeout-per-probe")]   // per-probe timeout (L21)
    [InlineData("max-turn-timeout")]    // per-turn timeout (L21)
    [InlineData("explain")]         // --explain LLM rationale
    [InlineData("package-registry")] // LLM03 live registry oracle
    [InlineData("calibration")]     // garak-inspired relative z-score scoring
    [InlineData("pack")]            // benchmark pack downloader (#10)
    [InlineData("accept-license")]  // benchmark pack license gate (#10)
    public void Create_ContainsExpectedOption(string optionName)
    {
        var command = RedTeamCommand.Create();
        Assert.Contains(command.Options,
            o => o.Name.Contains(optionName, StringComparison.OrdinalIgnoreCase));
    }

    [Theory] // M10: --import-probes dispatches the importer by file extension (mirrors PackDownloader.ImporterFor).
    [InlineData(".csv", "csv")]
    [InlineData(".CSV", "csv")]
    [InlineData(".json", "json")]
    [InlineData(".txt", "json")]   // unknown extension → JSON default
    public void SelectImporter_DispatchesByExtension(string extension, string expectedFormat)
        => Assert.Equal(expectedFormat, RedTeamCommand.SelectImporter(extension, null, null).Format);

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

    [Fact]
    public async Task ExecuteAsync_InvalidFailOn_Throws_BeforeAnyScan()
    {
        // --fail-on is validated up front (after endpoint/model checks, before the chat client is built), so a typo
        // fails fast rather than after an expensive scan.
        var opts = new RedTeamOptions
        {
            Endpoint = "http://localhost:11434/v1",
            Model = "gpt-4o",
            Intensity = "moderate",
            Format = "json",
            FailOn = "bogus",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RedTeamCommand.ExecuteAsync(opts, CancellationToken.None));
        Assert.Contains("--fail-on", ex.Message);
    }

    [Fact] // Jun14v2-L4: a zero / over-ceiling timeout is a static config error — ExecuteAsync must fail fast in step 1
    // (before the --import-probes read and --pack download), not after BuildScanOptions runs post-import.
    public async Task ExecuteAsync_ZeroTimeoutPerProbe_FailsFast_BeforeAnyImport()
    {
        var opts = new RedTeamOptions
        {
            Endpoint = "http://localhost:11434/v1",
            Model = "gpt-4o",
            Intensity = "moderate",
            Format = "json",
            ImportProbes = new FileInfo("/no/such/file/should/not/be/read.json"),   // would throw if reached
            TimeoutPerProbeSeconds = 0,
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => RedTeamCommand.ExecuteAsync(opts, CancellationToken.None));
        Assert.Contains("--timeout-per-probe", ex.Message);
    }

    [Theory] // Direct contract on the hoisted validator.
    [InlineData(0, 1, 0)]        // zero per-probe
    [InlineData(100000, 1, 0)]   // over the 1-day ceiling
    [InlineData(1, 1, -5)]       // negative delay
    public void ValidateTimeouts_RejectsOutOfRangeValues(double perProbe, double perTurn, double delay)
    {
        var opts = new RedTeamOptions
        {
            Model = "gpt-4o", Intensity = "moderate", Format = "json",
            TimeoutPerProbeSeconds = perProbe, TimeoutPerTurnSeconds = perTurn, DelaySeconds = delay,
        };
        Assert.Throws<ArgumentException>(() => RedTeamCommand.ValidateTimeouts(opts));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // WAVE E — CI ON-RAMP: exit-code gate + baseline regression
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    // --fail-on vuln (default): only a Regression triggers code 4; otherwise the absolute vuln gate (passed) decides.
    [InlineData("vuln", null, true, ExitCodes.Success)]
    [InlineData("vuln", null, false, ExitCodes.TestFailure)]
    [InlineData("vuln", RegressionStatus.Stable, true, ExitCodes.Success)]
    [InlineData("vuln", RegressionStatus.Stable, false, ExitCodes.TestFailure)]
    [InlineData("vuln", RegressionStatus.Improved, true, ExitCodes.Success)]
    [InlineData("vuln", RegressionStatus.Improved, false, ExitCodes.TestFailure)]
    // Degraded is "within tolerance" — it must NEVER trigger the regression gate (code 4). It behaves like Stable:
    // the absolute vuln gate still applies under --fail-on vuln, but a Degraded classification alone never fails.
    [InlineData("vuln", RegressionStatus.Degraded, true, ExitCodes.Success)]
    [InlineData("vuln", RegressionStatus.Degraded, false, ExitCodes.TestFailure)]
    [InlineData("vuln", RegressionStatus.Regression, false, ExitCodes.RegressionFailure)]
    [InlineData("vuln", RegressionStatus.Regression, true, ExitCodes.RegressionFailure)]   // regression (4) outranks the vuln gate
    // --fail-on regression: tolerate pre-existing vulns, fail only on a NEW finding (Regression). Degraded/Improved pass.
    [InlineData("regression", RegressionStatus.Stable, false, ExitCodes.Success)]
    [InlineData("regression", RegressionStatus.Improved, false, ExitCodes.Success)]
    [InlineData("regression", RegressionStatus.Degraded, false, ExitCodes.Success)]
    [InlineData("regression", RegressionStatus.Regression, false, ExitCodes.RegressionFailure)]
    [InlineData("regression", null, false, ExitCodes.Success)]
    // --fail-on never: always pass.
    [InlineData("never", RegressionStatus.Regression, false, ExitCodes.Success)]
    [InlineData("never", RegressionStatus.Degraded, false, ExitCodes.Success)]
    [InlineData("never", null, false, ExitCodes.Success)]
    public void ComputeExitCode_GatesCorrectly(string failOn, RegressionStatus? regression, bool passed, int expected)
        => Assert.Equal(expected, RedTeamCommand.ComputeExitCode(failOn, regression, passed));

    [Fact]
    public async Task ExecuteAsync_MissingBaselineFile_Throws_BeforeAnyScan()
    {
        // A non-existent --baseline path must fail fast (before an expensive scan), with a clear message.
        var opts = new RedTeamOptions
        {
            Endpoint = "http://localhost:11434/v1",
            Model = "gpt-4o",
            Intensity = "moderate",
            Format = "json",
            Baseline = new FileInfo(Path.Combine(Path.GetTempPath(), $"nonexistent-baseline-{Guid.NewGuid():N}.json")),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RedTeamCommand.ExecuteAsync(opts, CancellationToken.None));
        Assert.Contains("Baseline file not found", ex.Message);
    }

    [Theory]
    [InlineData("PAIR")]
    [InlineData("TAP")]
    public async Task ExecuteAsync_AttackerRequiredAttack_WithoutAttacker_Throws(string attack)
    {
        // PAIR/TAP are attacker-LLM driven — without --attacker they must fail fast with a clear message (before scan).
        var opts = new RedTeamOptions
        {
            Endpoint = "http://localhost:11434/v1",
            Model = "gpt-4o",
            Intensity = "quick",
            Format = "json",
            Attacks = attack,
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RedTeamCommand.ExecuteAsync(opts, CancellationToken.None));
        Assert.Contains("requires an attacker", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Baseline_RoundTrip_StableRunPasses_NewVulnRegresses()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            // Capture a clean baseline (no vulnerabilities).
            await RedTeamBaseline.FromResult(MakeResult(), version: "1.0").SaveAsync(tmp);
            var baseline = await RedTeamBaseline.LoadAsync(tmp);

            // An identical clean run is Stable → exit 0 even under --fail-on regression.
            var stable = new RedTeamBaselineComparer().Compare(MakeResult(), baseline);
            Assert.Equal(RegressionStatus.Stable, stable.Status);
            Assert.Equal(ExitCodes.Success, RedTeamCommand.ComputeExitCode("regression", stable.Status, passed: true));

            // A run that introduces a NEW vulnerability is a Regression → exit 4.
            var regressed = new RedTeamBaselineComparer().Compare(MakeResult("PI-002"), baseline);
            Assert.Equal(RegressionStatus.Regression, regressed.Status);
            Assert.Single(regressed.NewVulnerabilities);
            Assert.Equal(ExitCodes.RegressionFailure, RedTeamCommand.ComputeExitCode("regression", regressed.Status, passed: false));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // Builds a 4-probe PromptInjection result; the named probe IDs are the ones that "succeeded" (vulnerabilities).
    private static RedTeamResult MakeResult(params string[] succeededProbeIds)
    {
        const int total = 4;
        var probes = Enumerable.Range(1, total).Select(i =>
        {
            var id = $"PI-{i:D3}";
            var failed = succeededProbeIds.Contains(id);
            return new ProbeResult
            {
                ProbeId = id, Prompt = "x", Response = failed ? "PWNED" : "I cannot help with that.",
                Reason = failed ? "complied" : "refused",
                Outcome = failed ? EvaluationOutcome.Succeeded : EvaluationOutcome.Resisted,
                Severity = Severity.High, Technique = "instruction_override",
            };
        }).ToList();

        var succeeded = succeededProbeIds.Length;
        return new RedTeamResult
        {
            AgentName = "agent",
            TotalProbes = total, ResistedProbes = total - succeeded, SucceededProbes = succeeded, InconclusiveProbes = 0,
            AttackResults =
            [
                new AttackResult
                {
                    AttackName = "PromptInjection", OwaspId = "LLM01", ProbeResults = probes,
                    ResistedCount = total - succeeded, SucceededCount = succeeded,
                }
            ],
        };
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

    // ═══════════════════════════════════════════════════════════════════════════
    // REAL-SURFACE HARNESS: --sut-tier / --system-prompt-canary (items 1+2)
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("text", SutTier.TextOnly)]
    [InlineData("verbal", SutTier.TextOnly)]
    [InlineData("function-calling", SutTier.FunctionCalling)]
    [InlineData("tier1", SutTier.FunctionCalling)]
    [InlineData("instrumented", SutTier.InstrumentedAgent)]
    [InlineData("TIER2", SutTier.InstrumentedAgent)]
    public void ParseSutTier_MapsKnownValues(string s, SutTier expected)
        => Assert.Equal(expected, RedTeamCommand.ParseSutTier(s));

    [Fact]
    public void ParseSutTier_Unknown_Throws()
        => Assert.Throws<ArgumentException>(() => RedTeamCommand.ParseSutTier("bogus"));

    [Fact]
    public void EmbedSystemPromptCanary_NoBasePrompt_CreatesPromptContainingCanary()
    {
        var sp = RedTeamCommand.EmbedSystemPromptCanary(null, "CANARY-xyz123");
        Assert.Contains("CANARY-xyz123", sp);
    }

    [Fact]
    public void EmbedSystemPromptCanary_PreservesBasePromptAndAddsCanary()
    {
        var sp = RedTeamCommand.EmbedSystemPromptCanary("You are a helpful bot.", "CANARY-xyz123");
        Assert.Contains("You are a helpful bot.", sp);
        Assert.Contains("CANARY-xyz123", sp);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // NIST AI RMF CLI ON-RAMP: --format nist / nist-md (item 3)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TryRenderComplianceReport_Nist_RendersNistJson()
    {
        Assert.True(RedTeamCommand.TryRenderComplianceReport("nist", MakeResult("PI-001"), out var content));
        Assert.Contains("NIST AI RMF", content);
    }

    [Fact]
    public void TryRenderComplianceReport_NistMd_RendersMarkdown()
    {
        Assert.True(RedTeamCommand.TryRenderComplianceReport("nist-md", MakeResult(), out var content));
        Assert.Contains("NIST AI RMF", content);
        Assert.Contains("#", content); // a markdown heading
    }

    [Theory]
    [InlineData("json")]
    [InlineData("sarif")]
    [InlineData("markdown")]
    public void TryRenderComplianceReport_StandardFormat_ReturnsFalse(string format)
        => Assert.False(RedTeamCommand.TryRenderComplianceReport(format, MakeResult(), out _));

    // ═══════════════════════════════════════════════════════════════════════════
    // ScanOptions seam: --verbose must not drop the attacker/judge client (item 6)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildScanOptions_VerboseWithAttacker_KeepsAttackerClient()
    {
        Microsoft.Extensions.AI.IChatClient attacker = new TestHelpers.MockStreamingChatClient([], "unused");
        var opts = new RedTeamOptions { Intensity = "quick", Format = "json", Verbose = true };

        var scan = RedTeamCommand.BuildScanOptions(opts, attacks: null, Intensity.Quick, judgeClient: null, attackerClient: attacker);

        Assert.Same(attacker, scan.AttackerClient);   // the --verbose path must not drop it
        Assert.NotNull(scan.OnProgress);              // verbose still builds the progress callback
    }

    [Fact]
    public void BuildScanOptions_Explain_IsGatedOnJudgePresence()
    {
        var judge = new TestHelpers.MockStreamingChatClient([], "unused");
        var withJudge = RedTeamCommand.BuildScanOptions(
            new RedTeamOptions { Intensity = "quick", Format = "json", Explain = true }, null, Intensity.Quick, judge, null);
        Assert.True(withJudge.ExplainFindings);

        var noJudge = RedTeamCommand.BuildScanOptions(
            new RedTeamOptions { Intensity = "quick", Format = "json", Explain = true }, null, Intensity.Quick, judgeClient: null, attackerClient: null);
        Assert.False(noJudge.ExplainFindings);   // --explain is a no-op without --judge
    }

    [Fact]
    public void BuildScanOptions_Quiet_SuppressesProgressButKeepsJudge()
    {
        Microsoft.Extensions.AI.IChatClient judge = new TestHelpers.MockStreamingChatClient([], "unused");
        var opts = new RedTeamOptions { Intensity = "quick", Format = "json", Verbose = true, Quiet = true };

        var scan = RedTeamCommand.BuildScanOptions(opts, attacks: null, Intensity.Quick, judgeClient: judge, attackerClient: null);

        Assert.Same(judge, scan.JudgeClient);
        Assert.Null(scan.OnProgress);                 // --quiet suppresses progress even under --verbose
    }

    // L21: throttle/timeout knobs thread through the BuildScanOptions seam.
    [Fact]
    public void BuildScanOptions_ThreadsThrottleKnobs()
    {
        var opts = new RedTeamOptions
        {
            Intensity = "quick", Format = "json",
            DelaySeconds = 2, Parallelism = 4, TimeoutPerProbeSeconds = 10,
        };

        var scan = RedTeamCommand.BuildScanOptions(opts, attacks: null, Intensity.Quick, judgeClient: null, attackerClient: null);

        Assert.Equal(TimeSpan.FromSeconds(2), scan.DelayBetweenProbes);
        Assert.Equal(4, scan.Parallelism);
        Assert.Equal(TimeSpan.FromSeconds(10), scan.TimeoutPerProbe);
    }

    [Fact]
    public void BuildScanOptions_TimeoutPerTurnDefaultsToProbe_WhenZero()
    {
        var opts = new RedTeamOptions { Intensity = "quick", Format = "json", TimeoutPerProbeSeconds = 15, TimeoutPerTurnSeconds = 0 };

        var scan = RedTeamCommand.BuildScanOptions(opts, attacks: null, Intensity.Quick, null, null);

        Assert.Equal(scan.TimeoutPerProbe, scan.TimeoutPerTurn);
    }

    [Fact]
    public void BuildScanOptions_NegativeThrottle_Throws()
        => Assert.Throws<ArgumentException>(() => RedTeamCommand.BuildScanOptions(
            new RedTeamOptions { Intensity = "quick", Format = "json", DelaySeconds = -1 }, null, Intensity.Quick, null, null));

    [Fact] // Jun14-M12: a 0s per-probe timeout would silently zero a whole scan's coverage — reject it.
    public void BuildScanOptions_ZeroTimeoutPerProbe_Throws()
        => Assert.Throws<ArgumentException>(() => RedTeamCommand.BuildScanOptions(
            new RedTeamOptions { Intensity = "quick", Format = "json", TimeoutPerProbeSeconds = 0 }, null, Intensity.Quick, null, null));

    [Fact] // Jun14-L11: an extreme timeout overflows CancelAfter / tick math mid-scan — fail fast at validation.
    public void BuildScanOptions_ExtremeTimeout_Throws()
        => Assert.Throws<ArgumentException>(() => RedTeamCommand.BuildScanOptions(
            new RedTeamOptions { Intensity = "quick", Format = "json", TimeoutPerProbeSeconds = 5_000_000 }, null, Intensity.Quick, null, null));

    // L22: an unknown --package-registry value fails fast in step 1, before any import/download.
    [Fact]
    public async Task ExecuteAsync_InvalidPackageRegistry_Throws_BeforeAnyScan()
    {
        var opts = new RedTeamOptions
        {
            Endpoint = "http://localhost:11434/v1",
            Model = "gpt-4o",
            Intensity = "moderate",
            Format = "json",
            PackageRegistry = "pypi",
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => RedTeamCommand.ExecuteAsync(opts, CancellationToken.None));
        Assert.Contains("--package-registry", ex.Message);
    }
}
