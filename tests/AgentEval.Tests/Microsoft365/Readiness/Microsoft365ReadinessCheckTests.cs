// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Auth;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Readiness;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Microsoft.Identity.Client;
using Xunit;
using static AgentEval.Tests.Microsoft365.Readiness.ReadinessTestRig;

namespace AgentEval.Tests.Microsoft365.Readiness;

/// <summary>
/// M37: the readiness check behind <c>agenteval m365 doctor</c>, stage by stage, offline — the real handler chain
/// (auth → capture → <see cref="FakeWorkIqHandler"/>) and a host token provider handing out <c>fake-token</c>. Every
/// verdict, the evidence a tester reads, the stop-at-first-failure rule, consent, and that no output ever carries the
/// token.
/// </summary>
public sealed class Microsoft365ReadinessCheckTests
{
    private static readonly HashSet<ReadinessStage> ConfigAndProbe = [ReadinessStage.Config, ReadinessStage.Probe];

    // ---------------------------------------------------------------------------------------------------------
    // Argument guards
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_BothConfigAndFile_ThrowsArgumentException()
    {
        using var rig = new ReadinessTestRig();
        var config = new Microsoft365Config { TenantId = "11111111-1111-1111-1111-111111111111", AppClientId = "22222222-2222-2222-2222-222222222222" };

        await Assert.ThrowsAsync<ArgumentException>(() => Microsoft365ReadinessCheck.RunAsync(config, rig.WriteConfig(), rig.Options()));
        await Assert.ThrowsAsync<ArgumentException>(() => Microsoft365ReadinessCheck.RunAsync(null, null, rig.Options()));
        rig.Handler.AssertNoRequests();
    }

    [Fact]
    public async Task RunAsync_NonPositiveStageTimeout_ThrowsBeforeAnything()
    {
        using var rig = new ReadinessTestRig();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.RunAsync(rig.Options() with { StageTimeout = TimeSpan.Zero }));
        rig.Handler.AssertNoRequests();
        Assert.False(rig.Diagnostics.Exists);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 1. Config
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Config_MissingAppClientId_FailsNamingTheField_AndEveryLaterStageIsSkipped()
    {
        using var rig = new ReadinessTestRig();
        var file = rig.WriteConfig("{\"tenantId\":\"11111111-1111-1111-1111-111111111111\"}");

        var report = await rig.RunAsync(rig.Options(probe: true, consent: true, continueAfterFailure: true), file);

        var config = report.Find(ReadinessStage.Config)!;
        Assert.Equal(ReadinessResult.Fail, config.Result);
        Assert.Contains("appClientId", config.Summary, StringComparison.Ordinal);
        Assert.Contains("appClientId", config.Action, StringComparison.Ordinal);
        Assert.NotNull(config.Issue);
        Assert.Equal(WorkIqIssueCategory.Config, config.Issue.Category);
        Assert.Equal(file.FullName, EvidenceOf(config, "config file"));
        foreach (var stage in report.Stages.Where(s => s.Stage != ReadinessStage.Config))
        {
            Assert.Equal(ReadinessResult.Skipped, stage.Result);
            Assert.Contains("config did not load", stage.SkipReason, StringComparison.Ordinal);
            Assert.False(stage.SkippedForConsent);
        }

        Assert.True(report.AnyFailed);
        Assert.Equal("FAIL", report.Verdict);
        Assert.Null(report.ConfigSha256);
        Assert.Equal(0, rig.Tokens.Calls);
        rig.Handler.AssertNoRequests();
        Assert.Contains("doctor.config.failed", Codes(report.IssueFile));
    }

    [Fact]
    public async Task Config_UntouchedTemplatePlaceholder_FailsAtConfigNamingTheField_NothingSent()
    {
        using var rig = new ReadinessTestRig();
        var file = rig.WriteConfig("{\"tenantId\":\"REPLACE-WITH-ENTRA-TENANT-GUID\",\"appClientId\":\"REPLACE-WITH-PUBLIC-CLIENT-APP-REGISTRATION-GUID\"}");

        var report = await rig.RunAsync(rig.Options(), file);

        var config = report.Find(ReadinessStage.Config)!;
        Assert.Equal(ReadinessResult.Fail, config.Result);
        Assert.Contains("'tenantId'", config.Summary, StringComparison.Ordinal);
        Assert.Contains("Replace the template placeholder in 'tenantId'", config.Action, StringComparison.Ordinal);
        Assert.Equal(ReadinessResult.Skipped, report.Find(ReadinessStage.Auth)!.Result);
        Assert.Equal(0, rig.Tokens.Calls);
        rig.Handler.AssertNoRequests();
    }

    [Theory]
    [InlineData("contoso", "app-1", 2)]
    [InlineData("contoso.onmicrosoft.com", "22222222-2222-2222-2222-222222222222", 0)]
    [InlineData("11111111-1111-1111-1111-111111111111", "app-1", 1)]
    public async Task Config_IdsThatCannotBeRight_PassWithAWarningEach(string tenantId, string appClientId, int warnings)
    {
        using var rig = new ReadinessTestRig();
        var file = rig.WriteConfig($"{{\"tenantId\":\"{tenantId}\",\"appClientId\":\"{appClientId}\"}}");

        var report = await rig.RunAsync(rig.Options(stages: new HashSet<ReadinessStage> { ReadinessStage.Config }), file);

        // Review L7: a shape check saves the tester a sign-in round trip; the sign-in stays the authority, so it only warns.
        var config = report.Find(ReadinessStage.Config)!;
        Assert.Equal(ReadinessResult.Pass, config.Result);
        Assert.Equal(warnings, config.Warnings.Count);
        if (warnings > 0)
        {
            Assert.Contains(config.Warnings, w => w.Contains("AADSTS", StringComparison.Ordinal));
        }

        rig.Handler.AssertNoRequests();
    }

    [Fact]
    public async Task Config_FileNotFound_FailsAndPointsAtThePath()
    {
        using var rig = new ReadinessTestRig();
        var missing = new FileInfo(Path.Combine(rig.Root.FullName, "nope.json"));

        var report = await rig.RunAsync(rig.Options(), missing);

        var config = report.Find(ReadinessStage.Config)!;
        Assert.Equal(ReadinessResult.Fail, config.Result);
        Assert.Contains("not found", config.Summary, StringComparison.Ordinal);
        Assert.Contains("--m365-config", config.Action, StringComparison.Ordinal);
        rig.Handler.AssertNoRequests();
    }

    [Fact]
    public async Task Config_Valid_PassesWithTheResolvedConnectionAndTheMachine()
    {
        using var rig = new ReadinessTestRig();

        var report = await rig.RunAsync(rig.Options(stages: new HashSet<ReadinessStage> { ReadinessStage.Config }));

        var config = report.Find(ReadinessStage.Config)!;
        Assert.Equal(ReadinessResult.Pass, config.Result);
        Assert.Equal("https://workiq.svc.cloud.microsoft/a2a/", EvidenceOf(config, "endpoint"));
        Assert.Equal("https://workiq.svc.cloud.microsoft/a2a/.well-known/agent-card.json", EvidenceOf(config, "card URI"));
        Assert.StartsWith("default Work IQ agent", EvidenceOf(config, "agent"), StringComparison.Ordinal);
        Assert.Equal("11111111-1111-1111-1111-111111111111", EvidenceOf(config, "tenant id"));
        Assert.Equal("22222222-2222-2222-2222-222222222222", EvidenceOf(config, "app (client) id"));
        Assert.Equal(Microsoft365Config.DefaultScope, EvidenceOf(config, "scope"));
        Assert.StartsWith("PublicAnswerOnly", EvidenceOf(config, "profile"), StringComparison.Ordinal);
        Assert.Equal(Microsoft365Config.DefaultTrustedHost, EvidenceOf(config, "trusted hosts"));
        Assert.StartsWith("not set", EvidenceOf(config, "time zone"), StringComparison.Ordinal);
        Assert.Equal(64, EvidenceOf(config, "config sha256").Length);
        Assert.Equal(report.ConfigSha256, EvidenceOf(config, "config sha256"));
        Assert.Equal(64, EvidenceOf(config, "config file sha256").Length);
        Assert.NotEmpty(EvidenceOf(config, "OS"));
        Assert.NotEmpty(EvidenceOf(config, ".NET"));
        Assert.StartsWith(TimeZoneInfo.Local.Id, EvidenceOf(config, "machine time zone"), StringComparison.Ordinal);
        Assert.Equal("1.0", EvidenceOf(config, "A2A version"));

        foreach (var stage in report.Stages.Where(s => s.Stage != ReadinessStage.Config))
        {
            Assert.Equal(ReadinessResult.Skipped, stage.Result);
            Assert.False(stage.Requested);
        }

        Assert.True(report.AllPassed);
        rig.Handler.AssertNoRequests();
    }

    [Fact]
    public async Task Config_ProfileOverride_WithoutASource_SaysTheOptions_NotACommandLineOption()
    {
        // Review L7: the check said "from --m365-profile" for every host, the demo (whose option is --profile) included.
        using var rig = new ReadinessTestRig();

        var report = await rig.RunAsync(rig.Options(stages: new HashSet<ReadinessStage> { ReadinessStage.Config }, profile: DiagnosticProfile.Native118Diagnostics));

        var config = report.Find(ReadinessStage.Config)!;
        var profile = EvidenceOf(config, "profile");
        Assert.Equal("Native118Diagnostics (from the options; it overrides the config's PublicAnswerOnly)", profile);
        Assert.DoesNotContain("--m365-profile", profile, StringComparison.Ordinal);
        Assert.Contains(config.Warnings, w => w.Contains("EXPERIMENTAL", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--m365-profile", DiagnosticProfile.Native118Diagnostics, "Native118Diagnostics (from --m365-profile; it overrides the config's PublicAnswerOnly)")]
    [InlineData("--profile", DiagnosticProfile.PublicAnswerOnly, "PublicAnswerOnly (from --profile; the config says the same)")]
    public async Task Config_ProfileOverride_IsNamedWithTheSourceTheHostGives(string source, DiagnosticProfile profile, string expected)
    {
        using var rig = new ReadinessTestRig();
        var options = rig.Options(stages: new HashSet<ReadinessStage> { ReadinessStage.Config }, profile: profile) with { ProfileSource = source };

        var report = await rig.RunAsync(options);

        Assert.Equal(expected, EvidenceOf(report.Find(ReadinessStage.Config)!, "profile"));
    }

    [Fact]
    public async Task IssueFileOption_TheRegisterIsWrittenThere_AndTheReportAndTheTraceNameIt()
    {
        // Review L2: a host that wants one register for the run (the demo's issues.jsonl) names it; nothing is renamed afterwards.
        using var rig = new ReadinessTestRig();
        rig.Handler.FailWith(FakeWorkIqHandler.CardMethod, System.Net.HttpStatusCode.Unauthorized);
        var issueFile = new FileInfo(Path.Combine(rig.Root.FullName, "run", "issues.jsonl"));
        var options = rig.Options() with { IssueFile = issueFile };

        var report = await rig.RunAsync(options);

        Assert.True(report.AnyFailed);
        Assert.Equal(issueFile.FullName, report.IssueFile);
        Assert.Contains("doctor.discovery.failed", Codes(issueFile.FullName));
        Assert.False(File.Exists(Path.Combine(rig.Diagnostics.FullName, "m365-issues.jsonl")), "Nothing goes to the default register when the host names one.");
        var trace = ReadText(report.TraceFile);
        Assert.Contains(issueFile.FullName.Replace("\\", "\\\\", StringComparison.Ordinal), trace, StringComparison.Ordinal);
        Assert.DoesNotContain("m365-issues.jsonl", trace, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 2. Auth
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Auth_Aadsts65001_FailsWithTheConsentCauseAndFix()
    {
        using var rig = new ReadinessTestRig();
        rig.WithHealthyEndpoint();
        rig.Tokens.Failure = new MsalServiceException(
            "invalid_grant",
            "AADSTS65001: The user or administrator has not consented to use the application with ID '22222222-2222-2222-2222-222222222222'. Trace ID: t Correlation ID: c Timestamp: 2026-11-05 10:00:00Z");

        var report = await rig.RunAsync(rig.Options());

        var auth = report.Find(ReadinessStage.Auth)!;
        Assert.Equal(ReadinessResult.Fail, auth.Result);
        Assert.Contains("AADSTS65001", auth.Summary, StringComparison.Ordinal);
        Assert.NotNull(auth.Issue);
        Assert.Equal("AADSTS65001", auth.Issue.AadstsCode);
        Assert.Equal(WorkIqIssueCategory.Authentication, auth.Issue.Category);
        Assert.Contains("not consented", auth.Issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Contains("not consented", auth.Meaning, StringComparison.Ordinal);
        Assert.Contains("admin consent", auth.Action, StringComparison.Ordinal);
        Assert.Equal("AADSTS65001", EvidenceOf(auth, "AADSTS code"));
        Assert.Equal("no", EvidenceOf(auth, "token acquired"));

        Assert.All(
            report.Stages.Where(s => s.Stage is ReadinessStage.Discovery or ReadinessStage.Protocol),
            s => Assert.Contains("auth stage failed", s.SkipReason, StringComparison.Ordinal));
        rig.Handler.AssertNoRequests();

        var issues = ReadLines(report.IssueFile);
        Assert.Contains(issues, line => line.Contains("doctor.auth.failed", StringComparison.Ordinal) && line.Contains("AADSTS65001", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Auth_HostTokenProvider_PassesWithoutRevealingTheToken()
    {
        using var rig = new ReadinessTestRig();

        var report = await rig.RunAsync(rig.Options(stages: new HashSet<ReadinessStage> { ReadinessStage.Config, ReadinessStage.Auth }));

        var auth = report.Find(ReadinessStage.Auth)!;
        Assert.Equal(ReadinessResult.Pass, auth.Result);
        Assert.StartsWith("yes", EvidenceOf(auth, "token acquired"), StringComparison.Ordinal);
        Assert.Contains("StaticTokenProvider", EvidenceOf(auth, "token provider"), StringComparison.Ordinal);
        Assert.DoesNotContain(auth.Evidence, e => e.Value.Contains(FakeToken, StringComparison.Ordinal));
        Assert.DoesNotContain(FakeToken, auth.Summary, StringComparison.Ordinal);
        Assert.Equal(1, rig.Tokens.Calls);
        rig.Handler.AssertNoRequests();
    }

    /// <summary>
    /// A host's device-code provider built without diagnostics (the demo builds one with its own prompt writer and
    /// shares it with the live agent later) traces the check's sign-in into the check's run: the auth stage attaches the
    /// run's diagnostics to it, so <c>auth.device_code_prompted</c> / <c>auth.token_acquired</c> reach this run's
    /// trace.jsonl. Offline by construction: the caller's token is canceled the moment the auth stage starts, after the
    /// stage has taken the provider (the attach) and before the provider's first await, which refuses a canceled token
    /// before any MSAL client, token cache or network call exists.
    /// </summary>
    [Fact]
    public async Task Auth_HostDeviceCodeProviderWithoutDiagnostics_TracesIntoTheChecksRun()
    {
        using var rig = new ReadinessTestRig();
        var provider = new WorkIqDeviceCodeTokenProvider("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222", Microsoft365Config.DefaultScope, TextWriter.Null);
        Assert.True(provider.Diagnostics.IsNone);

        var (report, codes) = await RunAuthStageCanceledAtItsStartAsync(rig, provider);

        AssertTheTokenCallNeverRan(report, codes, rig);
        Assert.False(provider.Diagnostics.IsNone);
        Assert.False(string.IsNullOrEmpty(report.RunId));
        Assert.Equal(report.RunId, provider.Diagnostics.RunId);
        Assert.Contains(provider.Diagnostics.RecentEvents, e => e.Code == "doctor.auth.start" && e.RunId == report.RunId);
        Assert.Same(TextWriter.Null, provider.DeviceCodePrompt);
    }

    /// <summary>A host's device-code provider that carries diagnostics of its own keeps them through the check (the host's choice wins).</summary>
    [Fact]
    public async Task Auth_HostDeviceCodeProviderWithItsOwnDiagnostics_KeepsThem()
    {
        using var rig = new ReadinessTestRig();
        using var own = new WorkIqDiagnostics("host-run", null, [], []);
        var provider = new WorkIqDeviceCodeTokenProvider("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222", Microsoft365Config.DefaultScope, TextWriter.Null) { Diagnostics = own };

        var (report, codes) = await RunAuthStageCanceledAtItsStartAsync(rig, provider);

        AssertTheTokenCallNeverRan(report, codes, rig);
        Assert.Same(own, provider.Diagnostics);
        Assert.Equal("host-run", provider.Diagnostics.RunId);
        Assert.NotEqual(report.RunId, provider.Diagnostics.RunId);
    }

    /// <summary>
    /// Runs config + auth with <paramref name="provider"/>, canceling the caller's token from a trace sink when
    /// <c>doctor.auth.start</c> is traced; returns the report and the trace file's event codes.
    /// </summary>
    private static async Task<(ReadinessReport Report, IReadOnlyList<string> Codes)> RunAuthStageCanceledAtItsStartAsync(ReadinessTestRig rig, IWorkIqTokenProvider provider)
    {
        using var cancel = new CancellationTokenSource();
        var options = rig.Options(stages: new HashSet<ReadinessStage> { ReadinessStage.Config, ReadinessStage.Auth }) with
        {
            TraceSinks = [new CancelOnTraceSink("doctor.auth.start", cancel)],
        };

        var report = await Microsoft365ReadinessCheck.RunAsync(null, rig.WriteConfig(), options, rig.Handler, provider, cancel.Token);
        return (report, Codes(report.TraceFile));
    }

    /// <summary>The auth stage took the device-code provider and stopped on the cancellation before MSAL was ever built: nothing touched the token cache or the network.</summary>
    private static void AssertTheTokenCallNeverRan(ReadinessReport report, IReadOnlyList<string> codes, ReadinessTestRig rig)
    {
        Assert.Equal(ReadinessResult.Pass, report.Find(ReadinessStage.Config)!.Result);
        var auth = report.Find(ReadinessStage.Auth)!;
        Assert.Equal(ReadinessResult.Fail, auth.Result);
        Assert.StartsWith("stopped: the check was canceled", auth.Summary, StringComparison.Ordinal);
        Assert.Equal("doctor.canceled", auth.Issue?.Code);
        Assert.Contains("doctor.auth.start", codes);
        Assert.DoesNotContain("auth.cache_registered", codes);
        Assert.DoesNotContain(codes, c => c.StartsWith("auth.", StringComparison.Ordinal));
        rig.Handler.AssertNoRequests();
    }

    /// <summary>A caller-owned trace sink that cancels <paramref name="source"/> when an event with <paramref name="code"/> is traced.</summary>
    private sealed class CancelOnTraceSink(string code, CancellationTokenSource source) : IWorkIqTraceSink
    {
        public void Write(WorkIqTraceEvent e)
        {
            if (string.Equals(e.Code, code, StringComparison.Ordinal))
            {
                source.Cancel();
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // 3. Discovery
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Discovery_DefaultCard_SelectsTheJsonRpc10Interface()
    {
        using var rig = new ReadinessTestRig();
        rig.WithHealthyEndpoint();

        var report = await rig.RunAsync(rig.Options());

        var discovery = report.Find(ReadinessStage.Discovery)!;
        Assert.Equal(ReadinessResult.Pass, discovery.Result);
        Assert.Equal("JSONRPC · 1.0 · workiq.svc.cloud.microsoft/a2a/", EvidenceOf(discovery, "chosen interface"));
        Assert.Equal("JSONRPC · 1.0 · workiq.svc.cloud.microsoft", EvidenceOf(discovery, "interface 1"));
        Assert.Equal("HTTP+JSON · 1.0 · workiq.svc.cloud.microsoft", EvidenceOf(discovery, "interface 2"));
        Assert.Equal("200", EvidenceOf(discovery, "HTTP status"));
        Assert.Equal("Work IQ", EvidenceOf(discovery, "card name"));
        Assert.Equal("2026.11.05", EvidenceOf(discovery, "card version"));
        Assert.Equal("true", EvidenceOf(discovery, "capabilities.streaming"));
        Assert.Equal("false", EvidenceOf(discovery, "capabilities.pushNotifications"));
        Assert.Equal("2", EvidenceOf(discovery, "skills"));
        Assert.Equal(64, EvidenceOf(discovery, "card sha256").Length);
        Assert.True(long.Parse(EvidenceOf(discovery, "bytes"), System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Equal(1, rig.Handler.CardRequestCount);
    }

    [Fact]
    public async Task Discovery_UntrustedHostCard_FailsBeforeAnyRpc()
    {
        using var rig = new ReadinessTestRig();
        rig.Handler.OnCard("cards/card-untrusted-host.json").OnRpc("GetTask", TaskNotFoundReply);

        var report = await rig.RunAsync(rig.Options());

        var discovery = report.Find(ReadinessStage.Discovery)!;
        Assert.Equal(ReadinessResult.Fail, discovery.Result);
        Assert.Contains("trustedHosts", discovery.Summary, StringComparison.Ordinal);
        Assert.Contains("trustedHosts", discovery.Action, StringComparison.Ordinal);
        Assert.Contains("example.invalid", EvidenceOf(discovery, "failure"), StringComparison.Ordinal);
        Assert.Equal(Microsoft365Config.DefaultTrustedHost, EvidenceOf(discovery, "trusted hosts"));
        Assert.NotNull(discovery.Issue);
        Assert.Equal("discovery.untrusted_host", discovery.Issue.Code);

        Assert.Equal(ReadinessResult.Skipped, report.Find(ReadinessStage.Protocol)!.Result);
        Assert.Equal(0, rig.Handler.CountOf("GetTask"));
        Assert.All(rig.Handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task Discovery_V03OnlyCard_FailsWithProtocolVersionMismatch()
    {
        using var rig = new ReadinessTestRig();
        rig.Handler.OnCard("cards/card-v03-only.json").OnRpc("GetTask", TaskNotFoundReply);

        var report = await rig.RunAsync(rig.Options());

        var discovery = report.Find(ReadinessStage.Discovery)!;
        Assert.Equal(ReadinessResult.Fail, discovery.Result);
        Assert.Contains("no 1.0 interface", discovery.Summary, StringComparison.Ordinal);
        Assert.NotNull(discovery.Issue);
        Assert.Equal(WorkIqIssueCategory.Protocol, discovery.Issue.Category);
        Assert.Equal("protocol.version_mismatch", discovery.Issue.Code);
        Assert.Contains("never downgrades", discovery.Meaning, StringComparison.Ordinal);
        Assert.Equal(0, rig.Handler.CountOf("GetTask"));
    }

    // ---------------------------------------------------------------------------------------------------------
    // 4. Protocol
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Protocol_TaskNotFound_Passes_AndTheExpectedReplyStaysOutOfTheIssueRegister()
    {
        using var rig = new ReadinessTestRig();
        rig.WithHealthyEndpoint(new Dictionary<string, string> { ["request-id"] = "req-doctor-1" });

        var report = await rig.RunAsync(rig.Options());

        var protocol = report.Find(ReadinessStage.Protocol)!;
        Assert.Equal(ReadinessResult.Pass, protocol.Result);
        Assert.Equal("-32001 (TaskNotFound)", EvidenceOf(protocol, "JSON-RPC code"));
        Assert.Equal("200", EvidenceOf(protocol, "HTTP status"));
        Assert.Equal("req-doctor-1", EvidenceOf(protocol, "request-id"));
        Assert.Contains("speaks A2A 1.0 JSON-RPC and accepted the token", protocol.Meaning, StringComparison.Ordinal);

        var get = Assert.Single(rig.Handler.Requests, r => r.JsonRpcMethod == "GetTask");
        var requestedId = JsonNode.Parse(get.BodyJson!)!["params"]!["id"]!.GetValue<string>();
        Assert.StartsWith(Microsoft365ReadinessCheck.ProtocolTaskIdPrefix, requestedId, StringComparison.Ordinal);

        Assert.Empty(ReadLines(report.IssueFile));
        Assert.Contains("doctor.protocol.expected_not_found", Codes(report.TraceFile));
        Assert.True(report.AllPassed);
    }

    [Fact]
    public async Task Protocol_Http401_FailsWithAnAuthenticationIssue_AndStopsTheRun()
    {
        using var rig = new ReadinessTestRig();
        rig.Handler.OnCard(DefaultCard).FailWith("GetTask", HttpStatusCode.Unauthorized, headers: new Dictionary<string, string> { ["request-id"] = "req-401" });

        var report = await rig.RunAsync(rig.Options(probe: true, consent: true));

        var protocol = report.Find(ReadinessStage.Protocol)!;
        Assert.Equal(ReadinessResult.Fail, protocol.Result);
        Assert.Contains("HTTP 401", protocol.Summary, StringComparison.Ordinal);
        Assert.NotNull(protocol.Issue);
        Assert.Equal(WorkIqIssueCategory.Authentication, protocol.Issue.Category);
        Assert.Equal(401, protocol.Issue.HttpStatus);
        Assert.Equal("req-401", protocol.Issue.RequestId);
        Assert.Contains("sign in", protocol.Action, StringComparison.Ordinal);

        var probe = report.Find(ReadinessStage.Probe)!;
        Assert.Equal(ReadinessResult.Skipped, probe.Result);
        Assert.Contains("protocol stage failed", probe.SkipReason, StringComparison.Ordinal);
        Assert.Equal(0, rig.Handler.CountOf("SendMessage"));
        Assert.Equal("FAIL", report.Verdict);
    }

    [Fact]
    public async Task Protocol_Http403_FailsWithAPermissionIssue()
    {
        using var rig = new ReadinessTestRig();
        rig.Handler.OnCard(DefaultCard).FailWith("GetTask", HttpStatusCode.Forbidden);

        var report = await rig.RunAsync(rig.Options());

        var protocol = report.Find(ReadinessStage.Protocol)!;
        Assert.Equal(ReadinessResult.Fail, protocol.Result);
        Assert.Contains("HTTP 403", protocol.Summary, StringComparison.Ordinal);
        Assert.NotNull(protocol.Issue);
        Assert.Equal(WorkIqIssueCategory.Permission, protocol.Issue.Category);
        Assert.Equal(403, protocol.Issue.HttpStatus);
        Assert.Contains("admin consent", protocol.Action, StringComparison.Ordinal);
        Assert.Contains("licence", protocol.Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protocol_VersionNotSupported_Fails()
    {
        using var rig = new ReadinessTestRig();
        rig.Handler.OnCard(DefaultCard).OnRpc("GetTask", "envelopes/rpc-error-on-http-200-version-not-supported.json");

        var report = await rig.RunAsync(rig.Options());

        var protocol = report.Find(ReadinessStage.Protocol)!;
        Assert.Equal(ReadinessResult.Fail, protocol.Result);
        Assert.Contains("-32009", protocol.Summary, StringComparison.Ordinal);
        Assert.Equal(-32009, protocol.Issue!.RpcErrorCode);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Default stage set
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DefaultStages_HealthyEndpoint_FourPass_ProbeAndCensusNotRequested()
    {
        using var rig = new ReadinessTestRig();
        rig.WithHealthyEndpoint();

        var report = await rig.RunAsync(rig.Options());

        Assert.Equal(
            new[] { ReadinessStage.Config, ReadinessStage.Auth, ReadinessStage.Discovery, ReadinessStage.Protocol, ReadinessStage.Probe, ReadinessStage.Census },
            report.Stages.Select(s => s.Stage).ToArray());
        Assert.All(report.Stages.Take(4), s => Assert.Equal(ReadinessResult.Pass, s.Result));
        foreach (var stage in report.Stages.Skip(4))
        {
            Assert.Equal(ReadinessResult.Skipped, stage.Result);
            Assert.False(stage.Requested);
            Assert.False(stage.SkippedForConsent);
            Assert.Contains("--probe", stage.SkipReason, StringComparison.Ordinal);
        }

        Assert.True(report.AllPassed);
        Assert.False(report.AnyFailed);
        Assert.False(report.AnySkippedForConsent);
        Assert.Equal("PASS", report.Verdict);
        Assert.Equal(0, rig.Handler.CountOf("SendMessage"));
        Assert.Equal(Path.Combine(rig.Diagnostics.FullName, $"m365-trace-{report.RunId}.jsonl"), report.TraceFile);
        Assert.Equal(Path.Combine(rig.Diagnostics.FullName, "m365-issues.jsonl"), report.IssueFile);
        Assert.True(report.EndedAt >= report.StartedAt);
    }

    [Fact]
    public async Task EveryStage_IsTracedOnTheDoctorStage()
    {
        using var rig = new ReadinessTestRig();
        rig.WithHealthyEndpoint();

        var report = await rig.RunAsync(rig.Options());

        var codes = Codes(report.TraceFile);
        Assert.Equal("config.diagnostics_ready", codes[0]);
        foreach (var expected in new[]
        {
            "doctor.started", "doctor.config.start", "doctor.config.pass", "doctor.auth.start", "doctor.auth.pass",
            "doctor.discovery.start", "discovery.card_fetched", "doctor.discovery.pass", "doctor.protocol.start", "rpc.expected_error",
            "doctor.protocol.pass", "doctor.probe.skipped", "doctor.census.skipped", "doctor.finished",
        })
        {
            Assert.Contains(expected, codes);
        }

        // The TaskNotFound the protocol stage provokes on purpose is no anomaly: no Error-level rpc.error and no mirrored
        // issue.* line reaches the trace (or the verbose console) for it.
        Assert.DoesNotContain("rpc.error", codes);
        Assert.DoesNotContain(codes, c => c.StartsWith("issue.", StringComparison.Ordinal));
        Assert.DoesNotContain("rpc.error", rig.Console.ToString(), StringComparison.Ordinal);

        foreach (var line in ReadLines(report.TraceFile).Where(l => l.Contains("\"code\":\"doctor.", StringComparison.Ordinal)))
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal("Doctor", document.RootElement.GetProperty("stage").GetString());
            Assert.Equal(report.RunId, document.RootElement.GetProperty("runId").GetString());
        }

        // The verbose console saw the same events.
        Assert.Contains("doctor.protocol.pass", rig.Console.ToString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 5. Probe
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Probe_WithoutConsent_IsSkippedForConsent_AndNothingIsSent()
    {
        using var rig = new ReadinessTestRig();
        rig.WithHealthyEndpoint().Handler.OnRpc("SendMessage", CompletedWithAttributions);

        var report = await rig.RunAsync(rig.Options(probe: true, consent: false));

        var probe = report.Find(ReadinessStage.Probe)!;
        Assert.Equal(ReadinessResult.Skipped, probe.Result);
        Assert.True(probe.Requested);
        Assert.True(probe.SkippedForConsent);
        Assert.Equal(ReadinessStageResult.ConsentSkipReason, probe.SkipReason);
        var census = report.Find(ReadinessStage.Census)!;
        Assert.Equal(ReadinessResult.Skipped, census.Result);
        Assert.True(census.SkippedForConsent);

        Assert.True(report.AnySkippedForConsent);
        Assert.False(report.AnyFailed);
        Assert.False(report.AllPassed);
        Assert.Equal("INCOMPLETE", report.Verdict);
        Assert.Equal(0, rig.Handler.CountOf("SendMessage"));
    }

    [Fact]
    public async Task Probe_WithConsent_CompletedWithAnswerAndAttributions_Passes()
    {
        using var rig = new ReadinessTestRig();
        rig.Handler.OnCard(DefaultCard).OnRpc("SendMessage", CompletedWithAttributions);

        var report = await rig.RunAsync(rig.Options(stages: ConfigAndProbe, consent: true));

        var probe = report.Find(ReadinessStage.Probe)!;
        Assert.Equal(ReadinessResult.Pass, probe.Result);
        Assert.Equal("Completed", EvidenceOf(probe, "outcome"));
        Assert.Equal("task-2c9f4e7a-3b1d-4a6e-8f5c-7d0e1b9a2c43", EvidenceOf(probe, "task id"));
        Assert.Equal("ctx-5e0c7a2b-91d4-4f8e-b6a3-0d2f8c1e7b55", EvidenceOf(probe, "context id"));
        Assert.Contains("TASK_STATE_COMPLETED", EvidenceOf(probe, "observed states"), StringComparison.Ordinal);
        Assert.Equal(AnswerLength(CompletedWithAttributions).ToString(System.Globalization.CultureInfo.InvariantCulture), EvidenceOf(probe, "answer length"));
        Assert.StartsWith("Project Atlas is on schedule", EvidenceOf(probe, "answer (first 80 characters)"), StringComparison.Ordinal);
        Assert.Equal("2", EvidenceOf(probe, "attributions"));
        Assert.Equal("0", EvidenceOf(probe, "recovery polls (GetTask)"));
        Assert.Equal(Microsoft365ReadinessCheck.ProbePrompt, EvidenceOf(probe, "prompt"));
        Assert.Contains(probe.Warnings, w => w.Contains("not the single word READY", StringComparison.Ordinal));

        var send = Assert.Single(rig.Handler.Requests, r => r.JsonRpcMethod == "SendMessage");
        Assert.Contains(Microsoft365ReadinessCheck.ProbePrompt, send.BodyJson!, StringComparison.Ordinal);

        // The trace never carries the prompt or the answer; the report does.
        var trace = ReadText(report.TraceFile);
        Assert.DoesNotContain(Microsoft365ReadinessCheck.ProbePrompt, trace, StringComparison.Ordinal);
        Assert.DoesNotContain("Project Atlas", trace, StringComparison.Ordinal);
        Assert.Contains("Project Atlas", ReadinessReportRenderer.ToMarkdown(report), StringComparison.Ordinal);

        var census = report.Find(ReadinessStage.Census)!;
        Assert.Equal(ReadinessResult.Skipped, census.Result);
        Assert.False(census.Requested);
        Assert.True(report.AllPassed);
    }

    [Fact]
    public async Task Probe_InputRequired_FailsNamingTheInteraction_AndApprovesNothing()
    {
        using var rig = new ReadinessTestRig();
        rig.Handler.OnCard(DefaultCard).OnRpc("SendMessage", "envelopes/send-task-input-required-confirmation.json");

        var report = await rig.RunAsync(rig.Options(stages: ConfigAndProbe, consent: true));

        var probe = report.Find(ReadinessStage.Probe)!;
        Assert.Equal(ReadinessResult.Fail, probe.Result);
        Assert.Contains("InputRequired", probe.Summary, StringComparison.Ordinal);
        Assert.Contains("confirmation", probe.Summary, StringComparison.Ordinal);
        Assert.Contains("nothing was approved", probe.Summary, StringComparison.Ordinal);
        Assert.Equal(1, rig.Handler.CountOf("SendMessage"));
    }

    // ---------------------------------------------------------------------------------------------------------
    // 6. Census
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Census_Native118Probe_ReusesTheTurn_AndRecordsTheArtifactsAndCancelTask()
    {
        using var rig = new ReadinessTestRig();
        rig.Handler.OnCard(DefaultCard).OnRpc("SendMessage", CompletedWithDiagnostics).OnRpc("CancelTask", NotCancelable);

        var report = await rig.RunAsync(rig.Options(
            stages: new HashSet<ReadinessStage> { ReadinessStage.Config, ReadinessStage.Probe, ReadinessStage.Census },
            consent: true,
            profile: DiagnosticProfile.Native118Diagnostics));

        Assert.Equal(ReadinessResult.Pass, report.Find(ReadinessStage.Probe)!.Result);
        var census = report.Find(ReadinessStage.Census)!;
        Assert.Equal(ReadinessResult.Pass, census.Result);
        Assert.StartsWith("the probe turn", EvidenceOf(census, "census turn"), StringComparison.Ordinal);
        Assert.StartsWith("yes", EvidenceOf(census, "diagnostic modes accepted"), StringComparison.Ordinal);
        Assert.StartsWith("present", EvidenceOf(census, "Developer Info artifact"), StringComparison.Ordinal);
        Assert.StartsWith("present", EvidenceOf(census, "Retrieval Info artifact"), StringComparison.Ordinal);
        Assert.Equal("unicode-cite", EvidenceOf(census, "citation marker format"));
        Assert.StartsWith("error: JSON-RPC -32002 (TaskNotCancelable", EvidenceOf(census, "CancelTask on a completed task"), StringComparison.Ordinal);
        Assert.Equal("true", EvidenceOf(census, "streaming (card)"));

        Assert.Equal(1, rig.Handler.CountOf("SendMessage"));
        Assert.Equal(1, rig.Handler.CountOf("CancelTask"));
        Assert.DoesNotContain(ReadLines(report.IssueFile), line => line.Contains("\"rpcErrorCode\":-32002", StringComparison.Ordinal));
        Assert.Contains("doctor.census.expected_not_cancelable", Codes(report.TraceFile));
    }

    [Fact]
    public async Task Census_PublicProbe_SendsASecondTurnUnderNative118()
    {
        using var rig = new ReadinessTestRig();
        rig.Handler.OnCard(DefaultCard).OnRpc("SendMessage", CompletedWithAttributions).OnRpc("CancelTask", NotCancelable);

        var report = await rig.RunAsync(rig.Options(
            stages: new HashSet<ReadinessStage> { ReadinessStage.Config, ReadinessStage.Probe, ReadinessStage.Census },
            consent: true));

        var census = report.Find(ReadinessStage.Census)!;
        Assert.Equal(ReadinessResult.Pass, census.Result);
        Assert.StartsWith("a second turn", EvidenceOf(census, "census turn"), StringComparison.Ordinal);
        Assert.StartsWith("absent", EvidenceOf(census, "Developer Info artifact"), StringComparison.Ordinal);
        Assert.StartsWith("absent", EvidenceOf(census, "Retrieval Info artifact"), StringComparison.Ordinal);

        var sends = rig.Handler.Requests.Where(r => r.JsonRpcMethod == "SendMessage").ToArray();
        Assert.Equal(2, sends.Length);
        Assert.DoesNotContain("developer-info", sends[0].BodyJson!, StringComparison.Ordinal);
        Assert.Contains("developer-info", sends[1].BodyJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Census_PublicProbe_Native118TurnRejected_PassesAsARefusalWithAWarning()
    {
        using var rig = new ReadinessTestRig();
        rig.Handler.OnCard(DefaultCard)
            .OnRpcSequence("SendMessage", CompletedWithAttributions, "envelopes/send-task-rejected.json")
            .OnRpc("CancelTask", NotCancelable);

        var report = await rig.RunAsync(rig.Options(
            stages: new HashSet<ReadinessStage> { ReadinessStage.Config, ReadinessStage.Probe, ReadinessStage.Census },
            consent: true));

        var census = report.Find(ReadinessStage.Census)!;
        Assert.Equal(ReadinessResult.Pass, census.Result);
        Assert.StartsWith("no: the service refused the turn", EvidenceOf(census, "diagnostic modes accepted"), StringComparison.Ordinal);
        Assert.StartsWith("not observed", EvidenceOf(census, "Developer Info artifact"), StringComparison.Ordinal);
        var warning = Assert.Single(census.Warnings);
        Assert.Contains("completed under the public profile", warning, StringComparison.Ordinal);
        Assert.Contains("native118 refused", census.Summary, StringComparison.Ordinal);
        Assert.Equal(2, rig.Handler.CountOf("SendMessage"));
    }

    [Fact]
    public async Task Census_PublicProbe_Native118HttpBadRequest_PassesAsARefusal()
    {
        using var rig = new ReadinessTestRig();
        rig.Handler.OnCard(DefaultCard)
            .OnRpcSequence(
                "SendMessage",
                FakeWorkIqResponse.Json(CompletedWithAttributions),
                FakeWorkIqResponse.Fail(HttpStatusCode.BadRequest))
            .OnRpc("CancelTask", NotCancelable);

        var report = await rig.RunAsync(rig.Options(
            stages: new HashSet<ReadinessStage> { ReadinessStage.Config, ReadinessStage.Probe, ReadinessStage.Census },
            consent: true));

        var census = report.Find(ReadinessStage.Census)!;
        Assert.Equal(ReadinessResult.Pass, census.Result);
        Assert.Contains("HTTP 400", EvidenceOf(census, "diagnostic modes accepted"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Census_Native118TurnIndeterminate_FailsAsNotMeasured()
    {
        using var rig = new ReadinessTestRig();
        rig.Handler.OnCard(DefaultCard)
            .OnRpcSequence(
                "SendMessage",
                FakeWorkIqResponse.Json(CompletedWithAttributions),
                FakeWorkIqResponse.Throw(new HttpRequestException("connection reset")))
            .OnRpc("CancelTask", NotCancelable);

        var report = await rig.RunAsync(rig.Options(
            stages: new HashSet<ReadinessStage> { ReadinessStage.Config, ReadinessStage.Probe, ReadinessStage.Census },
            consent: true));

        Assert.Equal(ReadinessResult.Pass, report.Find(ReadinessStage.Probe)!.Result);
        var census = report.Find(ReadinessStage.Census)!;
        Assert.Equal(ReadinessResult.Fail, census.Result);
        Assert.Contains("the census could not be measured", census.Summary, StringComparison.Ordinal);
        Assert.StartsWith("unknown", EvidenceOf(census, "diagnostic modes accepted"), StringComparison.Ordinal);
        Assert.Contains("doctor.census.failed", Codes(report.IssueFile));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Run-level rules
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ContinueAfterFailure_RunsEveryStage()
    {
        using var rig = new ReadinessTestRig();
        rig.Handler.OnCard(DefaultCard)
            .FailWith("GetTask", HttpStatusCode.Forbidden)
            .OnRpc("SendMessage", CompletedWithAttributions)
            .OnRpc("CancelTask", NotCancelable);

        var report = await rig.RunAsync(rig.Options(probe: true, consent: true, continueAfterFailure: true));

        Assert.DoesNotContain(report.Stages, s => s.Result == ReadinessResult.Skipped);
        Assert.Equal(ReadinessResult.Fail, report.Find(ReadinessStage.Protocol)!.Result);
        Assert.Equal(ReadinessResult.Pass, report.Find(ReadinessStage.Probe)!.Result);
        Assert.Equal(ReadinessResult.Pass, report.Find(ReadinessStage.Census)!.Result);
        Assert.True(report.AnyFailed);
        Assert.Equal("FAIL", report.Verdict);
    }

    [Fact]
    public async Task NoOutput_EverContainsTheToken()
    {
        using var rig = new ReadinessTestRig();
        rig.Handler.OnCard(DefaultCard)
            .OnRpc("GetTask", TaskNotFoundReply)
            .OnRpc("SendMessage", CompletedWithAttributions)
            .OnRpc("CancelTask", NotCancelable);
        var raw = new DirectoryInfo(Path.Combine(rig.Root.FullName, "raw"));

        var report = await rig.RunAsync(rig.Options(probe: true, consent: true, rawCapture: raw));

        Assert.True(report.AllPassed, string.Join("; ", report.Stages.Select(s => $"{s.Stage}={s.Result}: {s.Summary}")));
        Assert.All(rig.Handler.Requests, r => Assert.Equal("Bearer", r.AuthorizationScheme));

        var outputs = new List<(string Name, string Text)>
        {
            ("readiness.md", ReadinessReportRenderer.ToMarkdown(report)),
            ("readiness.json", ReadinessReportRenderer.ToJson(report)),
            ("trace", ReadText(report.TraceFile)),
            ("issues", ReadText(report.IssueFile)),
            ("console", rig.Console.ToString()),
        };
        using (var console = new StringWriter())
        {
            ReadinessReportRenderer.WriteConsole(report, console);
            outputs.Add(("console view", console.ToString()));
        }

        Assert.True(raw.Exists, "the raw capture folder was written");
        outputs.AddRange(raw.EnumerateFiles("*", SearchOption.AllDirectories).Select(f => (f.FullName, File.ReadAllText(f.FullName))));

        foreach (var (name, text) in outputs)
        {
            Assert.False(text.Contains(FakeToken, StringComparison.Ordinal), $"{name} contains the token");
        }

        Assert.NotEmpty(outputs.Single(o => o.Name == "trace").Text);
    }

    private static int AnswerLength(string fixture)
    {
        var root = JsonNode.Parse(FixtureLoader.Text(fixture))!;
        return root["result"]!["task"]!["artifacts"]![0]!["parts"]!.AsArray().Sum(p => p!["text"]!.GetValue<string>().Length);
    }
}
