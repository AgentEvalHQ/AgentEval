// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// A component that did not run can never make a verdict better (#203 review, B6c-6). The mid-branch review found a
/// child [ok, critical failure averaged out, skipped] lifting its parent from FAIL to WARN: the skipped leaf made the
/// child withhold its pass (NotMeasured), and the parent then dropped the child — with the critical failure it carried —
/// from its score and severity. The property is checked over every small shape, two levels deep: adding a skipped
/// required part to the child never lowers the parent's verdict rank (pass &lt; warn &lt; fail).
/// </summary>
public class CompositeEvalMonotonicityTests
{
    private sealed class Leaf(string key, string label, double value, string severity) : IEval
    {
        public string Key => key;
        public string Name => key;
        public string Category => "test";
        public string Version => "1.0.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) => Task.FromResult(
            label == "skipped"
                ? EvalResult.Skipped(this, "did not run")
                : new EvalResult(
                    new(key, key, "test", "1.0.0"),
                    new EvalScore(value, null, label, label == "pass", null, severity, null),
                    new(null, null, null, null, null),
                    new("atomic-code", null, null, null, null, 0, false),
                    DateTimeOffset.UtcNow));
    }

    private static readonly (string Label, double Value, string Severity)[] Parts =
    [
        ("pass", 1.0, "none"),
        ("fail", 0.5, "medium"),
        ("fail", 0.5, "critical"),
    ];

    private static int Rank(string label) => label switch { "pass" => 0, "warn" => 1, _ => 2 };

    private static CompositeEval Child(IReadOnlyList<EvalComponent> parts, bool caps) =>
        new("child", "Child", "test", "1.0.0", parts, WeightedSumAggregation.Instance, threshold: 0.7) { SeverityCapsThreshold = caps };

    private static CompositeEval Parent(CompositeEval child, bool caps) =>
        new("parent", "Parent", "test", "1.0.0",
            [new EvalComponent(child, 0.5), new EvalComponent(new Leaf("sibling", "pass", 1.0, "none"), 0.5)],
            WeightedSumAggregation.Instance, threshold: 0.7) { SeverityCapsThreshold = caps };

    public static IEnumerable<object[]> Shapes()
    {
        var effects = new[] { ComponentFailureEffect.Averaged, ComponentFailureEffect.Warn, ComponentFailureEffect.Fail, ComponentFailureEffect.FailUnlessPass };
        foreach (var a in Parts)
            foreach (var b in Parts)
                foreach (var effect in effects)
                    foreach (var childCaps in new[] { false, true })
                        foreach (var parentCaps in new[] { false, true })
                            yield return [a.Label, a.Severity, b.Label, b.Severity, effect, childCaps, parentCaps];
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task ASkippedPart_NeverMakesTheParentsVerdictBetter(
        string aLabel, string aSeverity, string bLabel, string bSeverity, ComponentFailureEffect effect, bool childCaps, bool parentCaps)
    {
        EvalComponent Part(string key, string label, string severity) =>
            new(new Leaf(key, label, label == "pass" ? 1.0 : 0.5, severity), 0.45) { OnFailure = effect };

        var parts = new List<EvalComponent> { Part("a", aLabel, aSeverity), Part("b", bLabel, bSeverity) };
        var withSkipped = parts.Append(new EvalComponent(new Leaf("unrun", "skipped", 0, "none"), 0.10)).ToList();

        var without = await Parent(Child(parts, childCaps), parentCaps).EvaluateAsync(new EvalInput("q", "r"));
        var with = await Parent(Child(withSkipped, childCaps), parentCaps).EvaluateAsync(new EvalInput("q", "r"));

        Assert.True(Rank(with.Score.Label) >= Rank(without.Score.Label),
            $"a skipped part lifted the parent: {without.Score.Label} ({without.Score.Value:0.000}/{without.Score.Severity}) → " +
            $"{with.Score.Label} ({with.Score.Value:0.000}/{with.Score.Severity})");
    }
}
