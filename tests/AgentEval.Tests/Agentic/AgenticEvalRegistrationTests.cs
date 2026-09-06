// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// ADR-031 C1 — the 40-entry calibration dispatch table, moved out of
// BenchAgenticCalibrateCommand.RunCoreAsync and into AgenticEvalRegistration.
//
// The dictionary this replaced had NO test of its own: a dropped entry, a typo'd
// key or an eval swapped for its neighbour would have shown up only as
// "unknown evaluator key — skipping" on stderr during a paid calibration run,
// with the run still exiting 0. These tests pin the key set literally and
// construct every registered factory.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic;
using Xunit;

namespace AgentEval.Tests.Agentic;

public class AgenticEvalRegistrationTests
{
    private sealed class StubEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
            => Task.FromResult(new EvaluationResult { OverallScore = 100, Summary = "stub" });
    }

    /// <summary>
    /// The 40 keys, written out. This is the anti-regression: it fails on an added key, a removed
    /// key and a renamed key, and it says which. Path A' (v1.1) scope — system 5, process 6, ux 3,
    /// adversarial 5, reasoning 2, calibration 2, memory 0, quality 6, safety 11.
    /// </summary>
    private static readonly string[] ExpectedKeys =
    [
        // system (5)
        "task_completion", "task_adherence", "intent_identification", "intent_resolution",
        "task_navigation_efficiency",
        // process (6)
        "tool_selection", "tool_input_accuracy", "tool_output_utilization", "tool_call_success",
        "tool_efficiency", "tool_call_accuracy",
        // ux (3)
        "verbosity_appropriateness", "tone_appropriateness", "refusal_quality",
        // adversarial (5)
        "direct_injection", "persona_attack", "jailbreak_resistance", "prompt_leak",
        "escalation_resistance",
        // reasoning (2)
        "reasoning_correctness", "goal_decomposition_quality",
        // calibration (2)
        "confidence_calibration", "uncertainty_acknowledgment",
        // quality (6)
        "groundedness", "relevance", "coherence", "fluency", "similarity", "response_completeness",
        // safety (11)
        "system_prompt_leakage", "indirect_attack", "unsafe_tool_use", "hate_unfairness",
        "self_harm", "sexual", "violence", "code_vulnerability", "ungrounded_attributes",
        "sensitive_data_leakage", "protected_material",
    ];

    private static EvalRegistry Populated()
    {
        var registry = new EvalRegistry();
        AgenticEvalRegistration.RegisterInto(registry);
        return registry;
    }

    [Fact]
    public void RegisterInto_RegistersExactlyTheFortyDispatchedKeys()
    {
        var registry = Populated();

        Assert.Equal(AgenticEvalRegistration.DispatchedEvaluatorCount, registry.All.Count);
        Assert.Equal(
            ExpectedKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase),
            registry.All.Select(e => e.Key));
    }

    [Fact] // The constant and the literal list must agree, or one of them is decoration.
    public void DispatchedEvaluatorCount_MatchesTheExpectedKeyList()
        => Assert.Equal(AgenticEvalRegistration.DispatchedEvaluatorCount, ExpectedKeys.Length);

    [Fact]
    public void EveryRegisteredKey_ResolvesToAnInstanceOfItsDeclaredEvalType()
    {
        var registry = Populated();
        var judge = new StubEvaluator();

        // Vacuity guard: an empty registry would make the loop below pass while proving nothing.
        Assert.Equal(AgenticEvalRegistration.DispatchedEvaluatorCount, registry.All.Count);

        var failures = new List<string>();
        foreach (var entry in registry.All)
        {
            IEval? built;
            try
            {
                built = registry.Resolve(entry.Key, judge, "test-deployment-x");
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.Key}: factory threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (built is null)
            {
                failures.Add($"{entry.Key}: resolved to null");
            }
            else if (!entry.EvalType.IsInstanceOfType(built))
            {
                failures.Add($"{entry.Key}: declared {entry.EvalType.Name} but built {built.GetType().Name}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact] // The two documented aliases. If either is re-pointed, the calibration table changed meaning.
    public void CalibrationAliases_PointAtTheDocumentedEvals()
    {
        var registry = Populated();

        Assert.Equal(
            registry.TryGet("system_prompt_leakage")!.EvalType,
            registry.TryGet("prompt_leak")!.EvalType);
        Assert.Equal(
            registry.TryGet("jailbreak_resistance")!.EvalType,
            registry.TryGet("escalation_resistance")!.EvalType);
    }

    [Fact] // The CLI calls Register() explicitly on top of the [ModuleInitializer]. Both must be safe.
    public void RegisterInto_IsIdempotent()
    {
        var registry = Populated();
        AgenticEvalRegistration.RegisterInto(registry);

        Assert.Equal(AgenticEvalRegistration.DispatchedEvaluatorCount, registry.All.Count);
    }

    [Fact]
    public void RegisterInto_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => AgenticEvalRegistration.RegisterInto(null!));

    [Fact] // Every entry names the assembly that owns it, so a key conflict says where both came from.
    public void EveryRegistration_NamesItsOwningAssembly()
    {
        var registry = Populated();
        Assert.All(registry.All, e => Assert.Equal("AgentEval.Evals.Agentic", e.OwningAssemblyName));
    }

    [Fact] // The shared registry is what the CLI resolves from; Register() must reach it.
    public void Register_PopulatesTheSharedRegistry()
    {
        AgenticEvalRegistration.Register();

        foreach (var key in ExpectedKeys)
            Assert.NotNull(EvalRegistry.Shared.TryGet(key));
    }
}
