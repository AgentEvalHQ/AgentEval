// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

public class AtomicLlmEvalTests
{
    // ── Stub ─────────────────────────────────────────────────────────────────

    private sealed class FakeEvaluator : AgentEval.Core.IEvaluator
    {
        public AgentEval.Core.EvaluationResult Result { get; set; } = new();

        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input,
            string output,
            IEnumerable<string> criteria,
            CancellationToken ct = default) =>
            Task.FromResult(Result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AtomicLlmEval MakeSut(
        FakeEvaluator evaluator,
        IReadOnlyList<string>? criteria = null,
        double passThreshold = 0.70,
        string? judgeModel = null,
        string? promptId = null) =>
        new(
            evaluator,
            key: "test-llm",
            name: "Test LLM Eval",
            category: "quality",
            version: "1.0.0",
            criteria: criteria ?? new[] { "accuracy" },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: promptId);

    private static EvalInput MakeInput(string? response = "some response") =>
        new(Query: "some query", Response: response);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_PassCase_Score85_PassedTrueNoneSeverity()
    {
        var evaluator = new FakeEvaluator
        {
            Result = new AgentEval.Core.EvaluationResult { OverallScore = 85 }
        };
        var sut = MakeSut(evaluator);

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.Equal(0.85, result.Score.Value, precision: 10);
        Assert.True(result.Score.Passed);
        Assert.Equal("none", result.Score.Severity);
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_FailCase_Score50_MediumSeverity()
    {
        var evaluator = new FakeEvaluator
        {
            Result = new AgentEval.Core.EvaluationResult { OverallScore = 50 }
        };
        var sut = MakeSut(evaluator);

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.Equal(0.50, result.Score.Value, precision: 10);
        Assert.False(result.Score.Passed);
        Assert.Equal("medium", result.Score.Severity);
        Assert.Equal("fail", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_CriticalFail_Score20_HighSeverity()
    {
        var evaluator = new FakeEvaluator
        {
            Result = new AgentEval.Core.EvaluationResult { OverallScore = 20 }
        };
        var sut = MakeSut(evaluator);

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.Equal(0.20, result.Score.Value, precision: 10);
        Assert.False(result.Score.Passed);
        Assert.Equal("high", result.Score.Severity);
        Assert.Equal("fail", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_ProvenanceType_IsAtomicLlm()
    {
        var evaluator = new FakeEvaluator
        {
            Result = new AgentEval.Core.EvaluationResult { OverallScore = 80 }
        };
        var sut = MakeSut(evaluator, judgeModel: "my-model", promptId: "p-001");

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.Equal("atomic-llm", result.Provenance.Type);
        Assert.Equal("my-model", result.Provenance.JudgeModel);
        Assert.Equal("p-001", result.Provenance.PromptId);
    }

    [Fact]
    public async Task EvaluateAsync_NullResponse_ThrowsInvalidOperationException()
    {
        var evaluator = new FakeEvaluator();
        var sut = MakeSut(evaluator);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.EvaluateAsync(MakeInput(response: null)));
    }

    [Fact]
    public async Task EvaluateAsync_CriteriaResults_MappedToDimensionsAndEvidence()
    {
        var evaluator = new FakeEvaluator
        {
            Result = new AgentEval.Core.EvaluationResult
            {
                OverallScore = 80,
                CriteriaResults =
                [
                    new AgentEval.Core.CriterionResult { Criterion = "accuracy", Met = true,  Explanation = "Accurate answer" },
                    new AgentEval.Core.CriterionResult { Criterion = "clarity",  Met = false, Explanation = "Could be clearer" }
                ]
            }
        };
        var sut = MakeSut(evaluator, criteria: ["accuracy", "clarity"]);

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.NotNull(result.Details.Dimensions);
        Assert.Equal(1.0, result.Details.Dimensions!["accuracy"]);
        Assert.Equal(0.0, result.Details.Dimensions!["clarity"]);

        Assert.NotNull(result.Details.Evidence);
        Assert.Equal(2, result.Details.Evidence!.Count);
        Assert.Contains(result.Details.Evidence, e => e.Reference == "accuracy" && e.Message == "Accurate answer");
        Assert.Contains(result.Details.Evidence, e => e.Reference == "clarity"  && e.Message == "Could be clearer");
    }

    [Fact]
    public async Task EvaluateAsync_DuplicateCriterion_LastValueWins()
    {
        // GroupBy + .Last() means the last occurrence wins for duplicate criterion names.
        var evaluator = new FakeEvaluator
        {
            Result = new AgentEval.Core.EvaluationResult
            {
                OverallScore = 80,
                CriteriaResults =
                [
                    new AgentEval.Core.CriterionResult { Criterion = "accuracy", Met = false, Explanation = "First" },
                    new AgentEval.Core.CriterionResult { Criterion = "accuracy", Met = true,  Explanation = "Second (overrides)" }
                ]
            }
        };
        var sut = MakeSut(evaluator, criteria: ["accuracy"]);

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.NotNull(result.Details.Dimensions);
        Assert.Equal(1.0, result.Details.Dimensions!["accuracy"]);  // Last wins: Met=true => 1.0
    }
    /// <summary>Records exactly what the judge was handed, so the test can assert on the prompt itself.</summary>
    private sealed class CapturingEvaluator : AgentEval.Core.IEvaluator
    {
        public string? Input { get; private set; }
        public string? Output { get; private set; }

        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            Input = input;
            Output = output;
            return Task.FromResult(new AgentEval.Core.EvaluationResult { OverallScore = 100, Summary = "ok" });
        }
    }

    [Fact]
    public async Task Context_ReachesTheJudge_BecauseAJudgeCannotGradeGroundingItCannotSee()
    {
        // This leaf used to hand the judge the query and the response only. An eval that set Context — a
        // retrieved passage, a ledger extract, the source document — then asked "is this grounded?" while
        // withholding the ground, and the judge graded plausibility instead. Two samples carried a wrapper
        // to work around it; this is the library doing it.
        var judge = new CapturingEvaluator();
        var eval = new AtomicLlmEval(judge, "k", "n", "c", "1.0.0", ["Every fact appears in the context."]);

        await eval.EvaluateAsync(new EvalInput(
            Query: "When was invoice 4471 paid?",
            Response: "On 3 May 2026.",
            Context: "Ledger: invoice 4471 paid 2026-05-03 by bank transfer."));

        Assert.Contains("Ledger: invoice 4471 paid 2026-05-03", judge.Input!, StringComparison.Ordinal);
        Assert.Contains("When was invoice 4471 paid?", judge.Input!, StringComparison.Ordinal);
        Assert.Equal("On 3 May 2026.", judge.Output);
    }

    [Fact]
    public async Task NoContext_LeavesTheJudgeInputExactlyTheQuery()
    {
        // The common case must not gain a dangling label or a trailing blank section.
        var judge = new CapturingEvaluator();
        var eval = new AtomicLlmEval(judge, "k", "n", "c", "1.0.0", ["Answers the question."]);

        await eval.EvaluateAsync(new EvalInput(Query: "What is 2 + 2?", Response: "4"));

        Assert.Equal("What is 2 + 2?", judge.Input);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankContext_IsNotAppended(string context)
    {
        var judge = new CapturingEvaluator();
        var eval = new AtomicLlmEval(judge, "k", "n", "c", "1.0.0", ["Answers the question."]);

        await eval.EvaluateAsync(new EvalInput(Query: "Q", Response: "A", Context: context));

        Assert.Equal("Q", judge.Input);
    }

}
