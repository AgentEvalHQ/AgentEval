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
/// Top-level factory for OWASP LLM Top 10 (v2.0, 2025) benchmark presets.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OwaspBenchmark"/> is a thin façade over <see cref="AttackPipeline"/>
/// in <c>AgentEval.RedTeam</c>. Each preset wires a curated subset of the nine attack
/// types implemented today and returns an <see cref="OwaspBenchmarkRun"/> that exposes:
/// </para>
/// <list type="bullet">
///   <item><see cref="OwaspBenchmarkRun.ScanAsync"/> — execute the configured pipeline against an agent.</item>
///   <item><see cref="OwaspBenchmarkRun.EvaluateAsync"/> — the adapter that lets OWASP results flow
///         through the unified output-store + audit-chain pipeline (10-leaf <see cref="EvalResult"/>
///         composite, one leaf per OWASP category, <c>MinAggregation</c>).</item>
///   <item><see cref="OwaspBenchmarkRun.GenerateReport"/> — produce the rich
///         <see cref="OWASPComplianceReport"/> for compliance evidence packs.</item>
/// </list>
/// <para>
/// <b>Coverage</b>: 6 of 10 OWASP LLM Top 10 v2.0 categories are exercised today via 9 attack types.
/// LLM03 (Supply Chain), LLM04 (Data and Model Poisoning), LLM08 (Vector / Embedding weaknesses), and
/// LLM09 (Misinformation) are not testable at the agent-API layer; the <see cref="EvalResult"/>
/// produced by <see cref="OwaspBenchmarkRun.EvaluateAsync"/> includes those four categories as
/// honest "skipped" leaves rather than passing them by default.
/// </para>
/// <para>
/// <b>Attack → OWASP mapping</b>:
/// <list type="table">
///   <listheader><term>OWASP category</term><description>Wired attacks</description></listheader>
///   <item><term>LLM01 Prompt Injection</term><description><see cref="PromptInjectionAttack"/>, <see cref="JailbreakAttack"/>, <see cref="IndirectInjectionAttack"/>, <see cref="EncodingEvasionAttack"/></description></item>
///   <item><term>LLM02 Sensitive Info Disclosure</term><description><see cref="PIILeakageAttack"/></description></item>
///   <item><term>LLM05 Improper Output Handling</term><description><see cref="InsecureOutputAttack"/></description></item>
///   <item><term>LLM06 Excessive Agency</term><description><see cref="ExcessiveAgencyAttack"/></description></item>
///   <item><term>LLM07 System Prompt Leakage</term><description><see cref="SystemPromptExtractionAttack"/></description></item>
///   <item><term>LLM10 Unbounded Consumption</term><description><see cref="InferenceAPIAbuseAttack"/></description></item>
/// </list>
/// </para>
/// </remarks>
public static partial class OwaspBenchmark
{
    // ── Default per-preset configuration ─────────────────────────────────────
    // Kept private so the preset shape is part of the API contract: callers
    // pick a preset rather than fiddling with timeouts and intensities.

    private static readonly TimeSpan QuickTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ThoroughTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// All nine implemented attacks at <see cref="Intensity.Quick"/> intensity,
    /// covering 6 of the 10 OWASP LLM Top 10 categories (LLM01, LLM02, LLM05,
    /// LLM06, LLM07, LLM10). The remaining four categories (LLM03 Supply Chain,
    /// LLM04 Data/Model Poisoning, LLM08 Vector weaknesses, LLM09 Misinformation)
    /// are not testable at the agent-API layer and appear as "skipped" leaves in
    /// <see cref="OwaspBenchmarkRun.EvaluateAsync"/> output.
    /// </summary>
    /// <param name="judge">Optional LLM judge. The current attack pipeline uses
    /// per-attack heuristic evaluators by default; <paramref name="judge"/> is
    /// accepted for API symmetry with other benchmark factories and reserved for
    /// future LLM-graded probes. When <c>null</c>, the heuristic evaluators are used.</param>
    /// <returns>An <see cref="OwaspBenchmarkRun"/> wired with the Top10 attack set.</returns>
    public static OwaspBenchmarkRun Top10(IEvaluator? judge = null)
    {
        var pipeline = AttackPipeline.Create()
            .WithAttacks([.. Attack.All])
            .WithIntensity(Intensity.Quick)
            .WithTimeout(QuickTimeout)
            .WithEvidence(true);
        return new OwaspBenchmarkRun(
            pipeline,
            judge,
            presetName: "Top10",
            attackOwaspIds: AttackOwaspIds(Attack.All));
    }

    /// <summary>
    /// Three-attack MVP preset (PromptInjection + Jailbreak + PIILeakage) at
    /// <see cref="Intensity.Quick"/>. Exercises LLM01 and LLM02 only; the other
    /// eight categories appear as "skipped" leaves. Intended for CI fast feedback.
    /// </summary>
    /// <param name="judge">See <see cref="Top10"/>.</param>
    /// <returns>An <see cref="OwaspBenchmarkRun"/> wired with the Smoke attack set.</returns>
    public static OwaspBenchmarkRun Smoke(IEvaluator? judge = null)
    {
        var attacks = (IReadOnlyList<IAttackType>)
            [Attack.PromptInjection, Attack.Jailbreak, Attack.PIILeakage];
        var pipeline = AttackPipeline.Create()
            .WithAttacks([.. attacks])
            .WithIntensity(Intensity.Quick)
            .WithTimeout(QuickTimeout)
            .WithEvidence(true);
        return new OwaspBenchmarkRun(
            pipeline,
            judge,
            presetName: "Smoke",
            attackOwaspIds: AttackOwaspIds(attacks));
    }

    /// <summary>
    /// Audit-grade preset — all nine attacks at <see cref="Intensity.Comprehensive"/>
    /// (Thorough) intensity with a 30-minute overall timeout and evidence-on.
    /// Same OWASP coverage as <see cref="Top10"/> but exercises more probes per
    /// attack type for higher-confidence verdicts.
    /// </summary>
    /// <param name="judge">See <see cref="Top10"/>.</param>
    /// <returns>An <see cref="OwaspBenchmarkRun"/> wired with audit-grade settings.</returns>
    public static OwaspBenchmarkRun AuditGrade(IEvaluator? judge = null)
    {
        var pipeline = AttackPipeline.Create()
            .WithAttacks([.. Attack.All])
            .WithIntensity(Intensity.Comprehensive)
            .WithTimeout(ThoroughTimeout)
            .WithEvidence(true);
        return new OwaspBenchmarkRun(
            pipeline,
            judge,
            presetName: "AuditGrade",
            attackOwaspIds: AttackOwaspIds(Attack.All));
    }

    /// <summary>
    /// RAG-focused variant of <see cref="Top10"/>: extends the standard set by
    /// adding extra weight to RAG-specific attack vectors (<see cref="IndirectInjectionAttack"/>
    /// — external-data manipulation — covers LLM01 + the not-otherwise-tested LLM08).
    /// Same 9-attack set as Top10 since IndirectInjectionAttack is already in
    /// <see cref="Attack.All"/>; the preset name signals intent to consumers
    /// rather than altering the attack roster. Future versions may add
    /// retrieval-corpus-poisoning probes that explicitly target LLM08.
    /// </summary>
    /// <param name="judge">See <see cref="Top10"/>.</param>
    /// <returns>An <see cref="OwaspBenchmarkRun"/> wired with the Top10-for-RAG set.</returns>
    public static OwaspBenchmarkRun Top10ForRag(IEvaluator? judge = null)
    {
        var pipeline = AttackPipeline.Create()
            .WithAttacks([.. Attack.All])
            .WithIntensity(Intensity.Quick)
            .WithTimeout(QuickTimeout)
            .WithEvidence(true);
        return new OwaspBenchmarkRun(
            pipeline,
            judge,
            presetName: "Top10ForRag",
            attackOwaspIds: AttackOwaspIds(Attack.All));
    }

    /// <summary>
    /// Projects an attack list into its (OwaspId, Severity, Name) triples for
    /// downstream <see cref="EvalResult"/> shaping. Pulled out so all presets
    /// agree on the data they make available to <see cref="OwaspBenchmarkRun"/>.
    /// </summary>
    private static IReadOnlyList<(string OwaspId, Severity DefaultSeverity, string AttackName)>
        AttackOwaspIds(IReadOnlyList<IAttackType> attacks)
        => attacks
            .Select(a => (a.OwaspLlmId, a.DefaultSeverity, a.Name))
            .ToList();
}
