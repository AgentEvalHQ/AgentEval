// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.JudgeQuality;
using Xunit;

namespace AgentEval.Tests.Agentic.JudgeQuality;

/// <summary>
/// Unit tests for <see cref="JudgeAgreementEval"/>.
/// </summary>
public class JudgeAgreementEvalTests
{
    private static EvalInput BuildInputFromResults(IEnumerable<EvalResult> results) =>
        new(Query: "test", Metadata: new Dictionary<string, object>
        {
            ["judge_results"] = results.ToList()
        });

    private static EvalResult MakeResult(string label, double score = 1.0) =>
        new(
            Metric: new("test", "Test", "test", "1.0.0"),
            Score: new(score, null, label, label == "pass", 0.70, "none", null),
            Details: new(null, null, null, null, null),
            Provenance: new("stub", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task EvaluateAsync_PerfectAgreement_HighKappa()
    {
        // 3 judges all returning "pass" — perfect agreement → kappa = 1.0
        var eval = new JudgeAgreementEval(passThreshold: 0.60);
        var input = BuildInputFromResults(new[]
        {
            MakeResult("pass"),
            MakeResult("pass"),
            MakeResult("pass"),
        });

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
        // Kappa should be 1.0 (all three agree)
        Assert.Equal(1.0, result.Score.Value, precision: 5);
    }

    [Fact]
    public async Task EvaluateAsync_PerfectDisagreement_ZeroKappa()
    {
        // 2 judges with opposite verdicts — complete disagreement → kappa should be clamped to 0
        var eval = new JudgeAgreementEval(passThreshold: 0.60);
        var input = BuildInputFromResults(new[]
        {
            MakeResult("pass", 1.0),
            MakeResult("fail", 0.0),
        });

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("fail", result.Score.Label);
        Assert.False(result.Score.Passed);
        // Kappa = 0 when raters disagree completely (clamped, so 0)
        Assert.Equal(0.0, result.Score.Value, precision: 5);
    }

    [Fact]
    public async Task EvaluateAsync_IntermediateAgreement_PartialKappa()
    {
        // 3 judges: 2 agree (pass), 1 disagrees (fail) — intermediate agreement
        var eval = new JudgeAgreementEval(passThreshold: 0.60);
        var input = BuildInputFromResults(new[]
        {
            MakeResult("pass", 1.0),
            MakeResult("pass", 1.0),
            MakeResult("fail", 0.0),
        });

        var result = await eval.EvaluateAsync(input);

        // Score should be between 0 and 1 (partial agreement)
        Assert.InRange(result.Score.Value, 0.0, 1.0);
        // Dimensions should contain kappa
        Assert.True(result.Details.Dimensions!.ContainsKey("kappa"));
        Assert.Equal(3.0, result.Details.Dimensions!["judge_count"], precision: 5);
    }
}
