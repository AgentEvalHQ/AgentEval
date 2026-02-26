// SPDX-License-Identifier: MIT
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace AgentEval.Core;

/// <summary>
/// Default implementation of IMetricRegistry using a thread-safe dictionary.
/// </summary>
public sealed class MetricRegistry : IMetricRegistry
{
    private readonly ConcurrentDictionary<string, IMetric> _metrics = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new empty registry.
    /// </summary>
    public MetricRegistry() { }

    /// <summary>
    /// Creates a registry pre-populated with the specified metrics.
    /// </summary>
    public MetricRegistry(IEnumerable<IMetric> metrics)
    {
        foreach (var metric in metrics)
        {
            Register(metric);
        }
    }

    public void Register(string name, IMetric metric)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Metric name cannot be null or whitespace.", nameof(name));

        if (metric == null)
            throw new ArgumentNullException(nameof(metric));

        if (!_metrics.TryAdd(name, metric))
        {
            throw new InvalidOperationException($"A metric with name '{name}' is already registered.");
        }
    }

    public void Register(IMetric metric)
    {
        if (metric == null)
            throw new ArgumentNullException(nameof(metric));

        Register(metric.Name, metric);
    }

    public IMetric? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return _metrics.TryGetValue(name, out var metric) ? metric : null;
    }

    public IMetric GetRequired(string name)
    {
        var metric = Get(name);
        if (metric == null)
        {
            var available = string.Join(", ", _metrics.Keys.Take(10));
            throw new KeyNotFoundException(
                $"Metric '{name}' is not registered. Available metrics: {available}" +
                (_metrics.Count > 10 ? $" (and {_metrics.Count - 10} more)" : ""));
        }
        return metric;
    }

    public bool Contains(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && _metrics.ContainsKey(name);
    }

    public IEnumerable<string> GetRegisteredNames()
    {
        return _metrics.Keys.ToList();
    }

    public IEnumerable<IMetric> GetAll()
    {
        return _metrics.Values.ToList();
    }

    public bool Remove(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && _metrics.TryRemove(name, out _);
    }

    public void Clear()
    {
        _metrics.Clear();
    }

    /// <summary>
    /// Gets the number of registered metrics.
    /// </summary>
    public int Count => _metrics.Count;
}
