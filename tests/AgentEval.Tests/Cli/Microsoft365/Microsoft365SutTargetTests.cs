// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Cli.Microsoft365;
using AgentEval.Core;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using Xunit;

namespace AgentEval.Tests.Cli.Microsoft365;

/// <summary>
/// The <c>--sut microsoft365</c> built-in target under the shared <c>eval</c>/<c>bench</c> seam. Mirrors
/// <c>SutTargetResolverTests</c>' shape: registration, option binding, the safety gates in their documented order (all
/// before any network call), config fail-fast, the resolved name, and the <c>sutOverride</c> seam.
/// </summary>
public class Microsoft365SutTargetTests
{
    // ── registration ──

    [Fact]
    public void BuiltInTargets_IncludesMicrosoft365_AndCopilotStudio()
    {
        var targets = SutTargetResolver.BuiltInTargets();
        Assert.Contains(targets, t => t.Sut == "microsoft365");
        Assert.Contains(targets, t => t.Sut == "copilot-studio");
    }

    [Fact]
    public void BuiltInTargets_MintsAFreshMicrosoft365InstancePerCall()
    {
        // A target memoises its config across Validate/ResolvedName/Build of ONE execution; sharing an instance across
        // two executions would leak it.
        var first = SutTargetResolver.BuiltInTargets().Single(t => t.Sut == "microsoft365");
        var second = SutTargetResolver.BuiltInTargets().Single(t => t.Sut == "microsoft365");
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Microsoft365Target_SupportsEvalAndBench_NotRedteam()
    {
        var target = SutTargetResolver.BuiltInTargets().Single(t => t.Sut == "microsoft365");
        Assert.Contains("eval", target.SupportedVerbs, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("BENCH", target.SupportedVerbs, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("redteam", target.SupportedVerbs, StringComparer.OrdinalIgnoreCase);
    }

    // ── options ──

    [Fact]
    public void AddOptionsTo_RegistersTheSixM365Options_NeverTheConsentFlag()
    {
        var command = new Command("shared");
        new Microsoft365SutTarget(new StringWriter()).AddOptionsTo(command);

        var names = command.Options.Select(o => o.Name).ToList();
        Assert.Equal(
            new[] { "--m365-config", "--m365-max-turns", "--m365-profile", "--m365-capture-raw", "--m365-verbose", "--m365-trace" },
            names);
        Assert.DoesNotContain("--i-understand-live-side-effects", names);
    }

    [Fact]
    public void AddOptionsTo_Eval_RegistersBothLiveTargets_AndOneConsentFlag()
    {
        // Two live targets on one verb: the resolver owns the single consent flag, so no duplicate alias.
        var command = new Command("eval-test");
        var (_, ackOpt, targets) = SutTargetResolver.AddOptionsTo(command, "eval");

        Assert.Contains(targets, t => t.Sut == "microsoft365");
        Assert.Single(command.Options, o => o.Name == "--i-understand-live-side-effects");
        Assert.Contains(command.Options, o => o.Name == "--m365-config");
        Assert.Contains(command.Options, o => o.Name == "--copilotstudio-config");
        Assert.True(command.Parse("--sut microsoft365 --i-understand-live-side-effects").GetValue(ackOpt));
    }

    [Fact]
    public void BindOptions_BindsEveryFlagFromTheCommandLine()
    {
        var command = new Command("eval-test");
        var target = new Microsoft365SutTarget(new StringWriter());
        target.AddOptionsTo(command);

        var parsed = command.Parse("--m365-config cfg.json --m365-max-turns 3 --m365-profile native118 --m365-capture-raw raw --m365-verbose --m365-trace t.jsonl");

        Assert.Empty(parsed.Errors);
        var own = Assert.IsType<Microsoft365SutOptions>(target.BindOptions(parsed));
        Assert.Equal("cfg.json", own.ConfigFile!.Name);
        Assert.Equal(3, own.MaxTurns);
        Assert.Equal("native118", own.Profile);
        Assert.Equal("raw", own.CaptureRaw!.Name);
        Assert.True(own.Verbose);
        Assert.Equal("t.jsonl", own.TraceFile!.Name);
    }

    [Fact]
    public void BindOptions_Defaults_NoCap_PublicProfile_NotVerbose_NoCapture_NoTrace()
    {
        var command = new Command("eval-test");
        var target = new Microsoft365SutTarget(new StringWriter());
        target.AddOptionsTo(command);

        var own = Assert.IsType<Microsoft365SutOptions>(target.BindOptions(command.Parse("")));

        Assert.Null(own.ConfigFile);
        Assert.Equal(0, own.MaxTurns);
        Assert.Equal("public", own.Profile);
        Assert.Null(own.CaptureRaw);
        Assert.False(own.Verbose);
        Assert.Null(own.TraceFile);
    }

    // ── safety gates, in their documented order, before any network call ──

    [Fact]
    public void Validate_WithoutConsent_Refuses_BeforeAnythingElse()
    {
        var target = new Microsoft365SutTarget(new StringWriter());

        // No config and a negative cap too: consent must still be the first refusal.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            target.Validate(Common(consent: false), new Microsoft365SutOptions { ConfigFile = null, MaxTurns = -1 }));

        Assert.Contains("i-understand-live-side-effects", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NON-PROD", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nothing was sent", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_WithoutConfig_Refuses_BeforeTheCapCheck()
    {
        var target = new Microsoft365SutTarget(new StringWriter());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            target.Validate(Common(consent: true), new Microsoft365SutOptions { ConfigFile = null, MaxTurns = -1 }));

        Assert.Contains("m365-config", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_NoOwnOptionsBound_RefusesForTheMissingConfig()
    {
        var target = new Microsoft365SutTarget(new StringWriter());
        var ex = Assert.Throws<InvalidOperationException>(() => target.Validate(Common(consent: true), own: null));
        Assert.Contains("m365-config", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_NegativeMaxTurns_Refuses_BeforeTheProfileCheck()
    {
        var cfg = WriteConfig(ValidConfigJson);
        try
        {
            var target = new Microsoft365SutTarget(new StringWriter());

            var ex = Assert.Throws<InvalidOperationException>(() =>
                target.Validate(Common(consent: true), new Microsoft365SutOptions { ConfigFile = cfg, MaxTurns = -1, Profile = "bogus" }));

            Assert.Contains("m365-max-turns", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(cfg); }
    }

    [Fact]
    public void Validate_BadProfile_Refuses_BeforeTheConfigIsRead()
    {
        // The config path does not exist: a profile error must win, proving the order profile -> config load.
        var missing = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-m365-missing-" + Guid.NewGuid().ToString("N") + ".json"));
        var target = new Microsoft365SutTarget(new StringWriter());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            target.Validate(Common(consent: true), new Microsoft365SutOptions { ConfigFile = missing, Profile = "bogus" }));

        Assert.Contains("m365-profile", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("public", ex.Message, StringComparison.Ordinal);
        Assert.Contains("native118", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public", "PublicAnswerOnly")]
    [InlineData("PUBLIC", "PublicAnswerOnly")]
    [InlineData(" Public ", "PublicAnswerOnly")]
    [InlineData("native118", "Native118Diagnostics")]
    [InlineData("Native118", "Native118Diagnostics")]
    public void Validate_ProfileIsCaseInsensitiveAndTrimmed(string profile, string expected)
    {
        var cfg = WriteConfig(ValidConfigJson);
        try
        {
            var target = new Microsoft365SutTarget(new StringWriter());
            target.Validate(Common(consent: true), new Microsoft365SutOptions { ConfigFile = cfg, Profile = profile });

            // The memoised profile is the one the run records, not merely "Validate did not throw".
            Assert.Equal(expected, (string?)target.AefRunSettings(null)!["m365Profile"]);
        }
        finally { TryDelete(cfg); }
    }

    [Theory]
    [InlineData("public", DiagnosticProfile.PublicAnswerOnly)]
    [InlineData("NATIVE118", DiagnosticProfile.Native118Diagnostics)]
    public void ParseProfile_MapsTheTwoValues(string value, DiagnosticProfile expected)
    {
        Assert.Equal(expected, Microsoft365CliOptions.ParseProfile(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("native")]
    [InlineData("PublicAnswerOnly")]
    public void ParseProfile_RejectsEverythingElse(string? value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Microsoft365CliOptions.ParseProfile(value));
        Assert.Contains("m365-profile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_MissingConfigFile_FailsFast()
    {
        var missing = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-m365-missing-" + Guid.NewGuid().ToString("N") + ".json"));
        var target = new Microsoft365SutTarget(new StringWriter());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            target.Validate(Common(consent: true), new Microsoft365SutOptions { ConfigFile = missing }));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_LoadsTheConfig_AndFailsFastOnAMissingField()
    {
        var cfg = WriteConfig("{ \"tenantId\": \"tenant-1\" }");   // appClientId missing
        try
        {
            var target = new Microsoft365SutTarget(new StringWriter());

            var ex = Assert.Throws<InvalidOperationException>(() =>
                target.Validate(Common(consent: true), new Microsoft365SutOptions { ConfigFile = cfg }));

            Assert.Contains("appClientId", ex.Message, StringComparison.Ordinal);
        }
        finally { TryDelete(cfg); }
    }

    [Fact]
    public void Validate_LoadsTheConfig_AndFailsFastOnABadConfigProfile()
    {
        var cfg = WriteConfig("{ \"tenantId\": \"tenant-1\", \"appClientId\": \"app-1\", \"diagnosticProfile\": \"Nope\" }");
        try
        {
            var target = new Microsoft365SutTarget(new StringWriter());

            var ex = Assert.Throws<InvalidOperationException>(() =>
                target.Validate(Common(consent: true), new Microsoft365SutOptions { ConfigFile = cfg }));

            Assert.Contains("diagnosticProfile", ex.Message, StringComparison.Ordinal);
        }
        finally { TryDelete(cfg); }
    }

    // ── resolved name ──

    [Fact]
    public void ResolvedName_ValidMinimalConfig_IsTheDefaultAgentDisplayName()
    {
        var cfg = WriteConfig(ValidConfigJson);
        try
        {
            var target = new Microsoft365SutTarget(new StringWriter());
            var own = new Microsoft365SutOptions { ConfigFile = cfg };
            target.Validate(Common(consent: true), own);

            Assert.Equal("Work IQ default agent", target.ResolvedName(Common(consent: true), own));
            Assert.Equal(Microsoft365Config.DefaultDisplayName, target.ResolvedName(Common(consent: true), own));
        }
        finally { TryDelete(cfg); }
    }

    [Fact]
    public void ResolvedName_AgentNameInConfig_IsUsed()
    {
        var cfg = WriteConfig("{ \"tenantId\": \"tenant-1\", \"appClientId\": \"app-1\", \"agentName\": \"HR Helper\" }");
        try
        {
            var target = new Microsoft365SutTarget(new StringWriter());
            var own = new Microsoft365SutOptions { ConfigFile = cfg };
            target.Validate(Common(consent: true), own);

            Assert.Equal("HR Helper", target.ResolvedName(Common(consent: true), own));
        }
        finally { TryDelete(cfg); }
    }

    [Fact]
    public void ResolvedName_WithoutValidate_LoadsTheConfigItself()
    {
        var cfg = WriteConfig(ValidConfigJson);
        try
        {
            var target = new Microsoft365SutTarget(new StringWriter());
            Assert.Equal("Work IQ default agent", target.ResolvedName(Common(consent: true), new Microsoft365SutOptions { ConfigFile = cfg }));
        }
        finally { TryDelete(cfg); }
    }

    // ── resolver dispatch ──

    [Fact]
    public void TryResolve_UnknownSut_NamesBothTargets()
    {
        var opts = new CommonTargetOptions { Sut = "totally-bogus" };

        var ex = Assert.Throws<InvalidOperationException>(() => SutTargetResolver.TryResolve(opts, SutTargetResolver.BuiltInTargets(), sutOverride: null));

        Assert.Contains("copilot-studio", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("microsoft365", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryResolve_Microsoft365_WithoutConsent_Refuses_BeforeAnyBuild()
    {
        var cfg = WriteConfig(ValidConfigJson);
        try
        {
            var opts = new CommonTargetOptions
            {
                Sut = "microsoft365",
                AckLiveSideEffects = false,
                TargetOptions = new Dictionary<string, ISutTargetOptions?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["microsoft365"] = new Microsoft365SutOptions { ConfigFile = cfg },
                },
            };

            var ex = Assert.Throws<InvalidOperationException>(() => SutTargetResolver.TryResolve(opts, SutTargetResolver.BuiltInTargets(), sutOverride: null));
            Assert.Contains("i-understand-live-side-effects", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(cfg); }
    }

    [Fact]
    public void TryResolve_Microsoft365_WithSutOverride_ReturnsOverrideAndTheResolvedName()
    {
        var cfg = WriteConfig(ValidConfigJson);
        try
        {
            var opts = new CommonTargetOptions
            {
                Sut = "Microsoft365",   // case-insensitive --sut value
                AckLiveSideEffects = true,
                TargetOptions = new Dictionary<string, ISutTargetOptions?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["microsoft365"] = new Microsoft365SutOptions { ConfigFile = cfg },
                },
            };
            IEvaluableAgent fake = new FakeEvaluableAgent();

            var (agent, name) = SutTargetResolver.TryResolve(opts, SutTargetResolver.BuiltInTargets(), sutOverride: fake);

            Assert.Same(fake, agent);
            Assert.Equal("Work IQ default agent", name);
        }
        finally { TryDelete(cfg); }
    }

    // ── Build ──

    [Fact]
    public void Build_WithSutOverride_ReturnsItUntouched_PrintsNoBadge_CreatesNoSink()
    {
        var cfg = WriteConfig(ValidConfigJson);
        try
        {
            var err = new StringWriter();
            var target = new Microsoft365SutTarget(err);
            var own = new Microsoft365SutOptions { ConfigFile = cfg, Verbose = true };
            target.Validate(Common(consent: true), own);
            IEvaluableAgent fake = new FakeEvaluableAgent();

            var built = target.Build(Common(consent: true), own, fake);

            Assert.Same(fake, built);
            Assert.Equal("", err.ToString());
            Assert.Null(target.TurnSink);
        }
        finally { TryDelete(cfg); }
    }

    // ── the honesty badge ──

    [Fact]
    public void FormatBadge_CarriesTheHonestyStatement_AndBothDiagnosticsPaths()
    {
        var paths = Paths("m365-run-1");

        var line = Microsoft365SutTarget.FormatBadge(paths, DiagnosticProfile.PublicAnswerOnly, maxTurns: 0);

        Assert.StartsWith("Note: --sut microsoft365 observes the agent's final answer and the evidence the tenant emits;", line, StringComparison.Ordinal);
        Assert.Contains("NotRequested / NotProvided / Provided per channel, never inferred.", line, StringComparison.Ordinal);
        Assert.Contains("Trace: " + paths.TraceFile.FullName, line, StringComparison.Ordinal);
        Assert.Contains("Issues: " + paths.IssueFile.FullName, line, StringComparison.Ordinal);
        Assert.DoesNotContain("EXPERIMENTAL", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Turn cap", line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void FormatBadge_Native118_SaysExperimentalNotADocumentedContract()
    {
        var line = Microsoft365SutTarget.FormatBadge(Paths("m365-run-1"), DiagnosticProfile.Native118Diagnostics, maxTurns: 0);

        Assert.Contains("native118", line, StringComparison.Ordinal);
        Assert.Contains("EXPERIMENTAL", line, StringComparison.Ordinal);
        Assert.Contains("1.18.0", line, StringComparison.Ordinal);
        Assert.Contains("not a documented contract", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatBadge_WithATurnCap_SaysTurnsCountedConsumptionNotMetered()
    {
        var line = Microsoft365SutTarget.FormatBadge(Paths("m365-run-1"), DiagnosticProfile.PublicAnswerOnly, maxTurns: 7);

        Assert.Contains("Turn cap: 7", line, StringComparison.Ordinal);
        Assert.Contains("turns counted, consumption not metered", line, StringComparison.Ordinal);
    }

    // ── the public diagnostics forwarder the badge relies on (a provider type) ──

    [Fact]
    public void Diagnostics_Describe_HonoursTraceFileRunIdAndDirectory_CreatesNothing()
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "agenteval-m365-diag-" + Guid.NewGuid().ToString("N")));
        var trace = new FileInfo(Path.Combine(dir.FullName, "custom", "trace.jsonl"));
        var options = new WorkIqOptions { RunId = "m365-run-1", TraceFile = trace, DiagnosticsDirectory = dir };

        var paths = Microsoft365Diagnostics.Describe(options);

        Assert.Equal("m365-run-1", paths.RunId);
        Assert.Equal(trace.FullName, paths.TraceFile.FullName);
        Assert.Equal(dir.FullName, paths.Directory.FullName);
        Assert.Equal(Path.Combine(dir.FullName, "m365-issues.jsonl"), paths.IssueFile.FullName);
        Assert.False(Directory.Exists(dir.FullName));
    }

    [Fact]
    public void Diagnostics_Describe_NoRunId_LeavesThePlaceholderInTheTraceFileName()
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "agenteval-m365-diag-" + Guid.NewGuid().ToString("N")));

        var paths = Microsoft365Diagnostics.Describe(new WorkIqOptions { DiagnosticsDirectory = dir });

        Assert.Null(paths.RunId);
        Assert.Contains("{runId}", paths.TraceFile.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_NewRunId_HasTheDocumentedShape_AndIsUnique()
    {
        var first = Microsoft365Diagnostics.NewRunId();
        var second = Microsoft365Diagnostics.NewRunId();

        Assert.Matches("^m365-[0-9]{8}-[0-9]{6}-[0-9a-f]{4}$", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Diagnostics_Describe_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Microsoft365Diagnostics.Describe(null!));
    }

    // ── helpers ──

    private const string ValidConfigJson = "{ \"tenantId\": \"tenant-1\", \"appClientId\": \"app-1\" }";

    private static CommonTargetOptions Common(bool consent) => new()
    {
        Sut = "microsoft365",
        AckLiveSideEffects = consent,
    };

    private static Microsoft365DiagnosticsPaths Paths(string runId)
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "agenteval-m365-badge"));
        return new Microsoft365DiagnosticsPaths(
            runId,
            dir,
            new FileInfo(Path.Combine(dir.FullName, "m365-trace-" + runId + ".jsonl")),
            new FileInfo(Path.Combine(dir.FullName, "m365-issues.jsonl")));
    }

    private static FileInfo WriteConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-m365-sut-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return new FileInfo(path);
    }

    private static void TryDelete(FileInfo f)
    {
        try { if (f.Exists) { f.Delete(); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }
}
