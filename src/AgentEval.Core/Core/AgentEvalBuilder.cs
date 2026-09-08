// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
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
    private readonly List<FloorAdmittedEval> _evals = new();
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
    /// Adds an <see cref="IEval"/> to the pipeline — <b>only</b> with the chance floor it is to be
    /// measured against.
    /// </summary>
    /// <param name="eval">The eval to admit.</param>
    /// <param name="floor">
    /// What an arm that understands nothing would score on this eval. An eval that needs no floor
    /// passes <c>ChanceFloor.NotDerivable(reason)</c> — the reason is mandatory and is what makes
    /// "no floor is derivable here" different from "nobody asked".
    /// </param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="eval"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// No floor was offered, the floor carries no derivation, its derived bar is not a probability,
    /// or an eval with the same <see cref="IEval.Key"/> was already admitted. Every message names the
    /// eval.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>There is deliberately no floorless overload.</b> The programme's loudest rule — "AE-04
    /// before AE-06" — forbids wiring evals into the agent-evaluation entry point <i>while none of
    /// them has a chance floor</i>. This door does not waive that rule and does not bulk-wire the
    /// library's existing <see cref="IEval"/> implementations: it makes the prohibited state
    /// unreachable, one explicit registration at a time. An eval nobody passes through here is
    /// exactly as unwired as it was before. The measurement behind that claim, and the commands that
    /// re-derive it, live on <see cref="FloorAdmittedEval"/> — stated once, because the same two
    /// counts written out twice is how they went stale in the first place.
    /// </para>
    /// <para>
    /// <b>A duplicate key is refused.</b> Two results carrying the same <see cref="IEval.Key"/> and
    /// different floors cannot be told apart once persisted — <c>ComparabilityFacts</c> is keyed on
    /// exactly that pair.
    /// </para>
    /// </remarks>
    public AgentEvalBuilder AddEval(IEval eval, ChanceFloor floor)
    {
        var admitted = FloorAdmittedEval.Admit(eval, floor);

        foreach (var existing in _evals)
        {
            if (string.Equals(existing.Key, admitted.Key, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"An eval with key '{admitted.Key}' is already registered. Two evals sharing a key produce "
                    + "results that cannot be told apart once persisted, and the floor recorded against that key "
                    + "would be whichever one ran last.",
                    nameof(eval));
            }
        }

        _evals.Add(admitted);
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

        // Initialize plugins in dependency order. If any InitializeAsync throws, shut down + dispose
        // the already-initialized plugins in reverse order before rethrowing, so a partial build does
        // not leak their resources (BUG-40).
        var orderedPlugins = OrderByDependencies(_plugins);
        var initialized = new List<IAgentEvalPlugin>();
        try
        {
            foreach (var plugin in orderedPlugins)
            {
                _logger.LogDebug($"Initializing plugin: {plugin.Name} v{plugin.Version}");
                await plugin.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
                initialized.Add(plugin);
                _logger.LogDebug($"Plugin initialized: {plugin.Name}");
            }
        }
        catch
        {
            for (int i = initialized.Count - 1; i >= 0; i--)
            {
                var p = initialized[i];
                try { await p.ShutdownAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, $"Error shutting down plugin {p.Name} after failed build"); }
                try { p.Dispose(); }
                catch (Exception ex) { _logger.LogError(ex, $"Error disposing plugin {p.Name} after failed build"); }
            }
            throw;
        }

        // Order transformers by priority
        _transformers.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        return new AgentEvalRunner(
            registry,
            orderedPlugins,
            _transformers,
            _evaluatorClient,
            _logger,
            _defaultThreshold,
            _evals.ToList());
    }

    /// <summary>
    /// Builds synchronously (blocks the calling thread until initialization completes).
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="BuildAsync"/>. This blocking overload offloads the async build onto a
    /// thread-pool thread via <see cref="Task.Run{TResult}(Func{Task{TResult}})"/> so plugin
    /// <c>InitializeAsync</c> continuations resume on the pool rather than a captured
    /// <see cref="SynchronizationContext"/> — avoiding the classic sync-over-async deadlock on a
    /// UI/legacy-ASP.NET thread (PERF-01).
    /// </remarks>
    public AgentEvalRunner Build()
    {
        return Task.Run(() => BuildAsync()).GetAwaiter().GetResult();
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
    private readonly IReadOnlyList<FloorAdmittedEval> _evals;

    internal AgentEvalRunner(
        MetricRegistry registry,
        IReadOnlyList<IAgentEvalPlugin> plugins,
        IReadOnlyList<IResultTransformer> transformers,
        IChatClient? evaluatorClient,
        IAgentEvalLogger logger,
        double defaultThreshold,
        IReadOnlyList<FloorAdmittedEval> evals)
    {
        _registry = registry;
        _plugins = plugins;
        _transformers = transformers;
        _evaluatorClient = evaluatorClient;
        _logger = logger;
        _defaultThreshold = defaultThreshold;
        _evals = evals;
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
    /// The evals admitted through <see cref="AgentEvalBuilder.AddEval"/>, each carrying the chance
    /// floor it was admitted under. Empty when none were registered — which is not the same fact as
    /// "the evals that ran had no floors".
    /// </summary>
    public IReadOnlyList<FloorAdmittedEval> Evals => _evals;

    /// <summary>
    /// Runs every admitted eval against one input.
    /// </summary>
    /// <param name="input">The stimulus — build one from an agent run with <c>TestCase.ToEvalInput(TestResult)</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// One result per admitted eval, in registration order, each carrying the floor its eval was
    /// admitted under. <b>Empty when no eval was registered</b> — an empty result set is a statement
    /// about the registry, never a pass.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <remarks>
    /// This is the far end of AE-04's join: agent run → <c>EvalInput</c> → <c>EvalResult</c> with its
    /// floor recorded in ADR-030 §3.2's convention, which <c>EvalResultPersistence.ToScenarioResult</c>
    /// then reads straight back into <c>ComparabilityFacts.ChanceFloor</c> without a schema change.
    /// </remarks>
    public async Task<IReadOnlyList<EvalResult>> EvaluateEvalsAsync(
        EvalInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var results = new List<EvalResult>(_evals.Count);
        foreach (var eval in _evals)
        {
            results.Add(await eval.EvaluateAsync(input, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

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
            await plugin.OnBeforeEvaluationAsync(context, cancellationToken).ConfigureAwait(false);
        }

        // Run metric
        var result = await metric.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);

        // Apply transformers
        foreach (var transformer in _transformers)
        {
            result = transformer.Transform(result, context);
        }

        // Run after hooks
        var results = new List<MetricResult> { result };
        foreach (var plugin in _plugins)
        {
            await plugin.OnAfterEvaluationAsync(context, results, cancellationToken).ConfigureAwait(false);
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
            await plugin.OnBeforeEvaluationAsync(context, cancellationToken).ConfigureAwait(false);
        }

        // Run all metrics
        foreach (var metricName in metricNames)
        {
            var metric = _registry.GetRequired(metricName);
            var result = await metric.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);

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
            await plugin.OnAfterEvaluationAsync(context, results, cancellationToken).ConfigureAwait(false);
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
                await plugin.ShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error shutting down plugin {plugin.Name}");
            }

            // IAgentEvalPlugin extends IDisposable, so a plugin may release native/IDisposable
            // resources only in Dispose(). ShutdownAsync alone leaked those for process life (BUG-40).
            try
            {
                plugin.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error disposing plugin {plugin.Name}");
            }
        }
    }
}
