// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.RedTeam;

namespace AgentEval.Benchmarks;

/// <summary>
/// Top-level factory for NIST AI RMF (AI 100-1) benchmark presets — parity with <see cref="OwaspBenchmark"/> /
/// <see cref="MitreBenchmark"/>. A thin façade over <see cref="AttackPipeline"/>; each preset wires the 13 built-in
/// attacks and returns a <see cref="NistBenchmarkRun"/> exposing <c>ScanAsync</c> / <c>EvaluateAsync</c> /
/// <c>BuildEvalResult</c> / <c>GenerateReport</c>.
/// </summary>
/// <remarks>
/// <b>Scope (honesty):</b> the composite scores only the MEASURE security/privacy/validity sub-actions a black-box
/// red-team can exercise (MEASURE 2.7 Security &amp; Resilience, 2.10 Privacy, 2.5 Validity/Reliability). GOVERN / MAP /
/// MANAGE controls (and MEASURE 2.6 Safety) are organizational and surface as Not-Applicable / skipped leaves — never
/// PASS. A passing run is one input into an AI RMF program, not RMF conformance.
/// </remarks>
public static partial class NistBenchmark
{
    private static readonly TimeSpan QuickTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ThoroughTimeout = TimeSpan.FromMinutes(30);

    /// <summary>All 13 built-in attacks at <see cref="Intensity.Quick"/> — the default NIST AI RMF preset.</summary>
    /// <param name="judge">Optional LLM judge (accepted for API symmetry; heuristic evaluators are used by default).</param>
    /// <param name="systemPromptCanary">Optional system-prompt canary; instruments SystemPromptExtraction (privacy/2.10).</param>
    public static NistBenchmarkRun RmfBaseline(IEvaluator? judge = null, string? systemPromptCanary = null)
    {
        var attacks = Attack.RosterWithCanary(systemPromptCanary);
        var pipeline = AttackPipeline.Create()
            .WithAttacks([.. attacks])
            .WithIntensity(Intensity.Quick)
            .WithTimeout(QuickTimeout)
            .WithEvidence(true);
        return new NistBenchmarkRun(pipeline, judge, "RmfBaseline", attacks);
    }

    /// <summary>Three-attack MVP preset (PromptInjection + Jailbreak + PIILeakage) at Quick intensity — CI-friendly.
    /// Exercises MEASURE 2.7 (security) and 2.10 (privacy); other controls surface as Not-Evaluated.</summary>
    /// <param name="judge">See <see cref="RmfBaseline"/>.</param>
    public static NistBenchmarkRun RmfSmoke(IEvaluator? judge = null)
    {
        IReadOnlyList<IAttackType> attacks = [Attack.PromptInjection, Attack.Jailbreak, Attack.PIILeakage];
        var pipeline = AttackPipeline.Create()
            .WithAttacks([.. attacks])
            .WithIntensity(Intensity.Quick)
            .WithTimeout(QuickTimeout)
            .WithEvidence(true);
        return new NistBenchmarkRun(pipeline, judge, "RmfSmoke", attacks);
    }

    /// <summary>All 13 built-in attacks at <see cref="Intensity.Comprehensive"/> with a 30-minute timeout — audit-grade
    /// evidence. Same control coverage as <see cref="RmfBaseline"/> with more probes per attack.</summary>
    /// <param name="judge">See <see cref="RmfBaseline"/>.</param>
    /// <param name="systemPromptCanary">See <see cref="RmfBaseline"/>.</param>
    public static NistBenchmarkRun RmfAuditGrade(IEvaluator? judge = null, string? systemPromptCanary = null)
    {
        var attacks = Attack.RosterWithCanary(systemPromptCanary);
        var pipeline = AttackPipeline.Create()
            .WithAttacks([.. attacks])
            .WithIntensity(Intensity.Comprehensive)
            .WithTimeout(ThoroughTimeout)
            .WithEvidence(true);
        return new NistBenchmarkRun(pipeline, judge, "RmfAuditGrade", attacks);
    }
}
