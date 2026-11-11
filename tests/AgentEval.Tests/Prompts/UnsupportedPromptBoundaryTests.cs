// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Adapters;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Metrics.Agentic;
using AgentEval.Metrics.ResponsibleAI;
using AgentEval.Metrics.Safety;
using AgentEval.Prompts;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests.Prompts;

public sealed class UnsupportedPromptBoundaryTests
{
    [Theory]
    [InlineData("groundedness")]
    [InlineData("coherence")]
    [InlineData("fluency")]
    [InlineData("bias")]
    [InlineData("misinformation")]
    [InlineData("task_completion")]
    [InlineData("toxicity")]
    [InlineData("code_toxicity")]
    public async Task Evaluate_UnconvertedMetricWithExplicitPromptSelection_RejectsBeforeProviderOrPrefilter(string key)
    {
        using var client = new FakeChatClient("""{"score":90,"reasoning":"good"}""");
        IMetric metric = key switch
        {
            "groundedness" => new GroundednessMetric(client),
            "coherence" => new CoherenceMetric(client),
            "fluency" => new FluencyMetric(client),
            "bias" => new BiasMetric(client),
            "misinformation" => new MisinformationMetric(client),
            "task_completion" => new TaskCompletionMetric(client),
            "toxicity" => new ToxicityMetric(client),
            _ => new ToxicityMetric()
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => metric.EvaluateAsync(new()
        {
            Input = "Fråga", Output = "Ett kort svar.", Context = "Underlag", GroundTruth = "Referens",
            JudgePrompts = new() { Locale = "sv-SE" }
        }));
        Assert.Empty(client.ReceivedMessages);
    }

    [Fact]
    public async Task Evaluate_CodeOnlyEnglishPrefilter_AllowsSharedEnglishOptions()
    {
        var result = await new ToxicityMetric().EvaluateAsync(new()
        {
            Input = "Question", Output = "A helpful answer.",
            JudgePrompts = new() { Locale = "en-GB", Pack = new([BuiltInJudgePrompts.GetRag("llm_faithfulness")]) }
        });
        Assert.True(result.Measured);
        Assert.False(result.ModelGraded);
    }

    [Theory]
    [InlineData("override")]
    [InlineData("pack")]
    [InlineData("fallback")]
    public async Task Evaluate_CodeOnlyEnglishPrefilter_RejectsDefinitionThatTargetsItsRules(string source)
    {
        var unsupported = BuiltInJudgePrompts.GetRag("llm_faithfulness") with { EvaluatorKey = "code_toxicity" };
        var options = source switch
        {
            "override" => new JudgePromptOptions { Override = unsupported },
            "pack" => new JudgePromptOptions { Pack = new([unsupported]) },
            _ => new JudgePromptOptions { Locale = "en-GB", FallbackLocale = "en", Pack = new([unsupported]) }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => new ToxicityMetric().EvaluateAsync(new()
        {
            Input = "Question", Output = "A helpful answer.", JudgePrompts = options
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evaluate_UpstreamMicrosoftJudge_RejectsUnsupportedPackInBothContracts(bool canonical)
    {
        using var client = new FakeChatClient("4");
        var adapter = MicrosoftEvaluatorAdapter.CreateRelevanceEvaluator(client);
        var prompts = new JudgePromptOptions { Locale = "sv-SE" };
        if (canonical)
            await Assert.ThrowsAsync<NotSupportedException>(() => adapter.EvaluateAsync(new EvalInput("Fråga", "Svar") { JudgePrompts = prompts }));
        else
            await Assert.ThrowsAsync<NotSupportedException>(() => adapter.EvaluateAsync(new EvaluationContext
            { Input = "Fråga", Output = "Svar", JudgePrompts = prompts }));
        Assert.Empty(client.ReceivedMessages);
    }
}
