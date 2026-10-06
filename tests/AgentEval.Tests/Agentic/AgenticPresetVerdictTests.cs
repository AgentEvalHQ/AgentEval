// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Safety.Policy;
using Xunit;

namespace AgentEval.Tests.Agentic;

/// <summary>
/// Every agentic preset says what each check's failure does to its verdict (#203 review, B6b — the owner's rule:
/// keep working and say the answer is not optimal because of the failed dimension, unless the failure means the
/// answer cannot be trusted). No preset can read a clean PASS with a check failing: before, every component was
/// averaged — fluency 0.30 with the rest perfect read RAG 0.965 = PASS; intent resolution failing read the standard
/// agent gate 0.85 = PASS.
/// <para>
/// The sweep forces one component at a time to a measured fail (the rest pass) through each preset's own settings.
/// Accuracy dimensions (Fail) fail the preset; quality dimensions (Warn) never let it pass, and a warn names them.
/// </para>
/// </summary>
public class AgenticPresetVerdictTests
{
    private sealed class EmptyPolicy : IPolicyResolver
    {
        public ProhibitedActionPolicy GetPolicyFor(string subjectId) => new([], [], [], []);
    }

    // A component forced to a measured verdict, keeping its key so the summary can name it.
    private sealed class Forced(IEval inner, string label) : IEval
    {
        public Forced(IEval inner, bool pass) : this(inner, pass ? "pass" : "fail") { }

        public string Key => inner.Key;
        public string Name => inner.Name;
        public string Category => inner.Category;
        public string Version => inner.Version;

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) => Task.FromResult(new EvalResult(
            new(Key, Name, Category, Version),
            // A warn is a judge score in its rubric's needs-review band: 0.8, low severity, not passed.
            new EvalScore(label switch { "pass" => 1.0, "warn" => 0.8, _ => 0.3 }, null, label, label == "pass", null,
                label switch { "pass" => "none", "warn" => "low", _ => "medium" }, null),
            new(null, null, null, null, null),
            new("atomic-code", null, null, null, null, 0, false),
            DateTimeOffset.UtcNow));
    }

    public static TheoryData<string> Presets => new()
    {
        "agentic-execution", "tool-call-accuracy", "rag-quality", "judge-quality", "safety", "telemetry",
        "glass-box-diagnostics", "stochastic-stability", "conversational", "reasoning", "user-experience", "adversarial-direct",
    };

    private static CompositeEval Build(string preset)
    {
        var judge = new FixedScoreEvaluator(100);
        return preset switch
        {
            "agentic-execution" => AgenticBenchmark.AgenticExecution(judge),
            "tool-call-accuracy" => AgenticBenchmark.ToolCallAccuracy(judge),
            "rag-quality" => AgenticBenchmark.RagQuality(judge),
            "judge-quality" => AgenticBenchmark.JudgeQuality(),
            "safety" => AgenticBenchmark.Safety(judge, new EmptyPolicy(), "s"),
            "telemetry" => AgenticBenchmark.Telemetry(),
            "glass-box-diagnostics" => AgenticBenchmark.GlassBoxDiagnostics(judge),
            "stochastic-stability" => AgenticBenchmark.StochasticStability(),
            "conversational" => AgenticBenchmark.Conversational(judge),
            "reasoning" => AgenticBenchmark.Reasoning(judge),
            "user-experience" => AgenticBenchmark.UserExperience(judge),
            "adversarial-direct" => AgenticBenchmark.AdversarialDirect(judge),
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };
    }

    private static CompositeEval WithForced(CompositeEval preset, int failing) =>
        preset.WithComponents(preset.Components
            .Select((c, i) => c with { Eval = new Forced(c.Eval, pass: i != failing) })
            .ToList());

    [Theory]
    [MemberData(nameof(Presets))]
    public void EveryComponent_SaysWhatItsFailureDoes(string preset)
    {
        var averaged = Build(preset).Components.Where(c => c.OnFailure == ComponentFailureEffect.Averaged).Select(c => c.Eval.Key).ToList();

        Assert.True(averaged.Count == 0, $"{preset}: components whose failure only moves the average: {string.Join(", ", averaged)}");
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public async Task EveryComponentPassing_ThePresetPasses(string preset)
    {
        var all = Build(preset);
        var result = await all.WithComponents(all.Components.Select(c => c with { Eval = new Forced(c.Eval, pass: true) }).ToList())
            .EvaluateAsync(new EvalInput("q", "r"));

        Assert.Equal("pass", result.Score.Label);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public async Task AnyOneComponentNeedingReview_NeverReadsPass_AndASecurityGateFails(string preset)
    {
        // Review round 3 H3 / B10c: a needs-review score (a warn) FAILed AdversarialDirect and prohibited_actions (through a
        // threshold or a severity cap) but WARNed on hate — and the docs promised WARN. A security gate's check now fails
        // its gate on anything short of a pass; an accuracy check that only warned makes its preset warn, "Not confirmed".
        var built = Build(preset);
        var wrong = new List<string>();
        for (var i = 0; i < built.Components.Count; i++)
        {
            var component = built.Components[i];
            var result = await built.WithComponents(built.Components
                    .Select((c, j) => c with { Eval = new Forced(c.Eval, j == i ? "warn" : "pass") }).ToList())
                .EvaluateAsync(new EvalInput("q", "r"));
            var key = component.Eval.Key;
            var named = result.Details.Summary?.Contains(key, StringComparison.Ordinal) == true;

            var ok = component.OnFailure switch
            {
                ComponentFailureEffect.FailUnlessPass => result.Score.Label == "fail" && named,
                ComponentFailureEffect.Fail or ComponentFailureEffect.Warn => result.Score.Label is "warn" or "fail" && named,
                _ => false,
            };
            if (!ok)
                wrong.Add($"{key} ({component.OnFailure}) → {result.Score.Label} \"{result.Details.Summary}\"");
        }

        Assert.True(wrong.Count == 0, $"{preset}: " + string.Join("; ", wrong));
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public async Task AnyOneComponentFailing_NeverReadsPass_AndAnAccuracyFailureFails(string preset)
    {
        var built = Build(preset);
        var wrong = new List<string>();

        for (var i = 0; i < built.Components.Count; i++)
        {
            var component = built.Components[i];
            var result = await WithForced(built, i).EvaluateAsync(new EvalInput("q", "r"));
            var key = component.Eval.Key;

            var ok = component.OnFailure switch
            {
                ComponentFailureEffect.Fail or ComponentFailureEffect.FailUnlessPass => result.Score.Label == "fail",
                // A quality dimension warns and is named — or fails, when it weighs enough to sink the average.
                ComponentFailureEffect.Warn => result.Score.Label == "fail"
                    || (result.Score.Label == "warn" && result.Details.Summary?.Contains(key, StringComparison.Ordinal) == true),
                _ => false,
            };
            if (!ok)
                wrong.Add($"{key} ({component.OnFailure}) → {result.Score.Label} {result.Score.Value:0.000} \"{result.Details.Summary}\"");
        }

        Assert.True(wrong.Count == 0, $"{preset}: " + string.Join("; ", wrong));
    }
}
