// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Benchmarks;

/// <summary>
/// Top-level factory for MITRE ATLAS (Adversarial Threat Landscape for AI Systems)
/// benchmark presets.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MitreBenchmark"/> is a thin façade over <see cref="AttackPipeline"/>
/// in <c>AgentEval.RedTeam</c>. Each preset wires a curated subset of the nine
/// attack types implemented today and returns a <see cref="MitreBenchmarkRun"/>
/// that exposes:
/// </para>
/// <list type="bullet">
///   <item><see cref="MitreBenchmarkRun.ScanAsync"/> — execute the configured pipeline against an agent.</item>
///   <item><see cref="MitreBenchmarkRun.EvaluateAsync"/> — the adapter that lets ATLAS results flow
///         through the unified output-store + audit-chain pipeline (one leaf per ATLAS technique
///         covered, <c>MinAggregation</c>).</item>
///   <item><see cref="MitreBenchmarkRun.GenerateReport"/> — produce the rich
///         <see cref="MITREATLASReport"/> for compliance evidence packs.</item>
/// </list>
/// <para>
/// <b>Coverage</b>: 12 ATLAS techniques are defined in <see cref="MITREATLASReporter"/>:
/// 8 are <c>IsApplicable=true</c> at the agent-API layer, 4 are <c>IsApplicable=false</c>
/// (model replication, dataset poisoning, AI-phishing, adversarial SEO). The existing nine
/// attack types in this assembly self-tag <see cref="IAttackType.MitreAtlasIds"/> against
/// 6 of the 8 applicable techniques (AML.T0024, T0037, T0043, T0045, T0051, T0054). The
/// remaining two applicable techniques (T0047 ML Artifact Collection, T0048 Exfiltration via
/// ML Inference API) have no probe coverage yet and surface as <c>NotTested</c> leaves; the
/// 4 non-applicable techniques surface as <c>NotApplicable</c> leaves. Both groups produce
/// <c>skipped</c>-labelled <see cref="EvalResult"/> leaves rather than passing-by-default.
/// </para>
/// <para>
/// <b>Attack → ATLAS mapping</b> is owned by each <see cref="IAttackType"/> via its
/// <see cref="IAttackType.MitreAtlasIds"/> property — this façade does not duplicate the mapping.
/// </para>
/// </remarks>
public static partial class MitreBenchmark
{
    // ── Default per-preset configuration ─────────────────────────────────────
    // Kept private so the preset shape is part of the API contract: callers
    // pick a preset rather than fiddling with timeouts and intensities.

    private static readonly TimeSpan QuickTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ThoroughTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// All nine implemented attacks at <see cref="Intensity.Quick"/> intensity,
    /// covering 6 of the 12 MITRE ATLAS techniques recognised by
    /// <see cref="MITREATLASReporter"/> (AML.T0024, T0037, T0043, T0045, T0051, T0054).
    /// Two applicable techniques (AML.T0047 ML Artifact Collection, AML.T0048
    /// Exfiltration via ML Inference API) have no probe coverage yet and appear as
    /// <c>NotTested</c> skipped leaves. The four non-applicable techniques
    /// (AML.T0044 Full ML Model Replication, AML.T0046 Publish Poisoned Dataset,
    /// AML.T0052 Phishing via AI-Generated Content, AML.T0053 Adversarial SEO)
    /// are not testable at the agent-API layer and appear as <c>NotApplicable</c>
    /// skipped leaves in <see cref="MitreBenchmarkRun.EvaluateAsync"/> output.
    /// </summary>
    /// <param name="judge">Optional LLM judge. The current attack pipeline uses
    /// per-attack heuristic evaluators by default; <paramref name="judge"/> is
    /// accepted for API symmetry with other benchmark factories and reserved for
    /// future LLM-graded probes. When <c>null</c>, the heuristic evaluators are used.</param>
    /// <returns>A <see cref="MitreBenchmarkRun"/> wired with the AtlasBaseline attack set.</returns>
    public static MitreBenchmarkRun AtlasBaseline(IEvaluator? judge = null)
    {
        var pipeline = AttackPipeline.Create()
            .WithAttacks([.. Attack.All])
            .WithIntensity(Intensity.Quick)
            .WithTimeout(QuickTimeout)
            .WithEvidence(true);
        return new MitreBenchmarkRun(
            pipeline,
            judge,
            presetName: "AtlasBaseline",
            attackAtlasIds: AttackAtlasIds(Attack.All));
    }

    /// <summary>
    /// Three-attack MVP preset (PromptInjection + Jailbreak + PIILeakage) at
    /// <see cref="Intensity.Quick"/>. Exercises 4 distinct ATLAS techniques —
    /// AML.T0051 (LLM Prompt Injection), AML.T0054 (LLM Jailbreak),
    /// AML.T0024 (Develop Capabilities — via PII probes), AML.T0037 (Data from
    /// Information Repositories — via PII probes). The other 8 techniques appear
    /// as "skipped" leaves. Intended for CI fast feedback.
    /// </summary>
    /// <param name="judge">See <see cref="AtlasBaseline"/>.</param>
    /// <returns>A <see cref="MitreBenchmarkRun"/> wired with the AtlasSmoke attack set.</returns>
    public static MitreBenchmarkRun AtlasSmoke(IEvaluator? judge = null)
    {
        var attacks = (IReadOnlyList<IAttackType>)
            [Attack.PromptInjection, Attack.Jailbreak, Attack.PIILeakage];
        var pipeline = AttackPipeline.Create()
            .WithAttacks([.. attacks])
            .WithIntensity(Intensity.Quick)
            .WithTimeout(QuickTimeout)
            .WithEvidence(true);
        return new MitreBenchmarkRun(
            pipeline,
            judge,
            presetName: "AtlasSmoke",
            attackAtlasIds: AttackAtlasIds(attacks));
    }

    /// <summary>
    /// Audit-grade preset — all nine attacks at <see cref="Intensity.Comprehensive"/>
    /// intensity with a 30-minute overall timeout and evidence-on. Same ATLAS coverage
    /// as <see cref="AtlasBaseline"/> but exercises more probes per attack type for
    /// higher-confidence verdicts. Intended for formal compliance evidence packs.
    /// </summary>
    /// <param name="judge">See <see cref="AtlasBaseline"/>.</param>
    /// <returns>A <see cref="MitreBenchmarkRun"/> wired with audit-grade settings.</returns>
    public static MitreBenchmarkRun AtlasAuditGrade(IEvaluator? judge = null)
    {
        var pipeline = AttackPipeline.Create()
            .WithAttacks([.. Attack.All])
            .WithIntensity(Intensity.Comprehensive)
            .WithTimeout(ThoroughTimeout)
            .WithEvidence(true);
        return new MitreBenchmarkRun(
            pipeline,
            judge,
            presetName: "AtlasAuditGrade",
            attackAtlasIds: AttackAtlasIds(Attack.All));
    }

    /// <summary>
    /// Projects an attack list into its (AtlasId, DefaultSeverity, AttackName) triples
    /// for downstream <see cref="EvalResult"/> shaping. One triple per (attack, atlasId)
    /// pair; an attack carrying two ATLAS IDs contributes two triples. Pulled out so
    /// all presets agree on the data they make available to <see cref="MitreBenchmarkRun"/>.
    /// </summary>
    private static IReadOnlyList<(string AtlasId, Severity DefaultSeverity, string AttackName)>
        AttackAtlasIds(IReadOnlyList<IAttackType> attacks)
        => attacks
            .SelectMany(a => a.MitreAtlasIds.Select(id => (AtlasId: id, a.DefaultSeverity, AttackName: a.Name)))
            .ToList();
}
