// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;
using AgentEval.MAF.Gatekeeper.Egress;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper.Egress;

/// <summary>
/// Gatekeeper Hardening Phase 2, #10 — the real HTTP-egress-enforcement handler: allow-list, DNS-rebind/SSRF
/// defense, and redirect-chasing with per-hop re-validation. Uses a scripted inner handler + a fake DNS
/// resolver so every scenario (including a DNS-rebind and a redirect-to-forbidden-host) is deterministic, with
/// zero real network/DNS access.
/// </summary>
public class GatekeeperHttpMessageHandlerTests
{
    private static GatekeeperHttpEgressOptions Options(IDnsResolver resolver, int maxRedirects = 5, bool blockPrivate = true) => new()
    {
        DnsResolver = resolver,
        MaxRedirects = maxRedirects,
        BlockPrivateNetworks = blockPrivate,
        DnsResolutionTimeout = TimeSpan.FromMilliseconds(500),
    };

    [Fact]
    public async Task AllowedHost_PublicDns_PassesThrough()
    {
        var inner = new ScriptedHttpMessageHandler().EnqueueResponse(HttpStatusCode.OK);
        var dns = new FakeDnsResolver().With("api.example.com", IPAddress.Parse("8.8.8.8"));
        var handler = new GatekeeperHttpMessageHandler(inner, ["example.com"], Options(dns));
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://api.example.com/orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(inner.ReceivedRequests);
    }

    [Fact]
    public async Task NonAllowedHost_ThrowsBeforeAnyNetworkCall()
    {
        var inner = new ScriptedHttpMessageHandler().EnqueueResponse(HttpStatusCode.OK);
        var dns = new FakeDnsResolver().With("attacker.example", IPAddress.Parse("8.8.8.8"));
        var handler = new GatekeeperHttpMessageHandler(inner, ["example.com"], Options(dns));
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpEgressBlockedException>(() => client.GetAsync("https://attacker.example/collect"));

        Assert.Equal("attacker.example", ex.Host);
        Assert.Empty(inner.ReceivedRequests);   // blocked BEFORE any network call — not just a post-hoc rejection
    }

    [Fact]
    public async Task AllowedHost_DnsResolvesToPrivateAddress_Blocked()
    {
        // The DNS-rebind / SSRF case: the hostname is legitimately allow-listed, but its CURRENT DNS answer
        // resolves to an internal address — the argument-string-level DomainAllowListGate cannot see this at
        // all (it never resolves DNS); this handler must.
        var inner = new ScriptedHttpMessageHandler().EnqueueResponse(HttpStatusCode.OK);
        var dns = new FakeDnsResolver().With("api.example.com", IPAddress.Parse("169.254.169.254"));
        var handler = new GatekeeperHttpMessageHandler(inner, ["example.com"], Options(dns));
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpEgressBlockedException>(() => client.GetAsync("https://api.example.com/"));

        Assert.Equal("api.example.com", ex.Host);
        Assert.Empty(inner.ReceivedRequests);
    }

    [Fact]
    public async Task BlockPrivateNetworks_False_AllowsPrivateDnsAnswer()
    {
        var inner = new ScriptedHttpMessageHandler().EnqueueResponse(HttpStatusCode.OK);
        var dns = new FakeDnsResolver().With("internal.example.com", IPAddress.Parse("10.0.0.5"));
        var handler = new GatekeeperHttpMessageHandler(inner, ["example.com"], Options(dns, blockPrivate: false));
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://internal.example.com/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LiteralIpInUrl_Public_Allowed_NoDnsResolutionAttempted()
    {
        var inner = new ScriptedHttpMessageHandler().EnqueueResponse(HttpStatusCode.OK);
        var dns = new ThrowingDnsResolver();   // must never be called — a literal IP needs no DNS lookup
        var handler = new GatekeeperHttpMessageHandler(inner, ["8.8.8.8"], Options(dns));
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://8.8.8.8/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LiteralIpInUrl_Private_Blocked()
    {
        var inner = new ScriptedHttpMessageHandler().EnqueueResponse(HttpStatusCode.OK);
        var handler = new GatekeeperHttpMessageHandler(inner, ["127.0.0.1"], Options(new ThrowingDnsResolver()));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpEgressBlockedException>(() => client.GetAsync("https://127.0.0.1/"));
    }

    [Fact]
    public async Task Redirect_ToAllowedHost_FollowsAndRevalidatesSecondHop()
    {
        var inner = new ScriptedHttpMessageHandler()
            .EnqueueResponse(HttpStatusCode.Found, "https://cdn.example.com/final")
            .EnqueueResponse(HttpStatusCode.OK);
        var dns = new FakeDnsResolver()
            .With("api.example.com", IPAddress.Parse("8.8.8.8"))
            .With("cdn.example.com", IPAddress.Parse("8.8.4.4"));
        var handler = new GatekeeperHttpMessageHandler(inner, ["example.com"], Options(dns));
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://api.example.com/redirect-me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.ReceivedRequests.Count);   // both hops actually went out
        Assert.Equal("cdn.example.com", inner.ReceivedRequests[1].RequestUri!.Host);
    }

    [Fact]
    public async Task Redirect_ToForbiddenHost_BlocksAtSecondHop_FirstHopStillWentOut()
    {
        var inner = new ScriptedHttpMessageHandler()
            .EnqueueResponse(HttpStatusCode.Found, "https://attacker.example/steal");
        var dns = new FakeDnsResolver().With("api.example.com", IPAddress.Parse("8.8.8.8"));
        var handler = new GatekeeperHttpMessageHandler(inner, ["example.com"], Options(dns));
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpEgressBlockedException>(() => client.GetAsync("https://api.example.com/redirect-me"));

        Assert.Equal("attacker.example", ex.Host);
        Assert.Single(inner.ReceivedRequests);   // the first (allowed) hop DID go out — only the 2nd hop is blocked
    }

    [Fact]
    public async Task Redirect_ChainExceedsMaxRedirects_Blocks()
    {
        var inner = new ScriptedHttpMessageHandler();
        for (var i = 0; i < 10; i++)
        {
            inner.EnqueueResponse(HttpStatusCode.Found, $"https://api.example.com/hop{i}");
        }

        var dns = new FakeDnsResolver().With("api.example.com", IPAddress.Parse("8.8.8.8"));
        var handler = new GatekeeperHttpMessageHandler(inner, ["example.com"], Options(dns, maxRedirects: 3));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpEgressBlockedException>(() => client.GetAsync("https://api.example.com/start"));

        Assert.Equal(4, inner.ReceivedRequests.Count);   // the original + 3 allowed redirects; the 4th redirect is refused
    }

    [Fact]
    public async Task Redirect_307_PreservesMethodAndBody()
    {
        var inner = new ScriptedHttpMessageHandler()
            .EnqueueResponse(HttpStatusCode.TemporaryRedirect, "https://api.example.com/v2/submit")
            .EnqueueResponse(HttpStatusCode.OK);
        var dns = new FakeDnsResolver().With("api.example.com", IPAddress.Parse("8.8.8.8"));
        var handler = new GatekeeperHttpMessageHandler(inner, ["example.com"], Options(dns));
        using var client = new HttpClient(handler);

        var response = await client.PostAsync("https://api.example.com/v1/submit", new StringContent("payload"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpMethod.Post, inner.ReceivedRequests[1].Method);
        Assert.NotNull(inner.ReceivedRequests[1].Content);
    }

    [Fact]
    public async Task Redirect_303_DowngradesPostToGet()
    {
        var inner = new ScriptedHttpMessageHandler()
            .EnqueueResponse(HttpStatusCode.SeeOther, "https://api.example.com/result")
            .EnqueueResponse(HttpStatusCode.OK);
        var dns = new FakeDnsResolver().With("api.example.com", IPAddress.Parse("8.8.8.8"));
        var handler = new GatekeeperHttpMessageHandler(inner, ["example.com"], Options(dns));
        using var client = new HttpClient(handler);

        await client.PostAsync("https://api.example.com/submit", new StringContent("payload"));

        Assert.Equal(HttpMethod.Get, inner.ReceivedRequests[1].Method);
        Assert.Null(inner.ReceivedRequests[1].Content);
    }

    [Fact]
    public async Task DnsResolutionFailure_SocketException_Blocked()
    {
        var inner = new ScriptedHttpMessageHandler().EnqueueResponse(HttpStatusCode.OK);
        var dns = new FailingDnsResolver();
        var handler = new GatekeeperHttpMessageHandler(inner, ["example.com"], Options(dns));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpEgressBlockedException>(() => client.GetAsync("https://api.example.com/"));
        Assert.Empty(inner.ReceivedRequests);
    }

    [Fact]
    public async Task DnsResolutionTimeout_Blocked()
    {
        var inner = new ScriptedHttpMessageHandler().EnqueueResponse(HttpStatusCode.OK);
        var dns = new HangingDnsResolver();
        var handler = new GatekeeperHttpMessageHandler(inner, ["example.com"], Options(dns));
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpEgressBlockedException>(() => client.GetAsync("https://api.example.com/"));
        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SynchronousSend_ThrowsNotSupported_RatherThanBypassingChecks()
    {
        var inner = new ScriptedHttpMessageHandler().EnqueueResponse(HttpStatusCode.OK);
        var dns = new FakeDnsResolver().With("api.example.com", IPAddress.Parse("8.8.8.8"));
        var handler = new GatekeeperHttpMessageHandler(inner, ["example.com"], Options(dns));
        using var client = new HttpClient(handler);

        Assert.Throws<NotSupportedException>(() => client.Send(new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/")));
    }

    [Fact]
    public async Task CreateHttpClient_DisallowedHost_ThrowsWithoutRealNetworkAccess()
    {
        using var client = GatekeeperHttpMessageHandler.CreateHttpClient(["example.com"]);

        // The host is blocked at the allow-list check, before any DNS resolution or connection attempt — safe
        // to run in an offline/sandboxed CI environment with no real network egress.
        await Assert.ThrowsAsync<HttpEgressBlockedException>(() => client.GetAsync("https://not-allowed.example/"));
    }

    // ── Test doubles ──

    private sealed class ScriptedHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
        public List<HttpRequestMessage> ReceivedRequests { get; } = new();

        public ScriptedHttpMessageHandler EnqueueResponse(HttpStatusCode statusCode, string? location = null)
        {
            _responses.Enqueue(_ =>
            {
                var response = new HttpResponseMessage(statusCode);
                if (location is not null)
                {
                    response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
                }

                return response;
            });
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ReceivedRequests.Add(request);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("Test bug: no scripted response was queued for this request.");
            }

            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private sealed class FakeDnsResolver : IDnsResolver
    {
        private readonly Dictionary<string, IPAddress[]> _byHost = new(StringComparer.OrdinalIgnoreCase);

        public FakeDnsResolver With(string host, params IPAddress[] addresses)
        {
            _byHost[host] = addresses;
            return this;
        }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
            => _byHost.TryGetValue(host, out var addresses)
                ? Task.FromResult(addresses)
                : throw new SocketException((int)SocketError.HostNotFound);
    }

    private sealed class ThrowingDnsResolver : IDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Test bug: DNS should never be consulted for a literal IP target.");
    }

    private sealed class FailingDnsResolver : IDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
            => throw new SocketException((int)SocketError.HostNotFound);
    }

    private sealed class HangingDnsResolver : IDnsResolver
    {
        public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return [];   // unreachable — the caller's timeout cancels the delay first
        }
    }
}
