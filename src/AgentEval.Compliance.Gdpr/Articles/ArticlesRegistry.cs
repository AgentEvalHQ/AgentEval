// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Loading;
using AgentEval.Compliance.Gdpr.Articles.Models;

namespace AgentEval.Compliance.Gdpr.Articles;

/// <summary>
/// Loads all 21 GDPR article YAMLs at construction (from embedded resources),
/// builds their <see cref="CompositeEval"/> representations, and exposes lookup
/// by <c>control_id</c>. Intended to be registered as a DI singleton.
/// </summary>
/// <remarks>
/// The synchronous YAML load is acceptable here because: (a) DI constructors
/// must be synchronous, (b) loading happens once at startup, and (c) embedded
/// resources read from in-memory streams with negligible latency.
/// </remarks>
public sealed class ArticlesRegistry
{
    private readonly Dictionary<string, CompositeEval> _articles;
    private readonly Dictionary<string, ArticleSpec> _specs;

    /// <summary>
    /// Initialises the registry by loading all non-fixture article YAMLs embedded
    /// in the <see cref="ArticlesRegistry"/> assembly and building composites.
    /// </summary>
    /// <param name="loader">YAML loader used to deserialise article specs.</param>
    /// <param name="builder">Builder used to convert specs into composite evals.</param>
    public ArticlesRegistry(ArticleScenarioYamlLoader loader, ArticleCompositeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(builder);

        var assembly = typeof(ArticlesRegistry).Assembly;
        var specs = loader.LoadAllFromAssemblyAsync(assembly, includeTestFixtures: false)
            .GetAwaiter()
            .GetResult();

        _specs = specs.ToDictionary(s => s.Metadata.ControlId, s => s);
        _articles = specs.ToDictionary(s => s.Metadata.ControlId, s => builder.Build(s));
    }

    /// <summary>Returns the <see cref="CompositeEval"/> for the given GDPR control identifier.</summary>
    /// <param name="controlId">The control id, e.g. <c>gdpr.art17.erasure</c>.</param>
    /// <exception cref="KeyNotFoundException">Thrown when <paramref name="controlId"/> is not registered.</exception>
    public CompositeEval Get(string controlId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlId);
        if (!_articles.TryGetValue(controlId, out var composite))
            throw new KeyNotFoundException(
                $"Unknown control id '{controlId}'. Known: {string.Join(", ", _articles.Keys)}");
        return composite;
    }

    /// <summary>Returns the raw <see cref="ArticleSpec"/> for the given GDPR control identifier.</summary>
    /// <param name="controlId">The control id, e.g. <c>gdpr.art9.special_categories</c>.</param>
    /// <exception cref="KeyNotFoundException">Thrown when <paramref name="controlId"/> is not registered.</exception>
    public ArticleSpec GetSpec(string controlId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlId);
        if (!_specs.TryGetValue(controlId, out var spec))
            throw new KeyNotFoundException($"Unknown control id '{controlId}'.");
        return spec;
    }

    /// <summary>All registered composites, keyed by control id.</summary>
    public IReadOnlyDictionary<string, CompositeEval> All => _articles;
}
