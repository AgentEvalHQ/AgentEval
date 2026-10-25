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
        Assert.Equal(NotMeasuredCause.MissingInput, result.NotMeasuredCause);
    }

    // T05: an embedding call that failed, or vectors the cosine cannot be read from, measured nothing. Each was a Fail
    // (a zero or NaN vector a measured 0 "Low semantic similarity"), and the caller's cancellation was swallowed.

    private static readonly EvaluationContext s_everyInput = new()
    {
        Input = "What is the capital of France?", Output = "Paris.", Context = "France's capital is Paris.", GroundTruth = "Paris",
    };

    public static TheoryData<string> EmbeddingMetricNames() => new()
    {
        "embed_answer_similarity", "embed_response_context", "embed_query_context",
    };

    private static EmbeddingBasedMetric Create(string name, IAgentEvalEmbeddings embeddings) => name switch
    {
        "embed_answer_similarity" => new AnswerSimilarityMetric(embeddings),
        "embed_response_context" => new ResponseContextSimilarityMetric(embeddings),
        _ => new QueryContextSimilarityMetric(embeddings),
    };

    [Theory]
    [MemberData(nameof(EmbeddingMetricNames))]
    public async Task AFailedEmbeddingCall_IsNotMeasured_WithTheError(string name)
    {
        var embeddings = new StubEmbeddings(_ => throw new HttpRequestException("503 Service Unavailable"));

        var result = await Create(name, embeddings).EvaluateAsync(s_everyInput);

        Assert.False(result.Measured, name);
        Assert.False(result.Passed, name);
        Assert.Equal(NotMeasuredCause.JudgeCallFailed, result.NotMeasuredCause);
        Assert.Equal("System.Net.Http.HttpRequestException", result.Details!["errorType"]);
        Assert.Equal("503 Service Unavailable", result.Details["error"]);   // the key it reported before
        Assert.Contains("503", result.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EmbeddingMetricNames))]
    public async Task AProviderSideTimeout_IsAFailedCall_NotACancellation(string name)
    {
        var embeddings = new StubEmbeddings(_ => throw new TaskCanceledException("HttpClient.Timeout"));

        var result = await Create(name, embeddings).EvaluateAsync(s_everyInput);

        Assert.False(result.Measured, name);
        Assert.Equal(NotMeasuredCause.JudgeCallFailed, result.NotMeasuredCause);
    }

    [Theory]
    [MemberData(nameof(EmbeddingMetricNames))]
    public async Task TheCallersCancellation_Propagates(string name)
    {
        // It was caught and returned as "Embedding generation failed".
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var embeddings = new StubEmbeddings(ct =>
        {
            ct.ThrowIfCancellationRequested();
            return [new float[] { 1, 0 }, new float[] { 1, 0 }];
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(name, embeddings).EvaluateAsync(s_everyInput, cts.Token));
    }

    public static TheoryData<string, float[][]> UnusableVectors() => new()
    {
        { "one vector", new[] { new float[] { 1, 0, 0 } } },
        { "two zero vectors", new[] { new float[] { 0, 0, 0 }, new float[] { 0, 0, 0 } } },
        { "different dimensions 3 and 2", new[] { new float[] { 1, 0, 0 }, new float[] { 1, 0 } } },
        { "a NaN component", new[] { new float[] { 1, float.NaN, 0 }, new float[] { 1, 0, 0 } } },
        { "empty vectors", new[] { Array.Empty<float>(), Array.Empty<float>() } },
    };

    [Theory]
    [MemberData(nameof(UnusableVectors))]
    public async Task VectorsTheCosineCannotBeReadFrom_AreNotMeasured(string because, float[][] vectors)
    {
        var embeddings = new StubEmbeddings(_ => vectors.Select(v => new ReadOnlyMemory<float>(v)).ToList());

        var result = await new AnswerSimilarityMetric(embeddings).EvaluateAsync(s_everyInput);

        Assert.False(result.Measured, because);
        Assert.False(result.Passed, because);
        Assert.Equal(NotMeasuredCause.JudgeReplyUnreadable, result.NotMeasuredCause);
    }

    [Fact]
    public async Task DifferentDimensions_AreNamedInTheReason()
    {
        // The ArgumentException from the cosine was caught and reported as "Embedding generation failed".
        var embeddings = new StubEmbeddings(_ => [new float[] { 1, 0, 0 }, new float[] { 1, 0 }]);

        var result = await new AnswerSimilarityMetric(embeddings).EvaluateAsync(s_everyInput);

        Assert.Contains("3 and 2", result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>Embeddings scripted per call; <see cref="GetEmbeddingAsync"/> is not used by the metrics.</summary>
    private sealed class StubEmbeddings(Func<CancellationToken, IReadOnlyList<ReadOnlyMemory<float>>> vectors) : IAgentEvalEmbeddings
    {
        public Task<ReadOnlyMemory<float>> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GetEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
            => Task.FromResult(vectors(cancellationToken));
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
