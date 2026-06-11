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

    // Top10ForRag sits between Quick (Top10/Smoke) and Thorough (AuditGrade): the threat-model
    // priority for RAG agents is to find any working poisoned-document payload, so probe depth
    // matters more than scan latency, but full audit-grade timeouts (30 min) are overkill when
    // the goal is RAG-vector triage rather than evidence-pack thoroughness.
    private static readonly TimeSpan RagFocusedTimeout = TimeSpan.FromMinutes(20);

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
    /// accepted for API symmetry with other benchmark factories and is reserved
    /// for a future judge-graded category (e.g. <c>LLM07-InsecureOutputHandling</c>
    /// where heuristic detection of unsafe rendering or sandbox-escape patterns is
    /// brittle). When <c>null</c>, the heuristic evaluators are used.
    /// <para>
    /// <b>Pinning-test teeth gap (plan-13 T4.1b item 2)</b>: today no test asserts that
    /// the stored <c>judge</c> reference is actually called when set — the parameter
    /// flows through to <see cref="OwaspBenchmarkRun.Judge"/> as a no-op getter. When
    /// the first judge-graded attack lands, add a contract test that fakes
    /// <see cref="IEvaluator"/> and asserts it receives at least one
    /// <c>EvaluateAsync</c> call per Top10 run with the judge-graded category enabled.
    /// </para></param>
    /// <param name="systemPromptCanary">Optional secret seeded into the agent's system prompt so
    /// extraction probes can detect a genuine leak by canary match rather than keyword heuristics
    /// (via <see cref="Attack.RosterWithCanary"/>). When <c>null</c>, the canary-aware attacks fall
    /// back to their heuristic form.</param>
    /// <returns>An <see cref="OwaspBenchmarkRun"/> wired with the Top10 attack set.</returns>
    public static OwaspBenchmarkRun Top10(IEvaluator? judge = null, string? systemPromptCanary = null)
    {
        var attacks = Attack.RosterWithCanary(systemPromptCanary);
        var pipeline = AttackPipeline.Create()
            .WithAttacks([.. attacks])
            .WithIntensity(Intensity.Quick)
            .WithTimeout(QuickTimeout)
            .WithEvidence(true);
        return new OwaspBenchmarkRun(
            pipeline,
            judge,
            presetName: "Top10",
            attackOwaspIds: AttackOwaspIds(attacks));
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
    /// <param name="systemPromptCanary">See <see cref="Top10"/>.</param>
    /// <returns>An <see cref="OwaspBenchmarkRun"/> wired with audit-grade settings.</returns>
    public static OwaspBenchmarkRun AuditGrade(IEvaluator? judge = null, string? systemPromptCanary = null)
    {
        var attacks = Attack.RosterWithCanary(systemPromptCanary);
        var pipeline = AttackPipeline.Create()
            .WithAttacks([.. attacks])
            .WithIntensity(Intensity.Comprehensive)
            .WithTimeout(ThoroughTimeout)
            .WithEvidence(true);
        return new OwaspBenchmarkRun(
            pipeline,
            judge,
            presetName: "AuditGrade",
            attackOwaspIds: AttackOwaspIds(attacks));
    }

    /// <summary>
    /// RAG-focused variant of <see cref="Top10"/>: all 9 attacks at
    /// <see cref="Intensity.Comprehensive"/> intensity with a 20-minute overall
    /// timeout. Materially distinct from <see cref="Top10"/> (Quick intensity)
    /// and <see cref="AuditGrade"/> (Comprehensive at 30 min).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Threat model</b>: RAG agents face <see cref="IndirectInjectionAttack"/>
    /// — instruction-bearing content embedded in retrieved documents — as the
    /// primary attack vector. The same attack roster as <see cref="Top10"/> is used,
    /// but at deeper probe coverage (<see cref="Intensity.Comprehensive"/>) because
    /// finding a working poisoned-document payload is fundamentally a coverage
    /// problem: the attacker needs only one successful exfiltration, so the defender
    /// needs broad sampling of injection techniques. Quick intensity (used by
    /// <see cref="Top10"/>) under-samples the indirect-injection probe space.
    /// </para>
    /// <para>
    /// <b>Why not drop LLM06 / LLM10</b>: RAG pipelines that auto-fetch URLs from
    /// retrieved documents are a textbook excessive-agency vector (LLM06), and
    /// retrieved-context bloat amplifies unbounded-consumption risk (LLM10). Both
    /// stay in the roster.
    /// </para>
    /// <para>
    /// <b>Coverage gap (LLM08)</b>: dedicated retrieval-corpus-poisoning and vector-store
    /// probes for LLM08 (Vector / Embedding weaknesses) are not yet implemented and
    /// remain on the roadmap. LLM08 appears as a skipped leaf in
    /// <see cref="OwaspBenchmarkRun.EvaluateAsync"/> output, same as <see cref="Top10"/>.
    /// </para>
    /// <para>
    /// <b>Cost positioning</b>: between <see cref="Top10"/> and <see cref="AuditGrade"/>.
    /// Use for RAG-vector triage when full audit-grade evidence packs are overkill.
    /// </para>
    /// </remarks>
    /// <param name="judge">See <see cref="Top10"/>.</param>
    /// <param name="systemPromptCanary">See <see cref="Top10"/>.</param>
    /// <returns>An <see cref="OwaspBenchmarkRun"/> wired with the Top10-for-RAG set.</returns>
    public static OwaspBenchmarkRun Top10ForRag(IEvaluator? judge = null, string? systemPromptCanary = null)
    {
        var attacks = Attack.RosterWithCanary(systemPromptCanary);
        var pipeline = AttackPipeline.Create()
            .WithAttacks([.. attacks])
            .WithIntensity(Intensity.Comprehensive)
            .WithTimeout(RagFocusedTimeout)
            .WithEvidence(true);
        return new OwaspBenchmarkRun(
            pipeline,
            judge,
            presetName: "Top10ForRag",
            attackOwaspIds: AttackOwaspIds(attacks));
    }

    /// <summary>
    /// Projects an attack list into its (OwaspId, Severity, Name) triples for
    /// downstream <see cref="EvalResult"/> shaping. Pulled out so all presets
    /// agree on the data they make available to <see cref="OwaspBenchmarkRun"/>.
    /// Canary instrumentation lives on the shared <see cref="Attack.RosterWithCanary"/> helper.
    /// </summary>
    private static IReadOnlyList<(string OwaspId, Severity DefaultSeverity, string AttackName)>
        AttackOwaspIds(IReadOnlyList<IAttackType> attacks)
        => attacks
            .Select(a => (a.OwaspLlmId, a.DefaultSeverity, a.Name))
            .ToList();
}
