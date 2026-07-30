// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Reflection;
using System.Text.Json;
using AgentEval.MAF.Gatekeeper;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 5, Task 5.2 — measured HTTP resource-isolation proof of concept.</summary>
public sealed class ContainmentHttpResourceIsolationTests
{
    private static readonly ContainmentTarget Target =
        new ContainmentTarget.Session("tenant-a", "session-a");

    [Fact]
    public async Task CleanAndActiveRootSessions_RouteToDistinctHttpPools()
    {
        var normal = new BlockingHandler("normal", initiallyReleased: true);
        var isolated = new BlockingHandler("isolated", initiallyReleased: true);
        using var pool = Pool(normal, isolated, normalCap: 4, isolatedCap: 1);
        var store = new RoutingStore(ContainmentSnapshot.NotContained(Target));
        var function = HttpFunction()
            .WithContainmentHttpResourceIsolation(store, _ => Target, pool);

        string? cleanResult;
        using (AgentRunScope.Begin(new TestSession(), "agent", trace: null))
        {
            cleanResult = (await function.InvokeAsync())?.ToString();
        }

        store.Snapshot = Active(Target);
        string? activeResult;
        using (AgentRunScope.Begin(new TestSession(), "agent", trace: null))
        {
            activeResult = (await function.InvokeAsync())?.ToString();
        }

        Assert.Equal("normal", cleanResult);
        Assert.Equal("isolated", activeResult);
        Assert.Equal(1, normal.StartedCount);
        Assert.Equal(1, isolated.StartedCount);
        Assert.NotSame(pool.NormalClient, pool.IsolatedClient);
    }

    [Fact]
    public async Task ConcurrentCleanAndActiveCalls_ObserveTheirConfiguredCaps()
    {
        var normal = new BlockingHandler("normal");
        var isolated = new BlockingHandler("isolated");
        using var pool = Pool(normal, isolated, normalCap: 3, isolatedCap: 1);
        var store = new RoutingStore(ContainmentSnapshot.NotContained(Target));
        var function = HttpFunction()
            .WithContainmentHttpResourceIsolation(store, _ => Target, pool);

        Task<object?>[] normalCalls;
        using (AgentRunScope.Begin(new TestSession(), "agent", trace: null))
        {
            normalCalls = StartCalls(function, count: 8);
            await normal.WaitForStartedAsync(3);
            await YieldToQueuedCalls();
            Assert.Equal(3, normal.StartedCount);
            Assert.Equal(3, normal.PeakCount);
            Assert.Equal(3, pool.NormalPeakRequests);
            Assert.Equal(0, pool.IsolatedPeakRequests);
            normal.Release();
            await Task.WhenAll(normalCalls);
        }

        store.Snapshot = Active(Target);
        Task<object?>[] isolatedCalls;
        using (AgentRunScope.Begin(new TestSession(), "agent", trace: null))
        {
            isolatedCalls = StartCalls(function, count: 8);
            await isolated.WaitForStartedAsync(1);
            await YieldToQueuedCalls();
            Assert.Equal(1, isolated.StartedCount);
            Assert.Equal(1, isolated.PeakCount);
            Assert.Equal(1, pool.IsolatedPeakRequests);
            isolated.Release();
            await Task.WhenAll(isolatedCalls);
        }

        Assert.Equal(8, normal.StartedCount);
        Assert.Equal(8, isolated.StartedCount);
        Assert.Equal(0, pool.NormalCurrentRequests);
        Assert.Equal(0, pool.IsolatedCurrentRequests);
    }

    [Fact]
    public async Task QueuedCancellation_DoesNotEnterPrimaryHandlerOrLeakPermit()
    {
        var normal = new BlockingHandler("normal");
        var isolated = new BlockingHandler("isolated", initiallyReleased: true);
        using var pool = Pool(normal, isolated, normalCap: 1, isolatedCap: 1);

        var first = pool.NormalClient.GetAsync("https://example.test/");
        await normal.WaitForStartedAsync(1);

        using var cancellation = new CancellationTokenSource();
        var queued = pool.NormalClient.GetAsync(
            "https://example.test/",
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, normal.StartedCount);
        Assert.Equal(1, pool.NormalCurrentRequests);

        normal.Release();
        using var firstResponse = await first;
        using var thirdResponse = await pool.NormalClient.GetAsync(
            "https://example.test/");

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, thirdResponse.StatusCode);
        Assert.Equal(2, normal.StartedCount);
        Assert.Equal(0, pool.NormalCurrentRequests);
    }

    [Fact]
    public async Task MissingIndeterminateOrThrowingContext_NeverUsesNormalClient()
    {
        var normal = new BlockingHandler("normal", initiallyReleased: true);
        var isolated = new BlockingHandler("isolated", initiallyReleased: true);
        using var pool = Pool(normal, isolated, normalCap: 4, isolatedCap: 1);
        var store = new RoutingStore(ContainmentSnapshot.NotContained(Target));
        var function = HttpFunction()
            .WithContainmentHttpResourceIsolation(store, _ => Target, pool);

        Assert.Equal("isolated", (await function.InvokeAsync())?.ToString());

        store.Snapshot = ContainmentSnapshot.Indeterminate(
            Target,
            "store_unavailable");
        using (AgentRunScope.Begin(new TestSession(), "agent", trace: null))
        {
            Assert.Equal("isolated", (await function.InvokeAsync())?.ToString());
        }

        store.ThrowOnRead = true;
        using (AgentRunScope.Begin(new TestSession(), "agent", trace: null))
        {
            Assert.Equal("isolated", (await function.InvokeAsync())?.ToString());
        }

        Assert.Equal(0, normal.StartedCount);
        Assert.Equal(3, isolated.StartedCount);
    }

    [Fact]
    public async Task Decorator_PreservesMetadataArgumentsAndNonHttpServices()
    {
        var normal = new BlockingHandler("normal", initiallyReleased: true);
        var isolated = new BlockingHandler("isolated", initiallyReleased: true);
        using var pool = Pool(normal, isolated, normalCap: 4, isolatedCap: 1);
        var marker = new Marker("caller-service");
        var inner = AIFunctionFactory.Create(
            async (
                string suffix,
                IServiceProvider services,
                CancellationToken cancellationToken) =>
            {
                var client = services.GetRequiredService<HttpClient>();
                var callerMarker = services.GetRequiredService<Marker>();
                var value = await client.GetStringAsync(
                    "https://example.test/",
                    cancellationToken);
                return $"{value}-{suffix}-{callerMarker.Value}";
            },
            "http_fetch",
            "Fetches through the invocation service provider.");
        var function = inner.WithContainmentHttpResourceIsolation(
            new RoutingStore(ContainmentSnapshot.NotContained(Target)),
            _ => Target,
            pool);
        var arguments = new AIFunctionArguments
        {
            ["suffix"] = "ok",
            Services = new MarkerServiceProvider(marker),
        };

        using var scope = AgentRunScope.Begin(
            new TestSession(),
            "agent",
            trace: null);
        var result = await function.InvokeAsync(arguments);

        Assert.Equal("normal-ok-caller-service", result?.ToString());
        AssertMetadataEqual(inner, function);
        Assert.Equal("ok", arguments["suffix"]);
        Assert.Same(marker, arguments.Services.GetRequiredService<Marker>());
    }

    [Fact]
    public async Task CallerCancellationBeforeSelection_SkipsStoreAndBothPools()
    {
        var normal = new BlockingHandler("normal", initiallyReleased: true);
        var isolated = new BlockingHandler("isolated", initiallyReleased: true);
        using var pool = Pool(normal, isolated, normalCap: 4, isolatedCap: 1);
        var store = new RoutingStore(ContainmentSnapshot.NotContained(Target));
        var function = HttpFunction()
            .WithContainmentHttpResourceIsolation(store, _ => Target, pool);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var scope = AgentRunScope.Begin(
            new TestSession(),
            "agent",
            trace: null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => function.InvokeAsync(
                new AIFunctionArguments(),
                cancellation.Token).AsTask());

        Assert.Equal(0, store.ReadCount);
        Assert.Equal(0, normal.StartedCount);
        Assert.Equal(0, isolated.StartedCount);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(4097, 1)]
    public void InvalidConcurrencyConfiguration_FailsBeforeCreatingHandlers(
        int normalCap,
        int isolatedCap)
    {
        var factoryCalls = 0;
        var options = new ContainmentHttpClientPoolOptions
        {
            NormalMaxConcurrency = normalCap,
            IsolatedMaxConcurrency = isolatedCap,
            NormalPrimaryHandlerFactory = () =>
            {
                factoryCalls++;
                return new BlockingHandler("normal", initiallyReleased: true);
            },
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContainmentHttpClientPool(options));
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public void SharedPrimaryHandler_IsRejectedAndDisposed()
    {
        var shared = new BlockingHandler("shared", initiallyReleased: true);
        var options = new ContainmentHttpClientPoolOptions
        {
            NormalPrimaryHandlerFactory = () => shared,
            IsolatedPrimaryHandlerFactory = () => shared,
        };

        Assert.Throws<ArgumentException>(
            () => new ContainmentHttpClientPool(options));

        Assert.True(shared.IsDisposed);
        Assert.Equal(1, shared.DisposeCount);
    }

    [Fact]
    public void DoubleWrappingOrMissingDeclaredCoverage_FailsConstruction()
    {
        var function = HttpFunction();
        using var pool = new ContainmentHttpClientPool();
        var store = new RoutingStore(ContainmentSnapshot.NotContained(Target));
        var wrapped = function.WithContainmentHttpResourceIsolation(
            store,
            _ => Target,
            pool);

        Assert.Throws<ArgumentException>(
            () => wrapped.WithContainmentHttpResourceIsolation(
                store,
                _ => Target,
                pool));
        Assert.Throws<InvalidOperationException>(
            () => new[] { function }
                .ValidateContainmentHttpResourceIsolation());
        Assert.Throws<ArgumentException>(
            () => Array.Empty<AIFunction>()
                .ValidateContainmentHttpResourceIsolation());

        new[] { wrapped }.ValidateContainmentHttpResourceIsolation();
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("file:///tmp/resource")]
    public void NonHttpBaseAddress_FailsBeforeCreatingHandlers(string value)
    {
        var factoryCalls = 0;
        var options = new ContainmentHttpClientPoolOptions
        {
            BaseAddress = new Uri(value, UriKind.RelativeOrAbsolute),
            NormalPrimaryHandlerFactory = () =>
            {
                factoryCalls++;
                return new BlockingHandler("normal", initiallyReleased: true);
            },
        };

        Assert.Throws<ArgumentException>(
            () => new ContainmentHttpClientPool(options));
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public void Extension_RejectsNullDependencies()
    {
        var function = HttpFunction();
        using var pool = new ContainmentHttpClientPool();
        var store = new RoutingStore(ContainmentSnapshot.NotContained(Target));

        Assert.Throws<ArgumentNullException>(
            () => ContainmentHttpResourceRoutingExtensions
                .WithContainmentHttpResourceIsolation(
                    null!,
                    store,
                    _ => Target,
                    pool));
        Assert.Throws<ArgumentNullException>(
            () => function.WithContainmentHttpResourceIsolation(
                null!,
                _ => Target,
                pool));
        Assert.Throws<ArgumentNullException>(
            () => function.WithContainmentHttpResourceIsolation(
                store,
                null!,
                pool));
        Assert.Throws<ArgumentNullException>(
            () => function.WithContainmentHttpResourceIsolation(
                store,
                _ => Target,
                null!));
    }

    private static AIFunction HttpFunction()
        => AIFunctionFactory.Create(
            async (
                IServiceProvider services,
                CancellationToken cancellationToken) =>
            {
                var client = services.GetRequiredService<HttpClient>();
                return await client.GetStringAsync(
                    "https://example.test/",
                    cancellationToken);
            },
            "http_fetch",
            "Fetches through the invocation service provider.");

    private static ContainmentHttpClientPool Pool(
        HttpMessageHandler normal,
        HttpMessageHandler isolated,
        int normalCap,
        int isolatedCap)
        => new(
            new ContainmentHttpClientPoolOptions
            {
                NormalMaxConcurrency = normalCap,
                IsolatedMaxConcurrency = isolatedCap,
                NormalPrimaryHandlerFactory = () => normal,
                IsolatedPrimaryHandlerFactory = () => isolated,
            });

    private static Task<object?>[] StartCalls(AIFunction function, int count)
        => Enumerable.Range(0, count)
            .Select(_ => function.InvokeAsync().AsTask())
            .ToArray();

    private static async Task YieldToQueuedCalls()
    {
        for (var i = 0; i < 10; i++)
        {
            await Task.Yield();
        }
    }

    private static void AssertMetadataEqual(AIFunction expected, AIFunction actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.JsonSchema.GetRawText(), actual.JsonSchema.GetRawText());
        Assert.Equal(
            expected.ReturnJsonSchema?.GetRawText(),
            actual.ReturnJsonSchema?.GetRawText());
        Assert.Equal(expected.UnderlyingMethod, actual.UnderlyingMethod);
        Assert.Same(expected.JsonSerializerOptions, actual.JsonSerializerOptions);
        Assert.Same(expected.AdditionalProperties, actual.AdditionalProperties);
    }

    private static ContainmentSnapshot Active(ContainmentTarget target)
        => ContainmentSnapshot.FromRecord(
            new ContainmentRecord(
                target,
                ContainmentStatus.Active,
                new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero),
                releasedAtUtc: null,
                reasonCode: "resource_exhaustion",
                evidenceReference: "incident-1",
                issuer: "gatekeeper",
                reviewer: null,
                version: 1,
                etag: "etag-1"));

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly string _response;
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startedCount;
        private int _currentCount;
        private int _peakCount;
        private int _disposeCount;

        public BlockingHandler(string response, bool initiallyReleased = false)
        {
            _response = response;
            if (initiallyReleased)
            {
                _release.TrySetResult();
            }
        }

        public int StartedCount => Volatile.Read(ref _startedCount);

        public int PeakCount => Volatile.Read(ref _peakCount);

        public bool IsDisposed => DisposeCount > 0;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public async Task WaitForStartedAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (StartedCount < count)
            {
                await _started.Task.WaitAsync(timeout.Token);
                if (StartedCount < count)
                {
                    await Task.Yield();
                }
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
                    Content = new StringContent(_response),
                };
            }
            finally
            {
                Interlocked.Decrement(ref _currentCount);
            }
        }

        protected override void Dispose(bool disposing)
        {
            Interlocked.Increment(ref _disposeCount);
            _release.TrySetResult();
            base.Dispose(disposing);
        }

        private void RecordPeak(int current)
        {
            var observed = Volatile.Read(ref _peakCount);
            while (current > observed)
            {
                var prior = Interlocked.CompareExchange(
                    ref _peakCount,
                    current,
                    observed);
                if (prior == observed)
                {
                    return;
                }

                observed = prior;
            }
        }
    }

    private sealed class RoutingStore(
        ContainmentSnapshot snapshot) : IContainmentStore
    {
        public ContainmentSnapshot Snapshot { get; set; } = snapshot;

        public bool ThrowOnRead { get; set; }

        public int ReadCount { get; private set; }

        public ContainmentSnapshot GetCurrent(ContainmentTarget target)
        {
            ReadCount++;
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("sensitive store detail");
            }

            return Snapshot;
        }

        public ValueTask<ContainmentMutationResult> ContainAsync(
            ContainmentRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<ContainmentMutationResult> ReleaseAsync(
            ContainmentReleaseAuthorization authorization,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class MarkerServiceProvider(Marker marker) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(Marker) ? marker : null;
    }

    private sealed record Marker(string Value);

    private sealed class TestSession : AgentSession;
}
