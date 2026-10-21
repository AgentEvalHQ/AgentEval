// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Process;
using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// A leaf that cannot be graded without some run data is not measured when that data was not captured, and its judge is
/// not called (owner's decision, 2026-10-21). Before, task adherence's rule judges graded "the system rules specified in
/// the system message" with no system message, and the tool judges graded tool use from the reply text when no tool
/// call was captured; each score read as a measurement. Every test here except the guards fails on the old code (the
/// needs did not exist, so every judge was called).
/// </summary>
public class AtomicLlmEvalJudgeNeedsTests
{
    /// <summary>Records every judge input. Its reply is never asserted on.</summary>
    private sealed class RecordingJudge : IEvaluator
    {
        public List<string> Inputs { get; } = [];

        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            Inputs.Add(input);
            return Task.FromResult(new EvaluationResult { OverallScore = 100, Summary = "ok" });
        }
    }

    private static AtomicLlmEval Leaf(IEvaluator judge, JudgeToolData needs, JudgeToolData sees = JudgeToolData.None) =>
        new(judge, "k", "Leaf Name", "c", "1.0.0", ["criterion"]) { JudgeSeesToolData = sees, JudgeNeedsData = needs };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ALeafThatNeedsTheInstructions_WithNoneCaptured_IsNotMeasured_AndAsksNoJudge(string? systemMessage)
    {
        var judge = new RecordingJudge();

        var result = await Leaf(judge, JudgeToolData.SystemMessage).EvaluateAsync(new EvalInput("Q", "A", SystemMessage: systemMessage));

        Assert.Equal("skipped", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.NotMeasured, result.Score.CensusBucket());
        Assert.Contains("No system message was captured", result.Details.Summary!, StringComparison.Ordinal);
        Assert.Contains("Leaf Name was not measured", result.Details.Summary!, StringComparison.Ordinal);
        Assert.Empty(judge.Inputs);
    }

    [Fact]
    public async Task ALeafThatNeedsTheInstructions_IsSentThem_WhenCaptured_EvenIfItDoesNotListThemAsSeen()
    {
        // A need implies the data is sent: a leaf graded against the instructions is never graded without them.
        var judge = new RecordingJudge();

        var result = await Leaf(judge, JudgeToolData.SystemMessage).EvaluateAsync(new EvalInput("Q", "A", SystemMessage: "SYS-4417 be brief"));

        Assert.Equal("pass", result.Score.Label);
        Assert.Contains("<agent_instructions>\nSYS-4417 be brief\n</agent_instructions>", Assert.Single(judge.Inputs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALeafThatNeedsTheToolCalls_WithNoneCaptured_IsNotMeasured_AndAsksNoJudge()
    {
        var judge = new RecordingJudge();

        var result = await Leaf(judge, JudgeToolData.ToolCalls).EvaluateAsync(new EvalInput("Q", "A"));

        Assert.Equal("skipped", result.Score.Label);
        Assert.Contains("No tool-call data was captured", result.Details.Summary!, StringComparison.Ordinal);
        Assert.Empty(judge.Inputs);
    }

    [Fact]
    public async Task ACapturedEmptyToolCallList_IsData_AndIsJudged()
    {
        // Guard: an empty list says the calls were recorded and none were made, which a judge can grade.
        var judge = new RecordingJudge();

        var result = await Leaf(judge, JudgeToolData.ToolCalls).EvaluateAsync(new EvalInput("Q", "A", ToolCalls: []));

        Assert.Equal("pass", result.Score.Label);
        Assert.Contains("Tool calls the agent made: none.", Assert.Single(judge.Inputs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryMissingNeed_IsNamed()
    {
        var judge = new RecordingJudge();
        var leaf = Leaf(judge, JudgeToolData.ToolCalls | JudgeToolData.ToolDefinitions | JudgeToolData.SystemMessage | JudgeToolData.ExpectedActions);

        var result = await leaf.EvaluateAsync(new EvalInput("Q", "A", ExpectedActions: []));

        foreach (var named in new[] { "EvalInput.ToolCalls", "EvalInput.ToolDefinitions", "EvalInput.SystemMessage", "EvalInput.ExpectedActions" })
            Assert.Contains(named, result.Details.Summary!, StringComparison.Ordinal);
        Assert.Empty(judge.Inputs);
    }

    [Fact]
    public async Task ALeafWithNoNeeds_GradesWhateverWasCaptured_AsBefore()
    {
        // Guard: the default is unchanged.
        var judge = new RecordingJudge();

        var result = await Leaf(judge, JudgeToolData.None, sees: JudgeToolData.ToolCalls | JudgeToolData.SystemMessage)
            .EvaluateAsync(new EvalInput("Q", "A"));

        Assert.Equal("pass", result.Score.Label);
        Assert.Equal("Q", Assert.Single(judge.Inputs));
    }

    [Fact]
    public void ANeedThatIsAlsoSeen_KeepsTheFingerprintOfALeafThatOnlySeesIt()
    {
        // What the judge is sent decides the fingerprint; declaring that it cannot be graded without it does not change
        // what it is sent, so it is the same instrument when the data is there.
        var judge = new RecordingJudge();
        var seesOnly = Leaf(judge, JudgeToolData.None, sees: JudgeToolData.SystemMessage).CaptureDefinition();
        var needsToo = Leaf(judge, JudgeToolData.SystemMessage, sees: JudgeToolData.SystemMessage).CaptureDefinition();
        var needsOnly = Leaf(judge, JudgeToolData.SystemMessage).CaptureDefinition();

        Assert.Equal(seesOnly.RubricDigest, needsToo.RubricDigest);
        Assert.Equal(seesOnly.RubricDigest, needsOnly.RubricDigest);
        Assert.Equal(JudgeToolData.SystemMessage, needsOnly.ToolData);
    }

    // ── The shipped tool evaluators (T12 part C) ─────────────────────────────

    public static TheoryData<string> ToolEvaluators => new()
    {
        "tool_selection", "tool_output_utilization", "tool_efficiency", "tool_call_success",
    };

    private static IEval Build(string key, IEvaluator judge) => key switch
    {
        "tool_selection" => new ToolSelectionEval(judge),
        "tool_output_utilization" => new ToolOutputUtilizationEval(judge),
        "tool_efficiency" => new ToolEfficiencyEval(judge),
        "tool_call_success" => new ToolCallSuccessEval(judge),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    [Theory]
    [MemberData(nameof(ToolEvaluators))]
    public async Task EveryToolJudge_WithNoToolCallDataCaptured_IsNotMeasured_AndIsNotAsked(string key)
    {
        // They graded "I called search_flights." as tool use: a judge at 100 passed a run nothing recorded.
        var judge = new RecordingJudge();

        var result = await Build(key, judge).EvaluateAsync(new EvalInput("Find flights to Paris", "I called search_flights."));

        Assert.Equal("skipped", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.NotMeasured, result.Score.CensusBucket());
        Assert.Contains("tool-call data", result.Details.Summary!, StringComparison.Ordinal);
        Assert.Empty(judge.Inputs);
    }

    [Theory]
    [MemberData(nameof(ToolEvaluators))]
    public async Task EveryToolJudge_WithACapturedEmptyList_IsStillAsked(string key)
    {
        // Guard: "no tool was called" is an observation each of these rubrics can grade.
        var judge = new RecordingJudge();

        var result = await Build(key, judge).EvaluateAsync(new EvalInput("Find flights to Paris", "I could not search.", ToolCalls: []));

        Assert.NotEqual("skipped", result.Score.Label);
        Assert.Single(judge.Inputs);
    }

    [Fact]
    public async Task ToolInputAccuracy_WithNoToolCallDataCaptured_AsksNoJudge_AndCannotPass()
    {
        // Its schema leaf already skipped without calls; its semantic judge graded the arguments of calls it never saw.
        var judge = new RecordingJudge();

        var result = await new ToolInputAccuracyEval(judge).EvaluateAsync(new EvalInput("Find flights", "Called search_flights."));

        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.NotMeasured, result.Score.CensusBucket());
        var semantic = Assert.Single(result.Details.SubResults!, s => s.Metric.Key == "tool_input_accuracy_semantic");
        Assert.Equal("skipped", semantic.Score.Label);
        Assert.Empty(judge.Inputs);
    }

    [Fact]
    public async Task ToolCallSuccess_StatusRecordsInMetadata_StillDecide_WithoutToolCalls()
    {
        // Guard: the deterministic path that needs no tool calls is untouched; only the judge fallback needs them.
        var judge = new RecordingJudge();
        var input = new EvalInput("Upload the report", "Uploaded.", Metadata: new Dictionary<string, object>
        {
            [ToolCallSuccessEval.MetadataStatusKey] = """[{"name":"upload","status":"success"}]""",
        });

        var result = await new ToolCallSuccessEval(judge).EvaluateAsync(input);

        Assert.Equal("pass", result.Score.Label);
        Assert.Empty(judge.Inputs);
    }
}
