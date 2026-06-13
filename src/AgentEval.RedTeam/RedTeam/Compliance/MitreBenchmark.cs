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
/// in <c>AgentEval.RedTeam</c>. Each preset wires a curated subset of the 13 built-in
/// attack types and returns a <see cref="MitreBenchmarkRun"/>
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
/// <b>Coverage</b>: 15 ATLAS techniques are defined in <see cref="MITREATLASReporter"/>:
/// 8 are <c>IsApplicable=true</c> at the agent-API layer, 7 are <c>IsApplicable=false</c>
/// (RC-5/T4-2: adversarial-data crafting, ML-artifact collection, inference-API exfiltration,
/// AI-phishing, model replication, dataset poisoning, adversarial SEO). The built-in
/// attack types in this assembly self-tag <see cref="IAttackType.MitreAtlasIds"/> against
/// all 8 applicable techniques (AML.T0010, T0020, T0034, T0037, T0051, T0054, T0056, T0057 —
/// AML.T0045 was retired → AML.T0034 Cost Harvesting, verified vs ATLAS.yaml 2026-06-13). The
/// 7 non-applicable techniques surface as <c>NotApplicable</c> leaves — <c>skipped</c>-labelled
/// <see cref="EvalResult"/> leaves rather than passing-by-default.
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
    /// All built-in attacks at <see cref="Intensity.Quick"/> intensity, covering the 8 applicable MITRE ATLAS
    /// techniques recognised by <see cref="MITREATLASReporter"/> (AML.T0010, T0020, T0034, T0037, T0051, T0054,
    /// T0056, T0057 — names/IDs verified vs ATLAS.yaml 2026-06-13). The non-applicable techniques (AML.T0043 Craft
    /// Adversarial Data, AML.T0044 Full AI Model Access, AML.T0046 Spamming AI System with Chaff Data,
    /// AML.T0047 AI-Enabled Product or Service, AML.T0048 External Harms, AML.T0052 Phishing, AML.T0053 AI Agent
    /// Tool Invocation) are not testable at the agent-API layer and appear as <c>NotApplicable</c>
    /// skipped leaves in <see cref="MitreBenchmarkRun.EvaluateAsync"/> output.
    /// </summary>
    /// <param name="judge">Optional LLM judge. The current attack pipeline uses
    /// per-attack heuristic evaluators by default; <paramref name="judge"/> is
    /// accepted for API symmetry with other benchmark factories and is reserved
    /// for a future judge-graded category (e.g. <c>AML.T0048</c> Exfiltration via
    /// ML Inference API, where heuristic detection of subtle data-leakage patterns
    /// is brittle). When <c>null</c>, the heuristic evaluators are used.
    /// <para>
    /// <b>Pinning-test teeth gap (plan-13 T4.1b item 2)</b>: today no test asserts
    /// that the stored <c>judge</c> reference is actually called when set — the
    /// parameter flows through to <see cref="MitreBenchmarkRun.Judge"/> as a no-op
    /// getter. When the first judge-graded technique lands, add a contract test that
    /// fakes <see cref="IEvaluator"/> and asserts at least one <c>EvaluateAsync</c>
    /// call per AtlasBaseline run with the judge-graded technique enabled.
    /// </para></param>
    /// <param name="systemPromptCanary">Optional secret seeded into the agent's system prompt so
    /// extraction probes can detect a genuine leak by canary match rather than keyword heuristics
    /// (via <see cref="Attack.RosterWithCanary"/>). When <c>null</c>, the canary-aware attacks fall
    /// back to their heuristic form.</param>
    /// <returns>A <see cref="MitreBenchmarkRun"/> wired with the AtlasBaseline attack set.</returns>
    public static MitreBenchmarkRun AtlasBaseline(IEvaluator? judge = null, string? systemPromptCanary = null)
    {
        var attacks = Attack.RosterWithCanary(systemPromptCanary);
        var pipeline = AttackPipeline.Create()
            .WithAttacks([.. attacks])
            .WithIntensity(Intensity.Quick)
            .WithTimeout(QuickTimeout)
            .WithEvidence(true);
        return new MitreBenchmarkRun(
            pipeline,
            judge,
            presetName: "AtlasBaseline",
            attackAtlasIds: AttackAtlasIds(attacks));
    }

    /// <summary>
    /// Three-attack MVP preset (PromptInjection + Jailbreak + PIILeakage) at
    /// <see cref="Intensity.Quick"/>. Exercises 4 distinct ATLAS techniques —
    /// AML.T0051 (LLM Prompt Injection), AML.T0054 (LLM Jailbreak),
    /// AML.T0037 (Data from Local System — via PII probes),
    /// AML.T0057 (LLM Data Leakage — via PII probes). The other techniques appear
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
    /// Audit-grade preset — all 13 built-in attacks at <see cref="Intensity.Comprehensive"/>
    /// intensity with a 30-minute overall timeout and evidence-on. Same ATLAS coverage
    /// as <see cref="AtlasBaseline"/> but exercises more probes per attack type for
    /// higher-confidence verdicts. Intended for formal compliance evidence packs.
    /// </summary>
    /// <param name="judge">See <see cref="AtlasBaseline"/>.</param>
    /// <param name="systemPromptCanary">See <see cref="AtlasBaseline"/>.</param>
    /// <returns>A <see cref="MitreBenchmarkRun"/> wired with audit-grade settings.</returns>
    public static MitreBenchmarkRun AtlasAuditGrade(IEvaluator? judge = null, string? systemPromptCanary = null)
    {
        var attacks = Attack.RosterWithCanary(systemPromptCanary);
        var pipeline = AttackPipeline.Create()
            .WithAttacks([.. attacks])
            .WithIntensity(Intensity.Comprehensive)
            .WithTimeout(ThoroughTimeout)
            .WithEvidence(true);
        return new MitreBenchmarkRun(
            pipeline,
            judge,
            presetName: "AtlasAuditGrade",
            attackAtlasIds: AttackAtlasIds(attacks));
    }

    /// <summary>
    /// Projects an attack list into its (AtlasId, DefaultSeverity, AttackName) triples
    /// for downstream <see cref="EvalResult"/> shaping. One triple per (attack, atlasId)
    /// pair; an attack carrying two ATLAS IDs contributes two triples. Pulled out so
    /// all presets agree on the data they make available to <see cref="MitreBenchmarkRun"/>.
    /// Canary instrumentation lives on the shared <see cref="Attack.RosterWithCanary"/> helper.
    /// </summary>
    private static IReadOnlyList<(string AtlasId, Severity DefaultSeverity, string AttackName)>
        AttackAtlasIds(IReadOnlyList<IAttackType> attacks)
        => attacks
            .SelectMany(a => a.MitreAtlasIds.Select(id => (AtlasId: id, a.DefaultSeverity, AttackName: a.Name)))
            .ToList();
}
