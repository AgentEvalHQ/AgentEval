// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.DataLoaders;

/// <summary>
/// Default implementation of <see cref="IDatasetLoaderFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton in DI via <c>services.AddAgentEvalDataLoaders()</c> (or <c>AddAgentEvalAll()</c>).
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

    // Format name → DI-registered loader, for Create(format) (see the constructor).
    private readonly Dictionary<string, Func<IDatasetLoader>> _formats = new(StringComparer.OrdinalIgnoreCase);

    // Extensions set through Register, which Create(format) honours for the same name (see Create).
    private readonly HashSet<string> _registered = new(StringComparer.OrdinalIgnoreCase);

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
    /// are used as keys for <see cref="CreateFromExtension"/>, and its <see cref="IDatasetLoader.Format"/>
    /// for <see cref="Create"/>. Built-in defaults are not overridden; use <see cref="Register"/>
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

            // ...and are reachable by their own Format name too. Create() checks the built-in names first, so a
            // DI loader that reuses one ("csv") cannot replace the built-in; the first loader with a name wins.
            if (!string.IsNullOrWhiteSpace(loader.Format))
                _formats.TryAdd(loader.Format, () => loader);
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
    /// <remarks>
    /// An extension set through <see cref="Register"/> answers for its name too: after <c>Register(".csv", f)</c>,
    /// <c>Create("csv")</c> returns <c>f()</c>, as <c>CreateFromExtension(".csv")</c> does. Otherwise a built-in name
    /// resolves to the built-in loader, then a DI-registered loader is found by its <see cref="IDatasetLoader.Format"/>.
    /// </remarks>
    public IDatasetLoader Create(string format)
    {
        ArgumentNullException.ThrowIfNull(format);

        // Register is the documented way to replace a built-in loader, but it reached only CreateFromExtension:
        // Create("csv") kept returning the built-in (#203 review, B7).
        if (_registered.Contains("." + format) && _loaders.TryGetValue("." + format, out var registered))
            return registered();

        return CreateByName(format);
    }

    private IDatasetLoader CreateByName(string format) => format.ToLowerInvariant() switch
    {
        "jsonl" or "ndjson" => new JsonlDatasetLoader(),
        "json" => new JsonDatasetLoader(),
        "csv" => new CsvDatasetLoader(),
        "tsv" => new CsvDatasetLoader('\t'),
        "yaml" or "yml" => new YamlDatasetLoader(),
        _ when _formats.TryGetValue(format, out var factory) => factory(),
        _ => throw new ArgumentException($"Unknown format: {format}", nameof(format))
    };

    /// <inheritdoc/>
    public void Register(string extension, Func<IDatasetLoader> factory)
    {
        _loaders[extension] = factory;
        _registered.Add(extension);
    }
}
