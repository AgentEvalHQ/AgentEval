// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AgentEval.Calibration;
using AgentEval.Core;
using AgentEval.MAF.Evaluators;
using AgentEval.Metrics.Agentic;
using AgentEval.Metrics.RAG;
using AgentEval.Metrics.ResponsibleAI;
using AgentEval.Metrics.Safety;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

using EvaluationContext = AgentEval.Core.EvaluationContext;

namespace AgentEval.Tests.Metrics;

/// <summary>
/// T03: a judge reply the metric could not read was a Fail at score 0 — a measured failure, and on a planted bad case a
/// correct detection — in 9 metrics on the shared parser, toxicity, bias (both paths) and misinformation. A bare
/// <c>85</c> threw out of the first nine; <c>} x {</c>, a numeric severity or a non-string category threw out of the
/// other three. Now each is not measured, with the reply's proof (length, SHA-256, an excerpt, the model) in its details.
/// The scripted replies are reply shapes for the reader to read or refuse; no agent is graded by them.
/// </summary>
public sealed class JudgeReplyNotMeasuredTests
{
    internal static readonly EvaluationContext Full = new()
    {
        Input = "What is the capital of France?",
        Output = "Paris is the capital of France.",
        Context = "France's capital city is Paris.",
        GroundTruth = "Paris",
    };

    private static readonly EvaluationContext Counterfactual = new()
    {
        Input = "What is the capital of France? I am a woman.",
        Output = "Paris is the capital of France.",
    };

    /// <summary>Every path in Core on which an LLM judge's reply is read into a metric result.</summary>
    internal static readonly IReadOnlyDictionary<string, Func<IChatClient, CancellationToken, Task<MetricResult>>> JudgePaths =
        new Dictionary<string, Func<IChatClient, CancellationToken, Task<MetricResult>>>
        {
            ["llm_faithfulness"] = (c, ct) => new FaithfulnessMetric(c).EvaluateAsync(Full, ct),
            ["llm_relevance"] = (c, ct) => new RelevanceMetric(c).EvaluateAsync(Full, ct),
            ["llm_context_precision"] = (c, ct) => new ContextPrecisionMetric(c).EvaluateAsync(Full, ct),
            ["llm_context_recall"] = (c, ct) => new ContextRecallMetric(c).EvaluateAsync(Full, ct),
            ["llm_answer_correctness"] = (c, ct) => new AnswerCorrectnessMetric(c).EvaluateAsync(Full, ct),
            ["llm_groundedness"] = (c, ct) => new GroundednessMetric(c).EvaluateAsync(Full, ct),
            ["llm_coherence"] = (c, ct) => new CoherenceMetric(c).EvaluateAsync(Full, ct),
            ["llm_fluency"] = (c, ct) => new FluencyMetric(c).EvaluateAsync(Full, ct),
            ["llm_task_completion"] = (c, ct) => new TaskCompletionMetric(c).EvaluateAsync(Full, ct),
            ["llm_toxicity"] = (c, ct) => new ToxicityMetric(c).EvaluateAsync(Full, ct),
            ["llm_bias"] = (c, ct) => new BiasMetric(c).EvaluateAsync(Full, ct),
            ["llm_bias counterfactual"] = (c, ct) => new BiasMetric(c).EvaluateCounterfactualAsync(Full, Counterfactual, "gender", ct),
            ["llm_misinformation"] = (c, ct) => new MisinformationMetric(c).EvaluateAsync(Full, ct),
        };

    public static TheoryData<string, string> UnreadableReplies()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in JudgePaths.Keys)
        {
            foreach (var reply in new[]
                     {
                         "This is not JSON", """{"reasoning":"x"}""", """{"score":"high"}""", """{"score": 80,,}""",
                         "85", "} x {", """{"score":150}""",
                     })
            {
                data.Add(path, reply);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(UnreadableReplies))]
    public async Task AnUnreadableReply_IsNotMeasured_WithTheReplysProof(string path, string reply)
    {
        var result = await JudgePaths[path](new FakeChatClient(reply), CancellationToken.None);

        Assert.False(result.Measured, path);
        Assert.False(result.Passed, path);
        Assert.Equal(NotMeasuredCause.JudgeReplyUnreadable, result.NotMeasuredCause);
        Assert.Null(result.ModelGraded);   // no grade was given, so no instrument is claimed
        var details = Assert.IsAssignableFrom<IDictionary<string, object>>(result.Details);
        Assert.Equal(reply.Length, details["judgeReplyLength"]);
        Assert.Equal(Sha256(reply), details["judgeReplySha256"]);
        Assert.Equal(reply, details["judgeReplyExcerpt"]);
        Assert.Contains(Sha256(reply), result.Explanation, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> ReplyShapesTheOldReaderMissed()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in JudgePaths.Keys)
        {
            foreach (var reply in new[]
                     {
                         "<think>format {score:n}</think>{\"score\":85}", "{\"score\":85} Note: {see above}",
                         "```JSON\n{\"score\":85,}\n```",
                     })
            {
                data.Add(path, reply);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ReplyShapesTheOldReaderMissed))]
    public async Task AVerdictTheOldReaderMissed_IsRead(string path, string reply)
    {
        // T04: a reasoning block before the verdict, a note after it, an uppercase fence with a trailing comma. Each was
        // unreadable, so a Fail at 0.
        var result = await JudgePaths[path](new FakeChatClient(reply), CancellationToken.None);

        Assert.True(result.Measured, path);
        Assert.Equal(85, result.Score);
    }

    [Fact]
    public async Task ALongReply_KeepsTheFirst2000Characters_NeverHalfASurrogatePair()
    {
        var plain = new string('x', 5000);
        var plainResult = await new FaithfulnessMetric(new FakeChatClient(plain)).EvaluateAsync(Full);
        Assert.Equal(5000, plainResult.Details!["judgeReplyLength"]);
        Assert.Equal(2000, Assert.IsType<string>(plainResult.Details["judgeReplyExcerpt"]).Length);

        var emoji = new string('x', 1999) + "\U0001F600" + new string('y', 3000);   // the pair sits at 1999-2000
        var emojiResult = await new FaithfulnessMetric(new FakeChatClient(emoji)).EvaluateAsync(Full);
        Assert.Equal(emoji[..1999], emojiResult.Details!["judgeReplyExcerpt"]);
    }

    [Fact]
    public async Task TheJudgesModel_IsRecorded()
    {
        var result = await new FaithfulnessMetric(new ModelChatClient("nope", "judge-x")).EvaluateAsync(Full);

        Assert.False(result.Measured);
        Assert.Equal("judge-x", result.Details!["judgeModel"]);
        Assert.Contains("judge-x", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReplysText_StaysOutOfTheExplanation()
    {
        // The explanation reaches every report export (JUnit, TRX, JSON, CSV, Markdown); a judge's reply often quotes the
        // case. The reply is kept in the details only.
        var result = await new FaithfulnessMetric(new FakeChatClient("CANARY-7f3 is not json")).EvaluateAsync(Full);

        Assert.DoesNotContain("CANARY-7f3", result.Explanation, StringComparison.Ordinal);
        Assert.Contains("CANARY-7f3", Assert.IsType<string>(result.Details!["judgeReplyExcerpt"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AToxicityVerdictWithWrongTypedFields_IsMeasured_NotAnException()
    {
        // JsonElement.GetString() threw InvalidOperationException on the number in "categories" and on "severity": 3.
        var result = await new ToxicityMetric(new FakeChatClient(
            """{"score":90,"categories":[1],"severity":3,"reasoning":"ok"}""")).EvaluateAsync(Full);

        Assert.True(result.Measured);
        Assert.True(result.Passed);
        Assert.Equal(90, result.Score);
    }

    [Fact]
    public async Task ABiasVerdictWithANumericSeverity_IsMeasured_NotAnException()
    {
        var result = await new BiasMetric(new FakeChatClient("""{"score":90,"severity":3}""")).EvaluateAsync(Full);

        Assert.True(result.Measured);
        Assert.True(result.Passed);
        Assert.Equal(90, result.Score);
    }

    [Fact]
    public async Task ACounterfactualFlagInTheWrongType_IsMeasured_AndDoesNotClearTheResponse()
    {
        // JsonElement.GetBoolean() threw on "yes". Read as false it would pass a response the judge said was treated
        // differently; only an explicit false (or "no") clears it.
        var result = await new BiasMetric(new FakeChatClient("""{"score":90,"differentialTreatmentDetected":"yes"}"""))
            .EvaluateCounterfactualAsync(Full, Counterfactual, "gender");

        Assert.True(result.Measured);
        Assert.False(result.Passed);
        Assert.True(Assert.IsType<bool>(result.Details!["differentialTreatmentDetected"]));
    }

    [Fact]
    public async Task EveryJudgeMetricInCore_ReportsAnUnreadableReplyAsNotMeasured()
    {
        // Census: a judge metric added later cannot slip back to a Fail at 0.
        var metrics = JudgeMetrics(() => new FakeChatClient("not json"));
        Assert.True(metrics.Count >= 12, $"found {metrics.Count} judge metrics; the census would pass vacuously");

        foreach (var metric in metrics)
        {
            var result = await metric.EvaluateAsync(Full);
            Assert.False(result.Measured, metric.GetType().Name);
            Assert.Equal(NotMeasuredCause.JudgeReplyUnreadable, result.NotMeasuredCause);
        }
    }

    [Fact]
    public async Task TheMeaiBridge_GivesAnUnreadableReplyNoValue()
    {
        // As a Fail at 0 it became a measured MEAI value of 1.0, MEAI's lowest score, rated Poor.
        var result = await new FaithfulnessMetric(new FakeChatClient("not json")).EvaluateAsync(Full);

        var metric = (NumericMetric)ResultConverter.ToMEAI(result).Metrics["llm_faithfulness"];

        Assert.Null(metric.Value);
        Assert.Equal(EvaluationRating.Inconclusive, metric.Interpretation!.Rating);
        Assert.True(metric.Interpretation.Failed);
    }

    [Fact]
    public async Task ACalibratedJudge_DoesNotCountAnUnreadableReplyAsAZeroVote()
    {
        var clients = new Dictionary<string, IChatClient>
        {
            ["garbage"] = new FakeChatClient("not json"),
            ["a"] = new FakeChatClient("""{"score": 80, "reasoning": "ok"}"""),
            ["b"] = new FakeChatClient("""{"score": 80, "reasoning": "ok"}"""),
        };
        var judge = new CalibratedJudge(clients.Select(kv => (kv.Key, kv.Value)).ToArray());

        var result = await judge.EvaluateAsync(Full, name => new FaithfulnessMetric(clients[name]));

        Assert.Equal(2, result.JudgeScores.Count);
        Assert.DoesNotContain("garbage", result.JudgeScores.Keys);
        Assert.Equal(80, result.Score);
    }

    /// <summary>Every non-abstract <see cref="IMetric"/> in Core whose public constructor takes an <see cref="IChatClient"/> first.</summary>
    internal static List<IMetric> JudgeMetrics(Func<IChatClient> client) =>
        typeof(FaithfulnessMetric).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IMetric).IsAssignableFrom(t))
            .Select(t => t.GetConstructors().FirstOrDefault(c =>
                c.GetParameters() is { Length: > 0 } parameters && parameters[0].ParameterType == typeof(IChatClient)))
            .OfType<ConstructorInfo>()
            .Select(ctor => (IMetric)ctor.Invoke(ctor.GetParameters()
                .Select((p, i) => i == 0 ? client() : p.HasDefaultValue ? p.DefaultValue : null)
                .ToArray()))
            .ToList();

    internal static string Sha256(string text) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Answers every call with <paramref name="reply"/>, reporting <paramref name="modelId"/> as the model.</summary>
    private sealed class ModelChatClient(string reply, string modelId) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)) { ModelId = modelId });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
