// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Capture;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Protocol;
using AgentEval.Tests.Microsoft365.Protocol;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Auth;

/// <summary>
/// <see cref="WorkIqAuthHandler"/>: bearer + <c>A2A-Version</c> + <c>User-Agent</c> on card and RPC requests,
/// <c>request-id</c> capture, 401/403 tracing with the device-code hint, the trust check before any token use,
/// and the guarantee that no token reaches a trace event.
/// </summary>
public sealed class WorkIqAuthHandlerTests
{
    private static Dictionary<string, string> Headers(params (string Name, string Value)[] headers) =>
        headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase);

    private static A2A.SendMessageRequest NewSend() =>
        new() { Message = new A2A.Message { MessageId = "msg-user-auth-1", Role = A2A.Role.User, Parts = [A2A.Part.FromText("hello")] } };

    [Fact]
    public async Task SendAsync_CardAndRpcRequests_CarryBearerVersionAndUserAgent()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("SendMessage", "envelopes/send-direct-message.json");

        await h.Client.DiscoverAsync(CancellationToken.None);
        await h.Client.SendAsync(NewSend(), CancellationToken.None);

        Assert.Equal(2, h.Handler.Requests.Count);
        Assert.All(h.Handler.Requests, r =>
        {
            Assert.Equal("Bearer", r.AuthorizationScheme);
            Assert.Equal("[redacted]", r.Headers["Authorization"]);
            Assert.Equal("1.0", r.Headers[WorkIqAuthHandler.ProtocolVersionHeader]);
            Assert.StartsWith(WorkIqAuthHandler.UserAgentProduct + "/", r.Headers["User-Agent"], StringComparison.Ordinal);
        });
        Assert.Equal(2, h.Tokens!.Calls);
        Assert.Equal(2, h.Events("auth.token_requested").Count);
        Assert.Equal(2, h.Events("auth.token_acquired").Count);
        Assert.All(h.Events("auth.token_acquired"), e => Assert.NotNull(e.ElapsedMs));
    }

    [Fact]
    public async Task SendAsync_RequestIdHeader_CapturedIntoResultAndTrace()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("SendMessage", "envelopes/send-direct-message.json", headers: Headers(("request-id", "req-auth-7")));

        var result = await h.Client.SendAsync(NewSend(), CancellationToken.None);

        Assert.Equal("req-auth-7", result.RequestId);
        var responses = h.Events("auth.response");
        Assert.Equal(2, responses.Count);
        Assert.Null(responses[0].RequestId);
        Assert.Equal("req-auth-7", responses[1].RequestId);
        Assert.Equal(200, responses[1].HttpStatus);
    }

    [Fact]
    public async Task SendAsync_Http401_TracedWithHintAndIssueNeverResent()
    {
        using var h = WorkIqClientHarness.Create();
        h.Handler.FailWith(FakeWorkIqHandler.CardMethod, HttpStatusCode.Unauthorized, headers: Headers(("request-id", "req-401")));

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.DiscoverAsync(CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.Unauthorized, ex.Failure.Category);
        Assert.Equal(1, h.Handler.CardRequestCount);
        var unauthorized = Assert.Single(h.Events("auth.unauthorized"));
        Assert.Equal(WorkIqTraceLevel.Error, unauthorized.Level);
        Assert.Equal(401, unauthorized.HttpStatus);
        Assert.Equal("req-401", unauthorized.RequestId);
        Assert.Contains("re-run the device-code sign-in", unauthorized.Message, StringComparison.Ordinal);
        Assert.Contains(h.Config.Scope, unauthorized.Message, StringComparison.Ordinal);
        Assert.Contains(h.Issues.Issues, i => i.Code == "auth.unauthorized" && i.Category == WorkIqIssueCategory.Authentication && i.HttpStatus == 401);
    }

    [Fact]
    public async Task SendAsync_Http403_TracedAsPermission()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.FailWith("SendMessage", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.SendAsync(NewSend(), CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.Forbidden, ex.Failure.Category);
        Assert.Single(h.Events("auth.forbidden"));
        Assert.Contains(h.Issues.Issues, i => i.Code == "auth.forbidden" && i.Category == WorkIqIssueCategory.Permission);
    }

    [Fact]
    public async Task SendAsync_NoTokenInAnyTraceEvent()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("SendMessage", "envelopes/send-direct-message.json");

        await h.Client.SendAsync(NewSend(), CancellationToken.None);

        h.AssertNoSecretInDiagnostics();
        Assert.Contains(h.Events("auth.token_acquired"), e => e.Message.Contains(StaticTokenProvider.DefaultToken.Length.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendAsync_UntrustedDestination_RefusedBeforeTokenRequest()
    {
        var fake = new FakeWorkIqHandler();
        var tokens = new StaticTokenProvider();
        var trace = new ListTraceSink();
        using var diagnostics = new WorkIqDiagnostics("run-auth", null, [trace], [new ListIssueSink()]);
        var handler = new WorkIqAuthHandler(tokens)
        {
            InnerHandler = fake,
            Diagnostics = diagnostics,
            Policy = new WorkIqTrustPolicy([Microsoft365Config.DefaultTrustedHost]),
        };
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => http.GetAsync("https://example.invalid/a2a/.well-known/agent-card.json"));

        Assert.Equal(WorkIqFailureCategory.UntrustedHost, ex.Failure.Category);
        Assert.Equal(WorkIqOperation.Discover, ex.Failure.Operation);
        Assert.Equal(0, tokens.Calls);
        fake.AssertNoRequests();
        Assert.Contains(trace.Events, e => e.Code == "auth.untrusted_destination" && e.Level == WorkIqTraceLevel.Error);
    }

    [Fact]
    public async Task SendAsync_HttpScheme_RefusedByPolicy()
    {
        var fake = new FakeWorkIqHandler();
        var tokens = new StaticTokenProvider();
        var handler = new WorkIqAuthHandler(tokens) { InnerHandler = fake, Policy = new WorkIqTrustPolicy([Microsoft365Config.DefaultTrustedHost]) };
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => http.GetAsync("http://workiq.svc.cloud.microsoft/a2a/.well-known/agent-card.json"));

        Assert.Equal(WorkIqFailureCategory.UntrustedHost, ex.Failure.Category);
        Assert.Equal(0, tokens.Calls);
    }

    [Fact]
    public async Task SendAsync_TokenProviderThrows_UnauthorizedNothingSent()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Tokens!.Failure = new InvalidOperationException("msal exploded with a secret-looking detail");

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.DiscoverAsync(CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.Unauthorized, ex.Failure.Category);
        Assert.Contains(nameof(InvalidOperationException), ex.Failure.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("exploded", ex.Failure.SafeMessage, StringComparison.Ordinal);
        h.Handler.AssertNoRequests();
        Assert.Single(h.Events("auth.failed"));
        Assert.Contains(h.Issues.Issues, i => i.Code == "auth.token_acquisition_failed");
    }

    [Fact]
    public async Task SendAsync_TokenProviderProtocolException_PassesThrough()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        var failure = new WorkIqFailure(WorkIqFailureCategory.Forbidden, "MSAL: consent missing", OutcomeCertainty.Known);
        h.Tokens!.Failure = new WorkIqProtocolException(failure);

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.DiscoverAsync(CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.Forbidden, ex.Failure.Category);
        h.Handler.AssertNoRequests();
        Assert.Single(h.Events("auth.failed"));
    }

    [Fact]
    public async Task SendAsync_EmptyToken_Unauthorized()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Tokens!.Token = "   ";

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.DiscoverAsync(CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.Unauthorized, ex.Failure.Category);
        h.Handler.AssertNoRequests();
    }

    [Fact]
    public async Task SendAsync_CallerCanceledDuringTokenAcquisition_PropagatesCancellation()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.DiscoverAsync(cts.Token));

        Assert.Equal(WorkIqFailureCategory.Canceled, ex.Failure.Category);
        h.Handler.AssertNoRequests();
    }

    [Theory]
    [InlineData("1.0.0-rc.1+abc123", "AgentEval.Microsoft365/1.0.0-rc.1+abc123")]
    [InlineData("a b/c(d)", "AgentEval.Microsoft365/a-b-c-d-")]
    [InlineData(null, "AgentEval.Microsoft365/unknown")]
    [InlineData("  ", "AgentEval.Microsoft365/unknown")]
    public void BuildUserAgent_KeepsOnlyTokenCharacters(string? version, string expected)
    {
        Assert.Equal(expected, WorkIqAuthHandler.BuildUserAgent(version));
    }

    [Fact]
    public void UserAgent_NamesPackageAndAdapterVersion()
    {
        Assert.StartsWith("AgentEval.Microsoft365/", WorkIqAuthHandler.UserAgent, StringComparison.Ordinal);
        Assert.Equal(WorkIqAuthHandler.BuildUserAgent(WorkIqDiagnostics.AdapterVersion), WorkIqAuthHandler.UserAgent);
    }

    [Theory]
    [InlineData("https://workiq.svc.cloud.microsoft/a2a/.well-known/agent-card.json", true)]
    [InlineData("https://workiq.svc.cloud.microsoft/a2a/U_1.2/.WELL-KNOWN/AGENT-CARD.JSON", true)]
    [InlineData("https://workiq.svc.cloud.microsoft/a2a/", false)]
    public void IsCardRequest_ByPathSuffix(string url, bool expected)
    {
        Assert.Equal(expected, WorkIqAuthHandler.IsCardRequest(new Uri(url)));
    }

    [Fact]
    public void Constructor_NullProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkIqAuthHandler(null!));
    }
}
