// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Agentic;
using AgentEval.Evals.Agentic.Adversarial;
using AgentEval.Evals.Agentic.Safety;
using AgentEval.Evals.Agentic.Safety.Policy;
using AgentEval.Tests.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Agentic;

/// <summary>
/// The decision tests rely on <see cref="UntimedRegex"/> to take the wall-clock budget off the pattern scans. If it
/// silently handed back the evaluator unchanged, those tests would depend on machine load again and nothing would say
/// so; if it silently dropped an option, they would test something else. These tests pin both.
/// </summary>
public class UntimedRegexTests
{
    private static readonly EvalInput Input = new(Query: "Summarise the order.", Response: "A blue kettle, delivered Monday.");

    // The evaluators publish their configuration for an input only while their matcher is the production one, so a
    // null here shows the matcher was swapped, through the public surface.
    private static bool UsesTheProductionMatcher(IEval eval) =>
        ((IEvalConfigurationSource)eval).CaptureConfiguration(Input) is not null;

    [Theory]
    [InlineData("direct_injection")]
    [InlineData("persona_attack")]
    [InlineData("jailbreak_resistance")]
    [InlineData("escalation_resistance")]
    [InlineData("sensitive_data_leakage")]
    public void Resolve_SwapsTheMatcher_OfEveryPatternScanningEvaluator(string key)
    {
        var registry = new EvalRegistry();
        AgenticEvalRegistration.RegisterInto(registry);
        var judge = new FixedScoreEvaluator(100);

        Assert.True(UsesTheProductionMatcher(registry.Resolve(key, judge, judgeModel: null)!));   // the control
        Assert.False(UsesTheProductionMatcher(UntimedRegex.Resolve(registry, key, judge)!));
    }

    [Fact]
    public void WithUntimedPatterns_RefusesAnInstanceBuiltWithOptionsItWouldDrop()
    {
        var judge = new FixedScoreEvaluator(100);

        Assert.Throws<InvalidOperationException>(() =>
            UntimedRegex.WithUntimedPatterns(new JailbreakResistanceEval(judge, patternsToRun: 1), judge));
        Assert.Throws<InvalidOperationException>(() =>
            UntimedRegex.WithUntimedPatterns(new DirectInjectionEval(judge, passThreshold: 0.8), judge));
        Assert.Throws<InvalidOperationException>(() =>
            UntimedRegex.WithUntimedPatterns(new SensitiveDataLeakageEval(judge, customPatterns: ["private"]), judge));
        Assert.Throws<InvalidOperationException>(() =>
            UntimedRegex.WithUntimedPatterns(new PersonaAttackEval(judge, judgeModel: "model-a"), judge, judgeModel: "model-b"));
    }

    [Fact]
    public void WithUntimedPatterns_OnAPreset_SwapsEveryScanner_AndKeepsTheGateSettings()
    {
        var judge = new FixedScoreEvaluator(100);
        var preset = AgenticBenchmark.Safety(judge, new StaticPolicyResolver(new ProhibitedActionPolicy([], [], [], [])), "s");

        var untimed = UntimedRegex.WithUntimedPatterns(preset, judge);

        Assert.Equal(preset.Key, untimed.Key);
        Assert.Equal(preset.Threshold, untimed.Threshold);
        Assert.Same(preset.Aggregation, untimed.Aggregation);
        Assert.Equal(preset.MinimumMeasuredShare, untimed.MinimumMeasuredShare);
        Assert.Equal(preset.SeverityCapsThreshold, untimed.SeverityCapsThreshold);
        Assert.Equal(preset.Components.Select(c => (c.Eval.Key, c.Weight, c.Required, c.OnFailure)),
            untimed.Components.Select(c => (c.Eval.Key, c.Weight, c.Required, c.OnFailure)));
        var leakage = Assert.Single(untimed.Components, c => c.Eval is SensitiveDataLeakageEval);
        Assert.False(UsesTheProductionMatcher(leakage.Eval));
    }

    [Fact]
    public void Copy_KeepsThePatternAndOptions_WithAFiniteBudget()
    {
        var production = new Regex(@"\bkey\s*=\s*\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

        var copy = UntimedRegex.Copy(production);

        Assert.Equal(production.ToString(), copy.ToString());
        Assert.Equal(RegexOptions.IgnoreCase, copy.Options);
        Assert.Equal(UntimedRegex.Budget, copy.MatchTimeout);
        Assert.NotEqual(Regex.InfiniteMatchTimeout, copy.MatchTimeout);   // a pathological pattern still fails, by name
        Assert.Same(copy, UntimedRegex.Copy(production));
        Assert.Equal("key=[x]", UntimedRegex.Replace(production, "KEY = abc", "key=[x]"));
    }
}
