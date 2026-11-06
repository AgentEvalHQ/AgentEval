// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using System.Text.Json;
using AgentEval.Cli;
using AgentEval.Cli.Microsoft365;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Readiness;
using AgentEval.Tests.Microsoft365.Readiness;
using Xunit;

namespace AgentEval.Tests.Cli.Microsoft365;

/// <summary>
/// <c>agenteval m365 doctor</c> — argument validation (usage errors exit 2 before anything is created), the
/// report folder and the four files written into it, the console view and the paths, and the exit codes 0 / 1 / 11,
/// all offline: the readiness runner is the provider's internal seam over <see cref="AgentEval.Tests.Microsoft365.TestHelpers.FakeWorkIqHandler"/>
/// and a host token provider handing out <c>fake-token</c>.
/// </summary>
public sealed class M365DoctorCommandTests
{
    private static M365DoctorCommand.ReadinessRunner Offline(ReadinessTestRig rig) =>
        (file, options, ct) => Microsoft365ReadinessCheck.RunAsync(null, file, options, rig.Handler, rig.Tokens, ct);

    private static DirectoryInfo ReportFolder(ReadinessTestRig rig) => new(Path.Combine(rig.Root.FullName, "report"));

    private static async Task<(int Code, string Out, string Err)> ExecuteAsync(ReadinessTestRig rig, M365DoctorCommand.DoctorArguments arguments)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await M365DoctorCommand.ExecuteAsync(arguments, Offline(rig), stdout, stderr, CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    // ── exit codes ──

    [Fact]
    public async Task Execute_HealthyEndpoint_ExitsZero_AndWritesEveryFileIntoTheReportFolder()
    {
        using var rig = new ReadinessTestRig();
        rig.WithHealthyEndpoint();
        var report = ReportFolder(rig);

        var (code, stdout, stderr) = await ExecuteAsync(rig, new() { ConfigFile = rig.WriteConfig(), Report = report });

        Assert.Equal(ExitCodes.Success, code);
        var markdown = Path.Combine(report.FullName, M365DoctorCommand.MarkdownFileName);
        var json = Path.Combine(report.FullName, M365DoctorCommand.JsonFileName);
        Assert.True(File.Exists(markdown));
        Assert.True(File.Exists(json));
        Assert.Single(Directory.GetFiles(report.FullName, "m365-trace-*.jsonl"));
        Assert.True(File.Exists(Path.Combine(report.FullName, "m365-issues.jsonl")));

        using (var document = JsonDocument.Parse(File.ReadAllText(json)))
        {
            Assert.Equal("PASS", document.RootElement.GetProperty("result").GetString());
            Assert.Equal(report.FullName, document.RootElement.GetProperty("reportDirectory").GetString());
            Assert.Equal(6, document.RootElement.GetProperty("stages").GetArrayLength());
        }

        Assert.Contains("| 4 | protocol | **PASS** |", File.ReadAllText(markdown), StringComparison.Ordinal);
        Assert.Contains("Report folder: " + report.FullName, stdout, StringComparison.Ordinal);
        Assert.Contains(markdown, stdout, StringComparison.Ordinal);
        Assert.Contains(json, stdout, StringComparison.Ordinal);
        Assert.Contains("Trace:  " + report.FullName, stdout, StringComparison.Ordinal);
        Assert.Contains("Issues: " + Path.Combine(report.FullName, "m365-issues.jsonl"), stdout, StringComparison.Ordinal);
        Assert.Contains(ReadinessReportRenderer.NoTokenStatement, stdout, StringComparison.Ordinal);
        Assert.Contains("Result: PASS", stdout, StringComparison.Ordinal);

        // --m365-verbose is on by default for the doctor: every trace event went to stderr.
        Assert.Contains("doctor.protocol.pass", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_ProtocolForbidden_ExitsOne()
    {
        using var rig = new ReadinessTestRig();
        rig.Handler.OnCard(ReadinessTestRig.DefaultCard).FailWith("GetTask", HttpStatusCode.Forbidden);
        var report = ReportFolder(rig);

        var (code, stdout, _) = await ExecuteAsync(rig, new() { ConfigFile = rig.WriteConfig(), Report = report });

        Assert.Equal(ExitCodes.TestFailure, code);
        Assert.Contains("| 4 | protocol | **FAIL** |", File.ReadAllText(Path.Combine(report.FullName, M365DoctorCommand.MarkdownFileName)), StringComparison.Ordinal);
        Assert.Contains("[FAIL]", stdout, StringComparison.Ordinal);
        Assert.Contains("Result: FAIL", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_ConfigMissingAField_ExitsOne_AndTheReportNamesTheField()
    {
        using var rig = new ReadinessTestRig();
        var report = ReportFolder(rig);

        var (code, _, _) = await ExecuteAsync(rig, new() { ConfigFile = rig.WriteConfig("{\"tenantId\":\"11111111-1111-1111-1111-111111111111\"}"), Report = report });

        Assert.Equal(ExitCodes.TestFailure, code);
        Assert.Contains("appClientId", File.ReadAllText(Path.Combine(report.FullName, M365DoctorCommand.MarkdownFileName)), StringComparison.Ordinal);
        rig.Handler.AssertNoRequests();
    }

    [Fact]
    public async Task Execute_ProbeWithoutConsent_ExitsEleven()
    {
        using var rig = new ReadinessTestRig();
        rig.WithHealthyEndpoint().Handler.OnRpc("SendMessage", ReadinessTestRig.CompletedWithAttributions);
        var report = ReportFolder(rig);

        var (code, stdout, _) = await ExecuteAsync(rig, new() { ConfigFile = rig.WriteConfig(), Report = report, Probe = true, Consent = false });

        Assert.Equal(ExitCodes.GateIndeterminate, code);
        Assert.Equal(11, code);
        Assert.Contains("skipped for consent", File.ReadAllText(Path.Combine(report.FullName, M365DoctorCommand.MarkdownFileName)), StringComparison.Ordinal);
        Assert.Contains("Result: INCOMPLETE", stdout, StringComparison.Ordinal);
        Assert.Equal(0, rig.Handler.CountOf("SendMessage"));
    }

    [Fact]
    public async Task Execute_ProbeWithConsent_AllPass_ExitsZero()
    {
        using var rig = new ReadinessTestRig();
        rig.WithHealthyEndpoint().Handler
            .OnRpc("SendMessage", ReadinessTestRig.CompletedWithAttributions)
            .OnRpc("CancelTask", ReadinessTestRig.NotCancelable);

        var (code, stdout, _) = await ExecuteAsync(rig, new() { ConfigFile = rig.WriteConfig(), Report = ReportFolder(rig), Probe = true, Consent = true });

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("probe", stdout, StringComparison.Ordinal);
        Assert.Equal(2, rig.Handler.CountOf("SendMessage"));
    }

    [Theory]
    [InlineData("bogus", 120, "public", true, "--stage")]
    [InlineData("all", 0, "public", true, "--timeout-seconds")]
    [InlineData("all", 120, "nope", true, "--m365-profile")]
    [InlineData("all", 120, "public", false, "--m365-config")]
    public async Task Execute_UsageError_ExitsTwo_AndCreatesNothing(string stage, int timeoutSeconds, string profile, bool withConfig, string named)
    {
        using var rig = new ReadinessTestRig();
        rig.WithHealthyEndpoint();
        var report = ReportFolder(rig);

        var (code, stdout, stderr) = await ExecuteAsync(rig, new()
        {
            ConfigFile = withConfig ? rig.WriteConfig() : null,
            Report = report,
            Stage = stage,
            TimeoutSeconds = timeoutSeconds,
            Profile = profile,
        });

        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Contains(named, stderr, StringComparison.Ordinal);
        Assert.Equal("", stdout);
        Assert.False(Directory.Exists(report.FullName));
        rig.Handler.AssertNoRequests();
        Assert.Equal(0, rig.Tokens.Calls);
    }

    [Fact]
    public async Task Execute_TraceOption_WritesTheTraceThere_TheIssuesStayInTheReportFolder()
    {
        using var rig = new ReadinessTestRig();
        rig.WithHealthyEndpoint();
        var report = ReportFolder(rig);
        var trace = new FileInfo(Path.Combine(rig.Root.FullName, "custom-trace.jsonl"));

        var (code, stdout, _) = await ExecuteAsync(rig, new() { ConfigFile = rig.WriteConfig(), Report = report, TraceFile = trace });

        Assert.Equal(ExitCodes.Success, code);
        Assert.True(File.Exists(trace.FullName));
        Assert.Contains("doctor.finished", File.ReadAllText(trace.FullName), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(report.FullName, "m365-trace-*.jsonl"));
        Assert.True(File.Exists(Path.Combine(report.FullName, "m365-issues.jsonl")));
        Assert.Contains("Trace:  " + trace.FullName, stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_NoOutputContainsTheToken()
    {
        using var rig = new ReadinessTestRig();
        rig.WithHealthyEndpoint().Handler
            .OnRpc("SendMessage", ReadinessTestRig.CompletedWithAttributions)
            .OnRpc("CancelTask", ReadinessTestRig.NotCancelable);
        var report = ReportFolder(rig);

        var (code, stdout, stderr) = await ExecuteAsync(rig, new() { ConfigFile = rig.WriteConfig(), Report = report, Probe = true, Consent = true });

        Assert.Equal(ExitCodes.Success, code);
        Assert.DoesNotContain(ReadinessTestRig.FakeToken, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(ReadinessTestRig.FakeToken, stderr, StringComparison.Ordinal);
        foreach (var file in Directory.GetFiles(report.FullName))
        {
            Assert.False(File.ReadAllText(file).Contains(ReadinessTestRig.FakeToken, StringComparison.Ordinal), $"{file} contains the token");
        }
    }

    [Fact]
    public async Task Command_ParsesEveryOption_AndHandsThemToTheCheck()
    {
        using var rig = new ReadinessTestRig();
        rig.WithHealthyEndpoint();
        var config = rig.WriteConfig();
        var report = ReportFolder(rig);
        var trace = Path.Combine(rig.Root.FullName, "t.jsonl");
        var raw = Path.Combine(rig.Root.FullName, "raw");
        ReadinessCheckOptions? captured = null;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var command = M365DoctorCommand.Create(
            (file, options, ct) =>
            {
                captured = options;
                return Microsoft365ReadinessCheck.RunAsync(null, file, options, rig.Handler, rig.Tokens, ct);
            },
            stdout,
            stderr);
        var root = new RootCommand();
        root.Add(command);

        var parse = root.Parse(new[]
        {
            "doctor", "--m365-config", config.FullName, "--stage", "protocol", "--continue", "--report", report.FullName,
            "--m365-profile", "native118", "--m365-capture-raw", raw, "--m365-verbose", "false", "--m365-trace", trace,
            "--timeout-seconds", "30",
        });
        Assert.Empty(parse.Errors);
        var code = await parse.InvokeAsync();

        Assert.Equal(ExitCodes.Success, code);
        Assert.NotNull(captured);
        Assert.Equal(new[] { ReadinessStage.Config, ReadinessStage.Protocol }, captured.Stages!.OrderBy(s => s).ToArray());
        Assert.False(captured.Probe);
        Assert.False(captured.IUnderstandLiveSideEffects);
        Assert.True(captured.ContinueAfterFailure);
        Assert.Equal(TimeSpan.FromSeconds(30), captured.StageTimeout);
        Assert.Equal(DiagnosticProfile.Native118Diagnostics, captured.Profile);
        Assert.Equal("--m365-profile", captured.ProfileSource);   // the doctor names its own option in the profile evidence
        Assert.Equal(raw, captured.RawCaptureDirectory!.FullName);
        Assert.False(captured.Verbose);
        Assert.Equal(trace, captured.TraceFile!.FullName);
        Assert.Equal(report.FullName, captured.DiagnosticsDirectory!.FullName);
        Assert.Same(stderr, captured.Console);
        Assert.DoesNotContain("doctor.protocol.pass", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CliTree_HasTheM365DoctorCommand_WithItsOptions()
    {
        var doctor = CliCommandTree.Build().Root.Subcommands.Single(c => c.Name == "m365").Subcommands.Single(c => c.Name == "doctor");

        Assert.Equal(
            new[]
            {
                "--continue", "--i-understand-live-side-effects", "--m365-capture-raw", "--m365-config", "--m365-profile",
                "--m365-trace", "--m365-verbose", "--probe", "--report", "--stage", "--timeout-seconds",
            },
            doctor.Options.Select(o => o.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.True(doctor.Options.Single(o => o.Name == "--m365-config").Required);
    }

    [Fact]
    public void Help_ProbeAndTimeout_SayWhatIsReallySentAndWhatBoundsTheSignIn()
    {
        var doctor = CliCommandTree.Build().Root.Subcommands.Single(c => c.Name == "m365").Subcommands.Single(c => c.Name == "doctor");

        // The census sends the probe prompt a second time and calls CancelTask: the help text must not say "one prompt".
        var probe = doctor.Options.Single(o => o.Name == "--probe").Description!;
        Assert.Contains("SECOND time", probe, StringComparison.Ordinal);
        Assert.Contains("CancelTask", probe, StringComparison.Ordinal);
        Assert.Contains("--i-understand-live-side-effects", probe, StringComparison.Ordinal);

        // A device-code sign-in is bounded by its code's expiry, not by the stage timeout.
        var timeout = doctor.Options.Single(o => o.Name == "--timeout-seconds").Description!;
        Assert.Contains("sign-in code expires", timeout, StringComparison.Ordinal);
        Assert.Contains("exit", doctor.Description!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3 the report", doctor.Description!, StringComparison.Ordinal);
    }

    // ── stage mapping, default folder, exit codes ──

    [Theory]
    [InlineData("all", false, null)]
    [InlineData("ALL", true, null)]
    [InlineData("config", false, new[] { ReadinessStage.Config })]
    [InlineData("auth", false, new[] { ReadinessStage.Config, ReadinessStage.Auth })]
    [InlineData("discovery", false, new[] { ReadinessStage.Config, ReadinessStage.Discovery })]
    [InlineData("protocol", true, new[] { ReadinessStage.Config, ReadinessStage.Protocol, ReadinessStage.Probe, ReadinessStage.Census })]
    [InlineData("probe", false, new[] { ReadinessStage.Config, ReadinessStage.Probe })]
    [InlineData(" Census ", false, new[] { ReadinessStage.Config, ReadinessStage.Probe, ReadinessStage.Census })]
    public void TryResolveStages_MapsEachValue(string stage, bool probe, ReadinessStage[]? expected)
    {
        Assert.True(M365DoctorCommand.TryResolveStages(stage, probe, out var stages, out var error));
        Assert.Equal("", error);
        if (expected is null)
        {
            Assert.Null(stages);
        }
        else
        {
            Assert.Equal(expected.OrderBy(s => s).ToArray(), stages!.OrderBy(s => s).ToArray());
        }
    }

    [Fact]
    public void TryResolveStages_UnknownValue_IsAUsageErrorNamingTheValidValues()
    {
        Assert.False(M365DoctorCommand.TryResolveStages("everything", probe: false, out _, out var error));
        Assert.Contains("everything", error, StringComparison.Ordinal);
        Assert.Contains("discovery", error, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultReportDirectory_IsDoctorUtcStampZ_InTheDiagnosticsFolder()
    {
        var folder = M365DoctorCommand.DefaultReportDirectory(new DateTimeOffset(2026, 11, 5, 15, 30, 45, TimeSpan.FromHours(1)));

        Assert.Equal("doctor-20261105-143045Z", folder.Name);
        Assert.Equal(Microsoft365Diagnostics.Describe(new WorkIqOptions()).Directory.FullName, folder.Parent!.FullName);
    }

    [Fact]
    public void ExitCodeFor_FailBeatsConsentSkip_ConsentSkipIsEleven_AllPassIsZero()
    {
        static ReadinessReport Report(params ReadinessStageResult[] stages) => new(stages);
        var pass = new ReadinessStageResult(ReadinessStage.Config, ReadinessResult.Pass, "ok");
        var fail = new ReadinessStageResult(ReadinessStage.Auth, ReadinessResult.Fail, "no");
        var consent = new ReadinessStageResult(ReadinessStage.Probe, ReadinessResult.Skipped, "needs consent") { SkippedForConsent = true };
        var notRequested = new ReadinessStageResult(ReadinessStage.Census, ReadinessResult.Skipped, "not requested") { Requested = false };
        var requestedNotRun = new ReadinessStageResult(ReadinessStage.Census, ReadinessResult.Skipped, "no probe result");

        Assert.Equal(ExitCodes.Success, M365DoctorCommand.ExitCodeFor(Report(pass, notRequested)));
        Assert.Equal(ExitCodes.TestFailure, M365DoctorCommand.ExitCodeFor(Report(pass, fail, consent)));
        Assert.Equal(ExitCodes.GateIndeterminate, M365DoctorCommand.ExitCodeFor(Report(pass, consent)));
        Assert.Equal(ExitCodes.GateIndeterminate, M365DoctorCommand.ExitCodeFor(Report(pass, requestedNotRun)));
    }
}

/// <summary>
/// <c>m365 doctor</c> through the real CLI entry point (<c>Program.cs</c>): a missing <c>--m365-config</c> is a parse error (exit 2,
/// nothing sent) and <c>--help</c> lists every option. Redirects the console, hence the <c>ConsoleTests</c> collection.
/// </summary>
[Collection("ConsoleTests")]
public sealed class M365DoctorCommandLineTests
{
    [Fact]
    public async Task Cli_WithoutConfig_ExitsUsageError()
    {
        var (exitCode, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync("m365", "doctor");

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains("--m365-config", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_Help_ListsEveryOption()
    {
        var (exitCode, stdout, _) = await CliParseErrorExitCodeTests.RunCliAsync("m365", "doctor", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        foreach (var option in new[]
        {
            "--m365-config", "--stage", "--probe", "--i-understand-live-side-effects", "--continue", "--report",
            "--m365-profile", "--m365-capture-raw", "--m365-verbose", "--m365-trace", "--timeout-seconds",
        })
        {
            Assert.Contains(option, stdout, StringComparison.Ordinal);
        }
    }
}
