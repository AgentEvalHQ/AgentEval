// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.Gdpr.Articles;

namespace AgentEval.Compliance.Gdpr.Pillars;

/// <summary>
/// Builds the Pillar 6 — Governance and Accountability composite (Art 5(2), 28,
/// 30, 33, 34, 35, 37-39, 44-49).
/// </summary>
/// <remarks>
/// <para>
/// Pillar 6 groups the GDPR governance / accountability articles that historically
/// fell into the "out of scope in v1" disclaimer band of the GDPR benchmark. v1.1
/// (plan-13 T1.1) closes that gap with DIALOG-AWARENESS probes — the agent is tested
/// on its ability to DESCRIBE the obligation, not on whether the organisation
/// actually maintains a ROPA / has a DPO / notifies the DPA within 72h / executes
/// SCCs with sufficient supplementary measures. The upstream-process attestation
/// (e.g., proof that a Schrems II transfer impact assessment was actually done)
/// remains out of scope of any dialog benchmark.
/// </para>
/// <para>
/// Article coverage and weight rationale (sums to 1.00):
/// <list type="bullet">
///   <item><b>Art 28 processor contracts (0.15)</b> — every controller routinely
///         contracts with processors; the Art 28(3)(a)-(h) mandatory contract terms
///         + Art 28(2) sub-processor change-notification + Art 26 joint-controller
///         boundary are high-frequency, high-stakes dialog topics.</item>
///   <item><b>Art 30 records of processing (0.12)</b> — the Art 5(2) demonstrability
///         spine; high-volume question category but the substantive rules are
///         narrower than Art 28.</item>
///   <item><b>Art 33 breach notification 72h DPA clock (0.15)</b> — high-severity
///         operational obligation with a hard deadline and well-defined Art 4(12)
///         trigger; ranks alongside Art 28 + Art 35 + Art 44-49 as a pillar
///         flagship.</item>
///   <item><b>Art 34 breach communication to data subjects (0.10)</b> — narrower
///         scope (high-risk-only threshold) but consequential; the Art 34(3)
///         exemptions are a classic trap surface.</item>
///   <item><b>Art 35 DPIA (0.15)</b> — flagship governance probe: Art 35(3)(a)-(c)
///         mandatory triggers + Art 35(1) general high-risk test + WP29 WP248rev.01
///         nine-criteria framework + Art 36 prior-consultation tie-in. High weight
///         reflects breadth and severity.</item>
///   <item><b>Art 37-39 DPO (0.10)</b> — Art 37(1)(a)(b)(c) three mandatory
///         appointment triggers + Art 38(3) independence + Art 38(6) conflict of
///         interests + Art 39(1) tasks; well-defined surface, narrower than Art 35
///         or Art 44-49.</item>
///   <item><b>Art 44-49 international transfers / Schrems II (0.15)</b> — flagship:
///         Schrems II post-judgment regime is one of the most enforced GDPR
///         topics; SCC + TIA + supplementary measures + Art 49 narrow-derogation
///         + Art 48 third-country-order conflict pattern all sit here.</item>
///   <item><b>Art 5(2) accountability (0.08)</b> — meta-control that ties the rest
///         together. Lower weight because most demonstrability scenarios surface
///         through one of the seven articles above; Art 5(2) appears explicitly
///         where the question is fundamentally about evidentiability rather than
///         a specific substantive obligation.</item>
/// </list>
/// </para>
/// <para>
/// Pillar-level weight in <c>GdprBenchmark.Standard()</c> and <c>AuditGrade()</c>
/// is 0.15 per the plan-13 T1.1 rebalancing (governance is dialog-awareness only;
/// the operational pillars Art 3 subject rights + Art 1 foundations carry higher
/// weight as they reflect direct dialog-observable compliance dynamics).
/// </para>
/// </remarks>
public static class Pillar6Governance
{
    /// <summary>
    /// Builds the Pillar 6 <see cref="CompositeEval"/> from the provided <paramref name="articles"/> registry.
    /// </summary>
    /// <param name="articles">Registry containing all 29 GDPR article composites (21 baseline + 8 Pillar 6).</param>
    /// <returns>A configured pillar <see cref="CompositeEval"/>.</returns>
    public static CompositeEval Build(ArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return PillarCompositeBuilder.Build(
            pillarKey: "Pillar6-Governance",
            pillarName: "Governance and Accountability (Art 5(2), 28, 30, 33, 34, 35, 37-39, 44-49)",
            articles:
            [
                (articles.Get("gdpr.art28.processor_contracts"),       0.15),
                (articles.Get("gdpr.art30.records_of_processing"),     0.12),
                (articles.Get("gdpr.art33.breach_notification"),       0.15),
                (articles.Get("gdpr.art34.breach_communication"),      0.10),
                (articles.Get("gdpr.art35.dpia"),                      0.15),
                (articles.Get("gdpr.art37_39.dpo"),                    0.10),
                (articles.Get("gdpr.art44_49.international_transfers"),0.15),
                (articles.Get("gdpr.art5_2.accountability"),           0.08),
            ]);
    }
}
