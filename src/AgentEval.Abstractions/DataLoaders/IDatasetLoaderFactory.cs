// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.DataLoaders;

/// <summary>
/// Factory interface for creating <see cref="IDatasetLoader"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// Use this interface via DI for testable, extensible loader resolution.
/// The static <see cref="DatasetLoaderFactory"/> class delegates to a
/// <see cref="DefaultDatasetLoaderFactory"/> internally and remains available
/// for quick, non-DI usage.
/// </para>
/// <para>See ADR-006 and ADR-014 for architectural context.</para>
/// </remarks>
public interface IDatasetLoaderFactory
{
    /// <summary>
    /// Create a loader based on file extension (e.g., ".jsonl", ".csv").
    /// </summary>
    /// <param name="extension">The file extension including the leading dot.</param>
    /// <returns>An <see cref="IDatasetLoader"/> for the given extension.</returns>
    /// <exception cref="ArgumentException">No loader registered for the extension.</exception>
    IDatasetLoader CreateFromExtension(string extension);

    /// <summary>
    /// Create a loader for a specific format name (e.g., "jsonl", "csv").
    /// </summary>
    /// <param name="format">The format identifier (case-insensitive).</param>
    /// <returns>An <see cref="IDatasetLoader"/> for the given format.</returns>
    /// <exception cref="ArgumentException">Unknown format.</exception>
    IDatasetLoader Create(string format);

    /// <summary>
    /// Register a custom loader factory for an extension.
    /// </summary>
    /// <param name="extension">The file extension including the leading dot.</param>
    /// <param name="factory">A factory function that produces the loader.</param>
    void Register(string extension, Func<IDatasetLoader> factory);
}
