// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Adapters;
using AgentEval.Evals;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;
using MeaiEvaluationContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;
using MeaiEvaluationResult = Microsoft.Extensions.AI.Evaluation.EvaluationResult;
using MeaiIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;

namespace AgentEval.Tests.MAF;

/// <summary>
/// T82: <see cref="MicrosoftEvaluatorAdapter"/>'s token total (<see cref="EvalProvenance.TokensUsed"/>) is complete or
/// absent, like its cost. A judge call that reported only one of its two counts already left the cost not priced, but
/// the total summed what was reported and read as the whole spend.
/// </summary>
public sealed class MicrosoftEvaluatorAdapterTokenTotalTests
{
    // Makes the given number of judge calls through the configuration, as the first-party evaluators do, then returns
    // one metric. Only the usage reporting is under test, never the grade.
    private sealed class JudgeCallingEvaluator(int calls) : MeaiIEvaluator
    {
        public IReadOnlyCollection<string> EvaluationMetricNames => new[] { "Fluency" };

        public async ValueTask<MeaiEvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse modelResponse,
            Microsoft.Extensions.AI.Evaluation.ChatConfiguration? chatConfiguration = null,
            IEnumerable<MeaiEvaluationContext>? additionalContext = null,
            CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < calls && chatConfiguration is not null; i++)
            {
                await chatConfiguration.ChatClient.GetResponseAsync(
                    new[] { new ChatMessage(ChatRole.User, "grade this") }, cancellationToken: cancellationToken);
            }

            var result = new MeaiEvaluationResult();
            result.Metrics["Fluency"] = new Microsoft.Extensions.AI.Evaluation.NumericMetric("Fluency", 4);
            return result;
        }
    }

    private static readonly EvalInput Input = new(Query: "Describe the weather.", Response: "It is sunny and mild today.");

    private static Task<EvalResult> EvaluateAsync(ScriptedChatClient judge, int calls) =>
        ((IEval)new MicrosoftEvaluatorAdapter(new JudgeCallingEvaluator(calls), judge, name: "Fluency")
        {
            JudgeModel = "gpt-4o-mini",
        }).EvaluateAsync(Input);

    [Fact]
    public async Task OneCountMissing_HasNoTokenTotal()
    {
        var judge = new ScriptedChatClient().AddText("{\"score\": 4}", inTok: 1000, outTok: null);

        var result = await EvaluateAsync(judge, calls: 1);

        Assert.Null(result.Provenance.TokensUsed);      // was 1000: the input count alone
        Assert.Null(result.Provenance.EstimatedCost);
    }

    [Fact]
    public async Task OneCallOfTwoMissingACount_HasNoTokenTotal()
    {
        // The first call is complete; the second reports no output count. Summed, they read as 2200 tokens spent.
        var judge = new ScriptedChatClient()
            .AddText("{\"score\": 4}", inTok: 1000, outTok: 200)
            .AddText("{\"score\": 4}", inTok: 1000, outTok: null);

        var result = await EvaluateAsync(judge, calls: 2);

        Assert.Null(result.Provenance.TokensUsed);
        Assert.Null(result.Provenance.EstimatedCost);
    }

    [Fact]
    public async Task EveryCallComplete_TotalIsTheSumOverCalls()
    {
        // Guard: two complete calls are a complete total.
        var judge = new ScriptedChatClient()
            .AddText("{\"score\": 4}", inTok: 1000, outTok: 200)
            .AddText("{\"score\": 4}", inTok: 500, outTok: 100);

        var result = await EvaluateAsync(judge, calls: 2);

        Assert.Equal(1800, result.Provenance.TokensUsed);
        Assert.Equal(JudgeCostMap.EstimateCost("gpt-4o-mini", 1500, 300)!.Value, result.Provenance.EstimatedCost!.Value, precision: 12);
    }
}
