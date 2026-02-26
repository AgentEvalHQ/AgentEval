// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>
/// Base class for plugins with common functionality.
/// </summary>
public abstract class AgentEvalPluginBase : IAgentEvalPlugin
{
    private PluginLifecycleStage _stage = PluginLifecycleStage.Initializing;

    public abstract string PluginId { get; }
    public abstract string Name { get; }
    public virtual Version Version => new(1, 0, 0);
    public virtual string? Description => null;
    public PluginLifecycleStage Stage => _stage;
    public virtual IReadOnlyList<string> Dependencies => Array.Empty<string>();

    protected IPluginContext? Context { get; private set; }
    protected IAgentEvalLogger Logger => Context?.Logger ?? NullAgentEvalLogger.Instance;

    public virtual async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        Context = context;
        await OnInitializeAsync(cancellationToken);
        _stage = PluginLifecycleStage.Ready;
    }

    protected virtual Task OnInitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual Task OnBeforeEvaluationAsync(EvaluationContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public virtual Task OnAfterEvaluationAsync(EvaluationContext context, IList<MetricResult> results, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public virtual async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _stage = PluginLifecycleStage.ShuttingDown;
        await OnShutdownAsync(cancellationToken);
        _stage = PluginLifecycleStage.Disposed;
    }

    protected virtual Task OnShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual void Dispose()
    {
        if (_stage != PluginLifecycleStage.Disposed)
        {
            ShutdownAsync().GetAwaiter().GetResult();
        }
        GC.SuppressFinalize(this);
    }
}
