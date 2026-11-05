// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net.Http.Headers;
using System.Text;
using AgentEval.Microsoft365.Auth;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365.Capture;

/// <summary>
/// A <see cref="DelegatingHandler"/> that authenticates every A2A request: <c>Authorization: Bearer</c> from the
/// <see cref="IWorkIqTokenProvider"/> (only for destinations the trust policy allows), <c>A2A-Version: 1.0</c>
/// (card requests included — Work IQ defaults to 0.3 without it), a <c>User-Agent</c> naming this package, and
/// capture of the <c>request-id</c> response header for correlation. A 401 surfaces
/// <see cref="WorkIqFailureCategory.Unauthorized"/> with a device-code hint and is never resent. Owned by lane A (M11).
/// </summary>
internal sealed class WorkIqAuthHandler : DelegatingHandler
{
    /// <summary>The request header that pins the protocol version.</summary>
    public const string ProtocolVersionHeader = "A2A-Version";

    /// <summary>The only protocol version sent.</summary>
    public const string ProtocolVersion = "1.0";

    /// <summary>The response header carrying the service's correlation id.</summary>
    public const string RequestIdHeader = "request-id";

    /// <summary>The <c>User-Agent</c> product name sent on every request (followed by <c>/&lt;informational version&gt;</c>).</summary>
    public const string UserAgentProduct = "AgentEval.Microsoft365";

    /// <summary>The request option under which the service's <c>request-id</c> response header is copied for the client to read.</summary>
    internal static readonly HttpRequestOptionsKey<string> RequestIdOption = new("AgentEval.Microsoft365.RequestId");

    /// <summary>Creates the handler.</summary>
    /// <param name="tokenProvider">The host-owned token source.</param>
    public WorkIqAuthHandler(IWorkIqTokenProvider tokenProvider)
    {
        TokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
    }

    /// <summary>The host-owned token source.</summary>
    public IWorkIqTokenProvider TokenProvider { get; }

    /// <summary>The diagnostics auth events (<c>auth.*</c>) are traced through; never null.</summary>
    public WorkIqDiagnostics Diagnostics { get; init; } = WorkIqDiagnostics.None;

    /// <summary>
    /// When set, a request to a destination the policy does not trust is refused <b>before</b> a token is
    /// requested or attached (<see cref="WorkIqFailureCategory.UntrustedHost"/>). Null skips the check (the client
    /// enforces the policy at discovery; the factory always sets this as defence in depth).
    /// </summary>
    internal WorkIqTrustPolicy? Policy { get; init; }

    /// <summary>The scope named in the 401 hint; defaults to <see cref="Microsoft365Config.DefaultScope"/>.</summary>
    internal string ExpectedScope { get; init; } = Microsoft365Config.DefaultScope;

    /// <summary>The <c>User-Agent</c> value: <c>AgentEval.Microsoft365/&lt;informational version&gt;</c> (characters outside the HTTP token set replaced).</summary>
    internal static string UserAgent { get; } = BuildUserAgent(WorkIqDiagnostics.AdapterVersion);

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri ?? throw new InvalidOperationException("The request has no URI.");
        var op = IsCardRequest(uri) ? WorkIqOperation.Discover : WorkIqOperation.Unknown;
        var destination = $"{request.Method} {uri.Scheme}://{uri.Host}{uri.AbsolutePath}";

        if (Policy is not null && !Policy.IsTrusted(uri))
        {
            var refused = new WorkIqFailure(
                WorkIqFailureCategory.UntrustedHost,
                $"refused to attach a bearer token to {uri.Scheme}://{uri.Host}: outside the trust policy (trusted: {string.Join(", ", Policy.TrustedHosts)}); nothing was sent",
                OutcomeCertainty.Known,
                op);
            Diagnostics.Trace(WorkIqTraceStage.Auth, "auth.untrusted_destination", refused.SafeMessage, WorkIqTraceLevel.Error);
            Diagnostics.Issue(WorkIqIssue.Create(WorkIqTraceStage.Auth, WorkIqIssueCategory.Protocol, "auth.untrusted_destination", refused.SafeMessage, WorkIqA2AClient.SuggestedCauseOf(WorkIqFailureCategory.UntrustedHost)));
            throw new WorkIqProtocolException(refused);
        }

        var started = Diagnostics.Clock.GetTimestamp();
        Diagnostics.Trace(WorkIqTraceStage.Auth, "auth.token_requested", $"requesting a bearer token for {destination}");

        string token;
        try
        {
            token = await TokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkIqProtocolException ex)
        {
            Diagnostics.Trace(
                WorkIqTraceStage.Auth,
                "auth.failed",
                $"token acquisition failed for {destination}: {ex.Failure.Category} ({ex.Failure.Certainty}): {ex.Failure.SafeMessage}; nothing was sent",
                WorkIqTraceLevel.Error,
                httpStatus: ex.Failure.HttpStatus,
                elapsedMs: Diagnostics.Clock.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            var failure = new WorkIqFailure(
                WorkIqFailureCategory.Unauthorized,
                $"token acquisition failed: {ex.GetType().Name} (nothing was sent); re-run the device-code sign-in; scope must be {ExpectedScope}",
                OutcomeCertainty.Known,
                op);
            Diagnostics.Trace(WorkIqTraceStage.Auth, "auth.failed", failure.SafeMessage, WorkIqTraceLevel.Error, elapsedMs: Diagnostics.Clock.GetElapsedTime(started).TotalMilliseconds);
            Diagnostics.Issue(WorkIqIssue.Create(WorkIqTraceStage.Auth, WorkIqIssueCategory.Authentication, "auth.token_acquisition_failed", failure.SafeMessage, WorkIqA2AClient.SuggestedCauseOf(WorkIqFailureCategory.Unauthorized)));
            throw new WorkIqProtocolException(failure, ex);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            var failure = new WorkIqFailure(
                WorkIqFailureCategory.Unauthorized,
                $"the token provider returned an empty token (nothing was sent); re-run the device-code sign-in; scope must be {ExpectedScope}",
                OutcomeCertainty.Known,
                op);
            Diagnostics.Trace(WorkIqTraceStage.Auth, "auth.failed", failure.SafeMessage, WorkIqTraceLevel.Error);
            Diagnostics.Issue(WorkIqIssue.Create(WorkIqTraceStage.Auth, WorkIqIssueCategory.Authentication, "auth.empty_token", failure.SafeMessage, WorkIqA2AClient.SuggestedCauseOf(WorkIqFailureCategory.Unauthorized)));
            throw new WorkIqProtocolException(failure);
        }

        Diagnostics.Trace(
            WorkIqTraceStage.Auth,
            "auth.token_acquired",
            $"bearer token attached for {destination} ({token.Length} characters)",
            elapsedMs: Diagnostics.Clock.GetElapsedTime(started).TotalMilliseconds);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Remove(ProtocolVersionHeader);
        request.Headers.TryAddWithoutValidation(ProtocolVersionHeader, ProtocolVersion);
        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var requestId = ReadRequestId(response);
        if (requestId is not null)
        {
            request.Options.Set(RequestIdOption, requestId);
        }

        var status = (int)response.StatusCode;
        var elapsedMs = Diagnostics.Clock.GetElapsedTime(started).TotalMilliseconds;
        Diagnostics.Trace(
            WorkIqTraceStage.Auth,
            "auth.response",
            $"{destination} → HTTP {status} (request-id {(requestId is null ? "absent" : "captured")})",
            requestId: requestId,
            httpStatus: status,
            elapsedMs: elapsedMs);

        if (status == 401)
        {
            var hint = $"token rejected: audience/scope/expiry — re-run the device-code sign-in; scope must be {ExpectedScope}";
            Diagnostics.Trace(WorkIqTraceStage.Auth, "auth.unauthorized", $"{destination} → HTTP 401: {hint} (not resent)", WorkIqTraceLevel.Error, requestId: requestId, httpStatus: status, elapsedMs: elapsedMs);
            Diagnostics.Issue(new WorkIqIssue(default, WorkIqTraceStage.Auth, WorkIqIssueCategory.Authentication, "auth.unauthorized", $"HTTP 401 from {uri.Host}: {hint}", WorkIqA2AClient.SuggestedCauseOf(WorkIqFailureCategory.Unauthorized))
            {
                HttpStatus = status,
                RequestId = requestId,
            });
        }
        else if (status == 403)
        {
            var hint = $"the signed-in user or the app lacks the delegated permission, its admin consent or a Work IQ licence (scope {ExpectedScope})";
            Diagnostics.Trace(WorkIqTraceStage.Auth, "auth.forbidden", $"{destination} → HTTP 403: {hint} (not resent)", WorkIqTraceLevel.Error, requestId: requestId, httpStatus: status, elapsedMs: elapsedMs);
            Diagnostics.Issue(new WorkIqIssue(default, WorkIqTraceStage.Auth, WorkIqIssueCategory.Permission, "auth.forbidden", $"HTTP 403 from {uri.Host}: {hint}", WorkIqA2AClient.SuggestedCauseOf(WorkIqFailureCategory.Forbidden))
            {
                HttpStatus = status,
                RequestId = requestId,
            });
        }

        return response;
    }

    /// <summary>Whether <paramref name="uri"/> addresses an agent card (<c>/.well-known/agent-card.json</c>).</summary>
    /// <param name="uri">The request URI.</param>
    internal static bool IsCardRequest(Uri uri) =>
        uri.IsAbsoluteUri && uri.AbsolutePath.EndsWith("/.well-known/agent-card.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the <c>User-Agent</c> value from an informational version, keeping only HTTP token characters.</summary>
    /// <param name="version">The informational version; null or blank becomes <c>unknown</c>.</param>
    internal static string BuildUserAgent(string? version)
    {
        var source = string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim();
        var builder = new StringBuilder(source.Length);
        foreach (var c in source)
        {
            builder.Append(IsTokenChar(c) ? c : '-');
        }

        return UserAgentProduct + "/" + builder;
    }

    private static bool IsTokenChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);

    private static string? ReadRequestId(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues(RequestIdHeader, out var values))
        {
            var first = values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (first is not null)
            {
                return first.Trim();
            }
        }

        return null;
    }
}
