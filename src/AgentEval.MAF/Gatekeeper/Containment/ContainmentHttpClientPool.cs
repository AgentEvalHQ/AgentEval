// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Caller-owned normal and isolated <see cref="HttpClient"/> pipelines with separate handlers and enforced
/// request-concurrency caps. The default handlers also use separate socket pools.
/// </summary>
/// <remarks>
/// Dispose this object only after its in-flight requests finish. The pool bounds local request/connection
/// consumption; it cannot partition a downstream provider quota shared by the same credential.
/// </remarks>
public sealed class ContainmentHttpClientPool : IDisposable
{
    private const int MaximumConcurrency = 4096;
    private static readonly TimeSpan MaximumPooledConnectionLifetime = TimeSpan.FromDays(1);
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(10);

    private readonly ConcurrencyCappedHttpMessageHandler _normalHandler;
    private readonly ConcurrencyCappedHttpMessageHandler _isolatedHandler;
    private readonly SingleHttpClientServiceProvider _normalServices;
    private readonly SingleHttpClientServiceProvider _isolatedServices;
    private int _disposed;

    /// <summary>Creates two distinct caller-owned HTTP pipelines from a validated options snapshot.</summary>
    public ContainmentHttpClientPool(ContainmentHttpClientPoolOptions? options = null)
    {
        var snapshot = Snapshot.Create(options ?? new ContainmentHttpClientPoolOptions());
        HttpMessageHandler? normalPrimary = null;
        HttpMessageHandler? isolatedPrimary = null;
        ConcurrencyCappedHttpMessageHandler? normalHandler = null;
        ConcurrencyCappedHttpMessageHandler? isolatedHandler = null;
        HttpClient? normalClient = null;
        HttpClient? isolatedClient = null;

        try
        {
            normalPrimary = CreatePrimaryHandler(
                snapshot.NormalPrimaryHandlerFactory,
                snapshot.NormalMaxConcurrency,
                snapshot.PooledConnectionLifetime);
            isolatedPrimary = CreatePrimaryHandler(
                snapshot.IsolatedPrimaryHandlerFactory,
                snapshot.IsolatedMaxConcurrency,
                snapshot.PooledConnectionLifetime);

            if (ReferenceEquals(normalPrimary, isolatedPrimary))
            {
                normalPrimary.Dispose();
                normalPrimary = null;
                isolatedPrimary = null;
                throw new ArgumentException(
                    "Normal and isolated handler factories must return distinct handler instances.",
                    nameof(options));
            }

            normalHandler = new ConcurrencyCappedHttpMessageHandler(
                normalPrimary,
                snapshot.NormalMaxConcurrency);
            normalPrimary = null;
            isolatedHandler = new ConcurrencyCappedHttpMessageHandler(
                isolatedPrimary,
                snapshot.IsolatedMaxConcurrency);
            isolatedPrimary = null;

            normalClient = CreateClient(normalHandler, snapshot);
            isolatedClient = CreateClient(isolatedHandler, snapshot);

            _normalHandler = normalHandler;
            _isolatedHandler = isolatedHandler;
            NormalClient = normalClient;
            IsolatedClient = isolatedClient;
            _normalServices = new SingleHttpClientServiceProvider(NormalClient);
            _isolatedServices = new SingleHttpClientServiceProvider(IsolatedClient);
        }
        catch
        {
            if (normalClient is not null)
            {
                normalClient.Dispose();
            }
            else
            {
                normalHandler?.Dispose();
            }

            if (isolatedClient is not null)
            {
                isolatedClient.Dispose();
            }
            else
            {
                isolatedHandler?.Dispose();
            }

            normalPrimary?.Dispose();
            isolatedPrimary?.Dispose();
            throw;
        }
    }

    /// <summary>The normal caller pool.</summary>
    public HttpClient NormalClient { get; }

    /// <summary>The separately capped pool selected for contained or indeterminate sessions.</summary>
    public HttpClient IsolatedClient { get; }

    /// <summary>A provider that resolves only the normal <see cref="HttpClient"/>.</summary>
    public IServiceProvider NormalServices => _normalServices;

    /// <summary>A provider that resolves only the isolated <see cref="HttpClient"/>.</summary>
    public IServiceProvider IsolatedServices => _isolatedServices;

    /// <summary>Current number of normal requests that hold a concurrency permit.</summary>
    public int NormalCurrentRequests => _normalHandler.CurrentRequests;

    /// <summary>Highest observed number of simultaneous normal requests.</summary>
    public int NormalPeakRequests => _normalHandler.PeakRequests;

    /// <summary>Current number of isolated requests that hold a concurrency permit.</summary>
    public int IsolatedCurrentRequests => _isolatedHandler.CurrentRequests;

    /// <summary>Highest observed number of simultaneous isolated requests.</summary>
    public int IsolatedPeakRequests => _isolatedHandler.PeakRequests;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        NormalClient.Dispose();
        IsolatedClient.Dispose();
    }

    private static HttpMessageHandler CreatePrimaryHandler(
        Func<HttpMessageHandler>? factory,
        int maxConcurrency,
        TimeSpan pooledConnectionLifetime)
    {
        if (factory is not null)
        {
            return factory() ??
                throw new InvalidOperationException(
                    "An HTTP primary-handler factory returned null.");
        }

        return new SocketsHttpHandler
        {
            MaxConnectionsPerServer = maxConcurrency,
            PooledConnectionLifetime = pooledConnectionLifetime,
        };
    }

    private static HttpClient CreateClient(
        HttpMessageHandler handler,
        Snapshot snapshot)
    {
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = snapshot.Timeout,
        };
        if (snapshot.BaseAddress is not null)
        {
            client.BaseAddress = snapshot.BaseAddress;
        }

        return client;
    }

    private sealed class ConcurrencyCappedHttpMessageHandler : DelegatingHandler
    {
        private readonly SemaphoreSlim _permits;
        private int _currentRequests;
        private int _peakRequests;

        public ConcurrencyCappedHttpMessageHandler(
            HttpMessageHandler innerHandler,
            int maxConcurrency)
            : base(innerHandler)
        {
            _permits = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        }

        public int CurrentRequests => Volatile.Read(ref _currentRequests);

        public int PeakRequests => Volatile.Read(ref _peakRequests);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await _permits.WaitAsync(cancellationToken).ConfigureAwait(false);
            var current = Interlocked.Increment(ref _currentRequests);
            RecordPeak(current);

            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _currentRequests);
                _permits.Release();
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _permits.Dispose();
            }
        }

        private void RecordPeak(int current)
        {
            var observed = Volatile.Read(ref _peakRequests);
            while (current > observed)
            {
                var prior = Interlocked.CompareExchange(
                    ref _peakRequests,
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

    private sealed class SingleHttpClientServiceProvider(
        HttpClient client) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceType == typeof(HttpClient) ? client : null;
        }
    }

    private sealed record Snapshot(
        int NormalMaxConcurrency,
        int IsolatedMaxConcurrency,
        TimeSpan PooledConnectionLifetime,
        TimeSpan Timeout,
        Uri? BaseAddress,
        Func<HttpMessageHandler>? NormalPrimaryHandlerFactory,
        Func<HttpMessageHandler>? IsolatedPrimaryHandlerFactory)
    {
        public static Snapshot Create(ContainmentHttpClientPoolOptions options)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(
                options.NormalMaxConcurrency,
                1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                options.NormalMaxConcurrency,
                MaximumConcurrency);
            ArgumentOutOfRangeException.ThrowIfLessThan(
                options.IsolatedMaxConcurrency,
                1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                options.IsolatedMaxConcurrency,
                options.NormalMaxConcurrency);

            ValidateDuration(
                options.PooledConnectionLifetime,
                TimeSpan.FromSeconds(1),
                MaximumPooledConnectionLifetime,
                nameof(options.PooledConnectionLifetime));
            ValidateDuration(
                options.Timeout,
                TimeSpan.FromSeconds(1),
                MaximumTimeout,
                nameof(options.Timeout));

            if (options.BaseAddress is not null &&
                (!options.BaseAddress.IsAbsoluteUri ||
                 options.BaseAddress.Scheme is not ("http" or "https")))
            {
                throw new ArgumentException(
                    "The HTTP base address must be an absolute HTTP or HTTPS URI.",
                    nameof(options.BaseAddress));
            }

            return new Snapshot(
                options.NormalMaxConcurrency,
                options.IsolatedMaxConcurrency,
                options.PooledConnectionLifetime,
                options.Timeout,
                options.BaseAddress,
                options.NormalPrimaryHandlerFactory,
                options.IsolatedPrimaryHandlerFactory);
        }

        private static void ValidateDuration(
            TimeSpan value,
            TimeSpan minimum,
            TimeSpan maximum,
            string parameterName)
        {
            if (value < minimum || value > maximum)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    $"Value must be between {minimum} and {maximum}.");
            }
        }
    }
}
