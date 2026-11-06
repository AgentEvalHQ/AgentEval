// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Microsoft365Demo.Tests.TestHelpers;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Microsoft.Identity.Client;

namespace AgentEval.Microsoft365Demo.Tests;

/// <summary>
/// The readiness step of the demo, offline, through the provider's real readiness check: which card URI is fetched for the
/// default agent and for an agent id, what <c>--readiness-only</c> and a missing consent exit with, and how each readiness
/// failure reaches the tester — the stage line, the issue detail block (category, code, HTTP status, request-id, cause,
/// action), <c>issues.jsonl</c> and <c>summary.md</c>. No prompt is ever sent in this class.
/// </summary>
public sealed class ReadinessTests : IDisposable
{
    private const string DefaultCardPath = "/a2a/.well-known/agent-card.json";

    private readonly DemoRig _rig = new();

    public void Dispose() => _rig.Dispose();

    // ---------------------------------------------------------------------------------------------------------
    // Passing readiness
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadinessOnly_HealthyEndpoint_ExitsZero_WritesTheReports_AndPrintsTheCardName()
    {
        _rig.WithHealthyEndpoint();

        var run = await _rig.RunAsync("--readiness-only");

        run.AssertExit(0);
        Assert.Contains("card 'Work IQ'", run.Out, StringComparison.Ordinal);
        Assert.Contains("Result: PASS (exit 0)", run.Out, StringComparison.Ordinal);

        // The reports are in the run folder.
        Assert.Contains("Work IQ", run.Text("readiness.md"), StringComparison.Ordinal);
        var readiness = run.Json("readiness.json");
        Assert.Equal("agenteval.m365.readiness/v1", readiness.GetProperty("schema").GetString());
        Assert.Equal("PASS", readiness.GetProperty("result").GetString());
        var requested = readiness.GetProperty("stages").EnumerateArray()
            .Where(s => s.GetProperty("requested").GetBoolean())
            .Select(s => s.GetProperty("stage").GetString() + "=" + s.GetProperty("result").GetString())
            .ToArray();
        Assert.Equal(new[] { "config=PASS", "auth=PASS", "discovery=PASS", "protocol=PASS" }, requested);
        Assert.True(run.File("summary.md").Exists);
        Assert.True(run.File("console.log").Exists);
        Assert.True(run.File("trace.jsonl").Exists);

        // The handshake only: one card, one GetTask on a random doctor id, no prompt.
        Assert.True(_rig.Handler.CardRequestCount >= 1);
        var getTask = Assert.Single(_rig.Handler.Requests, r => r.JsonRpcMethod == "GetTask");
        Assert.StartsWith("doctor-", JsonNode.Parse(getTask.BodyJson!)!["params"]!["id"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, _rig.Handler.CountOf("SendMessage"));
        Assert.All(_rig.Handler.Requests, r => Assert.Equal("Bearer", r.AuthorizationScheme));
        Assert.All(_rig.Handler.Requests, r => Assert.Equal("[redacted]", r.Headers["Authorization"]));
    }

    [Fact]
    public async Task NoConsent_ReadinessPasses_StopsBeforeThePrompt_AndExitsEleven()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();

        var run = await _rig.RunAsync();

        run.AssertExit(11);
        Assert.Contains(DemoRig.Consent, run.Out, StringComparison.Ordinal);
        Assert.Contains("nothing was sent to the agent", run.Out, StringComparison.Ordinal);
        Assert.Equal(0, _rig.Handler.CountOf("SendMessage"));
        Assert.Contains("NOT MEASURED", run.Text("summary.md"), StringComparison.Ordinal);
        Assert.Contains("did not run", run.Text("results.md"), StringComparison.Ordinal);
        Assert.False(run.File("turns.jsonl").Exists);
    }

    [Fact]
    public async Task ConsentFromTheEnvironmentVariable_RunsThePrompt()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Environment["M365_I_UNDERSTAND_LIVE_SIDE_EFFECTS"] = "true";

        var run = await _rig.RunAsync("--no-benchmark");

        run.AssertExit(0);
        Assert.Equal(1, _rig.Handler.CountOf("SendMessage"));
        Assert.Contains("M365_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true", run.Text("summary.md"), StringComparison.Ordinal);
        Assert.Contains(DemoRig.AnswerStart, run.Out, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Which agent: the card URI
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoAgentId_FetchesTheDefaultAgentsCard()
    {
        _rig.WithHealthyEndpoint();

        var run = await _rig.RunAsync("--readiness-only");

        run.AssertExit(0);
        var cards = _rig.Handler.Requests.Where(r => r.Uri.AbsolutePath.EndsWith(FakeWorkIqHandler.CardPathSuffix, StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(cards);
        Assert.All(cards, r => Assert.Equal("https://workiq.svc.cloud.microsoft" + DefaultCardPath, r.Uri.GetLeftPart(UriPartial.Path)));
        Assert.Contains("the default Work IQ agent", run.Out, StringComparison.Ordinal);
        Assert.Contains("the default Work IQ agent", run.Text("summary.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentIdOption_FetchesThatAgentsCard_AndSendsTheHandshakeToItsInterface()
    {
        _rig.WithHealthyEndpoint(DemoRig.AgentCard);

        var run = await _rig.RunAsync("--readiness-only", "--agent-id", DemoRig.AgentId);

        run.AssertExit(0);
        var cards = _rig.Handler.Requests.Where(r => r.Uri.AbsolutePath.EndsWith(FakeWorkIqHandler.CardPathSuffix, StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(cards);
        Assert.All(cards, r => Assert.Equal($"https://workiq.svc.cloud.microsoft/a2a/{DemoRig.AgentId}/.well-known/agent-card.json", r.Uri.GetLeftPart(UriPartial.Path)));
        var getTask = Assert.Single(_rig.Handler.Requests, r => r.JsonRpcMethod == "GetTask");
        Assert.Equal($"/a2a/{DemoRig.AgentId}/", getTask.Uri.AbsolutePath);
        Assert.Contains("card 'Atlas Project Assistant'", run.Out, StringComparison.Ordinal);
        Assert.Contains(DemoRig.AgentId + " (from --agent-id)", run.Out, StringComparison.Ordinal);
        Assert.Contains("(--agent-id)", run.Text("summary.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentIdOption_OverridesTheConfigsAgentId()
    {
        _rig.WriteConfig("{\"tenantId\":\"11111111-1111-1111-1111-111111111111\",\"appClientId\":\"22222222-2222-2222-2222-222222222222\",\"agentId\":\"U_config.agent\"}");
        _rig.WithHealthyEndpoint(DemoRig.AgentCard);

        var run = await _rig.RunAsync("--readiness-only", "--agent-id", DemoRig.AgentId);

        run.AssertExit(0);
        Assert.All(
            _rig.Handler.Requests.Where(r => r.Uri.AbsolutePath.EndsWith(FakeWorkIqHandler.CardPathSuffix, StringComparison.Ordinal)),
            r => Assert.Equal($"/a2a/{DemoRig.AgentId}/.well-known/agent-card.json", r.Uri.AbsolutePath));
        Assert.DoesNotContain(_rig.Handler.Requests, r => r.Uri.AbsolutePath.Contains("U_config.agent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfigAgentId_WithoutTheOption_FetchesThatAgentsCard()
    {
        _rig.WriteConfig($"{{\"tenantId\":\"11111111-1111-1111-1111-111111111111\",\"appClientId\":\"22222222-2222-2222-2222-222222222222\",\"agentId\":\"{DemoRig.AgentId}\"}}");
        _rig.WithHealthyEndpoint(DemoRig.AgentCard);

        var run = await _rig.RunAsync("--readiness-only");

        run.AssertExit(0);
        Assert.All(
            _rig.Handler.Requests.Where(r => r.Uri.AbsolutePath.EndsWith(FakeWorkIqHandler.CardPathSuffix, StringComparison.Ordinal)),
            r => Assert.Equal($"/a2a/{DemoRig.AgentId}/.well-known/agent-card.json", r.Uri.AbsolutePath));
        Assert.Contains(DemoRig.AgentId + " (from the config)", run.Out, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Failing readiness: what the tester sees
    // ---------------------------------------------------------------------------------------------------------

    public static TheoryData<HttpStatusCode, string, string, string> CardFailures => new()
    {
        // status, issue category, a phrase of the suggested cause, a phrase of the action
        { HttpStatusCode.Unauthorized, "Authentication", "token rejected", "sign in" },
        { HttpStatusCode.Forbidden, "Permission", "lacks WorkIQAgent.Ask consent", "admin consent" },
    };

    [Theory]
    [MemberData(nameof(CardFailures))]
    public async Task CardHttpFailure_ReadinessFails_DetailBlockOnScreen_AndTheIssueIsRegistered(
        HttpStatusCode status,
        string category,
        string cause,
        string action)
    {
        var requestId = "req-card-" + ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _rig.Handler.FailWith(FakeWorkIqHandler.CardMethod, status, headers: new Dictionary<string, string> { ["request-id"] = requestId });
        _rig.Handler.OnRpc("GetTask", DemoRig.TaskNotFoundReply).OnRpc("SendMessage", DemoRig.CompletedWithAnswer);

        var run = await _rig.RunAsync("--verbosity", "detailed", DemoRig.Consent);

        run.AssertExit(1);
        Assert.Contains("✗ readiness: discovery", run.Out, StringComparison.Ordinal);

        // The issue detail block: header, category with who fixes it, the HTTP code, the request-id, the cause, the action.
        Assert.Contains($"! ISSUE {category} ", run.Out, StringComparison.Ordinal);
        var lines = run.OutLines;
        Assert.Contains(lines, l => l.TrimStart().StartsWith("category", StringComparison.Ordinal) && l.Contains(category, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.TrimStart().StartsWith("codes", StringComparison.Ordinal) && l.Contains("HTTP " + ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        Assert.Contains(lines, l => l.TrimStart().StartsWith("request-id", StringComparison.Ordinal) && l.Contains(requestId, StringComparison.Ordinal));
        // The plain-language cause is in the stage's FAILED block ("meaning"): the doctor's own stage issue, which repeats
        // it, is one line on the screen (its full block is in console.log).
        Assert.Contains(lines, l => (l.TrimStart().StartsWith("cause", StringComparison.Ordinal) || l.TrimStart().StartsWith("meaning", StringComparison.Ordinal))
                                    && l.Contains(cause, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.TrimStart().StartsWith("action", StringComparison.Ordinal) && l.Contains(action, StringComparison.Ordinal));
        Assert.Contains(run.Text("console.log").Split('\n'), l => l.TrimStart().StartsWith("cause", StringComparison.Ordinal) && l.Contains(cause, StringComparison.Ordinal));

        // issues.jsonl has it, with the category, the HTTP status and the request-id.
        var issues = run.JsonLines("issues.jsonl");
        Assert.Contains(issues, i =>
            i.GetProperty("category").GetString() == category
            && i.TryGetProperty("httpStatus", out var http) && http.ValueKind == JsonValueKind.Number && http.GetInt32() == (int)status
            && i.TryGetProperty("requestId", out var rid) && rid.GetString() == requestId);
        Assert.Contains(category, run.Text("summary.md"), StringComparison.Ordinal);
        Assert.Contains(cause, run.Text("summary.md"), StringComparison.Ordinal);

        // Readiness failed: nothing past the card was sent, the prompt and the benchmark did not run.
        Assert.Equal(0, _rig.Handler.CountOf("GetTask"));
        Assert.Equal(0, _rig.Handler.CountOf("SendMessage"));
        Assert.Equal("FAIL", run.Json("readiness.json").GetProperty("result").GetString());
        Assert.Empty(LeakScanner.Scan(run.Screen, run.Folder));
    }

    [Fact]
    public async Task UntrustedHostCard_ReadinessFails_TheIssueNamesTheUntrustedHost_AndNoTokenGoesThere()
    {
        _rig.Handler.OnCard(DemoRig.UntrustedHostCard).OnRpc("GetTask", DemoRig.TaskNotFoundReply).OnRpc("SendMessage", DemoRig.CompletedWithAnswer);

        var run = await _rig.RunAsync("--verbosity", "detailed", DemoRig.Consent);

        run.AssertExit(1);
        Assert.Contains("✗ readiness: discovery", run.Out, StringComparison.Ordinal);
        Assert.Contains("UntrustedHost", run.Out, StringComparison.Ordinal);
        Assert.Contains("trustedHosts", run.Out, StringComparison.Ordinal);
        Assert.Contains(run.OutLines, l => l.Contains("! ISSUE ", StringComparison.Ordinal) && l.Contains("untrusted_host", StringComparison.Ordinal));
        Assert.Contains(run.JsonLines("issues.jsonl"), i => (i.GetProperty("code").GetString() ?? "").Contains("untrusted_host", StringComparison.Ordinal)
            || (i.GetProperty("message").GetString() ?? "").Contains("untrusted_host", StringComparison.Ordinal));

        // The refused host never received anything; nothing was sent past the card.
        Assert.DoesNotContain(_rig.Handler.Requests, r => r.Uri.Host.EndsWith("example.invalid", StringComparison.OrdinalIgnoreCase));
        Assert.All(_rig.Handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal(0, _rig.Handler.CountOf("SendMessage"));
    }

    [Fact]
    public async Task SignInFailsWithAadsts65001_TheConsentCauseIsOnScreenAndInTheSummary()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Tokens.Failure = new MsalServiceException(
            "invalid_grant",
            "AADSTS65001: The user or administrator has not consented to use the application with ID '22222222-2222-2222-2222-222222222222'. Trace ID: t Correlation ID: c Timestamp: 2026-11-05 10:00:00Z");

        var run = await _rig.RunAsync(DemoRig.Consent);

        run.AssertExit(1);
        Assert.Contains("✗ readiness: auth", run.Out, StringComparison.Ordinal);
        Assert.Contains("AADSTS65001", run.Out, StringComparison.Ordinal);
        Assert.Contains("not consented", run.Out, StringComparison.Ordinal);
        Assert.Contains("admin consent", run.Out, StringComparison.Ordinal);

        var summary = run.Text("summary.md");
        Assert.Contains("AADSTS65001", summary, StringComparison.Ordinal);
        Assert.Contains("not consented", summary, StringComparison.Ordinal);
        Assert.Contains("### Authentication", summary, StringComparison.Ordinal);
        Assert.Contains(run.JsonLines("issues.jsonl"), i =>
            i.GetProperty("category").GetString() == "Authentication"
            && i.TryGetProperty("aadstsCode", out var code) && code.GetString() == "AADSTS65001");

        // The sign-in failed first: no request reached Work IQ.
        _rig.Handler.AssertNoRequests();
        Assert.Empty(LeakScanner.Scan(run.Screen, run.Folder));
    }

    [Fact]
    public async Task SignInFailsAtDetailed_TheStageVerdictIssueIsOneLine_TheStageBlockHasTheDetail_ConsoleLogHasBoth()
    {
        // One auth failure must not print two near-identical blocks (the doctor's stage issue and the stage's FAILED block).
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Tokens.Failure = new MsalServiceException(
            "invalid_grant",
            "AADSTS65001: The user or administrator has not consented to use the application with ID '22222222-2222-2222-2222-222222222222'. Trace ID: t Correlation ID: c Timestamp: 2026-11-05 10:00:00Z");

        var run = await _rig.RunAsync("--verbosity", "detailed", DemoRig.Consent);

        run.AssertExit(1);
        var lines = run.OutLines;
        Assert.DoesNotContain(lines, l => l.Contains("! ISSUE Authentication doctor.auth.failed", StringComparison.Ordinal));
        Assert.Single(lines, l => l.Contains("! issue Authentication doctor.auth.failed", StringComparison.Ordinal) && l.Contains("the stage's FAILED block has the detail", StringComparison.Ordinal));
        Assert.Single(lines, l => l.Contains("readiness: auth FAILED:", StringComparison.Ordinal));
        Assert.Single(lines, l => l.TrimStart().StartsWith("action", StringComparison.Ordinal) && l.Contains("admin consent", StringComparison.Ordinal));

        // console.log keeps the full issue block and the stage block; issues.jsonl keeps the issue.
        var log = run.Text("console.log");
        Assert.Contains("! ISSUE Authentication doctor.auth.failed", log, StringComparison.Ordinal);
        Assert.Contains("readiness: auth FAILED:", log, StringComparison.Ordinal);
        Assert.Contains(run.JsonLines("issues.jsonl"), i => i.GetProperty("code").GetString() == "doctor.auth.failed");
    }

    [Fact]
    public async Task Readiness_TheIssueRegisterIsIssuesJsonl_FromTheStart_AndTheProfileEvidenceNamesTheDemosOption()
    {
        // The check writes issues.jsonl from its first event, so the trace never names another path; the profile evidence
        // names the demo's own option (--profile), not the CLI's --m365-profile.
        _rig.Handler.FailWith(FakeWorkIqHandler.CardMethod, HttpStatusCode.Unauthorized);

        var run = await _rig.RunAsync("--readiness-only");

        run.AssertExit(1);
        Assert.False(run.File("m365-issues.jsonl").Exists);
        Assert.Contains(run.JsonLines("issues.jsonl"), i => i.GetProperty("code").GetString() == "http.401");
        Assert.Equal(run.File("issues.jsonl").FullName, run.Json("readiness.json").GetProperty("issueFile").GetString());
        foreach (var name in new[] { "trace.jsonl", "console.log", "readiness.md", "readiness.json", "summary.md" })
        {
            Assert.DoesNotContain("m365-issues.jsonl", run.Text(name), StringComparison.Ordinal);
        }

        var ready = Assert.Single(run.JsonLines("trace.jsonl"), e => e.GetProperty("code").GetString() == "config.diagnostics_ready");
        Assert.Contains(run.File("issues.jsonl").FullName, ready.GetProperty("message").GetString(), StringComparison.Ordinal);

        var readiness = run.Text("readiness.md");
        Assert.Contains("PublicAnswerOnly (from --profile; the config says the same)", readiness, StringComparison.Ordinal);
        Assert.DoesNotContain("--m365-profile", readiness, StringComparison.Ordinal);
    }
}
