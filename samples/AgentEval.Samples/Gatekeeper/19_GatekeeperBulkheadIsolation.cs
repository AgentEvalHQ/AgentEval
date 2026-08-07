// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Net;
using AgentEval.MAF.Gatekeeper;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.Samples;

/// <summary>Offline proof that contained work cannot starve the normal HTTP resource pool.</summary>
public static class GatekeeperBulkheadIsolation
{
    private static readonly ContainmentTarget Target =
        new ContainmentTarget.Session("sample-tenant", "contained-session");

    public static async Task RunAsync()
    {
        GatekeeperSampleContractRenderer.Print("19");
        Console.WriteLine("\n=== Gatekeeper — Bulkhead + Containment Isolation (offline) ===\n");

        var normal = new BlockingHandler("normal");
        var isolated = new BlockingHandler("isolated");
        using var pool = new ContainmentHttpClientPool(
            new ContainmentHttpClientPoolOptions
            {
                NormalMaxConcurrency = 3,
                IsolatedMaxConcurrency = 1,
                NormalPrimaryHandlerFactory = () => normal,
                IsolatedPrimaryHandlerFactory = () => isolated,
            });
        var store = new RoutingStore(Active(Target));
        var function = HttpFunction()
            .WithContainmentHttpResourceIsolation(store, _ => Target, pool);

        Console.WriteLine("── Act 1 · a CONTAINED session floods the HTTP tool with 6 concurrent calls ──");
        Task<object?>[] containedCalls;
        using (AgentRunScope.Begin(new SampleSession(), "bulkhead-sample", trace: null))
        {
            containedCalls = StartCalls(function, 6);
            await isolated.WaitForStartedAsync(1);
        }

        Console.WriteLine($"   6 contained calls launched → {isolated.PeakCount} running in the isolated pool (1 permit); the rest queue behind it");

        Console.WriteLine("\n── Act 2 · normal traffic arrives while the contained flood is still stuck ──");
        store.Snapshot = ContainmentSnapshot.NotContained(Target);
        Task<object?>[] normalCalls;
        using (AgentRunScope.Begin(new SampleSession(), "bulkhead-sample", trace: null))
        {
            normalCalls = StartCalls(function, 6);
            await normal.WaitForStartedAsync(3);
        }

        Console.WriteLine($"   6 normal calls launched → {normal.PeakCount} running at once on the normal pool (3 permits) — the flood took nothing from them");

        Require(isolated.PeakCount == 1, "contained work must use the one-permit isolated pool");
        Require(normal.PeakCount == 3, "normal work must retain all three normal permits");
        Require(pool.IsolatedPeakRequests == 1 && pool.NormalPeakRequests == 3,
            "pool metrics must report measured peaks, not configured claims");

        Console.WriteLine("\n── Act 3 · release and drain — routing is proved by the response bodies ──");
        normal.Release();
        var normalResults = await Task.WhenAll(normalCalls);
        Require(normalResults.All(result => string.Equals(result?.ToString(), "normal", StringComparison.Ordinal)),
            "normal work must route only through the normal client");
        Console.WriteLine("   normal pool released   → all 6 normal calls completed through the 'normal' client");

        isolated.Release();
        var containedResults = await Task.WhenAll(containedCalls);
        Require(containedResults.All(result => string.Equals(result?.ToString(), "isolated", StringComparison.Ordinal)),
            "contained work must route only through the isolated client");
        Require(pool.NormalCurrentRequests == 0 && pool.IsolatedCurrentRequests == 0,
            "all permits must be released after completion");
        Console.WriteLine("   bulkhead released      → the contained flood drained serially through the 'isolated' client");

        Console.WriteLine("\n   Effect ledger                Observed   Expected");
        Console.WriteLine($"   normal concurrency peak      {pool.NormalPeakRequests}          3 (all permits kept)");
        Console.WriteLine($"   contained concurrency peak   {pool.IsolatedPeakRequests}          1 (bulkhead held)");
        Console.WriteLine($"   permits leaked at the end    {pool.NormalCurrentRequests + pool.IsolatedCurrentRequests}          0");
        Console.WriteLine("   ✅ contained saturation stayed inside its one-permit bulkhead while normal work used three independent permits.");
    }

    private static AIFunction HttpFunction() => AIFunctionFactory.Create(
        async (IServiceProvider services, CancellationToken cancellationToken) =>
        {
            var client = services.GetRequiredService<HttpClient>();
            return await client.GetStringAsync("https://offline.invalid/", cancellationToken);
        },
        "offline_http_fetch");

    private static Task<object?>[] StartCalls(AIFunction function, int count) =>
        Enumerable.Range(0, count).Select(_ => function.InvokeAsync().AsTask()).ToArray();

    private static ContainmentSnapshot Active(ContainmentTarget target) =>
        ContainmentSnapshot.FromRecord(
            new ContainmentRecord(
                target,
                ContainmentStatus.Active,
                new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero),
                releasedAtUtc: null,
                reasonCode: "resource_exhaustion",
                evidenceReference: "sample-incident",
                issuer: "gatekeeper-sample",
                reviewer: null,
                version: 1,
                etag: "sample-etag"));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Bulkhead sample failed: " + message + ".");
        }
    }

    private sealed class BlockingHandler(string response) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startedCount;
        private int _currentCount;
        private int _peakCount;

        public int StartedCount => Volatile.Read(ref _startedCount);
        public int PeakCount => Volatile.Read(ref _peakCount);

        public async Task WaitForStartedAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (StartedCount < count)
            {
                await _started.Task.WaitAsync(timeout.Token);
                await Task.Yield();
            }
        }

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startedCount);
            _started.TrySetResult();
            var current = Interlocked.Increment(ref _currentCount);
            RecordPeak(current);
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response),
                };
            }
            finally
            {
                Interlocked.Decrement(ref _currentCount);
            }
        }

        private void RecordPeak(int current)
        {
            var observed = Volatile.Read(ref _peakCount);
            while (current > observed)
            {
                var prior = Interlocked.CompareExchange(ref _peakCount, current, observed);
                if (prior == observed)
                {
                    return;
                }

                observed = prior;
            }
        }
    }

    private sealed class RoutingStore(ContainmentSnapshot snapshot) : IContainmentStore
    {
        public ContainmentSnapshot Snapshot { get; set; } = snapshot;

        public ContainmentSnapshot GetCurrent(ContainmentTarget target) => Snapshot;

        public ValueTask<ContainmentMutationResult> ContainAsync(
            ContainmentRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ContainmentMutationResult> ReleaseAsync(
            ContainmentReleaseAuthorization authorization,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose() { }
    }

    private sealed class SampleSession : AgentSession;
}
