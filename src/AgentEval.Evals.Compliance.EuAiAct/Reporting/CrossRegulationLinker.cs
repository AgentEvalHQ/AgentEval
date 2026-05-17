// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.EuAiActBenchmark.Reporting;

/// <summary>
/// Surfaces overlapping compliance findings between GDPR and EU AI Act evidence wrappers.
/// </summary>
/// <remarks>
/// Certain GDPR articles and EU AI Act articles cover the same conceptual obligation
/// (e.g. human oversight, sensitive-attribute processing, transparency). When a deployer
/// runs both benchmarks, a failure on both sides of the same conceptual pair indicates a
/// cross-regulation gap that requires joint remediation.
/// </remarks>
public sealed class CrossRegulationLinker
{
    /// <summary>
    /// One overlap finding linking a GDPR article result to an EU AI Act article result.
    /// </summary>
    public sealed record CrossRegulationFinding(
        string GdprControlId,
        string EuAiActControlId,
        string OverlapNature,
        string GdprStatus,
        string EuAiActStatus);

    // Internal mapping table: (GDPR control id, EU AI Act control id, human-readable nature).
    private static readonly (string Gdpr, string EuAiAct, string Nature)[] s_overlap = new[]
    {
        ("gdpr.art22.automated",          "eu_ai.art14.human_oversight",          "Human oversight for legally significant automated decisions"),
        ("gdpr.art9.special_categories",  "eu_ai.art5.biometric_cat+realtime_id", "Sensitive-attribute processing — biometric categorization"),
        ("gdpr.art13.information_collection", "eu_ai.art50.ai_disclosure",        "Transparency obligation to users / data subjects"),
        ("gdpr.art14.information_indirect",   "eu_ai.art50.ai_disclosure",        "Transparency obligation to users / data subjects"),
    };

    /// <summary>
    /// Walks both evidence wrappers and returns overlap findings where BOTH sides report a
    /// failure or warning.
    /// </summary>
    /// <param name="gdpr">GDPR compliance evidence wrapper.</param>
    /// <param name="euAiAct">EU AI Act compliance evidence wrapper.</param>
    /// <returns>
    /// Findings list ordered by joint severity:
    /// FAIL+FAIL first, then FAIL+WARN, then WARN+FAIL, then WARN+WARN.
    /// </returns>
    public IReadOnlyList<CrossRegulationFinding> Link(
        AgentEval.GdprBenchmark.Reporting.GdprComplianceEvidence gdpr,
        AgentEval.EuAiActBenchmark.Reporting.EuAiActComplianceEvidence euAiAct)
    {
        ArgumentNullException.ThrowIfNull(gdpr);
        ArgumentNullException.ThrowIfNull(euAiAct);

        var findings = new List<CrossRegulationFinding>();

        foreach (var (gdprId, euAiId, nature) in s_overlap)
        {
            // Skip silently if either control was not run in this evidence snapshot.
            if (!gdpr.Summary.PerArticle.TryGetValue(gdprId, out var gdprArticle))
                continue;
            if (!euAiAct.Summary.PerArticle.TryGetValue(euAiId, out var euAiArticle))
                continue;

            var gdprStatus   = gdprArticle.Status.ToUpperInvariant();
            var euAiStatus   = euAiArticle.Status.ToUpperInvariant();

            bool gdprNonPass   = gdprStatus   is "FAIL" or "WARN";
            bool euAiNonPass   = euAiStatus   is "FAIL" or "WARN";

            // Only surface the finding when BOTH sides are non-passing.
            if (!gdprNonPass || !euAiNonPass)
                continue;

            findings.Add(new CrossRegulationFinding(
                GdprControlId:   gdprId,
                EuAiActControlId: euAiId,
                OverlapNature:   nature,
                GdprStatus:      gdprStatus,
                EuAiActStatus:   euAiStatus));
        }

        // Order: FAIL+FAIL > FAIL+WARN > WARN+FAIL > WARN+WARN
        return findings
            .OrderBy(f => SeverityRank(f.GdprStatus) + SeverityRank(f.EuAiActStatus))
            .ToList();
    }

    // Lower rank = higher severity (FAIL=0, WARN=1)
    private static int SeverityRank(string status) => status == "FAIL" ? 0 : 1;
}
