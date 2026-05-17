// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic;
using AgentEval.Evals.Agentic.Composition;
using AgentEval.Evals.Agentic.Memory;
using AgentEval.Evals.Agentic.MultiTurn;
using Xunit;

namespace AgentEval.Tests.Agentic.Composition;

/// <summary>
/// Tests for <see cref="CostFilteredCompositeBuilder.FilterByBudget"/>.
/// </summary>
public class CostFilteredCompositeBuilderTests
{
    private sealed class StubEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input,
            string output,
            IEnumerable<string> criteria,
            CancellationToken ct = default) =>
            Task.FromResult(new EvaluationResult
            {
                OverallScore = 75,
                Summary = "stub",
                CriteriaResults = criteria
                    .Select(c => new CriterionResult { Criterion = c, Met = true, Explanation = "stub" })
                    .ToList()
            });
    }

    /// <summary>
    /// The Conversational preset contains:
    /// - MemoryRecallAccuracy: HIGH
    /// - LongConversationCoherence: HIGH
    /// - TurnCoherence: MEDIUM
    /// - GoalTracking: HIGH
    /// - ClarificationAppropriateness: LOW
    ///
    /// Filtering to LOW should keep only ClarificationAppropriateness (1 component).
    /// Weight should renormalise to 1.0.
    /// </summary>
    [Fact]
    public void FilterByBudget_LowTier_RemovesHighAndMediumEvaluators()
    {
        var judge = new StubEvaluator();
        var original = AgenticBenchmark.Conversational(judge);

        var filtered = CostFilteredCompositeBuilder.FilterByBudget(original, EvaluatorCostTier.Low);

        // Only LOW-tier evaluator (ClarificationAppropriateness) should remain.
        Assert.Single(filtered.Components);
        Assert.Equal("clarification_appropriateness", filtered.Components[0].Eval.Key);

        // Weights must renormalise to 1.0.
        var totalWeight = filtered.Components.Sum(c => c.Weight);
        Assert.Equal(1.0, totalWeight, precision: 5);
    }

    /// <summary>
    /// Filtering to High should return the original composite unchanged (all evaluators are
    /// within budget) — the method returns the same reference (no-op path).
    /// </summary>
    [Fact]
    public void FilterByBudget_AllTier_NoOp()
    {
        var judge = new StubEvaluator();
        var original = AgenticBenchmark.Conversational(judge);

        var filtered = CostFilteredCompositeBuilder.FilterByBudget(original, EvaluatorCostTier.High);

        // All 5 components kept — no-op returns the original instance.
        Assert.Equal(5, filtered.Components.Count);
        Assert.Same(original, filtered);
    }

    /// <summary>
    /// Filtering a composite where all evaluators exceed the budget should throw.
    /// </summary>
    [Fact]
    public void FilterByBudget_RemovesEverything_Throws()
    {
        var judge = new StubEvaluator();
        // AdversarialDirect has DirectInjection (LOW) + PersonaAttack (LOW) + JailbreakResistance (MEDIUM).
        // Filtering to Trivial should remove all three → InvalidOperationException.
        var original = AgenticBenchmark.AdversarialDirect(judge);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CostFilteredCompositeBuilder.FilterByBudget(original, EvaluatorCostTier.Trivial));

        Assert.Contains("No evaluators remain", ex.Message);
    }

    /// <summary>
    /// Edge case (R6): when every surviving component has Weight==0, the renormalisation
    /// branch <c>totalWeight &gt; 0</c> is false. The builder must NOT divide by zero or
    /// produce <c>NaN</c> weights — it should return the filtered components with their
    /// original (zero) weights intact and let the aggregation strategy handle them.
    /// </summary>
    [Fact]
    public void FilterByBudget_AllSurvivingComponentsHaveZeroWeight_DoesNotProduceNaN()
    {
        var judge = new StubEvaluator();

        // Build a 2-component composite with both weights = 0:
        //   - clarification_appropriateness  (LOW)  — survives a Low filter
        //   - memory_recall_accuracy         (HIGH) — filtered out at Low
        var components = new List<EvalComponent>
        {
            new(new ClarificationAppropriatenessEval(judge), Weight: 0.0, Required: false),
            new(new MemoryRecallAccuracyEval(judge),         Weight: 0.0, Required: false),
        };

        var composite = new CompositeEval(
            key: "zero-weight-test",
            name: "Zero-Weight Test",
            category: "test",
            version: "1.0.0",
            components: components,
            aggregation: WeightedSumAggregation.Instance);

        var filtered = CostFilteredCompositeBuilder.FilterByBudget(composite, EvaluatorCostTier.Low);

        // Filtering changed the component count (HIGH dropped) → no-op early-return is skipped.
        Assert.Single(filtered.Components);
        Assert.Equal("clarification_appropriateness", filtered.Components[0].Eval.Key);

        // The surviving component keeps its zero weight (no NaN across any component, no exception).
        Assert.All(filtered.Components, c =>
        {
            Assert.False(double.IsNaN(c.Weight), "Filtered components must not have NaN weights.");
            Assert.False(double.IsInfinity(c.Weight), "Filtered components must not have infinite weights.");
        });
        Assert.Equal(0.0, filtered.Components[0].Weight);
    }
}
