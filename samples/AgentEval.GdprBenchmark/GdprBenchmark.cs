// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.GdprBenchmark.Articles;
using AgentEval.GdprBenchmark.Articles.Building;
using AgentEval.GdprBenchmark.Composition;
using AgentEval.GdprBenchmark.DomainPacks.ChildrensService;
using AgentEval.GdprBenchmark.DomainPacks.Healthcare;
using AgentEval.GdprBenchmark.DomainPacks.HR;
using AgentEval.GdprBenchmark.Pillars;

namespace AgentEval.GdprBenchmark;

/// <summary>
/// Options for multi-judge evaluation of Critical articles in the Audit-Grade preset.
/// </summary>
/// <param name="Judges">
/// Ordered list of judges (evaluator + weight) used for consensus aggregation
/// over Critical-severity articles. Typically 3 judges with equal weights.
/// </param>
public sealed record MultiJudgeOptions(
    IReadOnlyList<(IEvaluator Judge, double Weight)> Judges);

/// <summary>
/// Top-level factory methods for GDPR benchmark presets.
/// </summary>
public static class GdprBenchmark
{
    /// <summary>
    /// Builds the Standard preset: all five pillars with their canonical weights,
    /// pass threshold 0.85.
    /// </summary>
    /// <param name="articles">Registry containing all 21 GDPR article composites.</param>
    /// <returns>A fully configured <see cref="CompositeEval"/> for the Standard preset.</returns>
    public static CompositeEval Standard(ArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return new CompositeEval(
            key: "gdpr.compliance.standard",
            name: "GDPR Compliance — Standard Preset",
            category: "compliance.gdpr",
            version: "1.0.0",
            components:
            [
                new(Pillar1Foundations.Build(articles),     0.25),
                new(Pillar2LawfulBasis.Build(articles),     0.15),
                new(Pillar3SubjectRights.Build(articles),   0.30),
                new(Pillar4Transparency.Build(articles),    0.15),
                new(Pillar5PrivacyDesign.Build(articles),   0.15),
            ],
            aggregation: WeightedSumAggregation.Instance,
            threshold: 0.85);
    }

    /// <summary>
    /// Builds the Smoke preset: five representative articles across the full regulation,
    /// suitable for fast CI checks, pass threshold 0.80.
    /// </summary>
    /// <param name="articles">Registry containing all 21 GDPR article composites.</param>
    /// <returns>A fully configured <see cref="CompositeEval"/> for the Smoke preset.</returns>
    public static CompositeEval Smoke(ArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return new CompositeEval(
            key: "gdpr.compliance.smoke",
            name: "GDPR Compliance — Smoke (CI-friendly)",
            category: "compliance.gdpr",
            version: "1.0.0",
            components:
            [
                new(articles.Get("gdpr.art5.lawfulness"),         0.20),
                new(articles.Get("gdpr.art6.lawful_basis"),       0.20),
                new(articles.Get("gdpr.art9.special_categories"), 0.20),
                new(articles.Get("gdpr.art17.erasure"),           0.20),
                new(articles.Get("gdpr.art22.automated"),         0.20),
            ],
            aggregation: WeightedSumAggregation.Instance,
            threshold: 0.80);
    }

    /// <summary>
    /// Builds the Audit-Grade preset (single judge).
    /// Same five-pillar structure as <see cref="Standard"/>, but uses
    /// <see cref="CapByWorstAggregation"/> at the top level so that a single
    /// critical-article failure caps the overall composite score at fail (score &lt;= 0.40).
    /// Pass threshold is 0.90.
    /// </summary>
    /// <param name="articles">Registry containing all 21 GDPR article composites.</param>
    /// <returns>A fully configured <see cref="CompositeEval"/> for the Audit-Grade preset.</returns>
    public static CompositeEval AuditGrade(ArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return AuditGradeInternal(articles, name: "GDPR Compliance — Audit-Grade Preset (single judge)");
    }

    /// <summary>
    /// Builds the Audit-Grade preset with optional multi-judge consensus for Critical articles.
    /// When <paramref name="multiJudge"/> is provided with more than one judge, the
    /// <paramref name="articles"/> registry must have been built with a
    /// <see cref="Articles.Building.ScenarioToAtomicEval"/> that was constructed with the same
    /// judge list — the multi-judge wiring happens inside
    /// <see cref="Articles.Building.ScenarioToAtomicEval.Build"/> at the scenario level.
    /// For single-judge mode, pass <c>null</c> (or omit) to get the Phase-5 default behavior.
    /// </summary>
    /// <param name="articles">
    /// Registry built with the appropriate <see cref="Articles.Building.ScenarioToAtomicEval"/>
    /// (single-judge or multi-judge depending on <paramref name="multiJudge"/>).
    /// </param>
    /// <param name="multiJudge">
    /// When non-null and containing more than one judge, the returned preset name reflects
    /// multi-judge mode. The actual multi-judge wiring is in the registry; this parameter
    /// is used only to set the composite name for traceability.
    /// </param>
    /// <returns>A fully configured <see cref="CompositeEval"/> for the Audit-Grade preset.</returns>
    public static CompositeEval AuditGrade(ArticlesRegistry articles, MultiJudgeOptions? multiJudge)
    {
        ArgumentNullException.ThrowIfNull(articles);

        var useMultiJudge = multiJudge is { Judges.Count: > 1 };
        var name = useMultiJudge
            ? "GDPR Compliance — Audit-Grade (multi-judge)"
            : "GDPR Compliance — Audit-Grade Preset (single judge)";

        return AuditGradeInternal(articles, name);
    }

    private static CompositeEval AuditGradeInternal(ArticlesRegistry articles, string name) =>
        new CompositeEval(
            key: "gdpr.compliance.auditgrade",
            name: name,
            category: "compliance.gdpr",
            version: "1.0.0",
            components:
            [
                new(Pillar1Foundations.Build(articles),     0.25),
                new(Pillar2LawfulBasis.Build(articles),     0.15),
                new(Pillar3SubjectRights.Build(articles),   0.30),
                new(Pillar4Transparency.Build(articles),    0.15),
                new(Pillar5PrivacyDesign.Build(articles),   0.15),
            ],
            aggregation: CapByWorstAggregation.Instance,
            threshold: 0.90);

    /// <summary>
    /// Builds the Standard preset augmented with healthcare-domain scenario extensions.
    /// Scenarios for targeted articles (e.g. Art 9, Art 32) are appended and weights
    /// are renormalised. All other articles are unchanged.
    /// </summary>
    /// <param name="articles">Registry containing all 21 GDPR article composites.</param>
    /// <param name="scenarioBuilder">Builder used to construct eval components from scenario specs.</param>
    /// <returns>A <see cref="CompositeEval"/> combining the Standard preset with healthcare additions.</returns>
    public static CompositeEval Healthcare(ArticlesRegistry articles, ScenarioToAtomicEval scenarioBuilder)
    {
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(scenarioBuilder);
        var standard = Standard(articles);
        var additions = HealthcareScenarios.Load(scenarioBuilder);
        return standard.WithExtraScenarios(additions);
    }

    /// <summary>
    /// Builds the Standard preset augmented with HR-domain scenario extensions.
    /// Scenarios for targeted articles (e.g. Art 22, Art 6) are appended and weights
    /// are renormalised. All other articles are unchanged.
    /// </summary>
    /// <param name="articles">Registry containing all 21 GDPR article composites.</param>
    /// <param name="scenarioBuilder">Builder used to construct eval components from scenario specs.</param>
    /// <returns>A <see cref="CompositeEval"/> combining the Standard preset with HR additions.</returns>
    public static CompositeEval HR(ArticlesRegistry articles, ScenarioToAtomicEval scenarioBuilder)
    {
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(scenarioBuilder);
        var standard = Standard(articles);
        var additions = HRScenarios.Load(scenarioBuilder);
        return standard.WithExtraScenarios(additions);
    }

    /// <summary>
    /// Builds the Standard preset augmented with children's-service-domain scenario extensions.
    /// Scenarios for targeted articles (e.g. Art 8, Art 13) are appended and weights
    /// are renormalised. All other articles are unchanged.
    /// </summary>
    /// <param name="articles">Registry containing all 21 GDPR article composites.</param>
    /// <param name="scenarioBuilder">Builder used to construct eval components from scenario specs.</param>
    /// <returns>A <see cref="CompositeEval"/> combining the Standard preset with children's-service additions.</returns>
    public static CompositeEval ChildrensService(ArticlesRegistry articles, ScenarioToAtomicEval scenarioBuilder)
    {
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(scenarioBuilder);
        var standard = Standard(articles);
        var additions = ChildrensServiceScenarios.Load(scenarioBuilder);
        return standard.WithExtraScenarios(additions);
    }
}
