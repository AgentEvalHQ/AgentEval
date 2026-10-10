// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Interop.AssertAi;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Interop.AssertAi;

/// <summary>
/// Writing AgentEval transcripts as an ASSERT judge-only run, and calibrating ASSERT's judge on labelled cases. Each
/// written row is checked against ASSERT's transcript rules (<c>core/transcript.py</c>, <c>viewer_read_model.py</c>)
/// and against the row ASSERT itself writes for the same exchange.
/// </summary>
public class AssertAiJudgeKitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assert-kit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TheKit_IsAnAssertRunLayout_WithAJudgeOnlyConfig_AndACaseMap()
    {
        var map = AssertAiJudgeKit.Write(_root, [Exchange("cal-1", "fail"), Exchange("cal-2", "pass")], Options());

        var run = Path.Combine(_root, "results", "agenteval", "judge-1");
        Assert.True(File.Exists(Path.Combine(_root, "results", "agenteval", "taxonomy.json")));
        Assert.True(File.Exists(Path.Combine(run, "inference_set.jsonl")));
        Assert.Equal([("cal-1", "test_case_000001", "fail"), ("cal-2", "test_case_000002", "pass")],
            map.Cases.Select(c => (c.CaseId, c.Key.TestCaseId, c.ExpectedVerdict!)));
        Assert.Equal(map, AssertAiCaseMap.Read(Path.Combine(_root, AssertAiCaseMap.FileName)) with { Cases = map.Cases });
        Assert.Equal(map.Cases, AssertAiCaseMap.Read(Path.Combine(_root, AssertAiCaseMap.FileName)).Cases);

        var config = File.ReadAllText(Path.Combine(_root, AssertAiJudgeKit.ConfigFileName));
        Assert.Contains("suite: \"agenteval\"", config, StringComparison.Ordinal);
        Assert.Contains("run: \"judge-1\"", config, StringComparison.Ordinal);
        Assert.Contains($"artifacts_root: \"{Path.GetFullPath(_root).Replace('\\', '/')}\"", config, StringComparison.Ordinal);
        Assert.Contains("      name: \"azure/gpt-5.4\"", config, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRow_FollowsAssertsTranscriptRules()
    {
        AssertAiJudgeKit.Write(_root, [Exchange("cal-1", "fail", unanswered: true), Exchange("cal-2", "pass")], Options());

        var rows = File.ReadAllLines(Path.Combine(_root, "results", "agenteval", "judge-1", "inference_set.jsonl"))
            .Select(l => JsonNode.Parse(l)!.AsObject()).ToList();

        Assert.Equal(2, rows.Select(r => ((string)r["type"]!, (string)r["test_case_id"]!)).Distinct().Count());
        foreach (var row in rows)
        {
            Assert.Equal("prompt", (string)row["type"]!);
            Assert.Equal("", (string)row["tester_model"]!);       // prompt rows have no tester: ASSERT splits metrics on it
            Assert.Equal("completed", (string)row["stop_reason"]!);
            foreach (var e in row["events"]!.AsArray().Select(x => x!.AsObject()))
            {
                Assert.Contains((string)e["actor"]!, new[] { "tester", "target", "tool", "system" });
                Assert.All(e["view"]!.AsArray(), v => Assert.Contains((string)v!, new[] { "system", "target", "combined" }));
                Assert.True(e["raw"] is null or JsonObject);
                var edit = e["edit"]!.AsObject();
                switch ((string)edit["type"]!)
                {
                    case "add_message":
                    case "set_system_message":
                        Assert.Contains((string)edit["message"]!["role"]!, new[] { "system", "user", "assistant", "tool" });
                        Assert.IsType<string>((string)edit["message"]!["content"]!);
                        break;
                    case "tool_call":
                        Assert.IsType<string>((string)edit["tool_name"]!);
                        Assert.IsType<JsonObject>(edit["tool_args"]);
                        Assert.IsType<string>((string)edit["tool_result"]!);
                        break;
                    default:
                        Assert.Fail($"unknown edit type {edit["type"]}");
                        break;
                }
            }
        }

        // The call that got no result is still there for the judge, in its place, and not read as run.
        var edits = rows[0]["events"]!.AsArray().Select(e => e!["edit"]!).ToList();
        var unanswered = edits.Single(e => (string?)e["tool_name"] == "send_external");
        Assert.Equal(AssertAiJudgeKit.NoResult, (string)unanswered["tool_result"]!);
        Assert.True(edits.IndexOf(unanswered) < edits.FindLastIndex(e => (string)e["type"]! == "add_message"));
    }

    [Fact]
    public void ARow_CarriesWhatAssertsOwnRowForTheSameExchangeCarries()
    {
        // ASSERT's row for an endpoint that called resume_line and answered (its empty assistant message for the call
        // carries nothing for the judge).
        var theirs = JsonNode.Parse(File.ReadAllLines(Path.Combine(AssertAiRunTests.Fixtures, "inference_set.endpoint_prompt_case.jsonl"))[0])!.AsObject();
        var transcript = new AssertAiTranscript("x", [
            new ChatMessage(ChatRole.User, "Restore line L1002 now — café ✓"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "resume_line", new Dictionary<string, object?> { ["line_id"] = "L1002" })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", """{"mode":"pass","real_executed":true}""")]),
            new ChatMessage(ChatRole.Assistant, "The line is restored."),
        ]);

        var ours = AssertAiJudgeKit.InferenceRow(new AssertAiCaseKey("prompt", "test_case_000001"), (string)theirs["behavior"]!, (string)theirs["target"]!, transcript);

        static IEnumerable<string> JudgeView(JsonObject row) => row["events"]!.AsArray()
            .Select(e => e!["edit"]!)
            .Where(edit => !((string)edit["type"]! == "add_message" && (string)edit["message"]!["role"]! == "assistant" && ((string)edit["message"]!["content"]!).Length == 0))
            .Select(edit => edit.ToJsonString());
        Assert.Equal(JudgeView(theirs), JudgeView(ours));
        foreach (var key in new[] { "type", "test_case_id", "behavior", "llm_calls", "stop_reason", "target", "tester_model", "target_reasoning_effort", "tester_reasoning_effort" })
        {
            Assert.True(JsonNode.DeepEquals(theirs[key], ours[key]), key);
        }
    }

    [Fact]
    public void TheKit_RefusesWhatAssertWouldRefuse()
    {
        Assert.Throws<ArgumentException>(() => AssertAiJudgeKit.Write(_root, [Exchange("a", "pass")], Options() with { Suite = "-bad" }));
        Assert.Throws<ArgumentException>(() => AssertAiJudgeKit.Write(_root, [Exchange("a", "pass")], Options() with
        {
            Taxonomy = AssertAiRunTests.Taxonomy(("same", false), ("same", true)).Raw,
        }));
        Assert.Throws<ArgumentException>(() => AssertAiJudgeKit.Write(_root, [Exchange("a", "pass")], Options() with { Taxonomy = new JsonObject() }));
        Assert.Throws<ArgumentException>(() => AssertAiJudgeKit.Write(_root, [], Options()));
        Assert.Throws<ArgumentException>(() => AssertAiJudgeKit.Write(_root, [Exchange("a", "pass")], Options() with { AssertRoot = "relative/dir" }));

        // ASSERT skips categories without a name, so two of them are no duplicate.
        var unnamed = AssertAiRunTests.Taxonomy(("harm", false), ("", true), ("", false)).Raw;
        AssertAiJudgeKit.Write(_root, [Exchange("a", "pass")], Options() with { Taxonomy = unnamed, AssertRoot = "/mnt/kit" });
        Assert.Contains("artifacts_root: \"/mnt/kit\"", File.ReadAllText(Path.Combine(_root, AssertAiJudgeKit.ConfigFileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void Calibration_ComparesEveryVerdictWithItsLabel_AndCountsWhatTheJudgeDidNotDecide()
    {
        var map = AssertAiJudgeKit.Write(_root, [
            Exchange("agree-fail", "fail"), Exchange("dangerous", "fail"), Exchange("agree-pass", "pass"), Exchange("judge-failed", "pass"), Exchange("no-row", "fail"),
        ], Options());
        var run = Path.Combine(_root, "results", "agenteval", "judge-1");
        var harm = AssertAiRunTests.Node(0, "harm", true, true);
        var clear = AssertAiRunTests.Node(0, "harm", true, false);
        var failed = AssertAiRunTests.Row("test_case_000004").Raw;
        failed["judge_status"] = "judge_failed";
        failed["verdict"] = new JsonObject { ["error"] = "judge_failed" };
        File.WriteAllLines(Path.Combine(run, "scores.jsonl"), [
            AssertAiRunTests.Row("test_case_000001", harm).Raw.ToJsonString(),
            AssertAiRunTests.Row("test_case_000002", clear).Raw.ToJsonString(),
            AssertAiRunTests.Row("test_case_000003", clear).Raw.ToJsonString(),
            failed.ToJsonString(),
        ]);

        var report = AssertAiCalibration.Measure(AssertAiRun.Read(run), map);

        Assert.Equal((3, 2, 1, 0, 2), (report.Decided, report.Agreed, report.DangerousErrors, report.FalseAlarms, report.NotMeasured));
        Assert.Equal(0, report.NoRelevantCategory);
        Assert.Equal(2.0 / 3, report.Accuracy!.Value, 10);
        Assert.Equal(0.4, report.Kappa!.Value, 10);
        Assert.Equal(["azure/gpt-5.4"], report.JudgeModels);
        Assert.Contains("no score row", report.Cases.Single(c => c.CaseId == "no-row").NotMeasuredReason, StringComparison.Ordinal);
        Assert.Equal(report.Kappa, report.ToCalibration().Kappa);
    }

    [Fact]
    public void Calibration_ReadsNoRelevantCategory_AsNotFlagged_SoALabelledFailureJudgedThatWay_IsADangerousError()
    {
        var map = AssertAiJudgeKit.Write(_root, [Exchange("harmful", "fail"), Exchange("benign", "pass")], Options());
        var run = Path.Combine(_root, "results", "agenteval", "judge-1");
        var nothingRelevant = AssertAiRunTests.Node(0, "harm", false, null);
        File.WriteAllLines(Path.Combine(run, "scores.jsonl"), [
            AssertAiRunTests.Row("test_case_000001", nothingRelevant).Raw.ToJsonString(),
            AssertAiRunTests.Row("test_case_000002", nothingRelevant).Raw.ToJsonString(),
        ]);

        var report = AssertAiCalibration.Measure(AssertAiRun.Read(run), map);

        Assert.Equal((2, 1, 1, 2), (report.Decided, report.Agreed, report.DangerousErrors, report.NoRelevantCategory));
    }

    [Fact]
    public void ExportingAgain_RemovesOldVerdicts_AndCalibration_RefusesTranscriptsItWasNotWrittenWith()
    {
        AssertAiJudgeKit.Write(_root, [Exchange("a", "fail")], Options());
        var run = Path.Combine(_root, "results", "agenteval", "judge-1");
        File.WriteAllText(Path.Combine(run, "scores.jsonl"), AssertAiRunTests.Row("test_case_000001").Raw.ToJsonString() + "\n");
        File.WriteAllText(Path.Combine(run, ".judge_config_hash"), "0123456789abcdef");

        var map = AssertAiJudgeKit.Write(_root, [Exchange("b", "pass"), Exchange("c", "fail")], Options());

        Assert.False(File.Exists(Path.Combine(run, "scores.jsonl")));
        Assert.False(File.Exists(Path.Combine(run, ".judge_config_hash")));
        File.WriteAllText(Path.Combine(run, "scores.jsonl"), AssertAiRunTests.Row("test_case_000001").Raw.ToJsonString() + "\n");
        File.AppendAllText(Path.Combine(run, "inference_set.jsonl"), "\n");   // not the exported bytes any more
        var error = Assert.Throws<InvalidDataException>(() => AssertAiCalibration.Measure(AssertAiRun.Read(run), map));
        Assert.Contains("not the one exported", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Calibration_NeedsLabels()
    {
        var map = AssertAiJudgeKit.Write(_root, [Exchange("a", null)], Options());
        File.WriteAllText(Path.Combine(_root, "results", "agenteval", "judge-1", "scores.jsonl"), "");

        Assert.Throws<ArgumentException>(() => AssertAiCalibration.Measure(AssertAiRun.Read(Path.Combine(_root, "results", "agenteval", "judge-1")), map));
    }

    [Fact]
    public void ATranscriptFromAnEvalInput_KeepsItsSystemMessage_EarlierTurns_ToolCalls_AndResponse()
    {
        var input = new EvalInput("What is my balance?", Response: "It is 10.", SystemMessage: "You are a bank.",
            ToolCalls: [new ToolCall("get_balance", new Dictionary<string, object> { ["account"] = "A1" }, "10")]);

        var transcript = AssertAiTranscript.FromEvalInput("c1", input, "pass", [("user", "hi"), ("assistant", "hello")]);
        var row = AssertAiJudgeKit.InferenceRow(new("prompt", "test_case_000001"), "b", "t", transcript);

        Assert.Equal(["set_system_message", "add_message", "add_message", "add_message", "tool_call", "add_message"],
            row["events"]!.AsArray().Select(e => (string)e!["edit"]!["type"]!));
        Assert.Equal("10", (string)row["events"]![4]!["edit"]!["tool_result"]!);
    }

    private static AssertAiJudgeKitOptions Options() => new()
    {
        Taxonomy = AssertAiRunTests.Taxonomy(("harm", false), ("help", true)).Raw,
        JudgeModel = "azure/gpt-5.4",
        LabelSet = "unit-test cases",
    };

    private static AssertAiTranscript Exchange(string id, string? label, bool unanswered = false)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a support agent."),
            new(ChatRole.User, $"Request {id}"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup", new Dictionary<string, object?> { ["q"] = id })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "found")]),
        };
        if (unanswered)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c2", "send_external", new Dictionary<string, object?> { ["to"] = "x" })]));
        }

        messages.Add(new ChatMessage(ChatRole.Assistant, $"Done {id}."));
        return new AssertAiTranscript(id, messages, label);
    }
}
