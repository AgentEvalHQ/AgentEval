// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>
/// Registry for result exporters, analogous to <see cref="IMetricRegistry"/>.
/// Enables dynamic registration and lookup of exporters by format name.
/// </summary>
/// <remarks>
/// <para>
/// Extension packages can register custom exporters via DI:
/// <code>services.AddSingleton&lt;IResultExporter, PowerBIExporter&gt;();</code>
/// These are automatically discovered and added to the registry during
/// <c>AddAgentEval()</c> initialization.
/// </para>
/// <para>See ADR-006 for DI architecture context.</para>
/// </remarks>
public interface IExporterRegistry
{
    /// <summary>
    /// Register an exporter with an explicit format name.
    /// </summary>
    /// <param name="formatName">The format name (case-insensitive key).</param>
    /// <param name="exporter">The exporter instance.</param>
    /// <exception cref="ArgumentException"><paramref name="formatName"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="exporter"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A format with this name is already registered.</exception>
    void Register(string formatName, IResultExporter exporter);

    /// <summary>
    /// Register an exporter using its <see cref="IResultExporter.FormatName"/> property.
    /// </summary>
    /// <param name="exporter">The exporter instance.</param>
    void Register(IResultExporter exporter);

    /// <summary>
    /// Get an exporter by format name. Returns null if not found.
    /// </summary>
    /// <param name="formatName">The format name (case-insensitive).</param>
    /// <returns>The exporter, or null if not registered.</returns>
    IResultExporter? Get(string formatName);

    /// <summary>
    /// Get an exporter by format name, throwing if not found.
    /// </summary>
    /// <param name="formatName">The format name (case-insensitive).</param>
    /// <returns>The exporter.</returns>
    /// <exception cref="KeyNotFoundException">No exporter registered for this format.</exception>
    IResultExporter GetRequired(string formatName);

    /// <summary>
    /// Get all registered exporters.
    /// </summary>
    IEnumerable<IResultExporter> GetAll();

    /// <summary>
    /// Get all registered format names.
    /// </summary>
    IEnumerable<string> GetRegisteredFormats();

    /// <summary>
    /// Check if a format is registered.
    /// </summary>
    /// <param name="formatName">The format name (case-insensitive).</param>
    bool Contains(string formatName);

    /// <summary>
    /// Remove an exporter by format name.
    /// </summary>
    /// <param name="formatName">The format name.</param>
    /// <returns>True if the exporter was removed; false if not found.</returns>
    bool Remove(string formatName);

    /// <summary>
    /// Clear all registered exporters.
    /// </summary>
    void Clear();
}
