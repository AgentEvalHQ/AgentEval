// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Core.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Safety.Policy;

namespace AgentEval.Evals.Agentic;

/// <summary>
/// Module-initializer hook that registers <see cref="AgenticBenchmark"/> with
/// <see cref="BenchmarkFamilyRegistry"/> on assembly load (Phase 8 / ADR-017 Convention 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a module initializer</b>: this ensures the family auto-registers the first
/// time any code in <c>AgentEval.Evals.Agentic</c> is touched at runtime — no explicit
/// registration call needed from the CLI or Mission Control. The CLI's <c>bench --list</c>
/// command "warms" each expected assembly by touching one of its types, which guarantees
/// every family's module initializer has run before <see cref="BenchmarkFamilyRegistry.All"/>
/// is enumerated.
/// </para>
/// <para>
/// <b>Shape A (CompositeEval-native)</b> registration: all twelve presets return a
/// <see cref="CompositeEval"/>. Eight of the twelve (Safety, Conversational, RAG, Reasoning,
/// User-Experience, AgenticExecution, ToolCallAccuracy, AdversarialDirect) require an
/// <see cref="IEvaluator"/> judge; the other four (JudgeQuality, Telemetry, StochasticStability,
/// GlassBoxDiagnostics) are pure-code — GlassBoxDiagnostics accepts an optional judge for its
/// System Prompt Injection leaf. The registry's <c>CompositeFactory</c> delegate rejects calls that
/// omit a judge for the judge-requiring presets with a clear error.
/// </para>
/// </remarks>
internal static class AgenticBenchmarkRegistration
{
    // CA2255: ModuleInitializer is the canonical mechanism for auto-registering benchmark
    // families on assembly load (Phase 8 / ADR-017 Convention 3). Library use here is
    // intentional — every consumer of AgentEval.Evals.Agentic.dll needs the family present
    // in BenchmarkFamilyRegistry before any CLI / Mission Control surface inspects it.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void Register()
    {
        BenchmarkFamilyRegistry.Register(new BenchmarkFamily(
            name: "agentic",
            description: "Behavioural evaluation across the agent quality dimensions (12 presets)",
            defaultCostTier: CostTier.Medium,
            presets:
            [
                new("agentic-execution", "6-evaluator system-outcome + agentic-process composite (canonical default)", CostTier.Medium),
                new("tool-call-accuracy", "Focused tool-call diagnostic (5 sub-dimensions)", CostTier.Medium),
                new("rag-quality", "RAG dimensions: groundedness, relevance, coherence, fluency, similarity, completeness, F1", CostTier.Medium),
                new("judge-quality", "Meta-evaluator: inter-rater agreement, calibration, drift", CostTier.Free),
                new("safety", "12-evaluator safety/security composite (requires policy resolver — programmatic only)", CostTier.High),
                new("telemetry", "6 pure-code operational evaluators (latency, tokens, cost, errors, retries)", CostTier.Free),
                new("glass-box-diagnostics", "8 Glass Box trace evaluators, 7 pure-code (tool reliability/errors, safety interventions, truncation, prompt drift/injection, arg leaks, token skew) — requires --trace", CostTier.Free),
                new("stochastic-stability", "Run-to-run score consistency (requires N prior run results)", CostTier.Free),
                new("conversational", "5-evaluator multi-turn quality composite", CostTier.Medium),
                new("reasoning", "4-evaluator reasoning correctness composite", CostTier.Medium),
                new("user-experience", "5-evaluator UX composite (tone, verbosity, refusal, confidence)", CostTier.Medium),
                new("adversarial-direct", "3-evaluator adversarial-resistance composite (high-severity threshold)", CostTier.Medium),
            ],
            compositeFactory: (preset, judge) => BuildPreset(preset, judge),
            evaluateAsync: null,  // Each preset returns a CompositeEval; consumers call CompositeEval.EvaluateAsync directly.
            docLinkUrl: "https://github.com/joslat/AgentEval/blob/main/docs/agentic-benchmark.md",
            owningAssemblyName: typeof(AgenticBenchmark).Assembly.GetName().Name));
    }

    private static CompositeEval BuildPreset(string preset, IEvaluator? judge)
    {
        return preset switch
        {
            "agentic-execution"     => AgenticBenchmark.AgenticExecution(RequireJudge(judge, preset)),
            "tool-call-accuracy"    => AgenticBenchmark.ToolCallAccuracy(RequireJudge(judge, preset)),
            "rag-quality"           => AgenticBenchmark.RagQuality(RequireJudge(judge, preset)),
            "judge-quality"         => AgenticBenchmark.JudgeQuality(),
            "telemetry"             => AgenticBenchmark.Telemetry(),
            "glass-box-diagnostics" => AgenticBenchmark.GlassBoxDiagnostics(judge),
            "stochastic-stability"  => AgenticBenchmark.StochasticStability(),
            "conversational"        => AgenticBenchmark.Conversational(RequireJudge(judge, preset)),
            "reasoning"             => AgenticBenchmark.Reasoning(RequireJudge(judge, preset)),
            "user-experience"       => AgenticBenchmark.UserExperience(RequireJudge(judge, preset)),
            "adversarial-direct"    => AgenticBenchmark.AdversarialDirect(RequireJudge(judge, preset)),
            "safety" => throw new ArgumentException(
                "The 'safety' preset requires a policy resolver and subject ID that the registry " +
                "cannot construct on its own. Call AgenticBenchmark.Safety(judge, policyResolver, subjectId, ...) directly."),
            _ => throw new ArgumentException(
                $"Unknown agentic preset '{preset}'. Known presets: agentic-execution, tool-call-accuracy, " +
                $"rag-quality, judge-quality, safety, telemetry, glass-box-diagnostics, stochastic-stability, " +
                $"conversational, reasoning, user-experience, adversarial-direct.")
        };
    }

    private static IEvaluator RequireJudge(IEvaluator? judge, string preset)
    {
        if (judge is null)
        {
            throw new ArgumentException(
                $"Agentic preset '{preset}' requires a non-null IEvaluator judge. " +
                "Resolve a judge via JudgeFactory or construct an IEvaluator directly before " +
                "calling BenchmarkFamilyRegistry's CompositeFactory.");
        }
        return judge;
    }
}
