// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;

namespace AgentEval.GdprBenchmark.Composition;

/// <summary>
/// Extension methods for composing and augmenting <see cref="CompositeEval"/> benchmark trees.
/// </summary>
public static class CompositeExtensions
{
    /// <summary>
    /// Returns a new <see cref="CompositeEval"/> derived from <paramref name="benchmark"/>
    /// with extra scenarios appended to selected article composites. Weights are
    /// renormalised so each affected article's component weights still sum to 1.0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Weight semantics</b>: addition weights are interpreted as <i>relative</i> to
    /// the existing scenarios after concatenation. If the original article has
    /// scenarios with weights summing to 1.0 and you append two extras at weight 1.0
    /// each, the renormaliser divides every weight (originals included) by the new
    /// total — so the two extras dominate (1/3 each) and originals shrink
    /// proportionally. To preserve the original importance ratio, set the addition
    /// weight to roughly the average of the originals (e.g., for an article with 4
    /// originals at 0.25 each, set additions to 0.25 too).
    /// </para>
    /// <para>
    /// <b>Targeting</b>: the walk short-circuits on the first matching node — additions
    /// are applied at the most-shallow occurrence of a given control ID in the tree.
    /// In practice domain-pack additions target articles, and articles only appear
    /// once per benchmark, so this is the expected behaviour.
    /// </para>
    /// </remarks>
    /// <param name="benchmark">The base benchmark composite to augment.</param>
    /// <param name="additions">
    /// A dictionary mapping article control IDs (e.g. <c>gdpr.art9.special_categories</c>)
    /// to the extra <see cref="EvalComponent"/> instances to append.
    /// </param>
    /// <returns>
    /// A new <see cref="CompositeEval"/> with the additions baked in.
    /// The original <paramref name="benchmark"/> is not modified.
    /// </returns>
    public static CompositeEval WithExtraScenarios(
        this CompositeEval benchmark,
        IReadOnlyDictionary<string, IReadOnlyList<EvalComponent>> additions)
    {
        ArgumentNullException.ThrowIfNull(benchmark);
        ArgumentNullException.ThrowIfNull(additions);
        if (additions.Count == 0) return benchmark;

        return TransformTree(benchmark, additions);
    }

    private static CompositeEval TransformTree(
        CompositeEval node,
        IReadOnlyDictionary<string, IReadOnlyList<EvalComponent>> additions)
    {
        // If THIS node is one of the keyed articles, append + renormalise.
        if (additions.TryGetValue(node.Key, out var extras))
        {
            var combined = node.Components.Concat(extras).ToList();
            // Renormalise so the weights still sum to 1.0.
            var totalWeight = combined.Sum(c => c.Weight);
            var normalised = totalWeight > 0
                ? combined.Select(c => new EvalComponent(c.Eval, c.Weight / totalWeight, c.Required)).ToList()
                : combined;

            return new CompositeEval(
                key: node.Key,
                name: node.Name,
                category: node.Category,
                version: node.Version,
                components: normalised,
                aggregation: node.Aggregation,
                threshold: node.Threshold);
        }

        // Otherwise, recurse into child composites (children of this node that ARE composites).
        var newComponents = node.Components
            .Select(c => c.Eval is CompositeEval child
                ? new EvalComponent(TransformTree(child, additions), c.Weight, c.Required)
                : c)
            .ToList();

        return new CompositeEval(
            key: node.Key,
            name: node.Name,
            category: node.Category,
            version: node.Version,
            components: newComponents,
            aggregation: node.Aggregation,
            threshold: node.Threshold);
    }
}
