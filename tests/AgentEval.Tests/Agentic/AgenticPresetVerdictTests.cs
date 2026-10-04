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
                ComponentFailureEffect.Fail => result.Score.Label == "fail",
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
