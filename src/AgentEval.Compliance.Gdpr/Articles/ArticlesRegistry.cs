// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Loading;
using AgentEval.Compliance.Gdpr.Articles.Models;
using AgentEval.Compliance.Gdpr.Articles.Validation;

namespace AgentEval.Compliance.Gdpr.Articles;

/// <summary>
/// Loads all GDPR article YAMLs at construction (from embedded resources) —
/// currently 29 articles (21 baseline + 8 Pillar 6 Governance from plan-13 T1.1)
/// — builds their <see cref="CompositeEval"/> representations, and exposes lookup
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

        // Validate every loaded spec against the schema rules before building composites.
        // The validator was previously exercised only by tests, so a hand-edited YAML with
        // (e.g.) scenario weights not summing to 1.0, a bad pillar, or a missing criterion
        // flowed into the composite and mis-scored with no error (GAP-01).
        ValidateOrThrow(specs);

        _specs = specs.ToDictionary(s => s.Metadata.ControlId, s => s);
        _articles = specs.ToDictionary(s => s.Metadata.ControlId, s => builder.Build(s));
    }

    /// <summary>
    /// Validates each loaded <see cref="ArticleSpec"/> against the GDPR schema rules and throws
    /// <see cref="InvalidOperationException"/> (naming the offending control id) on the first
    /// invalid spec. Runs at registry construction so malformed YAML fails fast at startup
    /// rather than silently mis-scoring at audit time.
    /// </summary>
    internal static void ValidateOrThrow(IEnumerable<ArticleSpec> specs)
    {
        var specList = specs as IReadOnlyList<ArticleSpec> ?? specs.ToList();

        var validator = new ArticleYamlValidator();
        foreach (var spec in specList)
        {
            var result = validator.Validate(spec);
            if (!result.IsValid)
                throw new InvalidOperationException(
                    $"Invalid GDPR article spec '{spec.Metadata.ControlId}': {result.ErrorsJoined}");
        }

        // Cross-file duplicate control_id check. The per-article validator only dedups scenario
        // ids WITHIN an article; two embedded YAMLs sharing a control_id would otherwise throw a
        // non-diagnostic ArgumentException inside ToDictionary (no control_id / file named) (BUG-33).
        var duplicates = specList
            .GroupBy(s => s.Metadata.ControlId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                $"Duplicate GDPR article control_id(s) across embedded YAMLs: {string.Join(", ", duplicates)}. " +
                "Each control_id must be unique — check for copy-pasted article files.");
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
