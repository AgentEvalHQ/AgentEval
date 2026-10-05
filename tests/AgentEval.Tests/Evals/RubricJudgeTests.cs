// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// A check with a rubric grades with it (#203 review, B9). Before, every agentic LLM check sent a six-line default prompt
/// and read any reply's score as 0–100, so a 0–1 rubric's 0.85 would have read as 0.85 out of 100. These tests drive
/// <see cref="AtomicLlmEval"/> through a real <see cref="ChatClientEvaluator"/> over a recording chat client.
/// </summary>
public class RubricJudgeTests
{
    /// <summary>A chat client that records what it was sent and answers with a fixed reply.</summary>
    internal sealed class RecordingChatClient(Func<IList<ChatMessage>, string> reply) : IChatClient
    {
        public List<IList<ChatMessage>> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var sent = messages.ToList();
            lock (Calls) Calls.Add(sent);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply(sent))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static readonly EvalRubric UnitRubric = new()
    {
        Id = "test.rubric.unit.v1",
        Text = "UNIT RUBRIC: score 0.0–1.0; pass >= 0.80; needs_review >= 0.50.",
        Scale = RubricScoreScale.Unit,
        PassAt = 0.80,
        ReviewAt = 0.50,
        SeverityBands = [new(0.80, "none"), new(0.50, "low"), new(0.25, "high"), new(0.0, "critical")],
    };

    private static readonly EvalRubric PercentRubric = new()
    {
        Id = "test.rubric.percent.v1",
        Text = "PERCENT RUBRIC: integer 0-100; pass >= 75; needs_review >= 45.",
        Scale = RubricScoreScale.Percent,
        PassAt = 0.75,
        ReviewAt = 0.45,
    };

    private static readonly EvalRubric DimensionalRubric = new()
    {
        Id = "test.rubric.dimensional.v1",
        Text = "DIMENSIONAL RUBRIC: grade the named dimension.",
        Scale = RubricScoreScale.Percent,
        Dimensional = true,
    };

    static RubricJudgeTests()
    {
        EvalRubrics.Register(UnitRubric);
        EvalRubrics.Register(PercentRubric);
        EvalRubrics.Register(DimensionalRubric);
    }

    private static string Reply(string score, string label = "pass") =>
        $$"""{"score": {{score}}, "label": "{{label}}", "reasoning": "because", "criteria_results": [{"criterion": "Is good", "met": true, "explanation": "it is"}], "evidence": [{"source": "response", "reference": "r", "message": "seen"}]}""";

    private static (AtomicLlmEval Eval, RecordingChatClient Client) Leaf(EvalRubric rubric, string reply, double threshold, string key = "k",
        string? failureSeverity = null)
    {
        var client = new RecordingChatClient(_ => reply);
        var eval = new AtomicLlmEval(new ChatClientEvaluator(client), key, "n", "c", "1.0.0", ["Is good"],
            passThreshold: threshold, promptId: rubric.Id, failureSeverity: failureSeverity);
        return (eval, client);
    }

    private static Task<EvalResult> Run(AtomicLlmEval eval) => eval.EvaluateAsync(new EvalInput(Query: "q", Response: "r"));

    [Fact]
    public async Task TheRubric_IsTheSystemPromptSent_AndItsIdIsRecorded()
    {
        var (eval, client) = Leaf(UnitRubric, Reply("0.9"), 0.80);

        var result = await Run(eval);

        var system = Assert.Single(client.Calls).Single(m => m.Role == ChatRole.System);
        Assert.Equal(UnitRubric.Text, system.Text);
        Assert.Equal(UnitRubric.Id, result.Provenance.PromptId);
    }

    [Theory]
    // 0–1 rubric
    [InlineData("unit", "0.85", 0.85, "pass")]
    [InlineData("unit", "0.80", 0.80, "pass")]          // the boundary passes
    [InlineData("unit", "0.79", 0.79, "warn")]          // the needs-review band: not passed, not failed
    [InlineData("unit", "0.50", 0.50, "warn")]
    [InlineData("unit", "0.49", 0.49, "fail")]
    // 0–100 rubric
    [InlineData("percent", "85", 0.85, "pass")]
    [InlineData("percent", "60", 0.60, "warn")]
    [InlineData("percent", "44", 0.44, "fail")]
    public async Task TheVerdict_IsTheBandOfTheScore_ReadOnTheRubricsScale(string scale, string score, double value, string label)
    {
        var rubric = scale == "unit" ? UnitRubric : PercentRubric;
        var (eval, _) = Leaf(rubric, Reply(score), rubric.PassAt!.Value);

        var result = await Run(eval);

        Assert.Equal(label, result.Score.Label);
        Assert.Equal(label == "pass", result.Score.Passed);
        Assert.Equal(value, result.Score.Value, 6);
    }

    [Theory]
    [InlineData("unit", "85")]        // a 0–100 score sent to a 0–1 rubric: before B9 this read as 0.85/100 = fail
    [InlineData("unit", "1.5")]
    [InlineData("unit", "-0.1")]
    [InlineData("percent", "0.85")]   // a 0–1 score sent to a 0–100 rubric
    [InlineData("percent", "101")]
    public async Task AScoreOffTheRubricsScale_IsAnError_NeverAGrade(string scale, string score)
    {
        var rubric = scale == "unit" ? UnitRubric : PercentRubric;
        var (eval, client) = Leaf(rubric, Reply(score), rubric.PassAt!.Value);

        var result = await Run(eval);

        Assert.Equal("error", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(2, client.Calls.Count);   // the one corrective retry, as for any unparseable reply
    }

    [Theory]
    [InlineData("""{"label": "pass", "reasoning": "no score"}""")]
    [InlineData("not json at all")]
    public async Task AReplyWithoutAScore_IsAnError(string reply)
    {
        var (eval, _) = Leaf(UnitRubric, reply, 0.80);

        Assert.Equal("error", (await Run(eval)).Score.Label);
    }

    [Theory]
    [InlineData("0.90", "none")]
    [InlineData("0.60", "low")]       // needs review: the rubric's own severity, not the check's failure severity
    [InlineData("0.30", "high")]
    [InlineData("0.10", "critical")]
    public async Task TheSeverity_IsTheRubricsOwn_WhenItHasATable(string score, string severity)
    {
        var (eval, _) = Leaf(UnitRubric, Reply(score), 0.80, failureSeverity: "critical");

        Assert.Equal(severity, (await Run(eval)).Score.Severity);
    }

    [Fact]
    public async Task WithoutASeverityTable_ANeedsReviewIsMedium_AndAFailureKeepsTheChecksSeverity()
    {
        var (review, _) = Leaf(PercentRubric, Reply("60"), 0.75, failureSeverity: "critical");
        var (fail, _) = Leaf(PercentRubric, Reply("10"), 0.75, failureSeverity: "critical");

        Assert.Equal("medium", (await Run(review)).Score.Severity);
        Assert.Equal("critical", (await Run(fail)).Score.Severity);
    }

    [Fact]
    public async Task TheJudgesOwnLabel_IsEvidence_AndADisagreementIsRecorded()
    {
        var (eval, _) = Leaf(UnitRubric, Reply("0.90", label: "fail"), 0.80);

        var result = await Run(eval);

        Assert.Equal("pass", result.Score.Label);   // the score decides
        Assert.Contains(result.Details.Evidence!, e => e.Source == "judge-label" && e.Message.Contains("'fail'"));
        Assert.Contains(result.Details.Evidence!, e => e.Source == "judge:response" && e.Message == "seen");
        Assert.Equal("because", result.Details.Summary);   // the rubric's "reasoning"
    }

    [Fact]
    public async Task ADimensionalRubric_NamesTheLeafsDimension_AndEachDimensionHashesApart()
    {
        var (goal, goalClient) = Leaf(DimensionalRubric, Reply("90"), 0.70, key: "goal_adherence");
        var (rule, _) = Leaf(DimensionalRubric, Reply("90"), 0.70, key: "rule_adherence");

        var goalResult = await Run(goal);
        var ruleResult = await Run(rule);

        var user = Assert.Single(goalClient.Calls).Single(m => m.Role == ChatRole.User).Text;
        Assert.Contains("DIMENSION TO EVALUATE: goal_adherence", user);
        Assert.NotEqual(goalResult.Provenance.PromptHash, ruleResult.Provenance.PromptHash);
    }

    [Fact]
    public async Task ANonDimensionalRubric_SendsNoDimensionLine()
    {
        var (eval, client) = Leaf(UnitRubric, Reply("0.9"), 0.80, key: "goal_adherence");

        await Run(eval);

        Assert.DoesNotContain("DIMENSION TO EVALUATE", Assert.Single(client.Calls).Single(m => m.Role == ChatRole.User).Text);
    }

    [Fact]
    public async Task BindingTheRubric_MovesThePromptHash()
    {
        // The same check on the same judge, with and without the rubric registered under its prompt id.
        var bound = await Run(Leaf(UnitRubric, Reply("0.9"), 0.80).Eval);
        var unboundEval = new AtomicLlmEval(new ChatClientEvaluator(new RecordingChatClient(_ => """{"overallScore": 90}""")),
            "k", "n", "c", "1.0.0", ["Is good"], passThreshold: 0.80, promptId: "test.rubric.not-registered.v1");
        var unbound = await Run(unboundEval);

        Assert.NotEqual(bound.Provenance.PromptHash, unbound.Provenance.PromptHash);
        Assert.Equal(ChatClientEvaluator.DefaultSystemPromptId, unbound.Provenance.PromptId);
    }

    [Fact]
    public async Task AJudgeThatCannotBind_IsUsedAsItIs()
    {
        // A test fake or custom IEvaluator: no rubric is sent, and the 0–100 reading is unchanged.
        var eval = new AtomicLlmEval(new AgentEval.Tests.Agentic.FixedScoreEvaluator(85), "k", "n", "c", "1.0.0", ["Is good"],
            passThreshold: 0.80, promptId: UnitRubric.Id);

        var result = await Run(eval);

        Assert.Equal("pass", result.Score.Label);
        Assert.Equal(0.85, result.Score.Value, 6);
    }

    [Fact]
    public void TheDefaultPromptsReading_IsUnchanged()
    {
        // Guard: without a rubric, a reply's score is still read as 0–100 and no rubric field is set.
        var parsed = ChatClientEvaluator.ParseEvaluationResponse("""{"overallScore": 85, "summary": "s"}""");

        Assert.Equal(85, parsed.OverallScore);
        Assert.Null(parsed.RubricScore);
        Assert.False(parsed.EvaluationFailed);
    }

    [Fact]
    public void RegisteringADifferentRubricUnderAnExistingId_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => EvalRubrics.Register(UnitRubric with { Text = "something else" }));
        EvalRubrics.Register(UnitRubric with { });   // identical content is a no-op
    }

    [Theory]
    [InlineData(0.9, 0.95)]    // review band at or above the pass boundary
    [InlineData(1.5, null)]    // off the scale
    public void AnInconsistentRubric_IsRefused(double passAt, double? reviewAt)
    {
        var rubric = UnitRubric with { Id = "test.rubric.bad.v1", PassAt = passAt, ReviewAt = reviewAt };

        Assert.ThrowsAny<ArgumentException>(rubric.Validate);
    }
}
