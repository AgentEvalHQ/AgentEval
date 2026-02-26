// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>
/// Registry for discovering and accessing metrics by name.
/// Enables dynamic metric lookup and plugin-based metric registration.
/// </summary>
public interface IMetricRegistry
{
    /// <summary>
    /// Registers a metric by name.
    /// </summary>
    void Register(string name, IMetric metric);

    /// <summary>
    /// Registers a metric using its Name property.
    /// </summary>
    void Register(IMetric metric);

    /// <summary>
    /// Gets a metric by name. Returns null if not found.
    /// </summary>
    IMetric? Get(string name);

    /// <summary>
    /// Gets a metric by name, throwing if not found.
    /// </summary>
    IMetric GetRequired(string name);

    /// <summary>
    /// Checks if a metric is registered.
    /// </summary>
    bool Contains(string name);

    /// <summary>
    /// Gets all registered metric names.
    /// </summary>
    IEnumerable<string> GetRegisteredNames();

    /// <summary>
    /// Gets all registered metrics.
    /// </summary>
    IEnumerable<IMetric> GetAll();

    /// <summary>
    /// Removes a metric from the registry.
    /// </summary>
    bool Remove(string name);

    /// <summary>
    /// Clears all registered metrics.
    /// </summary>
    void Clear();
}

/// <summary>
/// Plugin lifecycle stage.
/// </summary>
public enum PluginLifecycleStage
{
    /// <summary>Plugin is being initialized.</summary>
    Initializing,
    /// <summary>Plugin is fully initialized and ready.</summary>
    Ready,
    /// <summary>Plugin is being shutdown.</summary>
    ShuttingDown,
    /// <summary>Plugin has been disposed.</summary>
    Disposed
}

/// <summary>
/// Context provided to plugins during initialization.
/// </summary>
public interface IPluginContext
{
    /// <summary>
    /// The metric registry for registering custom metrics.
    /// </summary>
    IMetricRegistry Metrics { get; }

    /// <summary>
    /// The logger for plugin diagnostics.
    /// </summary>
    IAgentEvalLogger Logger { get; }

    /// <summary>
    /// Plugin configuration dictionary.
    /// </summary>
    IReadOnlyDictionary<string, object?> Configuration { get; }

    /// <summary>
    /// Gets a typed configuration value.
    /// </summary>
    T? GetConfig<T>(string key);

    /// <summary>
    /// Gets a required typed configuration value.
    /// </summary>
    T GetRequiredConfig<T>(string key);
}

/// <summary>
/// Interface for AgentEval plugins. Plugins can register custom metrics,
/// transformers, and extend the evaluation pipeline.
/// </summary>
public interface IAgentEvalPlugin : IDisposable
{
    /// <summary>
    /// Unique identifier for this plugin.
    /// </summary>
    string PluginId { get; }

    /// <summary>
    /// Human-readable name for this plugin.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Plugin version.
    /// </summary>
    Version Version { get; }

    /// <summary>
    /// Plugin description.
    /// </summary>
    string? Description { get; }

    /// <summary>
    /// Current lifecycle stage of the plugin.
    /// </summary>
    PluginLifecycleStage Stage { get; }

    /// <summary>
    /// IDs of plugins this plugin depends on.
    /// </summary>
    IReadOnlyList<string> Dependencies { get; }

    /// <summary>
    /// Initializes the plugin with the provided context.
    /// Called once when the plugin is loaded.
    /// </summary>
    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called before evaluation starts. Can modify the evaluation context.
    /// </summary>
    Task OnBeforeEvaluationAsync(EvaluationContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called after evaluation completes. Can modify or enhance results.
    /// </summary>
    Task OnAfterEvaluationAsync(EvaluationContext context, IList<MetricResult> results, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shuts down the plugin gracefully.
    /// </summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for transforming evaluation results.
/// </summary>
public interface IResultTransformer
{
    /// <summary>
    /// Priority for ordering transformers (lower runs first).
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Transforms a metric result.
    /// </summary>
    MetricResult Transform(MetricResult result, EvaluationContext context);
}


