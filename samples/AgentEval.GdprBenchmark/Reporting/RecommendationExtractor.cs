// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;

namespace AgentEval.GdprBenchmark.Reporting;

/// <summary>
/// Walks the composite result tree, collects failed article control IDs, and maps
/// each to a static recommendation string. Dynamic LLM-generated recommendations
/// are deferred to a future phase.
/// </summary>
public sealed class RecommendationExtractor
{
    private static readonly IReadOnlyDictionary<string, string> s_recommendations =
        new Dictionary<string, string>
        {
            ["gdpr.art5.lawfulness"]         = "Ensure the agent always declares the lawful basis (Art 6) before processing.",
            ["gdpr.art5.purpose"]            = "Verify the agent stays within the originally-stated purpose; refuse purpose drift.",
            ["gdpr.art5.minimization"]       = "Audit which fields the agent requests; remove anything not strictly necessary.",
            ["gdpr.art5.accuracy"]           = "Have the agent acknowledge uncertainty and offer correction paths to subjects.",
            ["gdpr.art5.retention"]          = "Document a retention timeframe; the agent should cite it on request.",
            ["gdpr.art5.integrity"]          = "Tighten output channels — the agent should not leak processed data outside the request scope.",
            ["gdpr.art8.children"]           = "Add an age-verification or parental-consent step before processing children's data.",
            ["gdpr.art9.special_categories"] = "Refuse special-category processing without an explicit Art 9(2) ground; document the ground when one applies.",
            ["gdpr.art6.lawful_basis"]       = "Train the agent to articulate which Art 6 basis applies for each interaction.",
            ["gdpr.art7.consent"]            = "Ensure consent flows are specific, informed, freely given, and unambiguous (not bundled or implicit).",
            ["gdpr.art13.information_collection"] = "Surface controller identity, purposes, basis, retention, and rights at the point of collection.",
            ["gdpr.art14.information_indirect"]   = "When data wasn't collected directly, disclose the source and notify the subject within the Art 14 window.",
            ["gdpr.art15.access"]            = "Implement an export path so the agent can fulfil access requests under Art 15.",
            ["gdpr.art16.rectification"]     = "Add a rectification flow that updates downstream caches and storage.",
            ["gdpr.art17.erasure"]           = "Acknowledge deletion requests realistically — propagate to backups, document exceptions (legal-obligation retention).",
            ["gdpr.art18.restriction"]       = "Implement a 'pause processing' state that the agent can apply on subject request.",
            ["gdpr.art20.portability"]       = "Provide structured, machine-readable exports (JSON / CSV) for portability requests.",
            ["gdpr.art21.object"]            = "Honour objection requests; verify the agent stops processing for the objecting subject.",
            ["gdpr.art22.automated"]         = "Disclose automated decision-making explicitly; offer human review for legally-significant decisions.",
            ["gdpr.art25.privacy_by_design"] = "Default the agent's settings to privacy-protective values (opt-in, minimal disclosure).",
            ["gdpr.art32.security"]          = "Audit the agent's prompt-handling for secrets exfiltration risk; recommend secure-flow patterns.",
        };

    /// <summary>
    /// Builds a list of recommendation strings for every failed article in the result tree.
    /// </summary>
    /// <param name="root">The top-level composite <see cref="EvalResult"/>.</param>
    /// <returns>
    /// Recommendations in control-ID alphabetical order. Each item is the static
    /// recommendation for that control, or a generic fallback when no mapping is registered.
    /// </returns>
    public IReadOnlyList<string> Build(EvalResult root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var failed = new HashSet<string>();
        WalkArticles(root, failed);
        return failed
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => s_recommendations.TryGetValue(k, out var rec) ? rec : $"Review failures in {k}.")
            .ToList();
    }

    private static void WalkArticles(EvalResult node, HashSet<string> sink)
    {
        if (node.Metric.Key.StartsWith("gdpr.art", StringComparison.Ordinal) && !node.Score.Passed)
            sink.Add(node.Metric.Key);

        var subs = node.Details.SubResults;
        if (subs is null) return;
        foreach (var s in subs) WalkArticles(s, sink);
    }
}
