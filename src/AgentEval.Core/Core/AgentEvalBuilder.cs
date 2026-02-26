// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace AgentEval.Core;

/// <summary>
/// Fluent builder for configuring and creating an AgentEval evaluation pipeline.
/// Supports plugin registration, metric configuration, and DI integration.
/// </summary>
public sealed class AgentEvalBuilder
{
    private readonly List<IAgentEvalPlugin> _plugins = new();
    private readonly List<IMetric> _metrics = new();
    private readonly List<IResultTransformer> _transformers = new();
    private readonly Dictionary<string, object?> _configuration = new();
    private IChatClient? _evaluatorClient;
    private IAgentEvalLogger _logger = new ConsoleAgentEvalLogger();
    private double _defaultThreshold = 0.7;

    /// <summary>
    /// Creates a new AgentEval builder.
    /// </summary>
    public static AgentEvalBuilder Create() => new();

    /// <summary>
    /// Configures the chat client to use for LLM-based evaluations.
    /// </summary>
    public AgentEvalBuilder WithEvaluatorClient(IChatClient client)
    {
        _evaluatorClient = client ?? throw new ArgumentNullException(nameof(client));
        return this;
    }

    /// <summary>
    /// Configures the logger to use.
    /// </summary>
    public AgentEvalBuilder WithLogger(IAgentEvalLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        return this;
    }

    /// <summary>
    /// Uses a console logger with the specified minimum level.
    /// </summary>
    public AgentEvalBuilder WithConsoleLogger(LogLevel minimumLevel = LogLevel.Information)
    {
        _logger = new ConsoleAgentEvalLogger(minimumLevel);
        return this;
    }

    /// <summary>
    /// Disables logging.
    /// </summary>
    public AgentEvalBuilder WithNoLogging()
    {
        _logger = NullAgentEvalLogger.Instance;
        return this;
    }

    /// <summary>
    /// Adds a plugin to the evaluation pipeline.
    /// </summary>
    public AgentEvalBuilder AddPlugin(IAgentEvalPlugin plugin)
    {
        _plugins.Add(plugin ?? throw new ArgumentNullException(nameof(plugin)));
        return this;
    }

    /// <summary>
    /// Adds a plugin by type (will be instantiated).
    /// </summary>
    public AgentEvalBuilder AddPlugin<TPlugin>() where TPlugin : IAgentEvalPlugin, new()
    {
        _plugins.Add(new TPlugin());
        return this;
    }

    /// <summary>
    /// Adds a metric to the registry.
    /// </summary>
    public AgentEvalBuilder AddMetric(IMetric metric)
    {
        _metrics.Add(metric ?? throw new ArgumentNullException(nameof(metric)));
        return this;
    }

    /// <summary>
    /// Adds a metric by type (will be instantiated).
    /// </summary>
    public AgentEvalBuilder AddMetric<TMetric>() where TMetric : IMetric, new()
    {
        _metrics.Add(new TMetric());
        return this;
    }

    /// <summary>
    /// Adds multiple metrics.
    /// </summary>
    public AgentEvalBuilder AddMetrics(params IMetric[] metrics)
    {
        foreach (var metric in metrics)
        {
            AddMetric(metric);
        }
        return this;
    }

    /// <summary>
    /// Adds a result transformer.
    /// </summary>
    public AgentEvalBuilder AddTransformer(IResultTransformer transformer)
    {
        _transformers.Add(transformer ?? throw new ArgumentNullException(nameof(transformer)));
        return this;
    }

    /// <summary>
    /// Sets the default pass/fail threshold for metrics.
    /// </summary>
    public AgentEvalBuilder WithDefaultThreshold(double threshold)
    {
        if (threshold < 0 || threshold > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be between 0 and 1.");

        _defaultThreshold = threshold;
        return this;
    }

    /// <summary>
    /// Adds a configuration value.
    /// </summary>
    public AgentEvalBuilder Configure(string key, object? value)
    {
        _configuration[key] = value;
        return this;
    }

    /// <summary>
    /// Adds multiple configuration values.
    /// </summary>
    public AgentEvalBuilder Configure(IDictionary<string, object?> configuration)
    {
        foreach (var kvp in configuration)
        {
            _configuration[kvp.Key] = kvp.Value;
        }
        return this;
    }

    /// <summary>
    /// Builds and initializes the AgentEval runner.
    /// </summary>
    public async Task<AgentEvalRunner> BuildAsync(CancellationToken cancellationToken = default)
    {
        var registry = new MetricRegistry();

        // Register all configured metrics
        foreach (var metric in _metrics)
        {
            registry.Register(metric);
        }

        // Create plugin context
        var context = new PluginContextImpl(registry, _logger, _configuration);

        // Initialize plugins in dependency order
        var orderedPlugins = OrderByDependencies(_plugins);
        foreach (var plugin in orderedPlugins)
        {
            _logger.LogDebug($"Initializing plugin: {plugin.Name} v{plugin.Version}");
            await plugin.InitializeAsync(context, cancellationToken);
            _logger.LogDebug($"Plugin initialized: {plugin.Name}");
        }

        // Order transformers by priority
        _transformers.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        return new AgentEvalRunner(
            registry,
            orderedPlugins,
            _transformers,
            _evaluatorClient,
            _logger,
            _defaultThreshold);
    }

    /// <summary>
    /// Builds synchronously (blocks until initialization completes).
    /// </summary>
    public AgentEvalRunner Build()
    {
        return BuildAsync().GetAwaiter().GetResult();
    }

    private static List<IAgentEvalPlugin> OrderByDependencies(List<IAgentEvalPlugin> plugins)
    {
        var ordered = new List<IAgentEvalPlugin>();
        var remaining = new HashSet<IAgentEvalPlugin>(plugins);
        var resolved = new HashSet<string>();

        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(p => p.Dependencies.All(d => resolved.Contains(d)))
                .ToList();

            if (ready.Count == 0 && remaining.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Circular or missing plugin dependencies detected. Unresolved: {string.Join(", ", remaining.Select(p => p.PluginId))}");
            }

            foreach (var plugin in ready)
            {
                remaining.Remove(plugin);
                ordered.Add(plugin);
                resolved.Add(plugin.PluginId);
            }
        }

        return ordered;
    }

    private sealed class PluginContextImpl : IPluginContext
    {
        public IMetricRegistry Metrics { get; }
        public IAgentEvalLogger Logger { get; }
        public IReadOnlyDictionary<string, object?> Configuration { get; }

        public PluginContextImpl(IMetricRegistry metrics, IAgentEvalLogger logger, Dictionary<string, object?> configuration)
        {
            Metrics = metrics;
            Logger = logger;
            Configuration = configuration;
        }

        public T? GetConfig<T>(string key)
        {
            if (Configuration.TryGetValue(key, out var value) && value is T typedValue)
            {
                return typedValue;
            }
            return default;
        }

        public T GetRequiredConfig<T>(string key)
        {
            if (!Configuration.TryGetValue(key, out var value))
            {
                throw new KeyNotFoundException($"Required configuration key '{key}' not found.");
            }

            if (value is T typedValue)
            {
                return typedValue;
            }

            throw new InvalidCastException($"Configuration key '{key}' has type {value?.GetType().Name ?? "null"}, expected {typeof(T).Name}.");
        }
    }
}

/// <summary>
/// Runs evaluations with registered metrics and plugins.
/// </summary>
public sealed class AgentEvalRunner : IAsyncDisposable
{
    private readonly MetricRegistry _registry;
    private readonly IReadOnlyList<IAgentEvalPlugin> _plugins;
    private readonly IReadOnlyList<IResultTransformer> _transformers;
    private readonly IChatClient? _evaluatorClient;
    private readonly IAgentEvalLogger _logger;
    private readonly double _defaultThreshold;

    internal AgentEvalRunner(
        MetricRegistry registry,
        IReadOnlyList<IAgentEvalPlugin> plugins,
        IReadOnlyList<IResultTransformer> transformers,
        IChatClient? evaluatorClient,
        IAgentEvalLogger logger,
        double defaultThreshold)
    {
        _registry = registry;
        _plugins = plugins;
        _transformers = transformers;
        _evaluatorClient = evaluatorClient;
        _logger = logger;
        _defaultThreshold = defaultThreshold;
    }

    /// <summary>
    /// Gets the metric registry.
    /// </summary>
    public IMetricRegistry Metrics => _registry;

    /// <summary>
    /// Gets the logger.
    /// </summary>
    public IAgentEvalLogger Logger => _logger;

    /// <summary>
    /// Gets the evaluator client.
    /// </summary>
    public IChatClient? EvaluatorClient => _evaluatorClient;

    /// <summary>
    /// Runs a single metric by name.
    /// </summary>
    public async Task<MetricResult> EvaluateAsync(
        string metricName,
        EvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        var metric = _registry.GetRequired(metricName);

        // Run before hooks
        foreach (var plugin in _plugins)
        {
            await plugin.OnBeforeEvaluationAsync(context, cancellationToken);
        }

        // Run metric
        var result = await metric.EvaluateAsync(context, cancellationToken);

        // Apply transformers
        foreach (var transformer in _transformers)
        {
            result = transformer.Transform(result, context);
        }

        // Run after hooks
        var results = new List<MetricResult> { result };
        foreach (var plugin in _plugins)
        {
            await plugin.OnAfterEvaluationAsync(context, results, cancellationToken);
        }

        _logger.LogMetricResult(result);

        return results[0];
    }

    /// <summary>
    /// Runs a single metric by name with simple input/output.
    /// </summary>
    public Task<MetricResult> EvaluateAsync(
        string metricName,
        string input,
        string output,
        CancellationToken cancellationToken = default)
    {
        var context = new EvaluationContext { Input = input, Output = output };
        return EvaluateAsync(metricName, context, cancellationToken);
    }

    /// <summary>
    /// Runs multiple metrics.
    /// </summary>
    public async Task<IReadOnlyList<MetricResult>> EvaluateAsync(
        IEnumerable<string> metricNames,
        EvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        var results = new List<MetricResult>();

        // Run before hooks
        foreach (var plugin in _plugins)
        {
            await plugin.OnBeforeEvaluationAsync(context, cancellationToken);
        }

        // Run all metrics
        foreach (var metricName in metricNames)
        {
            var metric = _registry.GetRequired(metricName);
            var result = await metric.EvaluateAsync(context, cancellationToken);

            // Apply transformers
            foreach (var transformer in _transformers)
            {
                result = transformer.Transform(result, context);
            }

            results.Add(result);
            _logger.LogMetricResult(result);
        }

        // Run after hooks
        foreach (var plugin in _plugins)
        {
            await plugin.OnAfterEvaluationAsync(context, results, cancellationToken);
        }

        return results;
    }

    /// <summary>
    /// Runs multiple metrics with simple input/output.
    /// </summary>
    public Task<IReadOnlyList<MetricResult>> EvaluateAsync(
        IEnumerable<string> metricNames,
        string input,
        string output,
        CancellationToken cancellationToken = default)
    {
        var context = new EvaluationContext { Input = input, Output = output };
        return EvaluateAsync(metricNames, context, cancellationToken);
    }

    /// <summary>
    /// Runs all registered metrics.
    /// </summary>
    public Task<IReadOnlyList<MetricResult>> EvaluateAllAsync(
        EvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        return EvaluateAsync(_registry.GetRegisteredNames(), context, cancellationToken);
    }

    /// <summary>
    /// Runs all registered metrics with simple input/output.
    /// </summary>
    public Task<IReadOnlyList<MetricResult>> EvaluateAllAsync(
        string input,
        string output,
        CancellationToken cancellationToken = default)
    {
        var context = new EvaluationContext { Input = input, Output = output };
        return EvaluateAllAsync(context, cancellationToken);
    }

    /// <summary>
    /// Disposes the runner and shuts down all plugins.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var plugin in _plugins.Reverse())
        {
            try
            {
                await plugin.ShutdownAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error shutting down plugin {plugin.Name}");
            }
        }
    }
}
