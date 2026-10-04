// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Composition;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// A copy of a composite keeps every verdict setting (#203 review, B4 and B6b). Two sites rebuilt composites from the
/// constructor and dropped the init-only settings: <c>WithExtraScenarios</c> (B4) and the cost filter behind
/// <c>bench agentic --max-cost-tier</c> (B6b), which turned a filtered Glass Box run's "medium → WARN" back into PASS.
/// Both now go through <see cref="CompositeEval.WithComponents"/>; the reflection test makes a setting added later
/// fail here until <c>WithComponents</c> copies it.
/// </summary>
public class CompositeEvalCopyTests
{
    private sealed class Leaf(string key) : IEval
    {
        public string Key => key;
        public string Name => key;
        public string Category => "test";
        public string Version => "1.0.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            throw new NotSupportedException("not evaluated in these tests");
    }

    private static CompositeEval Composite() =>
        new("c", "C", "test", "1.0.0", [new EvalComponent(new Leaf("a"), 0.5), new EvalComponent(new Leaf("b"), 0.5, Required: false)],
            CapByWorstAggregation.Instance, threshold: 0.8);

    // A non-default value for an init-only property of the given type.
    private static object NonDefault(Type type) => type switch
    {
        _ when type == typeof(bool) => true,
        _ when type == typeof(double) => 0.42,
        _ when type == typeof(int) => 7,
        _ when type == typeof(string) => "non-default",
        _ => throw new NotSupportedException(
            $"CompositeEval gained an init-only property of type {type}: teach this test a non-default value for it, " +
            "and make WithComponents copy it."),
    };

    [Fact]
    public void WithComponents_KeepsEveryInitOnlySetting_IncludingOnesAddedLater()
    {
        var initOnly = typeof(CompositeEval)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { } set && set.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit"))
            .ToList();
        Assert.NotEmpty(initOnly);   // MinimumMeasuredShare, SeverityCapsThreshold on 2026-10-04

        var original = Composite();
        foreach (var p in initOnly)
            p.SetValue(original, NonDefault(p.PropertyType));   // init-only setters are callable by reflection

        var copy = original.WithComponents([new EvalComponent(new Leaf("z"), 1.0)]);

        foreach (var p in initOnly)
            Assert.True(Equals(p.GetValue(original), p.GetValue(copy)), $"WithComponents dropped {p.Name}");
        Assert.Equal(original.Key, copy.Key);
        Assert.Same(original.Aggregation, copy.Aggregation);
        Assert.Equal(original.Threshold, copy.Threshold);
        Assert.Equal("z", Assert.Single(copy.Components).Eval.Key);
    }

    // Every preset the CLI can build, with a judge that is never called here.
    private static IEnumerable<CompositeEval> AgenticPresets()
    {
        var judge = new AgentEval.Tests.Agentic.FixedScoreEvaluator(100);
        var policy = new EmptyPolicy();
        return
        [
            AgenticBenchmark.AgenticExecution(judge), AgenticBenchmark.ToolCallAccuracy(judge), AgenticBenchmark.RagQuality(judge),
            AgenticBenchmark.JudgeQuality(), AgenticBenchmark.Safety(judge, policy, "s"), AgenticBenchmark.Telemetry(),
            AgenticBenchmark.GlassBoxDiagnostics(judge), AgenticBenchmark.StochasticStability(), AgenticBenchmark.Conversational(judge),
            AgenticBenchmark.Reasoning(judge), AgenticBenchmark.UserExperience(judge), AgenticBenchmark.AdversarialDirect(judge),
        ];
    }

    private sealed class EmptyPolicy : AgentEval.Evals.Agentic.Safety.Policy.IPolicyResolver
    {
        public AgentEval.Evals.Agentic.Safety.Policy.ProhibitedActionPolicy GetPolicyFor(string subjectId) => new([], [], [], []);
    }

    [Fact]
    public void EveryPresetEvaluator_HasAnExplicitCostTier()
    {
        // An unmapped key defaults to Medium, so `--max-cost-tier low` dropped the Glass Box preset's seven pure-code
        // checks and the run failed with "no evaluators remain" (#203 review, B6b sweep).
        var unmapped = AgenticPresets()
            .SelectMany(p => p.Components.Select(c => $"{p.Key}/{c.Eval.Key}"))
            .Where(k => !AgentEval.Evals.Agentic.Cost.EvaluatorCostMap.IsRegistered(k[(k.IndexOf('/') + 1)..]))
            .ToList();

        Assert.True(unmapped.Count == 0, "evaluators with no cost tier (silently Medium): " + string.Join(", ", unmapped));
    }

    [Fact]
    public void TheCostFilter_KeepsTheGlassBoxPresetsVerdictSettings()
    {
        var preset = AgenticBenchmark.GlassBoxDiagnostics();

        // "trivial" drops the injection check (baseline first, judge fallback: Low), so the filter really rebuilds.
        var filtered = CostFilteredCompositeBuilder.FilterByBudget(preset, EvaluatorCostTier.Trivial);

        Assert.NotSame(preset, filtered);
        Assert.True(filtered.SeverityCapsThreshold);
        Assert.Same(preset.Aggregation, filtered.Aggregation);
        Assert.Equal(preset.MinimumMeasuredShare, filtered.MinimumMeasuredShare);
    }

    [Fact]
    public void TheCostFilter_KeepsEachComponentsRequiredFlag()
    {
        var preset = AgenticBenchmark.GlassBoxDiagnostics();
        var filtered = CostFilteredCompositeBuilder.FilterByBudget(preset, EvaluatorCostTier.Trivial);

        foreach (var c in filtered.Components)
            Assert.Equal(preset.Components.Single(p => p.Eval.Key == c.Eval.Key).Required, c.Required);
    }
}
