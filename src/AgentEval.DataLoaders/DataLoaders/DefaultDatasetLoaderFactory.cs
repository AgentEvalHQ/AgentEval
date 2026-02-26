// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.DataLoaders;

/// <summary>
/// Default implementation of <see cref="IDatasetLoaderFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton in DI via <c>services.AddAgentEval()</c>.
/// The static <see cref="DatasetLoaderFactory"/> class delegates to a
/// shared instance of this class for backwards compatibility.
/// </para>
/// <para>
/// Thread-safe: the internal dictionary uses <see cref="StringComparer.OrdinalIgnoreCase"/>
/// and writes are only expected during application startup.
/// </para>
/// <para>
/// DI-registered <see cref="IDatasetLoader"/> services are automatically
/// wired into the factory via the constructor overload.
/// </para>
/// </remarks>
public sealed class DefaultDatasetLoaderFactory : IDatasetLoaderFactory
{
    private readonly Dictionary<string, Func<IDatasetLoader>> _loaders = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jsonl"] = () => new JsonlDatasetLoader(),
        [".ndjson"] = () => new JsonlDatasetLoader(),
        [".json"] = () => new JsonDatasetLoader(),
        [".csv"] = () => new CsvDatasetLoader(),
        [".tsv"] = () => new CsvDatasetLoader('\t'),
        [".yaml"] = () => new YamlDatasetLoader(),
        [".yml"] = () => new YamlDatasetLoader(),
    };

    /// <summary>
    /// Creates a new factory with only built-in loaders (backward compatible).
    /// </summary>
    public DefaultDatasetLoaderFactory() { }

    /// <summary>
    /// Creates a factory with built-in loaders plus DI-registered loaders.
    /// DI will prefer this constructor when <c>IEnumerable&lt;IDatasetLoader&gt;</c> is available.
    /// </summary>
    /// <param name="additionalLoaders">
    /// DI-registered loaders. Each loader's <see cref="IDatasetLoader.SupportedExtensions"/>
    /// are used as keys. Built-in defaults are not overridden; use <see cref="Register"/>
    /// to explicitly replace a built-in loader.
    /// </param>
    public DefaultDatasetLoaderFactory(IEnumerable<IDatasetLoader> additionalLoaders) : this()
    {
        ArgumentNullException.ThrowIfNull(additionalLoaders);

        foreach (var loader in additionalLoaders)
        {
            foreach (var ext in loader.SupportedExtensions)
            {
                // DI-registered loaders don't override built-in defaults
                _loaders.TryAdd(ext, () => loader);
            }
        }
    }

    /// <inheritdoc/>
    public IDatasetLoader CreateFromExtension(string extension)
    {
        if (_loaders.TryGetValue(extension, out var factory))
        {
            return factory();
        }

        throw new ArgumentException($"No loader available for extension: {extension}", nameof(extension));
    }

    /// <inheritdoc/>
    public IDatasetLoader Create(string format) => format.ToLowerInvariant() switch
    {
        "jsonl" or "ndjson" => new JsonlDatasetLoader(),
        "json" => new JsonDatasetLoader(),
        "csv" => new CsvDatasetLoader(),
        "tsv" => new CsvDatasetLoader('\t'),
        "yaml" or "yml" => new YamlDatasetLoader(),
        _ => throw new ArgumentException($"Unknown format: {format}", nameof(format))
    };

    /// <inheritdoc/>
    public void Register(string extension, Func<IDatasetLoader> factory)
    {
        _loaders[extension] = factory;
    }
}
