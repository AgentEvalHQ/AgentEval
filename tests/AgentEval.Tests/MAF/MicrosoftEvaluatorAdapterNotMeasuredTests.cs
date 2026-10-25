// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Adapters;
using AgentEval.Core;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;
using MeaiEvaluationContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;
using MeaiEvaluationResult = Microsoft.Extensions.AI.Evaluation.EvaluationResult;
using MeaiIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;

namespace AgentEval.Tests.MAF;

/// <summary>
/// T03 / T59: the <see cref="IMetric"/> path of <see cref="MicrosoftEvaluatorAdapter"/> turned an evaluator that gave no
/// reading — no metric, a null value, a metric type it cannot read, an exception, even the caller cancelling — into a
/// Fail at score 0. Its <c>IEval</c> path already reported them as errors. Now each is not measured (still
/// flagged indeterminate), and the caller's cancellation propagates.
/// </summary>
public class MicrosoftEvaluatorAdapterNotMeasuredTests
{
    private static readonly AgentEval.Core.EvaluationContext Context = new() { Input = "q", Output = "a" };

    /// <summary>An M.E.AI evaluator that answers with whatever <paramref name="answer"/> builds.</summary>
    private sealed class ScriptedEvaluator(Func<CancellationToken, MeaiEvaluationResult> answer) : MeaiIEvaluator
    {
        public IReadOnlyCollection<string> EvaluationMetricNames => ["Fluency"];

        public ValueTask<MeaiEvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse modelResponse,
            Microsoft.Extensions.AI.Evaluation.ChatConfiguration? chatConfiguration = null,
            IEnumerable<MeaiEvaluationContext>? additionalContext = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(answer(cancellationToken));
    }

    private static MeaiEvaluationResult With(Microsoft.Extensions.AI.Evaluation.EvaluationMetric metric)
    {
        var result = new MeaiEvaluationResult();
        result.Metrics[metric.Name] = metric;
        return result;
    }

    public static TheoryData<string> NoReadings() => new() { "no metric", "null numeric", "null boolean", "string metric" };

    [Theory]
    [MemberData(nameof(NoReadings))]
    public async Task AnEvaluatorThatGivesNoReading_IsNotMeasured(string kind)
    {
        MeaiEvaluationResult Answer(CancellationToken _) => kind switch
        {
            "no metric" => new MeaiEvaluationResult(),
            "null numeric" => With(new Microsoft.Extensions.AI.Evaluation.NumericMetric("Fluency", value: null)),
            "null boolean" => With(new Microsoft.Extensions.AI.Evaluation.BooleanMetric("Fluency", value: null)),
            _ => With(new Microsoft.Extensions.AI.Evaluation.StringMetric("Fluency", "high")),
        };
        var adapter = new MicrosoftEvaluatorAdapter(new ScriptedEvaluator(Answer), new FakeChatClient(), name: "Fluency");

        var result = await adapter.EvaluateAsync(Context);

        Assert.False(result.Measured, kind);
        Assert.False(result.Passed, kind);
        Assert.Equal(NotMeasuredCause.JudgeReplyUnreadable, result.NotMeasuredCause);
        Assert.True(result.Details!.TryGetValue("indeterminate", out var flag) && flag is true, kind);
    }

    [Fact]
    public async Task AnEvaluatorThatThrows_IsNotMeasured_WithTheError()
    {
        var adapter = new MicrosoftEvaluatorAdapter(
            new ScriptedEvaluator(_ => throw new HttpRequestException("503 Service Unavailable")), new FakeChatClient(), name: "Fluency");

        var result = await adapter.EvaluateAsync(Context);

        Assert.False(result.Measured);
        Assert.Equal(NotMeasuredCause.JudgeCallFailed, result.NotMeasuredCause);
        Assert.Equal("System.Net.Http.HttpRequestException", result.Details!["errorType"]);
        Assert.Equal("503 Service Unavailable", result.Details["error"]);   // the key it reported before
    }

    [Fact]
    public async Task TheCallersCancellation_Propagates_NotAFail()
    {
        // It was caught with every other exception and reported as a Fail at score 0.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var adapter = new MicrosoftEvaluatorAdapter(new ScriptedEvaluator(ct =>
        {
            ct.ThrowIfCancellationRequested();
            return new MeaiEvaluationResult();
        }), new FakeChatClient(), name: "Fluency");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.EvaluateAsync(Context, cts.Token));
    }

    [Fact]
    public async Task AReadableVerdict_IsStillMeasured()
    {
        // Guard: a boolean verdict reads as before.
        var adapter = new MicrosoftEvaluatorAdapter(
            new ScriptedEvaluator(_ => With(new Microsoft.Extensions.AI.Evaluation.BooleanMetric("Fluency", value: true))),
            new FakeChatClient(), name: "Fluency");

        var result = await adapter.EvaluateAsync(Context);

        Assert.True(result.Measured);
        Assert.True(result.Passed);
        Assert.Equal(100, result.Score);
    }
}
