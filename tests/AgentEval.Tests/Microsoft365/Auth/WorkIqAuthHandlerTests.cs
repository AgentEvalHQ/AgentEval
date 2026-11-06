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
using Microsoft.Identity.Client;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Auth;

/// <summary>
/// <see cref="WorkIqAuthHandler"/>: bearer + <c>A2A-Version</c> + <c>User-Agent</c> on card and RPC requests,
/// <c>request-id</c> capture, 401/403 tracing with the device-code hint (the issue is the client's, once), the trust
/// check before any token use, a token failure registered once by the auth layer, and the guarantee that no token
/// reaches a trace event.
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
    public async Task SendAsync_Http401_TracedWithHint_OneIssue_NeverResent()
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

        // The handler traces the 401; the client, which ended the discovery, registers its one issue.
        var issue = Assert.Single(h.Issues.Issues);
        Assert.Equal(WorkIqIssueCategory.Authentication, issue.Category);
        Assert.Equal(401, issue.HttpStatus);
        Assert.Equal(WorkIqTraceStage.Discovery, issue.Stage);
    }

    [Fact]
    public async Task SendAsync_Http403_TracedAsPermission_OneIssue()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.FailWith("SendMessage", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.SendAsync(NewSend(), CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.Forbidden, ex.Failure.Category);
        Assert.Single(h.Events("auth.forbidden"));
        var issue = Assert.Single(h.Issues.Issues);
        Assert.Equal(WorkIqIssueCategory.Permission, issue.Category);
        Assert.Equal(WorkIqTraceStage.Send, issue.Stage);
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
        var issues = new ListIssueSink();
        using var diagnostics = new WorkIqDiagnostics("run-auth", null, [trace], [issues]);
        var handler = new WorkIqAuthHandler(tokens)
        {
            InnerHandler = fake,
            Diagnostics = diagnostics,
            Policy = new WorkIqTrustPolicy([Microsoft365Config.DefaultTrustedHost]),
        };
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => http.GetAsync("https://example.invalid/a2a/.well-known/agent-card.json"));

        // Traced here; the layer that ends the operation registers the one issue (the classifier says Config for an
        // untrusted host: the fix is in the trustedHosts config).
        Assert.Empty(issues.Issues);
        Assert.Equal(WorkIqIssueCategory.Config, WorkIqIssueClassifier.FromFailure(ex.Failure).Category);

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

        // A token failure is registered once, by the auth layer, and marked so the client above only traces it.
        Assert.Equal(WorkIqTokenFailure.ProviderFailed, ex.Failure.TokenFailure);
        var issue = Assert.Single(h.Issues.Issues);
        Assert.Equal(WorkIqTraceStage.Auth, issue.Stage);
        Assert.Equal(WorkIqIssueCategory.Authentication, issue.Category);
        Assert.Equal(WorkIqIssueClassifier.FromFailure(ex.Failure).Code, issue.Code);
    }

    [Fact]
    public async Task SendAsync_TokenProviderProtocolException_PassesThrough_OneIssue()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        var failure = new WorkIqFailure(WorkIqFailureCategory.Forbidden, "MSAL: consent missing", OutcomeCertainty.Known);
        h.Tokens!.Failure = new WorkIqProtocolException(failure);

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.DiscoverAsync(CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.Forbidden, ex.Failure.Category);
        Assert.Equal(WorkIqTokenFailure.ProviderFailed, ex.Failure.TokenFailure);
        h.Handler.AssertNoRequests();
        Assert.Single(h.Events("auth.failed"));
        Assert.Equal(WorkIqIssueCategory.Permission, Assert.Single(h.Issues.Issues).Category);
    }

    [Fact]
    public async Task SendAsync_TokenProviderMsalFailure_OneIssueWithTheAadstsCode()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        var msal = new MsalServiceException("unauthorized_client", "AADSTS7000218: The request body must contain the following parameter: 'client_assertion' or 'client_secret'.", 400);

        // The shape the device-code provider wraps an MSAL failure in: the codes in the safe message, MSAL inside.
        var wrapped = new WorkIqFailure(WorkIqFailureCategory.Unauthorized, "MSAL MsalServiceException during device-code sign-in: error code unauthorized_client, AADSTS7000218, HTTP 400", OutcomeCertainty.Known, HttpStatus: 400);
        h.Tokens!.Failure = new WorkIqProtocolException(wrapped, msal);

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.DiscoverAsync(CancellationToken.None));

        // One issue, registered by the auth layer and classified from the failure (no second one from the client).
        var issue = Assert.Single(h.Issues.Issues);
        var expected = WorkIqIssueClassifier.FromFailure(ex.Failure, WorkIqTraceStage.Auth);
        Assert.Equal("AADSTS7000218", issue.AadstsCode);
        Assert.Equal(expected.Category, issue.Category);
        Assert.Equal(expected.Code, issue.Code);
        Assert.Equal(expected.SuggestedCause, issue.SuggestedCause);
        Assert.Equal(WorkIqTokenFailure.ProviderFailed, ex.Failure.TokenFailure);
    }

    [Fact]
    public async Task SendAsync_EmptyToken_Unauthorized_OneIssue()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Tokens!.Token = "   ";

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.DiscoverAsync(CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.Unauthorized, ex.Failure.Category);
        Assert.Equal(WorkIqTokenFailure.EmptyToken, ex.Failure.TokenFailure);
        h.Handler.AssertNoRequests();
        Assert.Equal(WorkIqTraceStage.Auth, Assert.Single(h.Issues.Issues).Stage);
    }

    [Fact]
    public async Task AcquireTokenAsync_StoppedOutsideTheHttpTimeout_SignInNotCompletedOneIssueStampedWithTheTurn()
    {
        var tokens = new StaticTokenProvider();
        var trace = new ListTraceSink();
        var issues = new ListIssueSink();
        using var diagnostics = new WorkIqDiagnostics("run-auth", null, [trace], [issues]);
        var handler = new WorkIqAuthHandler(tokens) { Diagnostics = diagnostics };
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => handler.AcquireTokenAsync(
            WorkIqOperation.Send,
            "POST https://workiq.svc.cloud.microsoft/a2a/",
            outsideHttpTimeout: true,
            stop.Token,
            new WorkIqCallContext { CaseId = "case-7", TurnId = "turn-7" }));

        Assert.Equal(WorkIqTokenFailure.SignInNotCompleted, ex.Failure.TokenFailure);
        Assert.True(WorkIqIssueClassifier.IsSignInNotCompleted(ex.Failure));
        var issue = Assert.Single(issues.Issues);
        Assert.Equal("case-7", issue.CaseId);
        Assert.Equal("turn-7", issue.TurnId);
        var expected = WorkIqIssueClassifier.FromFailure(ex.Failure, WorkIqTraceStage.Auth);
        Assert.Equal(expected.Code, issue.Code);
        Assert.Equal(expected.SuggestedCause, issue.SuggestedCause);
    }

    [Fact]
    public void IsSignInNotCompleted_ReadsTheTypedMarker_NotTheMessage()
    {
        var marked = new WorkIqFailure(WorkIqFailureCategory.Unauthorized, "Send: the wait was stopped; nothing was sent", OutcomeCertainty.Known, WorkIqOperation.Send)
        {
            TokenFailure = WorkIqTokenFailure.SignInNotCompleted,
        };
        var phraseOnly = new WorkIqFailure(WorkIqFailureCategory.Unauthorized, "Send: " + WorkIqAuthHandler.SignInNotCompletedText + ": nothing was sent", OutcomeCertainty.Known, WorkIqOperation.Send);

        Assert.True(WorkIqIssueClassifier.IsSignInNotCompleted(marked));
        Assert.True(WorkIqIssueClassifier.IsSignInNotCompleted(marked with { SafeMessage = "reworded" }));
        Assert.False(WorkIqIssueClassifier.IsSignInNotCompleted(phraseOnly));
        Assert.False(WorkIqIssueClassifier.IsSignInNotCompleted(marked with { TokenFailure = WorkIqTokenFailure.ProviderFailed }));
        Assert.False(WorkIqIssueClassifier.IsSignInNotCompleted(null));
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
