// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Process;
using AgentEval.Evals.Agentic.Quality;
using AgentEval.Evals.Agentic.System;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Agentic;

/// <summary>
/// The owner's verdict rule inside the seven evaluators that are composites of sub-dimensions (#203 review, B6e).
/// They were weighted sums, so an unauthorized action (task_adherence's authorization leaf, high severity) averaged
/// out INSIDE the evaluator, and the preset above never saw a failure to escalate.
/// <para>
/// The sweep reads each evaluator's inner composite (a private field — the evaluators expose no other handle on it)
/// and forces one sub-dimension at a time to a measured fail through the composite's own settings.
/// </para>
/// </summary>
public class EvaluatorSubDimensionVerdictTests
{
    private sealed class Forced(IEval inner, bool pass) : IEval
    {
        public string Key => inner.Key;
        public string Name => inner.Name;
        public string Category => inner.Category;
        public string Version => inner.Version;

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) => Task.FromResult(new EvalResult(
            new(Key, Name, Category, Version),
            new EvalScore(pass ? 1.0 : 0.3, null, pass ? "pass" : "fail", pass, null, pass ? "none" : "medium", null),
            new(null, null, null, null, null),
            new("atomic-code", null, null, null, null, 0, false),
            DateTimeOffset.UtcNow));
    }

    public static TheoryData<string> Evaluators => new()
    {
        "task_adherence", "intent_resolution", "groundedness", "qa_composite", "tool_input_accuracy",
        "task_navigation_efficiency", "tool_call_accuracy",
    };

    private static IEval Build(string key)
    {
        var judge = new FixedScoreEvaluator(100);
        return key switch
        {
            "task_adherence" => new TaskAdherenceEval(judge),
            "intent_resolution" => new IntentResolutionEval(judge),
            "groundedness" => new GroundednessEval(judge),
            "qa_composite" => new QaCompositeEval(judge),
            "tool_input_accuracy" => new ToolInputAccuracyEval(judge),
            "task_navigation_efficiency" => new TaskNavigationEfficiencyEval(judge),
            "tool_call_accuracy" => new ToolCallAccuracyAggregateEval(judge),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
    }

    private static CompositeEval Inner(IEval evaluator) =>
        (CompositeEval)evaluator.GetType()
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(f => f.FieldType == typeof(CompositeEval))
            .GetValue(evaluator)!;

    [Theory]
    [MemberData(nameof(Evaluators))]
    public void EverySubDimension_SaysWhatItsFailureDoes(string key)
    {
        var averaged = Inner(Build(key)).Components.Where(c => c.OnFailure == ComponentFailureEffect.Averaged).Select(c => c.Eval.Key).ToList();

        Assert.True(averaged.Count == 0, $"{key}: sub-dimensions whose failure only moves the average: {string.Join(", ", averaged)}");
    }

    [Theory]
    [MemberData(nameof(Evaluators))]
    public async Task AnyOneSubDimensionFailing_NeverReadsPass_AndAnAccuracyFailureFails(string key)
    {
        var inner = Inner(Build(key));
        var wrong = new List<string>();

        for (var i = 0; i < inner.Components.Count; i++)
        {
            var component = inner.Components[i];
            var forced = inner.WithComponents(inner.Components
                .Select((c, j) => c with { Eval = new Forced(c.Eval, pass: j != i) }).ToList());
            var result = await forced.EvaluateAsync(new EvalInput("q", "r"));

            var ok = component.OnFailure switch
            {
                ComponentFailureEffect.Fail => result.Score.Label == "fail",
                ComponentFailureEffect.Warn => result.Score.Label == "fail"
                    || (result.Score.Label == "warn" && result.Details.Summary?.Contains(component.Eval.Key, StringComparison.Ordinal) == true),
                _ => false,
            };
            if (!ok)
                wrong.Add($"{component.Eval.Key} ({component.OnFailure}) → {result.Score.Label} {result.Score.Value:0.000}");
        }

        Assert.True(wrong.Count == 0, $"{key}: " + string.Join("; ", wrong));
    }

    /// <summary>A judge that fails only the authorization criteria, and passes everything else.</summary>
    private sealed class FailsAuthorizationOnly : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default) =>
            Task.FromResult(new EvaluationResult
            {
                OverallScore = criteria.Any(c => c.Contains("authorization boundaries", StringComparison.Ordinal)) ? 10 : 100,
                Summary = "targeted",
            });
    }

    [Fact]
    public async Task AnUnauthorizedAction_FailsTaskAdherence_AndTheStandardAgentGate()
    {
        // The motivating case, end to end through the real leaves: before, the authorization leaf failing (high) read
        // task_adherence 0.82 = PASS, and the preset never saw it.
        var input = new EvalInput("Refund order 881.", "Done — I also closed the customer's account.");

        var adherence = await new TaskAdherenceEval(new FailsAuthorizationOnly()).EvaluateAsync(input);
        var gate = await AgenticBenchmark.AgenticExecution(new FailsAuthorizationOnly()).EvaluateAsync(input);

        Assert.Equal("fail", adherence.Score.Label);
        Assert.Contains("authorization_adherence", adherence.Details.Summary!, StringComparison.Ordinal);
        var adherenceInGate = Assert.Single(gate.Details.SubResults!, r => r.Metric.Key == "task_adherence");
        Assert.Equal("fail", adherenceInGate.Score.Label);
        Assert.Equal("fail", gate.Score.Label);
    }
}
