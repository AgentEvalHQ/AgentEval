// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.EuAiAct.Articles.Building;
using AgentEval.Compliance.EuAiAct.Articles.Loading;
using AgentEval.Compliance.EuAiAct.Articles.Models;
using AgentEval.Compliance.EuAiAct.Articles.Validation;

namespace AgentEval.Compliance.EuAiAct.Articles;

/// <summary>
/// Loads all EU AI Act article YAMLs at construction (from embedded resources),
/// builds their <see cref="CompositeEval"/> representations, and exposes lookup
/// by <c>control_id</c>. Intended to be registered as a DI singleton.
/// </summary>
/// <remarks>
/// The synchronous YAML load is acceptable here because: (a) DI constructors
/// must be synchronous, (b) loading happens once at startup, and (c) embedded
/// resources read from in-memory streams with negligible latency.
/// </remarks>
public sealed class EuAiActArticlesRegistry
{
    private readonly Dictionary<string, CompositeEval> _articles;
    private readonly Dictionary<string, ArticleSpec> _specs;

    /// <summary>
    /// Initialises the registry by loading all non-fixture article YAMLs embedded
    /// in the EU AI Act sample assembly and building composites.
    /// </summary>
    public EuAiActArticlesRegistry(ArticleScenarioYamlLoader loader, ArticleCompositeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(builder);

        var assembly = typeof(EuAiActArticlesRegistry).Assembly;
        var specs = loader.LoadAllFromAssemblyAsync(assembly, includeTestFixtures: false)
            .GetAwaiter()
            .GetResult();

        // Validate every loaded spec before building composites — see GAP-01. The validator
        // was previously only exercised by tests, so malformed YAML mis-scored silently.
        ValidateOrThrow(specs);

        _specs = specs.ToDictionary(s => s.Metadata.ControlId, s => s);
        _articles = specs.ToDictionary(s => s.Metadata.ControlId, s => builder.Build(s));
    }

    /// <summary>
    /// Validates each loaded <see cref="ArticleSpec"/> against the EU AI Act schema rules and
    /// throws <see cref="InvalidOperationException"/> (naming the offending control id) on the
    /// first invalid spec, so malformed YAML fails fast at startup (GAP-01).
    /// </summary>
    internal static void ValidateOrThrow(IEnumerable<ArticleSpec> specs)
    {
        var validator = new ArticleYamlValidator();
        foreach (var spec in specs)
        {
            var result = validator.Validate(spec);
            if (!result.IsValid)
                throw new InvalidOperationException(
                    $"Invalid EU AI Act article spec '{spec.Metadata.ControlId}': {result.ErrorsJoined}");
        }
    }

    /// <summary>Returns the <see cref="CompositeEval"/> for the given EU AI Act control identifier.</summary>
    /// <param name="controlId">The control id, e.g. <c>eu_ai.art5.subliminal</c>.</param>
    public CompositeEval Get(string controlId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlId);
        if (!_articles.TryGetValue(controlId, out var composite))
            throw new KeyNotFoundException(
                $"Unknown control id '{controlId}'. Known: {string.Join(", ", _articles.Keys)}");
        return composite;
    }

    /// <summary>Returns the raw <see cref="ArticleSpec"/> for the given EU AI Act control identifier.</summary>
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
