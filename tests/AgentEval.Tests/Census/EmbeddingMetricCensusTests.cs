// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Embeddings;
using AgentEval.Metrics.RAG;
using Microsoft.Extensions.AI;
using Xunit;

using EvaluationContext = AgentEval.Core.EvaluationContext;

namespace AgentEval.Tests.Census;

/// <summary>
/// T13 embedding census: every embedding metric in the product, found by reflection, reports a failed embedding call or
/// a vector the cosine cannot be read from as not measured — never a Fail at 0 — and still measures usable vectors.
/// </summary>
/// <remarks>
/// The coverage before this was a hand-written list of three metric names whose fallback built the third, so a fourth
/// embedding metric would have gone untested. Here a metric is in the census because its constructor takes an embedding
/// provider; a constructor the census cannot fill fails, it is never skipped. The scripted embeddings supply vectors
/// for the metric to read or refuse; they grade nothing.
/// </remarks>
public sealed class EmbeddingMetricCensusTests
{
    private static readonly EvaluationContext s_everyInput = new()
    {
        // Every input is set, so the missing-input branch is never what runs.
        Input = "What is the capital of France?",
        Output = "Paris.",
        Context = "France's capital is Paris.",
        GroundTruth = "Paris",
    };

    // ── Discovery ────────────────────────────────────────────────────────────

    private static bool IsEmbeddingParameter(Type type) =>
        type == typeof(IAgentEvalEmbeddings) || type == typeof(IEmbeddingGenerator<string, Embedding<float>>);

    /// <summary>Every exported, non-abstract <see cref="IMetric"/> with a public constructor that takes an embedding provider.</summary>
    internal static IReadOnlyList<Type> EmbeddingMetricTypes() =>
        ProductAssemblies.ExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IMetric).IsAssignableFrom(t))
            .Where(t => t.GetConstructors().Any(c => c.GetParameters().Any(p => IsEmbeddingParameter(p.ParameterType))))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    public static TheoryData<string> Metrics()
    {
        var data = new TheoryData<string>();
        foreach (var type in EmbeddingMetricTypes())
            data.Add(type.FullName!);
        return data;
    }

    /// <summary>
    /// Builds <paramref name="typeName"/> over <paramref name="embeddings"/>: the provider goes to the embedding
    /// parameter, every other parameter takes its default. A metric no constructor of which fits fails the census.
    /// </summary>
    private static IMetric Build(string typeName, IAgentEvalEmbeddings embeddings)
    {
        var type = EmbeddingMetricTypes().Single(t => t.FullName == typeName);
        var ctor = type.GetConstructors()
            .Where(c => c.GetParameters().Any(p => IsEmbeddingParameter(p.ParameterType)))
            .FirstOrDefault(c => c.GetParameters().All(p => IsEmbeddingParameter(p.ParameterType) || p.HasDefaultValue));
        if (ctor is null)
        {
            Assert.Fail($"census cannot construct {typeName}: every constructor that takes an embedding provider has " +
                        "another parameter with no default. Teach Build to fill it.");
        }

        var args = ctor!.GetParameters()
            .Select(p => p.ParameterType == typeof(IAgentEvalEmbeddings) ? embeddings
                : p.ParameterType == typeof(IEmbeddingGenerator<string, Embedding<float>>) ? new EmbeddingsAsGenerator(embeddings)
                : p.DefaultValue)
            .ToArray();
        return (IMetric)ctor.Invoke(args);
    }

    // ── E1 ───────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCensusFindsEveryEmbeddingMetric()
    {
        var found = EmbeddingMetricTypes();

        // A census that found nothing would pass every test below vacuously.
        Assert.True(found.Count >= 3, $"found {found.Count} embedding metrics; the census would pass vacuously");
        Assert.Contains(typeof(AnswerSimilarityMetric), found);
        Assert.Contains(typeof(ResponseContextSimilarityMetric), found);
        Assert.Contains(typeof(QueryContextSimilarityMetric), found);
    }

    // ── E2 ───────────────────────────────────────────────────────────────────

    private static Exception Failure(string kind) => kind switch
    {
        "503" => new HttpRequestException("503 Service Unavailable"),
        // A provider-side timeout: the caller's own token is not cancelled.
        "timeout" => new TaskCanceledException("HttpClient.Timeout"),
        "invalid-operation" => new InvalidOperationException("Embeddings provider returned 1 embeddings for 2 inputs."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static TheoryData<string, string> MetricsByFailure()
    {
        var data = new TheoryData<string, string>();
        foreach (var type in EmbeddingMetricTypes())
            foreach (var kind in new[] { "503", "timeout", "invalid-operation" })
                data.Add(type.FullName!, kind);
        return data;
    }

    [Theory]
    [MemberData(nameof(MetricsByFailure))]
    public async Task AFailedEmbeddingCall_IsNotMeasured(string metric, string failure)
    {
        var expected = Failure(failure);
        var embeddings = RecordingEmbeddings.Throwing(expected);

        var result = await Build(metric, embeddings).EvaluateAsync(s_everyInput);

        NoReading.Metric(result, $"{metric} / {failure}");
        Assert.Equal(NotMeasuredCause.JudgeCallFailed, result.NotMeasuredCause);
        Assert.Equal(expected.GetType().FullName, result.Details!["errorType"]);
        Assert.Equal(1, embeddings.Calls);
    }

    // ── E3 ───────────────────────────────────────────────────────────────────

    // Each shape is one reply: the vectors the provider returned for the two texts.
    private static readonly IReadOnlyDictionary<string, float[][]> s_unreadable = new Dictionary<string, float[][]>
    {
        ["none"] = [],
        ["one"] = [[1, 0, 0]],
        ["three"] = [[1, 0, 0], [1, 0, 0], [1, 0, 0]],
        ["zero+zero"] = [[0, 0, 0], [0, 0, 0]],
        ["zero+unit"] = [[0, 0, 0], [1, 0, 0]],
        ["NaN"] = [[1, float.NaN, 0], [1, 0, 0]],
        ["+inf"] = [[1, float.PositiveInfinity, 0], [1, 0, 0]],
        ["-inf"] = [[1, 0, 0], [float.NegativeInfinity, 0, 0]],
        ["dimensions 3 and 2"] = [[1, 0, 0], [1, 0]],
        ["empty+empty"] = [[], []],
        // Finite components whose length overflows a float: the norm is +∞, which passed the zero-vector check, and the
        // cosine came out NaN — a measured Fail at 0 with NaN in the details.
        ["overflow"] = [[3e19f, 3e19f, 3e19f], [3e19f, 3e19f, 3e19f]],
    };

    public static TheoryData<string, string> MetricsByUnreadableShape()
    {
        var data = new TheoryData<string, string>();
        foreach (var type in EmbeddingMetricTypes())
            foreach (var shape in s_unreadable.Keys)
                data.Add(type.FullName!, shape);
        return data;
    }

    [Theory]
    [MemberData(nameof(MetricsByUnreadableShape))]
    public async Task AVectorTheCosineCannotBeReadFrom_IsNotMeasured(string metric, string shape)
    {
        var embeddings = RecordingEmbeddings.Returning(s_unreadable[shape]);

        var result = await Build(metric, embeddings).EvaluateAsync(s_everyInput);

        Assert.Equal(1, embeddings.Calls);
        if (shape == "overflow")
        {
            // Written to hold whatever TensorPrimitives does with the overflow: not measured, or — had it scaled the sum —
            // the true cosine of two parallel vectors. Never a measured 0 with NaN in the details.
            if (result.Measured)
            {
                var cosine = Assert.IsType<double>(result.Details!["cosineSimilarity"]);
                Assert.True(double.IsFinite(cosine) && result.Score == 100,
                    $"{metric} / overflow: measured at {result.Score} with cosine {cosine}");
            }
            else
            {
                Assert.Equal(NotMeasuredCause.JudgeReplyUnreadable, result.NotMeasuredCause);
            }
            return;
        }

        NoReading.Metric(result, $"{metric} / {shape}");
        Assert.Equal(NotMeasuredCause.JudgeReplyUnreadable, result.NotMeasuredCause);
    }

    // ── E4 ───────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Metrics))]
    public async Task TheCallersCancellation_Propagates(string metric)
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var embeddings = RecordingEmbeddings.Returning([1, 0, 0], [1, 0, 0]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build(metric, embeddings).EvaluateAsync(s_everyInput, cts.Token));
    }

    // ── E5 ───────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Metrics))]
    public async Task ThroughTheMeaiAdapter_AFailedOrMiscountedReply_IsNotMeasured(string metric)
    {
        // A count mismatch is refused by the adapter (one embedding per input, in order); a provider error propagates.
        var miscounted = new StubEmbeddingGenerator(_ => [[1f, 0f, 0f]]);
        var failing = new StubEmbeddingGenerator(_ => throw new HttpRequestException("503 Service Unavailable"));

        var miscount = await Build(metric, new MEAIEmbeddingAdapter(miscounted)).EvaluateAsync(s_everyInput);
        var down = await Build(metric, new MEAIEmbeddingAdapter(failing)).EvaluateAsync(s_everyInput);

        NoReading.Metric(miscount, $"{metric} / adapter miscount");
        Assert.Equal(NotMeasuredCause.JudgeCallFailed, miscount.NotMeasuredCause);
        Assert.Equal(typeof(InvalidOperationException).FullName, miscount.Details!["errorType"]);
        NoReading.Metric(down, $"{metric} / adapter 503");
        Assert.Equal(NotMeasuredCause.JudgeCallFailed, down.NotMeasuredCause);
        Assert.Equal(1, miscounted.Calls);
        Assert.Equal(1, failing.Calls);
    }

    // ── E6 ───────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Metrics))]
    public async Task UsableVectors_AreMeasured(string metric)
    {
        // The census the other way round: a metric that reported "not measured" for everything would pass E2 to E5.
        var embeddings = RecordingEmbeddings.Returning([1, 0, 0], [1, 0, 0]);

        var result = await Build(metric, embeddings).EvaluateAsync(s_everyInput);

        Assert.True(result.Measured, $"{metric}: {result.Explanation}");
        Assert.Equal(100, result.Score, precision: 6);
        Assert.Equal(1.0, Assert.IsType<double>(result.Details!["cosineSimilarity"]), precision: 6);
    }

    [Theory]
    [MemberData(nameof(Metrics))]
    public async Task UsableVectors_ThroughTheMeaiAdapter_AreMeasured(string metric)
    {
        var generator = new StubEmbeddingGenerator(texts => texts.Select(_ => new[] { 0f, 1f, 0f }).ToList());

        var result = await Build(metric, new MEAIEmbeddingAdapter(generator)).EvaluateAsync(s_everyInput);

        Assert.True(result.Measured, $"{metric}: {result.Explanation}");
        Assert.Equal(100, result.Score, precision: 6);
    }

    // ── E7 ───────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Metrics))]
    public async Task ABadVector_IsNotCached(string metric)
    {
        // One bad reply (zero vectors) was cached, so the same texts stayed "not measured" for the life of the cache.
        var inner = new RecordingEmbeddings(
            _ => [new float[] { 0, 0, 0 }, new float[] { 0, 0, 0 }],
            _ => [new float[] { 1, 0, 0 }, new float[] { 1, 0, 0 }]);
        var cache = new CachingEmbeddingsDecorator(inner);
        var instance = Build(metric, cache);

        var first = await instance.EvaluateAsync(s_everyInput);
        var second = await instance.EvaluateAsync(s_everyInput);

        NoReading.Metric(first, $"{metric} / zero vectors");
        Assert.True(second.Measured, $"{metric}: the zero vectors were served from the cache ({second.Explanation})");
        Assert.Equal(2, inner.Calls);
        Assert.Equal(2, cache.CacheCount);
    }

    /// <summary>Presents an <see cref="IAgentEvalEmbeddings"/> as a Microsoft.Extensions.AI generator, for a metric that takes one.</summary>
    private sealed class EmbeddingsAsGenerator(IAgentEvalEmbeddings inner) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var vectors = await inner.GetEmbeddingsAsync(values, cancellationToken);
            return new GeneratedEmbeddings<Embedding<float>>(vectors.Select(v => new Embedding<float>(v)));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
