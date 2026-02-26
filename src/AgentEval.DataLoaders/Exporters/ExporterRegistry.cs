// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using AgentEval.Core;

namespace AgentEval.Exporters;

/// <summary>
/// Default implementation of <see cref="IExporterRegistry"/> using a thread-safe dictionary.
/// Mirrors the design of <see cref="AgentEval.Core.MetricRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton in DI via <c>services.AddAgentEval()</c>.
/// Built-in exporters (JSON, JUnit, Markdown, CSV, TRX) are pre-registered.
/// Extension packages can register additional exporters via DI.
/// </para>
/// </remarks>
public sealed class ExporterRegistry : IExporterRegistry
{
    private readonly ConcurrentDictionary<string, IResultExporter> _exporters
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new empty registry.
    /// </summary>
    public ExporterRegistry() { }

    /// <summary>
    /// Creates a registry pre-populated with the specified exporters.
    /// </summary>
    /// <param name="exporters">Exporters to register, keyed by their <see cref="IResultExporter.FormatName"/>.</param>
    public ExporterRegistry(IEnumerable<IResultExporter> exporters)
    {
        foreach (var exporter in exporters)
        {
            Register(exporter);
        }
    }

    /// <inheritdoc/>
    public void Register(string formatName, IResultExporter exporter)
    {
        if (string.IsNullOrWhiteSpace(formatName))
            throw new ArgumentException("Format name cannot be null or whitespace.", nameof(formatName));

        ArgumentNullException.ThrowIfNull(exporter);

        if (!_exporters.TryAdd(formatName, exporter))
        {
            throw new InvalidOperationException(
                $"An exporter with format name '{formatName}' is already registered.");
        }
    }

    /// <inheritdoc/>
    public void Register(IResultExporter exporter)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        Register(exporter.FormatName, exporter);
    }

    /// <inheritdoc/>
    public IResultExporter? Get(string formatName)
    {
        if (string.IsNullOrWhiteSpace(formatName))
            return null;

        return _exporters.TryGetValue(formatName, out var exporter) ? exporter : null;
    }

    /// <inheritdoc/>
    public IResultExporter GetRequired(string formatName)
    {
        var exporter = Get(formatName);
        if (exporter == null)
        {
            var available = string.Join(", ", _exporters.Keys.Take(10));
            throw new KeyNotFoundException(
                $"Exporter '{formatName}' is not registered. Available formats: {available}" +
                (_exporters.Count > 10 ? $" (and {_exporters.Count - 10} more)" : ""));
        }
        return exporter;
    }

    /// <inheritdoc/>
    public IEnumerable<IResultExporter> GetAll()
    {
        return _exporters.Values.ToList();
    }

    /// <inheritdoc/>
    public IEnumerable<string> GetRegisteredFormats()
    {
        return _exporters.Keys.ToList();
    }

    /// <inheritdoc/>
    public bool Contains(string formatName)
    {
        return !string.IsNullOrWhiteSpace(formatName) && _exporters.ContainsKey(formatName);
    }

    /// <inheritdoc/>
    public bool Remove(string formatName)
    {
        return !string.IsNullOrWhiteSpace(formatName) && _exporters.TryRemove(formatName, out _);
    }

    /// <inheritdoc/>
    public void Clear()
    {
        _exporters.Clear();
    }

    /// <summary>
    /// Gets the number of registered exporters.
    /// </summary>
    public int Count => _exporters.Count;
}
