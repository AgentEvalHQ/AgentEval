// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Evals;

namespace AgentEval.MissionControl.Services;

/// <summary>
/// In-memory registry of <see cref="EvaluatorCard"/>s loaded from embedded resources at
/// startup. Plan-08 MC1.5.3 — drives the GraphQL <c>Query.evaluators(...)</c> +
/// <c>Query.evaluator(key:)</c> resolvers without a DB round-trip.
/// </summary>
/// <remarks>
/// Cards are sourced from <c>AgentEval.Evals.Agentic</c>'s <c>EvaluatorCards/*.json</c>
/// embedded resources. Adding a new card is "drop a JSON file + rebuild" — no code change
/// required for the registry to surface it. This is the schema-driven UI promise from
/// plan-07 §11 paid in full.
/// </remarks>
public sealed class EvaluatorCardRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IReadOnlyDictionary<string, EvaluatorCard> _byKey;

    public EvaluatorCardRegistry()
    {
        _byKey = LoadAll();
    }

    /// <summary>The number of cards loaded from embedded resources.</summary>
    public int Count => _byKey.Count;

    /// <summary>Enumerates all cards, optionally filtered by <paramref name="category"/> and / or <paramref name="costTier"/>.</summary>
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
    public EvaluatorCard? Get(string key) =>
        _byKey.TryGetValue(key, out var card) ? card : null;

    private static Dictionary<string, EvaluatorCard> LoadAll()
    {
        // Cards live in AgentEval.Evals.Agentic; locate that assembly via a known type.
        var asm = typeof(AgentEval.Benchmarks.AgenticBenchmark).Assembly;
        var cardResources = asm.GetManifestResourceNames()
            .Where(n => n.Contains("EvaluatorCards.") && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var byKey = new Dictionary<string, EvaluatorCard>(StringComparer.Ordinal);
        foreach (var resourceName in cardResources)
        {
            using var stream = asm.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var card = JsonSerializer.Deserialize<EvaluatorCard>(json, JsonOpts);
            if (card is null) continue;

            if (byKey.ContainsKey(card.Key))
            {
                throw new InvalidOperationException(
                    $"Duplicate EvaluatorCard key '{card.Key}' loaded from {resourceName}. " +
                    "Each evaluator must have exactly one card.");
            }

            byKey[card.Key] = card;
        }
        return byKey;
    }
}
