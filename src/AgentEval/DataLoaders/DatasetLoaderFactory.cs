// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.DataLoaders;

/// <summary>
/// Static convenience façade for creating dataset loaders.
/// </summary>
/// <remarks>
/// Delegates to a shared <see cref="DefaultDatasetLoaderFactory"/> instance internally.
/// For DI scenarios, inject <see cref="IDatasetLoaderFactory"/> instead.
/// </remarks>
public static class DatasetLoaderFactory
{
    private static readonly DefaultDatasetLoaderFactory s_default = new();

    /// <summary>
    /// Create a loader based on file extension.
    /// </summary>
    public static IDatasetLoader CreateFromExtension(string extension)
        => s_default.CreateFromExtension(extension);

    /// <summary>
    /// Create a loader for a specific format.
    /// </summary>
    public static IDatasetLoader Create(string format)
        => s_default.Create(format);

    /// <summary>
    /// Register a custom loader for an extension.
    /// </summary>
    public static void Register(string extension, Func<IDatasetLoader> factory)
        => s_default.Register(extension, factory);

    /// <summary>
    /// Load all test cases from a file, auto-detecting the loader from the file extension.
    /// </summary>
    /// <param name="path">Path to the dataset file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of dataset test cases.</returns>
    public static Task<IReadOnlyList<DatasetTestCase>> LoadAsync(string path, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(path);
        var loader = s_default.CreateFromExtension(extension);
        return loader.LoadAsync(path, ct);
    }

    /// <summary>
    /// Stream test cases from a file, auto-detecting the loader from the file extension.
    /// </summary>
    /// <param name="path">Path to the dataset file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of dataset test cases.</returns>
    public static IAsyncEnumerable<DatasetTestCase> LoadStreamingAsync(string path, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(path);
        var loader = s_default.CreateFromExtension(extension);
        return loader.LoadStreamingAsync(path, ct);
    }
}
