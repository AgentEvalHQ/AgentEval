// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using AgentEval.MAF.Gatekeeper;
using Microsoft.Extensions.DependencyInjection;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 5, Task 5.1 — measured AIFunction resource-selection seam spike.</summary>
public sealed class ResourceIsolationDecoratorSpikeTests
{
    private static readonly ContainmentTarget Target =
        new ContainmentTarget.Session("tenant-a", "session-a");

    [Fact]
    public async Task CleanRootSession_SelectsNormalProviderBeforeInnerExecution()
    {
        var fixture = Fixture(ContainmentSnapshot.NotContained(Target));
        var session = new TestSession();
        using var scope = AgentRunScope.Begin(session, "agent", trace: null);

        var result = await fixture.Function.InvokeAsync();

        Assert.Equal("normal", result?.ToString());
        Assert.Equal(1, fixture.Normal.CallCount);
        Assert.Equal(0, fixture.Isolated.CallCount);
        Assert.Same(session, fixture.ResolvedSession);
    }

    [Fact]
    public async Task ActiveRootSession_RealMafToolInvocationSelectsIsolatedProvider()
    {
        var fixture = Fixture(Active(Target));
        var scripted = new ScriptedChatClient()
            .AddToolCall("call-1", fixture.Function.Name, new Dictionary<string, object?>())
            .AddText("done");
        var inner = new ChatClientAgent(
            scripted,
            new ChatClientAgentOptions
            {
                Name = "resource-spike",
                ChatOptions = new ChatOptions { Tools = [fixture.Function] },
            });
        var agent = inner.AsBuilder()
            .UseGatekeeper(
                GatekeeperEnforcement.ReplaceResult,
                _ => { })
            .Build();
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("use the resource", session);

        Assert.Equal(0, fixture.Normal.CallCount);
        Assert.Equal(1, fixture.Isolated.CallCount);
        Assert.Same(session, fixture.ResolvedSession);
    }

    [Fact]
    public async Task NestedRun_RoutesFromRootSessionRatherThanChildSession()
    {
        var fixture = Fixture(Active(Target));
        var rootSession = new TestSession();
        var childSession = new TestSession();
        using var root = AgentRunScope.Begin(rootSession, "root-agent", trace: null);
        using var child = AgentRunScope.Begin(childSession, "child-agent", trace: null);

        await fixture.Function.InvokeAsync();

        Assert.Same(rootSession, fixture.ResolvedSession);
        Assert.Equal(1, fixture.Isolated.CallCount);
        Assert.Equal(0, fixture.Normal.CallCount);
    }

    [Fact]
    public async Task MissingIndeterminateOrThrowingContext_NeverSelectsNormalProvider()
    {
        var missing = Fixture(ContainmentSnapshot.NotContained(Target));
        await missing.Function.InvokeAsync();

        var indeterminate = Fixture(
            ContainmentSnapshot.Indeterminate(Target, "store_unavailable"));
        using (AgentRunScope.Begin(new TestSession(), "agent", trace: null))
        {
            await indeterminate.Function.InvokeAsync();
        }

        var throwing = Fixture(
            _ => throw new InvalidOperationException("sensitive store detail"));
        using (AgentRunScope.Begin(new TestSession(), "agent", trace: null))
        {
            await throwing.Function.InvokeAsync();
        }

        Assert.Equal(0, missing.Normal.CallCount);
        Assert.Equal(1, missing.Isolated.CallCount);
        Assert.Equal(0, indeterminate.Normal.CallCount);
        Assert.Equal(1, indeterminate.Isolated.CallCount);
        Assert.Equal(0, throwing.Normal.CallCount);
        Assert.Equal(1, throwing.Isolated.CallCount);
    }

    [Fact]
    public async Task Decorator_PreservesMetadataAndCallerCancellation()
    {
        var fixture = Fixture(ContainmentSnapshot.NotContained(Target));

        Assert.Equal(fixture.Inner.Name, fixture.Function.Name);
        Assert.Equal(fixture.Inner.Description, fixture.Function.Description);
        Assert.Equal(fixture.Inner.JsonSchema.GetRawText(), fixture.Function.JsonSchema.GetRawText());
        Assert.Equal(fixture.Inner.ReturnJsonSchema?.GetRawText(), fixture.Function.ReturnJsonSchema?.GetRawText());
        Assert.Equal(fixture.Inner.UnderlyingMethod, fixture.Function.UnderlyingMethod);
        Assert.Same(fixture.Inner.JsonSerializerOptions, fixture.Function.JsonSerializerOptions);
        Assert.Same(fixture.Inner.AdditionalProperties, fixture.Function.AdditionalProperties);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Function.InvokeAsync(
                new AIFunctionArguments(),
                cancellation.Token).AsTask());

        Assert.Equal(0, fixture.Store.ReadCount);
        Assert.Equal(0, fixture.Normal.CallCount);
        Assert.Equal(0, fixture.Isolated.CallCount);
    }

    private static SpikeFixture Fixture(ContainmentSnapshot snapshot)
        => Fixture(_ => snapshot);

    private static SpikeFixture Fixture(
        Func<ContainmentTarget, ContainmentSnapshot> read)
    {
        var normal = new ResourceProbe("normal");
        var isolated = new ResourceProbe("isolated");
        var store = new RoutingStore(read);
        AgentSession? resolvedSession = null;
        var inner = AIFunctionFactory.Create(
            (IServiceProvider services) =>
                services.GetRequiredService<ResourceProbe>().Use(),
            "resource_probe",
            "Resolves one resource from the invocation service provider.");
        var function = new ResourceRoutingFunction(
            inner,
            store,
            session =>
            {
                resolvedSession = session;
                return Target;
            },
            new ProbeServiceProvider(normal),
            new ProbeServiceProvider(isolated));
        return new SpikeFixture(
            function,
            inner,
            store,
            normal,
            isolated,
            () => resolvedSession);
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

    private sealed record SpikeFixture(
        ResourceRoutingFunction Function,
        AIFunction Inner,
        RoutingStore Store,
        ResourceProbe Normal,
        ResourceProbe Isolated,
        Func<AgentSession?> SessionAccessor)
    {
        public AgentSession? ResolvedSession => SessionAccessor();
    }

    private sealed class ResourceRoutingFunction : AIFunction
    {
        private readonly AIFunction _inner;
        private readonly IContainmentStore _store;
        private readonly Func<AgentSession, ContainmentTarget> _targetResolver;
        private readonly IServiceProvider _normalServices;
        private readonly IServiceProvider _isolatedServices;

        public ResourceRoutingFunction(
            AIFunction inner,
            IContainmentStore store,
            Func<AgentSession, ContainmentTarget> targetResolver,
            IServiceProvider normalServices,
            IServiceProvider isolatedServices)
        {
            _inner = inner;
            _store = store;
            _targetResolver = targetResolver;
            _normalServices = normalServices;
            _isolatedServices = isolatedServices;
        }

        public override string Name => _inner.Name;

        public override string Description => _inner.Description;

        public override JsonElement JsonSchema => _inner.JsonSchema;

        public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;

        public override MethodInfo? UnderlyingMethod => _inner.UnderlyingMethod;

        public override JsonSerializerOptions JsonSerializerOptions =>
            _inner.JsonSerializerOptions;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties =>
            _inner.AdditionalProperties;

        public override object? GetService(
            Type serviceType,
            object? serviceKey = null)
            => serviceType == typeof(ResourceRoutingFunction) ||
               serviceType == typeof(AIFunction)
                ? this
                : _inner.GetService(serviceType, serviceKey);

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = MustIsolate(cancellationToken)
                ? _isolatedServices
                : _normalServices;
            var routedArguments = new AIFunctionArguments(arguments)
            {
                Context = arguments.Context,
                Services = new SelectedServiceProvider(
                    selected,
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
                       snapshot.State is ContainmentSnapshotState.Active
                           or ContainmentSnapshotState.Indeterminate;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is not (StackOverflowException or OutOfMemoryException))
            {
                return true;
            }
        }
    }

    private sealed class SelectedServiceProvider(
        IServiceProvider selected,
        IServiceProvider? fallback) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => selected.GetService(serviceType) ??
               fallback?.GetService(serviceType);
    }

    private sealed class ProbeServiceProvider(ResourceProbe probe) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(ResourceProbe) ? probe : null;
    }

    private sealed class ResourceProbe(string name)
    {
        public int CallCount { get; private set; }

        public string Use()
        {
            CallCount++;
            return name;
        }
    }

    private sealed class RoutingStore(
        Func<ContainmentTarget, ContainmentSnapshot> read) : IContainmentStore
    {
        public int ReadCount { get; private set; }

        public ContainmentSnapshot GetCurrent(ContainmentTarget target)
        {
            ReadCount++;
            return read(target);
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

    private sealed class TestSession : AgentSession;
}
