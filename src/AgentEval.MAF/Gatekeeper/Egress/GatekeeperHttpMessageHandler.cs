// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;

namespace AgentEval.MAF.Gatekeeper.Egress;

/// <summary>
/// Gatekeeper Hardening Phase 2, #10 — a real HTTP-EGRESS-ENFORCEMENT <see cref="DelegatingHandler"/>, closing
/// the gap <see cref="DomainAllowListGate"/> candidly documents about itself: that gate inspects the URL
/// STRING inside a tool call's arguments, not the actual outgoing network request — so it cannot catch a
/// redirect to a forbidden host (an initially-allowed URL responding <c>302 Location: http://internal/</c>),
/// nor a DNS answer that resolves an allowed hostname to a private/internal address (SSRF / DNS-rebinding —
/// the classic pattern where a first, validating lookup gets a real external IP but the actual connection-time
/// lookup gets an internal one). This handler sits underneath whichever <see cref="HttpClient"/> a TOOL'S OWN
/// implementation uses (not the MAF function-invocation seam <see cref="IToolGate"/>/<see cref="IToolResultGate"/>
/// plug into) — wrap your tool's outbound HTTP calls with <see cref="CreateHttpClient"/> to get both defenses.
/// <para><b>Redirect-chasing.</b> The inner transport's own auto-redirect MUST be disabled (<see cref="CreateHttpClient"/>
/// does this for you); this handler follows redirects itself, one hop at a time, re-running the FULL
/// allow-list + private-network check against every hop's target before following it — an attacker cannot use
/// an allowed host as an open redirector to a forbidden one. Bounded by <see cref="GatekeeperHttpEgressOptions.MaxRedirects"/>
/// (fail-closed: exceeding it blocks, it does not silently stop following and return the last response).</para>
/// <para><b>DNS-rebind / SSRF defense — connection PINNED to the validated address.</b> Before connecting,
/// the target host is resolved via <see cref="GatekeeperHttpEgressOptions.DnsResolver"/> and every returned
/// address is checked against <see cref="PrivateNetworkClassifier.IsPrivateOrReserved"/>; a private/loopback/
/// link-local/reserved answer blocks the request (<see cref="GatekeeperHttpEgressOptions.BlockPrivateNetworks"/>,
/// default on). DNS resolution failure or timeout is ALSO a block — cannot prove the destination safe.
/// Validating the DNS answer is not enough on its own: a plain SocketsHttpHandler performs its OWN,
/// independent DNS resolution when it actually opens the socket, moments later — a classic rebinding
/// attacker returns a safe address to the validating lookup and a private one to that second,
/// connection-time lookup. <see cref="CreateHttpClient"/> closes this by installing a ConnectCallback that
/// connects DIRECTLY to the exact address just validated (via a per-request pinned option, so this is
/// race-safe across concurrent requests on one shared <see cref="HttpClient"/>) — DNS is resolved exactly
/// once per request, by this handler, never re-resolved by the transport.</para>
/// <para><b>Fail-closed contract.</b> Every block (non-allow-listed host, DNS failure, private address,
/// redirect-chain overrun) throws <see cref="HttpEgressBlockedException"/> — the natural, idiomatic
/// "this request cannot proceed" signal for an <see cref="HttpMessageHandler"/> (mirrors how
/// <see cref="HttpClient"/> itself already throws on a real connection failure), never a sentinel value a
/// caller could accidentally ignore.</para>
/// <para><b>Synchronous <see cref="Send"/> is refused, not silently unprotected.</b> This handler's validation
/// is inherently async (DNS resolution). Overriding only <see cref="SendAsync"/> and leaving <see cref="Send"/>
/// to <see cref="DelegatingHandler"/>'s default would let a caller who uses the synchronous
/// <c>HttpClient.Send(...)</c> API bypass every check here silently (the default forwards straight to the
/// inner handler). Instead, <see cref="Send"/> throws <see cref="NotSupportedException"/> — loud failure, not a
/// silent security gap.</para>
/// </summary>
public sealed class GatekeeperHttpMessageHandler : DelegatingHandler
{
    private static readonly HttpStatusCode[] RedirectStatusCodes =
    [
        HttpStatusCode.MovedPermanently, HttpStatusCode.Found, HttpStatusCode.SeeOther,
        HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect,
    ];

    // Per-REQUEST (not per-handler/per-instance) pinned address — HttpRequestMessage.Options is a private bag
    // scoped to that one request object, so concurrent requests on the same shared HttpClient/handler never
    // see each other's pinned address. Set by ValidateTargetAsync, read by the ConnectCallback CreateHttpClient
    // installs.
    private static readonly HttpRequestOptionsKey<IPAddress> PinnedAddressKey = new("AgentEval.Gatekeeper.Egress.PinnedAddress");

    private readonly HashSet<string> _allowed;
    private readonly GatekeeperHttpEgressOptions _options;

    /// <summary>
    /// Creates the handler over <paramref name="innerHandler"/> — for composing with a caller-supplied handler
    /// (e.g. a fake, in tests) or when you are managing redirect settings yourself.
    /// <para><b>The DNS-rebind connection-pinning defense (see the class remarks) only takes effect through
    /// <see cref="CreateHttpClient"/></b>, which controls its own <see cref="SocketsHttpHandler"/> and can
    /// install the required <see cref="SocketsHttpHandler.ConnectCallback"/>. An arbitrary caller-supplied
    /// <paramref name="innerHandler"/> here still gets the allow-list + validated-DNS-answer checks, but if it
    /// is a real <see cref="SocketsHttpHandler"/> without an equivalent pinning callback of its own, it remains
    /// exposed to the rebind race this handler otherwise closes.</para>
    /// </summary>
    public GatekeeperHttpMessageHandler(HttpMessageHandler innerHandler, IEnumerable<string> allowedDomains, GatekeeperHttpEgressOptions? options = null)
        : base(innerHandler)
    {
        _allowed = HostAllowList.Build(allowedDomains, nameof(allowedDomains));
        _options = options ?? new GatekeeperHttpEgressOptions();
    }

    /// <summary>
    /// The recommended entry point: builds a fresh <see cref="SocketsHttpHandler"/> with
    /// <c>AllowAutoRedirect = false</c> (required for this handler's own redirect re-validation to ever see a
    /// 3xx response at all) and a <see cref="SocketsHttpHandler.ConnectCallback"/> that connects to the exact
    /// address <see cref="ValidateTargetAsync"/> just validated (closing the DNS-rebind TOCTOU — see the class
    /// remarks), wrapped in this handler, and returns a ready-to-use <see cref="HttpClient"/>. Use this
    /// <see cref="HttpClient"/> for a tool's outbound HTTP calls to get both the allow-list and the
    /// DNS-rebind/SSRF defense.
    /// </summary>
    public static HttpClient CreateHttpClient(IEnumerable<string> allowedDomains, GatekeeperHttpEgressOptions? options = null)
    {
        var socketsHandler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectCallback = ConnectToPinnedAddressAsync };
        return new HttpClient(new GatekeeperHttpMessageHandler(socketsHandler, allowedDomains, options));
    }

    // The other half of the DNS-rebind fix: ValidateTargetAsync already proved this exact IPAddress is safe and
    // pinned it on the request — connect the raw socket DIRECTLY to it, so there is no second, independent,
    // unvalidated DNS lookup for an attacker's rebinding nameserver to answer differently on. SocketsHttpHandler
    // applies TLS on top of the returned stream itself (using the original hostname for SNI/cert validation),
    // so this only changes which IP the TCP connection targets, never which hostname the cert is checked against.
    private static async ValueTask<Stream> ConnectToPinnedAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(PinnedAddressKey, out var pinnedAddress))
        {
            // Should never happen — SendAsync always calls ValidateTargetAsync (which pins) before the base
            // handler ever sees the request. Fail closed rather than silently falling back to an unpinned,
            // re-resolved DNS lookup.
            throw new HttpEgressBlockedException(
                context.DnsEndPoint.Host,
                "No validated address was pinned for this connection — refusing rather than risk an unvalidated DNS-rebind lookup.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(pinnedAddress, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var currentRequest = request;
        for (var hop = 0; ; hop++)
        {
            var uri = currentRequest.RequestUri
                ?? throw new HttpEgressBlockedException("(none)", "Request has no RequestUri — cannot validate an egress target that doesn't exist.");

            await ValidateTargetAsync(currentRequest, cancellationToken).ConfigureAwait(false);

            var response = await base.SendAsync(currentRequest, cancellationToken).ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
            {
                return response;
            }

            if (hop >= _options.MaxRedirects)
            {
                response.Dispose();
                throw new HttpEgressBlockedException(uri.Host, $"Redirect chain exceeded the configured limit of {_options.MaxRedirects} hop(s) starting from '{uri}'.");
            }

            var nextUri = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(uri, response.Headers.Location);

            currentRequest = BuildRedirectRequest(currentRequest, response.StatusCode, nextUri);
            response.Dispose();
        }
    }

    /// <inheritdoc/>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            $"{nameof(GatekeeperHttpMessageHandler)} only supports the asynchronous HttpClient API (SendAsync). " +
            "Its validation (DNS resolution) is inherently async, and silently falling back to the base " +
            "synchronous Send() would bypass every check here rather than fail closed.");

    private async Task ValidateTargetAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var host = uri.Host.ToLowerInvariant();
        if (!HostAllowList.IsAllowed(host, _allowed))
        {
            throw new HttpEgressBlockedException(host, $"Host '{host}' is not on the egress allow-list.");
        }

        // A literal IP in the URL (e.g. an allow-listed IP address itself) — classify it directly, no DNS
        // round trip needed (and none would be meaningful: there's nothing to resolve). Still pinned below
        // (even when BlockPrivateNetworks is off) so the connect callback always has a validated address.
        if (IPAddress.TryParse(uri.Host, out var literalAddress))
        {
            if (_options.BlockPrivateNetworks && PrivateNetworkClassifier.IsPrivateOrReserved(literalAddress))
            {
                throw new HttpEgressBlockedException(host, $"Host '{host}' is a private/reserved address.");
            }

            request.Options.Set(PinnedAddressKey, literalAddress);
            return;
        }

        // Resolved UNCONDITIONALLY, even when BlockPrivateNetworks is off — the connect callback needs a
        // validated address to pin to regardless of whether the private-network check itself runs, or the
        // (now unpinned) connection would fall through to an unvalidated, independently-re-resolved DNS lookup.
        IPAddress[] addresses;
        using var timeoutCts = new CancellationTokenSource(_options.DnsResolutionTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            addresses = await _options.DnsResolver.ResolveAsync(host, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The DNS-timeout CTS fired (not the caller's own cancellation) — fail closed, not "no data ⇒ allow".
            throw new HttpEgressBlockedException(host, $"DNS resolution for '{host}' timed out after {_options.DnsResolutionTimeout}.");
        }
        catch (SocketException ex)
        {
            throw new HttpEgressBlockedException(host, $"DNS resolution for '{host}' failed: {ex.Message}");
        }

        if (addresses.Length == 0)
        {
            throw new HttpEgressBlockedException(host, $"DNS resolution for '{host}' returned no addresses.");
        }

        if (_options.BlockPrivateNetworks)
        {
            foreach (var address in addresses)
            {
                if (PrivateNetworkClassifier.IsPrivateOrReserved(address))
                {
                    throw new HttpEgressBlockedException(host, $"Host '{host}' resolves to a private/reserved address ({address}) — refusing (SSRF/DNS-rebind guard).");
                }
            }
        }

        // Pin the FIRST resolved (and, if checked above, validated-safe) address — the exact address the
        // connect callback will use, so there is no second, independent DNS lookup for a rebind attacker to
        // answer differently. No failover across addresses[1..] if the first is unreachable: this defense
        // connects to A specific validated address, it doesn't retry across several.
        request.Options.Set(PinnedAddressKey, addresses[0]);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => RedirectStatusCodes.Contains(statusCode);

    // 307/308 preserve method + body (RFC 7231/7238); 301/302/303 downgrade to GET and drop the body (except a
    // HEAD request stays HEAD) — matches HttpClientHandler's own default redirect semantics, so a tool wrapped
    // in this handler doesn't observe different redirect behavior than it would unwrapped.
    private static HttpRequestMessage BuildRedirectRequest(HttpRequestMessage original, HttpStatusCode statusCode, Uri nextUri)
    {
        var preserveMethodAndBody = statusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
        var method = preserveMethodAndBody || original.Method == HttpMethod.Head ? original.Method : HttpMethod.Get;

        var next = new HttpRequestMessage(method, nextUri);
        if (preserveMethodAndBody)
        {
            // NOTE: reuses the SAME HttpContent instance — a non-buffered (single-read stream) body cannot be
            // replayed across a 307/308 redirect hop, the same constraint HttpClientHandler's own redirect
            // handling has. Buffer your request content yourself if you need a 307/308 body to survive a hop.
            next.Content = original.Content;
        }

        foreach (var header in original.Headers)
        {
            next.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in original.Options)
        {
            next.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }

        return next;
    }
}
