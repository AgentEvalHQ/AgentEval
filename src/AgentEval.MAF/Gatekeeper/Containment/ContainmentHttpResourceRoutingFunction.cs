// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Routes a local, DI-aware <see cref="AIFunction"/> to the normal or isolated
/// <see cref="HttpClient"/> in a <see cref="ContainmentHttpClientPool"/> before the function executes.
/// </summary>
/// <remarks>
/// The inner function must resolve <see cref="HttpClient"/> from the invocation's
/// <see cref="AIFunctionArguments.Services"/> provider. A client already captured by the function cannot be
/// replaced. Selection linearizes at the containment snapshot read; an already in-flight invocation is not
/// rerouted by a later containment mutation. The decorator borrows its dependencies and does not dispose them.
/// </remarks>
public sealed class ContainmentHttpResourceRoutingFunction : AIFunction
{
    private readonly AIFunction _inner;
    private readonly IContainmentStore _store;
    private readonly Func<AgentSession, ContainmentTarget> _targetResolver;
    private readonly ContainmentHttpClientPool _pool;

    /// <summary>Creates a containment-aware HTTP resource decorator.</summary>
    /// <param name="inner">The local function to invoke after resource selection.</param>
    /// <param name="store">The synchronous containment snapshot store.</param>
    /// <param name="targetResolver">
    /// Resolves the exact tenant-scoped containment target from the authoritative root session.
    /// </param>
    /// <param name="pool">The caller-owned normal and isolated HTTP pool.</param>
    public ContainmentHttpResourceRoutingFunction(
        AIFunction inner,
        IContainmentStore store,
        Func<AgentSession, ContainmentTarget> targetResolver,
        ContainmentHttpClientPool pool)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _targetResolver = targetResolver ?? throw new ArgumentNullException(nameof(targetResolver));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));

        if (_inner.GetService(
                typeof(ContainmentHttpResourceRoutingFunction)) is
            ContainmentHttpResourceRoutingFunction)
        {
            throw new ArgumentException(
                "The function is already wrapped for containment HTTP resource isolation.",
                nameof(inner));
        }
    }

    /// <inheritdoc />
    public override string Name => _inner.Name;

    /// <inheritdoc />
    public override string Description => _inner.Description;

    /// <inheritdoc />
    public override JsonElement JsonSchema => _inner.JsonSchema;

    /// <inheritdoc />
    public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;

    /// <inheritdoc />
    public override MethodInfo? UnderlyingMethod => _inner.UnderlyingMethod;

    /// <inheritdoc />
    public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions;

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?> AdditionalProperties =>
        _inner.AdditionalProperties;

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceType == typeof(ContainmentHttpResourceRoutingFunction) ||
               serviceType == typeof(AIFunction)
            ? this
            : _inner.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc />
    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedServices = MustIsolate(cancellationToken)
            ? _pool.IsolatedServices
            : _pool.NormalServices;

        cancellationToken.ThrowIfCancellationRequested();
        var routedArguments = new AIFunctionArguments(arguments)
        {
            Context = arguments.Context,
            Services = new HttpResourceServiceProvider(
                selectedServices,
                arguments.Services),
        };

        return _inner.InvokeAsync(routedArguments, cancellationToken);
    }

    private bool MustIsolate(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = AgentRunScope.Current?.Root.Session;
        if (session is null)
        {
            return true;
        }

        try
        {
            var target = _targetResolver(session);
            if (target is null)
            {
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _store.GetCurrent(target);
            return snapshot is null ||
                   snapshot.Target != target ||
                   snapshot.State is not (
                       ContainmentSnapshotState.NotContained or
                       ContainmentSnapshotState.Released);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is not (StackOverflowException or OutOfMemoryException))
        {
            return true;
        }
    }

    private sealed class HttpResourceServiceProvider(
        IServiceProvider selected,
        IServiceProvider? fallback) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            var selectedService = selected.GetService(serviceType);
            return selectedService ?? (serviceType == typeof(HttpClient)
                ? null
                : fallback?.GetService(serviceType));
        }
    }
}

/// <summary>Composition helpers for containment-aware HTTP resource routing.</summary>
public static class ContainmentHttpResourceRoutingExtensions
{
    /// <summary>
    /// Wraps a local function so it resolves <see cref="HttpClient"/> from a separate capped pool when the
    /// authoritative root session is contained or containment cannot be proved safe.
    /// </summary>
    /// <remarks>
    /// This is an explicit degraded-execution mode. Do not also configure a
    /// <see cref="ContainedIdentityGate"/> for the same target: that admission gate intentionally blocks
    /// the run before any function can execute.
    /// </remarks>
    public static AIFunction WithContainmentHttpResourceIsolation(
        this AIFunction function,
        IContainmentStore store,
        Func<AgentSession, ContainmentTarget> targetResolver,
        ContainmentHttpClientPool pool)
        => new ContainmentHttpResourceRoutingFunction(
            function ?? throw new ArgumentNullException(nameof(function)),
            store ?? throw new ArgumentNullException(nameof(store)),
            targetResolver ?? throw new ArgumentNullException(nameof(targetResolver)),
            pool ?? throw new ArgumentNullException(nameof(pool)));

    /// <summary>
    /// Fails construction when any caller-declared HTTP-resource-consuming function is not decorated.
    /// </summary>
    /// <param name="resourceConsumingFunctions">
    /// The complete caller-declared set of local functions that resolve <see cref="HttpClient"/>.
    /// </param>
    public static void ValidateContainmentHttpResourceIsolation(
        this IEnumerable<AIFunction> resourceConsumingFunctions)
    {
        ArgumentNullException.ThrowIfNull(resourceConsumingFunctions);
        var count = 0;
        foreach (var function in resourceConsumingFunctions)
        {
            ArgumentNullException.ThrowIfNull(function);
            count++;
            if (count > 4096)
            {
                throw new ArgumentException(
                    "At most 4096 resource-consuming functions may be validated.",
                    nameof(resourceConsumingFunctions));
            }

            var isolation = function.GetService(
                typeof(ContainmentHttpResourceRoutingFunction));
            if (isolation is not ContainmentHttpResourceRoutingFunction)
            {
                throw new InvalidOperationException(
                    $"HTTP resource isolation is missing for a declared function at index {count - 1}.");
            }
        }

        if (count == 0)
        {
            throw new ArgumentException(
                "At least one resource-consuming function is required.",
                nameof(resourceConsumingFunctions));
        }
    }
}
