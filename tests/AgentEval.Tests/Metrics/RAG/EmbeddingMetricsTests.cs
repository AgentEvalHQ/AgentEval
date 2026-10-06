// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Embeddings;
using AgentEval.Metrics.RAG;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests.Metrics.RAG;

/// <summary>
/// #203 review round 16 (B12l): B12i turned every validation error of the embedding similarities into "not measured",
/// including the agent's own empty answer — a measured failure that MAF then passed. Only an input the caller did not
/// supply (a retrieved context, a reference answer) is not measured.
/// </summary>
public sealed class EmbeddingMetricsTests
{
    private static readonly IAgentEvalEmbeddings s_embeddings = new FakeEmbeddings(dimensions: 64);

    public static TheoryData<string, EmbeddingBasedMetric, EvaluationContext> MissingInputs() => new()
    {
        { "answer similarity, no reference", new AnswerSimilarityMetric(s_embeddings),
            new EvaluationContext { Input = "q", Output = "Paris.", GroundTruth = "  " } },
        { "response-context, no context", new ResponseContextSimilarityMetric(s_embeddings),
            new EvaluationContext { Input = "q", Output = "Paris.", Context = null } },
        { "query-context, no context", new QueryContextSimilarityMetric(s_embeddings),
            new EvaluationContext { Input = "q", Output = "Paris.", Context = "" } },
    };

    [Theory]
    [MemberData(nameof(MissingInputs))]
    public async Task AnInputTheCallerDidNotSupply_IsNotMeasured(string because, EmbeddingBasedMetric metric, EvaluationContext context)
    {
        var result = await metric.EvaluateAsync(context);

        Assert.False(result.Measured, because);
        Assert.False(result.Passed, because);
    }

    public static TheoryData<string, EmbeddingBasedMetric, EvaluationContext> EmptyAnswers() => new()
    {
        { "answer similarity, empty answer", new AnswerSimilarityMetric(s_embeddings),
            new EvaluationContext { Input = "q", Output = "", GroundTruth = "Paris is the capital of France." } },
        { "answer similarity, blank answer", new AnswerSimilarityMetric(s_embeddings),
            new EvaluationContext { Input = "q", Output = "   ", GroundTruth = "Paris is the capital of France." } },
        { "response-context, empty answer", new ResponseContextSimilarityMetric(s_embeddings),
            new EvaluationContext { Input = "q", Output = "", Context = "Paris is the capital of France." } },
        { "response-context, blank answer", new ResponseContextSimilarityMetric(s_embeddings),
            new EvaluationContext { Input = "q", Output = "\t", Context = "Paris is the capital of France." } },
    };

    [Theory]
    [MemberData(nameof(EmptyAnswers))]
    public async Task TheAgentsEmptyAnswer_IsAMeasuredFail(string because, EmbeddingBasedMetric metric, EvaluationContext context)
    {
        var result = await metric.EvaluateAsync(context);

        Assert.True(result.Measured, because);
        Assert.False(result.Passed, because);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public async Task ACustomSubclassError_IsAMeasuredFail()
    {
        var result = await new RefusingMetric().EvaluateAsync(new EvaluationContext { Input = "q", Output = "a" });

        Assert.True(result.Measured);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task WithEveryInput_TheComparisonIsMeasured()
    {
        var result = await new AnswerSimilarityMetric(s_embeddings, passingThreshold: 0).EvaluateAsync(
            new EvaluationContext { Input = "q", Output = "Paris.", GroundTruth = "Paris." });

        Assert.True(result.Measured);
        Assert.True(result.Passed);
    }

    private sealed class RefusingMetric() : EmbeddingBasedMetric(s_embeddings, 70)
    {
        public override string Name => "embed_custom";
        public override string Description => "custom";
        public override bool RequiresContext => false;
        public override bool RequiresGroundTruth => false;
        protected override (string? Text1, string? Text2, string? ValidationError) GetTextsToCompare(EvaluationContext context) =>
            (null, null, "The answer is longer than this check allows.");
        protected override string GetPassMessage(double similarity) => "pass";
        protected override string GetFailMessage(double similarity) => "fail";
    }
}
