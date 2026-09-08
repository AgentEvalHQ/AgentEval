// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// In-memory registry of <see cref="EvaluatorCard"/>s loaded from embedded resources.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-031 C3 — relocated out of Mission Control.</b> It lived in
/// <c>AgentEval.MissionControl</c>, which is <c>IsPackable=false</c> and <b>net10-only</b>, and
/// which the CLI references only under
/// <c>Condition="'$(TargetFramework)' == 'net10.0'"</c>. So sixty cards carrying
/// <c>defaultThreshold</c>, <c>costTier</c> and <c>expectedInputs</c> were reachable from a GraphQL
/// resolver and from nothing else — not from the CLI on net8 or net9, where the type is not even in
/// the compilation, and not from any package, because that assembly has never shipped in one.
/// </para>
/// <para>
/// ⚠ <b>WHY IT COULD NOT SIMPLY BE MOVED, and what changed instead.</b> The old implementation
/// hard-coded its source: <c>typeof(AgentEval.Benchmarks.AgenticBenchmark).Assembly</c>.
/// <c>AgentEval.Evals.Agentic</c> references <c>AgentEval.Core</c>, so carrying that line into Core
/// would have made the project graph <b>circular</b>. The registry therefore takes the assemblies to
/// scan. That is not a workaround: a registry that names one assembly can only ever describe one
/// assembly's evaluators, which is the wrong shape for a type whose whole job is "what evaluators
/// exist".
/// </para>
/// <para>
/// ⚠ <b>It asserts its own input.</b> "No cards were found" and "nothing was scanned" are the same
/// empty registry to a caller, and the second is a wiring fault wearing the first's clothes. The
/// constructor refuses an empty source list, and <see cref="ScannedAssemblies"/> lets a caller tell
/// an honest zero from a silent one.
/// </para>
/// </remarks>
public sealed class EvaluatorCardRegistry
{
    /// <summary>The embedded-resource name fragment that marks a card. "drop a JSON file + rebuild".</summary>
    public const string ResourceInfix = "EvaluatorCards.";

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IReadOnlyDictionary<string, EvaluatorCard> _byKey;

    /// <summary>Loads every card embedded in <paramref name="sources"/>.</summary>
    /// <param name="sources">The assemblies to scan. At least one is required.</param>
    /// <exception cref="ArgumentException">
    /// No assemblies were supplied — see the "asserts its own input" note on the type.
    /// </exception>
    /// <exception cref="InvalidOperationException">Two cards declare the same key.</exception>
    public EvaluatorCardRegistry(params Assembly[] sources)
        : this((IEnumerable<Assembly>)(sources ?? throw new ArgumentNullException(nameof(sources))))
    {
    }

    /// <inheritdoc cref="EvaluatorCardRegistry(Assembly[])"/>
    public EvaluatorCardRegistry(IEnumerable<Assembly> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var scanned = sources.Where(a => a is not null).Distinct().ToArray();
        if (scanned.Length == 0)
        {
            throw new ArgumentException(
                "An EvaluatorCardRegistry with no source assemblies would report zero evaluators and look "
                + "exactly like one whose cards had all been deleted. Name the assemblies to scan.",
                nameof(sources));
        }

        ScannedAssemblies = scanned.Select(a => a.GetName().Name ?? a.FullName ?? "<unnamed>").ToArray();
        _byKey = LoadAll(scanned);
    }

    /// <summary>The assemblies this registry actually scanned, by simple name.</summary>
    /// <remarks>
    /// Exposed so a caller looking at <see cref="Count"/> = 0 can tell "these assemblies carry no
    /// cards" from "the registry was pointed at nothing".
    /// </remarks>
    public IReadOnlyList<string> ScannedAssemblies { get; }

    /// <summary>The number of cards loaded from embedded resources.</summary>
    public int Count => _byKey.Count;

    /// <summary>Enumerates all cards, optionally filtered by <paramref name="category"/> and / or <paramref name="costTier"/>.</summary>
    /// <param name="category">Restrict to one category, case-insensitively.</param>
    /// <param name="costTier">Restrict to one cost tier.</param>
    /// <returns>The matching cards, ordered by category then key.</returns>
    public IEnumerable<EvaluatorCard> List(string? category = null, EvaluatorCostTier? costTier = null)
    {
        IEnumerable<EvaluatorCard> q = _byKey.Values;
        if (!string.IsNullOrWhiteSpace(category))
            q = q.Where(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase));
        if (costTier.HasValue)
            q = q.Where(c => c.CostTier == costTier.Value);
        return q.OrderBy(c => c.Category, StringComparer.Ordinal)
                .ThenBy(c => c.Key, StringComparer.Ordinal);
    }

    /// <summary>Looks up a single card by its evaluator key, or null if not registered.</summary>
    /// <param name="key">The evaluator key.</param>
    /// <returns>The card, or <see langword="null"/>.</returns>
    public EvaluatorCard? Get(string key) =>
        _byKey.TryGetValue(key, out var card) ? card : null;

    private static Dictionary<string, EvaluatorCard> LoadAll(IReadOnlyList<Assembly> sources)
    {
        var byKey = new Dictionary<string, EvaluatorCard>(StringComparer.Ordinal);

        foreach (var asm in sources)
        {
            var cardResources = asm.GetManifestResourceNames()
                .Where(n => n.Contains(ResourceInfix, StringComparison.Ordinal)
                         && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var resourceName in cardResources)
            {
                using var stream = asm.GetManifestResourceStream(resourceName)!;
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var card = JsonSerializer.Deserialize<EvaluatorCard>(json, s_jsonOpts);
                if (card is null) continue;

                if (byKey.ContainsKey(card.Key))
                {
                    throw new InvalidOperationException(
                        $"Duplicate EvaluatorCard key '{card.Key}' loaded from {resourceName}. " +
                        "Each evaluator must have exactly one card.");
                }

                byKey[card.Key] = card;
            }
        }

        return byKey;
    }
}
