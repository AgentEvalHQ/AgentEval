// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using System.Text.Json;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Calibration;
using AgentEval.Evals.Agentic.Conversation;
using AgentEval.Evals.Agentic.Memory;
using AgentEval.Evals.Agentic.MultiTurn;
using AgentEval.Evals.Agentic.Quality;
using AgentEval.Evals.Agentic.Reasoning;
using AgentEval.Prompts;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentEval.Tests.Evals.RubricJudgeTests;

namespace AgentEval.Tests.Prompts;

public sealed class LocalizedWrapperEvidenceTests
{
    public static IEnumerable<object[]> Wrappers => new[]
    {
        "similarity", "response_completeness", "confidence_calibration", "self_correction_quality",
        "memory_recall_accuracy", "long_conversation_coherence", "goal_tracking", "turn_coherence",
        "clarification_appropriateness", "reasoning_correctness", "plan_formulation_quality", "goal_decomposition_quality"
    }.SelectMany(key => new[] { new object[] { key, false }, [key, true] });

    [Theory]
    [MemberData(nameof(Wrappers))]
    public async Task Evaluate_ConfiguredWrapper_PreservesFactsWithoutAppendingEnglishInstructions(string key, bool configureJudge)
    {
        JudgePromptDefinition? definition = null;
        using var client = new RecordingChatClient(_ => Reply(definition!));
        var judge = new ChatClientEvaluator(client);
        definition = Assert.Single(BuiltInJudgePrompts.ForEval(Create(key, judge))) with { Locale = "sv-SE" };
        var options = new JudgePromptOptions { Locale = "sv-SE", Override = definition };
        var eval = Create(key, configureJudge ? judge.WithPrompts(options) : judge);
        var input = new EvalInput("Fråga", "Ursprungligt svar", Context: "Bevarat underlag", GroundTruth: "Unik referens")
        {
            JudgePrompts = configureJudge ? null : options,
            Metadata = new Dictionary<string, object>
            {
                [ConversationHistoryHelper.MetadataKey] = new ConversationTurn[] { new("user", "Tidigare fråga"), new("assistant", "Senaste svar") },
                [ConversationHistoryHelper.CorrectionTurnMetadataKey] = new ConversationTurn("assistant", "Rättat svar"),
                [ReasoningCorrectnessEval.MetadataKey] = "Observerad motivering",
                [PlanFormulationQualityEval.MetadataKey] = "Observerad plan"
            }
        };
        var result = await eval.EvaluateAsync(input);
        Assert.True(result.Score.Passed);
        Assert.Equal("sv-SE", result.Provenance.JudgePrompt!.EffectiveLocale);
        var user = string.Join("\n", Assert.Single(client.Calls).Where(m => m.Role == ChatRole.User).Select(m => m.Text));
        Assert.Contains("Bevarat underlag", user);
        Assert.Equal(1, user.Split("Unik referens", StringSplitOptions.None).Length - 1);
        foreach (var legacy in new[] { "Ground-truth reference answer:", "[Ground truth reference:", "=== Conversation history ===",
                     "=== Previous turn ===", "Current user query:", "=== Original exchange ===", "Evaluate the agent's self-correction response above.",
                     "[Agent reasoning trace]", "[Agent plan]", "[Agent decomposition]" })
            Assert.DoesNotContain(legacy, user, StringComparison.Ordinal);
        if (key is "memory_recall_accuracy" or "long_conversation_coherence" or "goal_tracking" or "turn_coherence" or "clarification_appropriateness")
        {
            Assert.Contains("\"conversationHistory\"", user);
            Assert.Contains("Senaste svar", user);
            if (key == "turn_coherence") Assert.DoesNotContain("Tidigare fråga", user);
            else Assert.Contains("Tidigare fråga", user);
        }
        if (key == "self_correction_quality")
        {
            Assert.Contains("Ursprungligt svar", user);
            Assert.Contains("Rättat svar", user);
        }
        if (key == "reasoning_correctness") Assert.Contains("Observerad motivering", user);
        if (key is "plan_formulation_quality" or "goal_decomposition_quality") Assert.Contains("Observerad plan", user);
    }

    [Theory]
    [InlineData("reasoning_correctness")]
    [InlineData("plan_formulation_quality")]
    [InlineData("goal_decomposition_quality")]
    public async Task Evaluate_ConfiguredSwedishResponse_BypassesEnglishExtractionMiss(string key)
    {
        JudgePromptDefinition? definition = null;
        using var client = new RecordingChatClient(_ => Reply(definition!));
        var judge = new ChatClientEvaluator(client);
        var eval = Create(key, judge);
        definition = Assert.Single(BuiltInJudgePrompts.ForEval(eval)) with { Locale = "sv-SE" };
        var input = new EvalInput("Hur ska vi arbeta?", "Börja med undersökningen, jämför resultaten och redovisa slutsatsen.");
        var legacy = await eval.EvaluateAsync(input);
        Assert.Empty(client.Calls);
        Assert.Equal("skipped", legacy.Score.Label);
        var localized = await eval.EvaluateAsync(input with { JudgePrompts = new() { Locale = "sv-SE", Override = definition } });
        Assert.Single(client.Calls);
        Assert.True(localized.Score.Passed);
    }

    [Fact]
    public async Task CalibrationDataset_DeclaredRichEvidence_SurvivesJsonLoadingAndInputConversion()
    {
        var entry = new CalibrationEntry("fixture", "plan_formulation_quality", "Fråga", "Svar", "pass", .8, 1, "AI-authored fixture")
        {
            ToolDefinitions = [new("search", "Sök", null)], ExpectedActions = [new("Sök i källan", ["search"])],
            CorrectionTurn = new("assistant", "Rättelse"), ReasoningTrace = "Motivering", Plan = "Plantext"
        };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        var loaded = Assert.Single((await new CalibrationDatasetLoader().LoadAsync("fixture", stream)).Entries).ToEvalInput();
        Assert.Equal("search", Assert.Single(loaded.ToolDefinitions!).Name);
        Assert.Equal("Sök i källan", Assert.Single(loaded.ExpectedActions!).Description);
        Assert.Equal(entry.CorrectionTurn, ConversationHistoryHelper.TryGetCorrectionTurn(loaded));
        Assert.Equal("Motivering", loaded.Metadata![ReasoningCorrectnessEval.MetadataKey]);
        Assert.Equal("Plantext", loaded.Metadata[PlanFormulationQualityEval.MetadataKey]);
    }

    private static IEval Create(string key, IEvaluator judge) => key switch
    {
        "similarity" => new SimilarityEval(judge), "response_completeness" => new ResponseCompletenessEval(judge),
        "confidence_calibration" => new ConfidenceCalibrationEval(judge), "self_correction_quality" => new SelfCorrectionQualityEval(judge),
        "memory_recall_accuracy" => new MemoryRecallAccuracyEval(judge), "long_conversation_coherence" => new LongConversationCoherenceEval(judge),
        "goal_tracking" => new GoalTrackingEval(judge), "turn_coherence" => new TurnCoherenceEval(judge),
        "clarification_appropriateness" => new ClarificationAppropriatenessEval(judge), "reasoning_correctness" => new ReasoningCorrectnessEval(judge),
        "plan_formulation_quality" => new PlanFormulationQualityEval(judge), "goal_decomposition_quality" => new GoalDecompositionQualityEval(judge),
        _ => throw new ArgumentException("Unknown test evaluator", nameof(key))
    };

    private static string Reply(JudgePromptDefinition definition) => definition.ResponseContract == JudgeResponseContract.CriteriaV1
        ? JsonSerializer.Serialize(new { overallScore = 100, summary = "Fixture", improvements = Array.Empty<string>(),
            criteriaResults = definition.Criteria.Select(c => new { id = c.Id, met = true, explanation = "Fixture" }) })
        : JsonSerializer.Serialize(new { score = definition.Scale == RubricScoreScale.Unit ? 1 : 100, reasoning = "Fixture" });
}
