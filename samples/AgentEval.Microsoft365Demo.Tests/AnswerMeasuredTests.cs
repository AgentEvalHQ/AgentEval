// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Microsoft365Demo.Tests.TestHelpers;

namespace AgentEval.Microsoft365Demo.Tests;

/// <summary>
/// One rule for the answer of a completed turn, the provider's (<c>Microsoft365Evidence.IsAnswerMeasured</c>): several
/// answer candidates with none selected is NOT MEASURED — in the prompt step, in the answer check and in the criteria —
/// never a FAIL and never a judge score; a completed turn whose answer is empty stays a FAIL.
/// </summary>
public sealed class AnswerMeasuredTests : IDisposable
{
    private const string AmbiguousMarker = "AMBIGUOUS-MARKER-5c1e";
    private const string EmptyMarker = "EMPTY-MARKER-9a7d";

    private readonly DemoRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task PromptStep_SeveralCandidatesAndNoneSelected_IsNotMeasured_NeverAFail()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        var routed = _rig.Router.RouteSendMessage(AmbiguousMarker, DemoRig.AmbiguousAnswer);

        var run = await _rig.RunAsync(DemoRig.Consent, "--no-benchmark", "--prompt", $"When does the pilot start? {AmbiguousMarker}");

        run.AssertExit(11);
        Assert.Contains("Result: NOT MEASURED (exit 11)", run.Out, StringComparison.Ordinal);
        var prompt = Stage(run, "prompt");
        Assert.Equal("NOT MEASURED", prompt.GetProperty("result").GetString());
        Assert.Contains("several answer candidates and none selected", prompt.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // The turn completed and is recorded as Ambiguous; it was sent once.
        var turn = Assert.Single(run.JsonLines("turns.jsonl"));
        Assert.Equal("Completed", turn.GetProperty("outcome").GetString());
        Assert.Equal("Ambiguous", turn.GetProperty("answerCompleteness").GetString());
        Assert.Equal(1, routed.CountOf("SendMessage"));
    }

    [Fact]
    public async Task BenchmarkCase_SeveralCandidatesAndNoneSelected_IsNotMeasured_TheOtherCasesAreGraded()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Router.RouteSendMessage(AmbiguousMarker, DemoRig.AmbiguousAnswer);
        _rig.Judge = new FakeJudgeChatClient();
        var dataset = _rig.WriteDataset($$"""
            [
              { "id": "case_ok", "input": "In one sentence, what is on my calendar?", "evaluation_criteria": ["Answers in one sentence"] },
              { "id": "case_ambiguous", "input": "When does the pilot start? {{AmbiguousMarker}}", "evaluation_criteria": ["Gives one date"] }
            ]
            """);

        var run = await _rig.RunAsync(DemoRig.Consent, "--dataset", dataset.FullName);

        run.AssertExit(11);
        var results = run.Json("results.json");
        var cases = results.GetProperty("cases").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!);
        Assert.Equal("PASS", cases["case_ok"].GetProperty("verdict").GetString());

        var ambiguous = cases["case_ambiguous"];
        Assert.Equal("NOT MEASURED", ambiguous.GetProperty("verdict").GetString());
        Assert.Equal(JsonValueKind.Null, ambiguous.GetProperty("score").ValueKind);
        Assert.Contains("no answer was selected", ambiguous.GetProperty("reason").GetString(), StringComparison.Ordinal);
        var checks = Checks(ambiguous);
        Assert.Equal("PASS", checks["turn completed"]);
        Assert.Equal("NOT MEASURED", checks["answer non-empty"]);
        Assert.Equal("NOT MEASURED", checks["criteria (judge)"]);
        Assert.Equal("Ambiguous", ambiguous.GetProperty("turn").GetProperty("answerCompleteness").GetString());
        Assert.Equal(0, results.GetProperty("counts").GetProperty("fail").GetInt32());
        Assert.Contains("## case_ambiguous — NOT MEASURED", run.Text("results.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptStep_CompletedWithAnEmptyAnswer_StaysAFail()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Router.RouteSendMessage(EmptyMarker, DemoRig.EmptyAnswer);

        var run = await _rig.RunAsync(DemoRig.Consent, "--no-benchmark", "--prompt", $"What did the committee decide? {EmptyMarker}");

        run.AssertExit(1);
        var prompt = Stage(run, "prompt");
        Assert.Equal("FAIL", prompt.GetProperty("result").GetString());
        Assert.Contains("with no answer text", prompt.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BenchmarkCase_CompletedWithAnEmptyAnswer_StaysAFail()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Router.RouteSendMessage(EmptyMarker, DemoRig.EmptyAnswer);
        _rig.Judge = new FakeJudgeChatClient();
        var dataset = _rig.WriteDataset($$"""
            [
              { "id": "case_empty", "input": "What did the committee decide? {{EmptyMarker}}", "evaluation_criteria": ["Names the decision"] }
            ]
            """);

        var run = await _rig.RunAsync(DemoRig.Consent, "--dataset", dataset.FullName);

        run.AssertExit(1);
        var c = Assert.Single(run.Json("results.json").GetProperty("cases").EnumerateArray());
        Assert.Equal("FAIL", c.GetProperty("verdict").GetString());
        Assert.Equal("FAIL", Checks(c)["answer non-empty"]);
    }

    private static JsonElement Stage(DemoRunResult run, string name) =>
        Assert.Single(run.Json("results.json").GetProperty("stages").EnumerateArray(), s => s.GetProperty("name").GetString() == name);

    private static Dictionary<string, string?> Checks(JsonElement caseElement) =>
        caseElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(k => k.GetProperty("name").GetString()!, k => k.GetProperty("result").GetString());
}
