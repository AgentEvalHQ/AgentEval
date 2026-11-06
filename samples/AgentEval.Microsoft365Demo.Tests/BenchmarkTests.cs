// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Microsoft365Demo.Tests.TestHelpers;

namespace AgentEval.Microsoft365Demo.Tests;

/// <summary>
/// The full run — readiness, one prompt, the benchmark through <c>MAFEvaluationHarness</c> — offline through the real
/// provider. Pins the honesty rules of the verdicts: criteria are graded only by a judge and are NOT MEASURED without one
/// (exit 11, never a pass); a turn that did not complete is an ERROR row with its outcome, never graded and never a FAIL
/// score; the deterministic checks always run; nothing is approved and nothing is resent.
/// </summary>
public sealed class BenchmarkTests : IDisposable
{
    private const string PauseMarker = "PAUSE-MARKER-7f3a";
    private const string OkMarker = "OK-MARKER-2b9c";

    private static readonly string[] DeterministicChecks =
        ["turn completed", "answer non-empty", "response time recorded", "evidence availability recorded"];

    private readonly DemoRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task FullRun_WithAJudge_EveryCaseCompletes_ExitsZero_AndResultsListEveryCasePass()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        var judge = new FakeJudgeChatClient();
        _rig.Judge = judge;

        var run = await _rig.RunAsync(DemoRig.Consent);

        run.AssertExit(0);
        Assert.Contains("Result: PASS (exit 0)", run.Out, StringComparison.Ordinal);

        // results.md lists every default case as PASS; results.json agrees, with the judge's grade measured.
        var resultsMd = run.Text("results.md");
        foreach (var id in DemoRig.DefaultCaseIds)
        {
            Assert.Contains($"## {id} — PASS", resultsMd, StringComparison.Ordinal);
        }

        var results = run.Json("results.json");
        Assert.Equal("agenteval.m365.demo-results/v1", results.GetProperty("schema").GetString());
        Assert.Equal(0, results.GetProperty("exitCode").GetInt32());
        var cases = results.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(DemoRig.DefaultCaseIds, cases.Select(c => c.GetProperty("id").GetString()!).ToArray());
        foreach (var c in cases)
        {
            Assert.Equal("PASS", c.GetProperty("verdict").GetString());
            Assert.Equal(judge.Score, c.GetProperty("score").GetInt32());
            var checks = Checks(c);
            Assert.All(DeterministicChecks, name => Assert.Equal("PASS", checks[name]));
            Assert.Equal("PASS", checks["criteria (judge)"]);
            Assert.Equal("Completed", c.GetProperty("turn").GetProperty("outcome").GetString());
        }

        Assert.Equal(5, results.GetProperty("counts").GetProperty("pass").GetInt32());
        Assert.Equal(DemoRig.DefaultCaseIds.Length, judge.Calls);

        // One prompt plus one turn per case, each recorded with its evidence document; turns.jsonl carries no answer text.
        Assert.Equal(1 + DemoRig.DefaultCaseIds.Length, _rig.Handler.CountOf("SendMessage"));
        var turns = run.JsonLines("turns.jsonl");
        Assert.Equal(new[] { "prompt" }.Concat(DemoRig.DefaultCaseIds).ToArray(), turns.Select(t => t.GetProperty("caseId").GetString()!).ToArray());
        Assert.All(turns, t => Assert.Equal("Completed", t.GetProperty("outcome").GetString()));
        Assert.DoesNotContain(DemoRig.AnswerStart, run.Text("turns.jsonl"), StringComparison.Ordinal);
        foreach (var id in DemoRig.DefaultCaseIds.Prepend("prompt"))
        {
            Assert.True(run.File(Path.Combine("evidence", id + ".json")).Exists, $"evidence/{id}.json is missing.\n{run.Describe()}");
        }

        Assert.Contains("**Result: PASS** (exit 0)", run.Text("summary.md"), StringComparison.Ordinal);
        Assert.Empty(run.JsonLines("issues.jsonl").Where(i => i.GetProperty("category").GetString() is "Authentication" or "Permission" or "Protocol"));
    }

    [Fact]
    public async Task FullRun_WithoutAJudge_CriteriaAreNotMeasured_DeterministicChecksStillRun_ExitsEleven()
    {
        // The rig's environment is empty: no inference provider can be resolved, whatever the developer's shell holds.
        _rig.WithHealthyEndpoint().WithCompletedTurns();

        var run = await _rig.RunAsync(DemoRig.Consent);

        run.AssertExit(11);
        Assert.Contains("Result: NOT MEASURED (exit 11)", run.Out, StringComparison.Ordinal);
        var resultsMd = run.Text("results.md");
        var results = run.Json("results.json");
        foreach (var c in results.GetProperty("cases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            Assert.Equal("NOT MEASURED", c.GetProperty("verdict").GetString());
            Assert.Contains($"## {id} — NOT MEASURED", resultsMd, StringComparison.Ordinal);
            var checks = Checks(c);
            Assert.Equal("NOT MEASURED", checks["criteria (judge)"]);
            Assert.All(DeterministicChecks, name => Assert.Equal("PASS", checks[name]));
            Assert.Equal(JsonValueKind.Null, c.GetProperty("score").ValueKind);
        }

        Assert.Equal(0, results.GetProperty("counts").GetProperty("pass").GetInt32());
        Assert.Equal(5, results.GetProperty("counts").GetProperty("notMeasured").GetInt32());
        Assert.DoesNotContain("## identity_disclosure — PASS", resultsMd, StringComparison.Ordinal);
        Assert.Equal(1 + DemoRig.DefaultCaseIds.Length, _rig.Handler.CountOf("SendMessage"));
    }

    [Fact]
    public async Task FullRun_NoJudgeOption_NeverCallsTheSuppliedJudge_AndExitsEleven()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        var judge = new FakeJudgeChatClient();
        _rig.Judge = judge;

        var run = await _rig.RunAsync(DemoRig.Consent, "--no-judge");

        run.AssertExit(11);
        Assert.Equal(0, judge.Calls);
        Assert.All(run.Json("results.json").GetProperty("cases").EnumerateArray(), c =>
        {
            Assert.Equal("NOT MEASURED", c.GetProperty("verdict").GetString());
            Assert.Equal("NOT MEASURED", Checks(c)["criteria (judge)"]);
        });
    }

    [Fact]
    public async Task FullRun_ACasePausesForConfirmation_ItIsAnErrorRowWithOutcomeInputRequired_NothingApproved_ExitsEleven()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        var paused = _rig.Router.RouteSendMessage(PauseMarker, DemoRig.InputRequiredConfirmation);
        var judge = new FakeJudgeChatClient();
        _rig.Judge = judge;
        var dataset = _rig.WriteDataset($$"""
            [
              { "id": "case_ok", "input": "In one sentence, what is {{OkMarker}}?", "evaluation_criteria": ["Answers in one sentence"], "passing_score": 70 },
              { "id": "case_pause", "input": "Describe the kickoff {{PauseMarker}} in one sentence.", "evaluation_criteria": ["Does not create anything"], "passing_score": 70 }
            ]
            """);

        var run = await _rig.RunAsync(DemoRig.Consent, "--dataset", dataset.FullName);

        // The contract: a turn that did not complete is ERROR (not measured), never FAIL — so nothing failed and the run exits 11.
        run.AssertExit(11);
        var results = run.Json("results.json");
        var cases = results.GetProperty("cases").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!);
        Assert.Equal("PASS", cases["case_ok"].GetProperty("verdict").GetString());

        var pause = cases["case_pause"];
        Assert.Equal("ERROR", pause.GetProperty("verdict").GetString());
        Assert.Equal("InputRequired", pause.GetProperty("turn").GetProperty("outcome").GetString());
        Assert.Contains("InputRequired", pause.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, pause.GetProperty("score").ValueKind);
        var checks = Checks(pause);
        Assert.Equal("NOT RUN", checks["turn completed"]);
        Assert.Equal("NOT RUN", checks["criteria (judge)"]);
        Assert.DoesNotContain(checks.Values, v => v == "FAIL");
        Assert.Equal(0, results.GetProperty("counts").GetProperty("fail").GetInt32());
        Assert.Equal(1, results.GetProperty("counts").GetProperty("error").GetInt32());

        var resultsMd = run.Text("results.md");
        Assert.Contains("## case_pause — ERROR", resultsMd, StringComparison.Ordinal);
        Assert.Contains("## case_ok — PASS", resultsMd, StringComparison.Ordinal);
        Assert.DoesNotContain("## case_pause — FAIL", resultsMd, StringComparison.Ordinal);

        // The paused turn is recorded with its outcome and pending interaction, and registered as an issue for the case.
        var turn = Assert.Single(run.JsonLines("turns.jsonl"), t => t.GetProperty("caseId").GetString() == "case_pause");
        Assert.Equal("InputRequired", turn.GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrEmpty(turn.GetProperty("pendingInteraction").GetString()), "The paused turn must carry its pending interaction kind.");
        Assert.Contains(run.JsonLines("issues.jsonl"), i => i.TryGetProperty("caseId", out var id) && id.GetString() == "case_pause");
        Assert.Contains(run.OutLines, l => l.Contains("turn  case_pause  InputRequired", StringComparison.Ordinal));

        // Graded only what completed; sent the paused prompt once and approved nothing (no further call of any kind for it).
        Assert.Equal(1, judge.Calls);
        Assert.Equal(1, paused.TotalRequests);
        Assert.Equal(2, _rig.Handler.CountOf("SendMessage"));
        Assert.Equal(0, _rig.Handler.CountOf("CancelTask"));
    }

    [Fact]
    public async Task FullRun_TheJudgeCallFailsWith401_TheCaseIsNotMeasured_TheAgentsChecksStand_AndTheIssueNamesTheJudge()
    {
        // A judge outage after a completed turn is the judge's failure: never an ERROR row "the turn did not complete: outcome Completed".
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        var judge = new ThrowingJudgeChatClient();
        _rig.Judge = judge;
        var dataset = _rig.WriteDataset($$"""
            [
              { "id": "case_ok", "input": "In one sentence, what is {{OkMarker}}?", "evaluation_criteria": ["Answers in one sentence"], "passing_score": 70 }
            ]
            """);

        var run = await _rig.RunAsync(DemoRig.Consent, "--dataset", dataset.FullName);

        // Nothing failed, the criteria were not measured: exit 11, NOT MEASURED, never ERROR and never a score.
        run.AssertExit(11);
        var results = run.Json("results.json");
        var c = Assert.Single(results.GetProperty("cases").EnumerateArray());
        Assert.Equal("NOT MEASURED", c.GetProperty("verdict").GetString());
        Assert.Equal(JsonValueKind.Null, c.GetProperty("score").ValueKind);
        var reason = c.GetProperty("reason").GetString()!;
        Assert.Contains("the judge call failed: HttpRequestException", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("did not complete", reason, StringComparison.Ordinal);

        // The agent was measured: its deterministic checks pass; the criteria are NOT MEASURED with the judge's error.
        var checks = Checks(c);
        Assert.All(DeterministicChecks, name => Assert.Equal("PASS", checks[name]));
        Assert.Equal("NOT MEASURED", checks["criteria (judge)"]);
        var criteria = c.GetProperty("checks").EnumerateArray().Single(k => k.GetProperty("name").GetString() == "criteria (judge)");
        Assert.Equal("the judge call failed: HttpRequestException: " + judge.Message, criteria.GetProperty("detail").GetString());
        Assert.Equal("Completed", c.GetProperty("turn").GetProperty("outcome").GetString());
        Assert.Equal(1, results.GetProperty("counts").GetProperty("notMeasured").GetInt32());
        Assert.Equal(0, results.GetProperty("counts").GetProperty("error").GetInt32());

        // One issue that names the JUDGE (Authentication for its 401), with the case id; on the screen and in the summary.
        var issue = Assert.Single(run.JsonLines("issues.jsonl"), i => i.GetProperty("code").GetString() == "judge.call_failed");
        Assert.Equal("Authentication", issue.GetProperty("category").GetString());
        Assert.Equal(401, issue.GetProperty("httpStatus").GetInt32());
        Assert.Equal("case_ok", issue.GetProperty("caseId").GetString());
        Assert.Contains("the JUDGE failed, not the agent", issue.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("AI_INFERENCE_PROVIDER", issue.GetProperty("suggestedCause").GetString(), StringComparison.Ordinal);
        Assert.Contains(run.OutLines, l => l.Contains("judge.call_failed", StringComparison.Ordinal) && l.Contains("HTTP 401", StringComparison.Ordinal));
        Assert.Contains("the judge call failed", run.Text("summary.md"), StringComparison.Ordinal);
        Assert.Contains("the judge call failed for 1 case(s)", results.GetProperty("exitMeaning").GetString(), StringComparison.Ordinal);

        // The judge was reached once; the agent got the prompt and the case once each — nothing resent.
        Assert.Equal(1, judge.Calls);
        Assert.Equal(2, _rig.Handler.CountOf("SendMessage"));
    }

    [Fact]
    public async Task FullRun_TheJudgeFailsWith503OnEveryCase_TheScreenShowsTheIssueOnceWithItsCount_TheFilesKeepEveryOccurrence()
    {
        // The same issue on three cases is one block on the screen and a ×3 count, three entries in the files.
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Judge = new ThrowingJudgeChatClient { Status = System.Net.HttpStatusCode.ServiceUnavailable };
        var dataset = _rig.WriteDataset("""
            [
              { "id": "case_a", "input": "In one sentence, what is A?", "evaluation_criteria": ["Answers in one sentence"], "passing_score": 70 },
              { "id": "case_b", "input": "In one sentence, what is B?", "evaluation_criteria": ["Answers in one sentence"], "passing_score": 70 },
              { "id": "case_c", "input": "In one sentence, what is C?", "evaluation_criteria": ["Answers in one sentence"], "passing_score": 70 }
            ]
            """);

        var run = await _rig.RunAsync(DemoRig.Consent, "--dataset", dataset.FullName, "--verbosity", "detailed");

        run.AssertExit(11);
        var issues = run.JsonLines("issues.jsonl").Where(i => i.GetProperty("code").GetString() == "judge.call_failed").ToArray();
        Assert.Equal(new[] { "case_a", "case_b", "case_c" }, issues.Select(i => i.GetProperty("caseId").GetString()).ToArray());
        Assert.All(issues, i => Assert.Equal("Service", i.GetProperty("category").GetString()));

        // The screen: one detail block, then the count at the end.
        Assert.Single(run.OutLines, l => l.Contains("! ISSUE Service judge.call_failed", StringComparison.Ordinal));
        Assert.Single(run.OutLines, l => l.Contains("judge.call_failed (Outcome) ×3", StringComparison.Ordinal));
        Assert.Contains(run.OutLines, l => l.TrimStart().StartsWith("category", StringComparison.Ordinal) && l.Contains("the judge model's provider settings", StringComparison.Ordinal));

        // console.log keeps every occurrence in full.
        Assert.Equal(3, run.Text("console.log").Split('\n').Count(l => l.Contains("! ISSUE Service judge.call_failed", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FullRun_TurnCapBelowThePlan_StopsBeforeTheCrossingTurn_AndTheRestAreNotMeasured()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Judge = new FakeJudgeChatClient();

        var run = await _rig.RunAsync(DemoRig.Consent, "--max-turns", "3");

        run.AssertExit(11);
        Assert.Equal(3, _rig.Handler.CountOf("SendMessage"));
        var verdicts = run.Json("results.json").GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("verdict").GetString()!).ToArray();
        Assert.Equal(new[] { "PASS", "PASS", "NOT MEASURED", "NOT MEASURED", "NOT MEASURED" }, verdicts);
        Assert.Contains("--max-turns", run.Text("summary.md"), StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> Checks(JsonElement caseElement) =>
        caseElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(k => k.GetProperty("name").GetString()!, k => k.GetProperty("result").GetString());
}
