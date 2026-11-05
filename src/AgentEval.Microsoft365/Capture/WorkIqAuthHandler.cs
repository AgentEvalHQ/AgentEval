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
/// <see cref="WorkIqFailureCategory.Unauthorized"/> with a device-code hint and is never resent. On the live transport
/// the A2A client acquires the token before the HTTP call (<see cref="AcquireTokenAsync"/>, outside
/// <c>HttpClient.Timeout</c>) and hands it over in <see cref="PreAcquiredTokenOption"/>; this handler then only attaches
/// it. Owned by lane A (M11).
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

    /// <summary>
    /// The request option carrying a token the A2A client acquired for this request before the HTTP call (outside
    /// <c>HttpClient.Timeout</c>); the handler attaches it instead of calling the provider again and removes the option.
    /// In memory only: request options are never captured or traced.
    /// </summary>
    internal static readonly HttpRequestOptionsKey<string> PreAcquiredTokenOption = new("AgentEval.Microsoft365.PreAcquiredToken");

    /// <summary>The phrase that marks a token wait stopped before the HTTP call (see <see cref="IsSignInNotCompleted"/>).</summary>
    internal const string SignInNotCompletedText = "sign-in not completed";

    /// <summary>The suggested cause of <c>auth.sign_in_not_completed</c>.</summary>
    internal const string SignInNotCompletedCause =
        "The sign-in did not finish before the wait ended: a device-code prompt was not completed in time (open the page it names, enter the code, sign in as the test user), or the run was stopped. " +
        "Nothing was sent to Work IQ. Sign in first with `agenteval m365 doctor --stage auth` (the token is cached), then run again.";

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
            Diagnostics.Issue(WorkIqIssue.Create(WorkIqTraceStage.Auth, WorkIqIssueCategory.Config, "auth.untrusted_destination", refused.SafeMessage, WorkIqA2AClient.SuggestedCauseOf(WorkIqFailureCategory.UntrustedHost)));
            throw new WorkIqProtocolException(refused);
        }

        var started = Diagnostics.Clock.GetTimestamp();
        string token;
        if (request.Options.TryGetValue(PreAcquiredTokenOption, out var preAcquired) && !string.IsNullOrWhiteSpace(preAcquired))
        {
            // The client acquired this token for this request before the HTTP call, outside HttpClient.Timeout (and
            // traced it); it is attached as is. The option is cleared so the token lives on the request header only.
            ((IDictionary<string, object?>)request.Options).Remove(PreAcquiredTokenOption.Key);
            token = preAcquired;
        }
        else
        {
            token = await AcquireTokenAsync(op, destination, outsideHttpTimeout: false, cancellationToken).ConfigureAwait(false);
        }

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

    /// <summary>
    /// Acquires the bearer token for one request from <see cref="TokenProvider"/>, tracing <c>auth.token_requested</c>
    /// and <c>auth.token_acquired</c> (its length only, never the token). A provider failure becomes a
    /// <see cref="WorkIqProtocolException"/> (<see cref="WorkIqFailureCategory.Unauthorized"/>, or the provider's own
    /// classified failure, <see cref="OutcomeCertainty.Known"/>: nothing was sent); an empty token too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="outsideHttpTimeout"/> is <see langword="true"/> when the A2A client acquires the token before the HTTP
    /// call (the live path does, for every operation): the wait is then bounded by the operation's own token and by the
    /// sign-in code's expiry, never by <c>HttpClient.Timeout</c>, so a slow device-code sign-in cannot be mistaken for a
    /// network timeout. A stop of that wait is reported as <see cref="SignInNotCompletedText"/> (Unauthorized, Known,
    /// nothing was sent; issue <c>auth.sign_in_not_completed</c>).
    /// </para>
    /// <para>
    /// Inside the pipeline (<paramref name="outsideHttpTimeout"/> false: a request the client did not prepare, such as the
    /// SDK's streaming requests after their own preparation) a stop is traced and rethrown as the cancellation it is.
    /// </para>
    /// </remarks>
    /// <param name="op">The operation the token is for (named in a failure).</param>
    /// <param name="destination">The safe <c>METHOD scheme://host/path</c> of the request (no query).</param>
    /// <param name="outsideHttpTimeout">Whether the caller acquires the token before the HTTP call.</param>
    /// <param name="cancellationToken">The operation's token.</param>
    /// <exception cref="WorkIqProtocolException">No usable token was acquired; nothing was sent.</exception>
    internal async Task<string> AcquireTokenAsync(WorkIqOperation op, string destination, bool outsideHttpTimeout, CancellationToken cancellationToken)
    {
        var started = Diagnostics.Clock.GetTimestamp();
        Diagnostics.Trace(
            WorkIqTraceStage.Auth,
            "auth.token_requested",
            outsideHttpTimeout
                ? $"requesting a bearer token for {destination} before the HTTP call (outside its timeout: a device-code sign-in may wait here until its code expires)"
                : $"requesting a bearer token for {destination}");

        string token;
        try
        {
            token = await TokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (outsideHttpTimeout)
        {
            var elapsed = Diagnostics.Clock.GetElapsedTime(started);
            var failure = new WorkIqFailure(
                WorkIqFailureCategory.Unauthorized,
                $"{op}: {SignInNotCompletedText}: no token was acquired before the wait was stopped after {elapsed.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s " +
                "(the caller's cancellation or the turn deadline); nothing was sent",
                OutcomeCertainty.Known,
                op);
            Diagnostics.Trace(WorkIqTraceStage.Auth, "auth.sign_in_not_completed", failure.SafeMessage, WorkIqTraceLevel.Error, elapsedMs: elapsed.TotalMilliseconds);
            Diagnostics.Issue(WorkIqIssue.Create(WorkIqTraceStage.Auth, WorkIqIssueCategory.Authentication, "auth.sign_in_not_completed", failure.SafeMessage, SignInNotCompletedCause));
            throw new WorkIqProtocolException(failure, ex);
        }
        catch (OperationCanceledException)
        {
            Diagnostics.Trace(
                WorkIqTraceStage.Auth,
                "auth.failed",
                $"token acquisition for {destination} was stopped before a token arrived (HTTP timeout or cancellation); nothing was sent",
                WorkIqTraceLevel.Warning,
                elapsedMs: Diagnostics.Clock.GetElapsedTime(started).TotalMilliseconds);
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
            outsideHttpTimeout
                ? $"bearer token acquired for {destination} ({token.Length} characters) before the HTTP call; it is attached to that request"
                : $"bearer token attached for {destination} ({token.Length} characters)",
            elapsedMs: Diagnostics.Clock.GetElapsedTime(started).TotalMilliseconds);
        return token;
    }

    /// <summary>
    /// Whether <paramref name="failure"/> is a stopped token wait before the HTTP call (<see cref="SignInNotCompletedText"/>):
    /// nothing was sent, and it is no network timeout.
    /// </summary>
    /// <param name="failure">The failure.</param>
    internal static bool IsSignInNotCompleted(WorkIqFailure? failure) =>
        failure is { Category: WorkIqFailureCategory.Unauthorized, HttpStatus: null, Certainty: OutcomeCertainty.Known }
        && failure.SafeMessage.Contains(SignInNotCompletedText, StringComparison.Ordinal);

    /// <summary>The safe <c>METHOD scheme://host/path</c> text of a request destination (no query, no fragment).</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="uri">The absolute URI.</param>
    internal static string DescribeDestination(HttpMethod method, Uri uri) => $"{method} {uri.Scheme}://{uri.Host}{uri.AbsolutePath}";

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
