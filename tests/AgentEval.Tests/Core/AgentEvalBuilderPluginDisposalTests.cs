// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Core;

/// <summary>
/// Regression coverage for BUG-40: plugin disposal (Dispose alongside ShutdownAsync) and cleanup
/// of already-initialized plugins on a partial BuildAsync failure.
/// </summary>
public class AgentEvalBuilderPluginDisposalTests
{
    [Fact]
    public async Task DisposeAsync_CallsBothShutdownAndDispose()
    {
        var plugin = new RecordingPlugin("p1");
        var runner = await AgentEvalBuilder.Create().WithNoLogging().AddPlugin(plugin).BuildAsync();

        await runner.DisposeAsync();

        Assert.True(plugin.ShutdownCalled, "ShutdownAsync should be called on dispose");
        Assert.True(plugin.DisposeCalled, "Dispose() should also be called on dispose (BUG-40)");
    }

    [Fact]
    public async Task BuildAsync_PartialInitFailure_CleansUpInitializedPlugins()
    {
        var p1 = new RecordingPlugin("p1");
        var p2 = new RecordingPlugin("p2", throwOnInit: true);
        var builder = AgentEvalBuilder.Create().WithNoLogging().AddPlugin(p1).AddPlugin(p2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync());

        Assert.True(p1.ShutdownCalled, "already-initialized p1 must be shut down on partial-init failure (BUG-40)");
        Assert.True(p1.DisposeCalled, "already-initialized p1 must be disposed on partial-init failure (BUG-40)");
    }

    private sealed class RecordingPlugin : IAgentEvalPlugin
    {
        private readonly bool _throwOnInit;

        public RecordingPlugin(string name, bool throwOnInit = false)
        {
            Name = name;
            _throwOnInit = throwOnInit;
        }

        public bool ShutdownCalled { get; private set; }
        public bool DisposeCalled { get; private set; }

        public string PluginId => Name;
        public string Name { get; }
        public string? Description => "recording test plugin";
        public Version Version { get; } = new(1, 0, 0);
        public PluginLifecycleStage Stage => PluginLifecycleStage.Ready;
        public IReadOnlyList<string> Dependencies { get; } = Array.Empty<string>();

        public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => _throwOnInit ? throw new InvalidOperationException("init failed") : Task.CompletedTask;

        public Task OnBeforeEvaluationAsync(EvaluationContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task OnAfterEvaluationAsync(EvaluationContext context, IList<MetricResult> results, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            ShutdownCalled = true;
            return Task.CompletedTask;
        }

        public void Dispose() => DisposeCalled = true;
    }
}
