// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Interop.AssertAi;
using AgentEval.Tests.Contracts;
using Xunit;

namespace AgentEval.Tests.Interop.AssertAi;

/// <summary>
/// Reading ASSERT runs: the status rules, the headline rates, the cases with no score row, and how each verdict becomes
/// an AgentEval result. The expected rates of <c>permissibility_split.json</c> are ASSERT's own test's assertions.
/// </summary>
public class AssertAiRunTests : IDisposable
{
    internal static readonly string Fixtures = Path.Combine(AefSchemaSet.RepoRoot(), "tests", "AgentEval.Tests", "Interop", "AssertAi", "Fixtures");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assert-ai-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- ASSERT's headline -------------------------------------------------------------------------------------

    [Fact]
    public void TheHarmAndOverRefusalRates_AreAssertsOwn_OnItsOwnTestRows()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "permissibility_split.json")))!;
        var taxonomy = AssertAiTaxonomy.FromJson(new JsonObject { ["behavior_categories"] = fixture["behavior_categories"]!.DeepClone() });
        var rows = fixture["score_rows"]!.AsArray().Select(r => AssertAiScoreRows.Parse((JsonObject)r!)).ToList();
        Assert.All(rows, r => Assert.Equal(AssertAiJudgeStatus.Ok, r.Status));

        var (permissible, notPermissible) = AssertAiHeadline.Split(rows, taxonomy);

        var expected = fixture["expected"]!;
        Assert.Equal((int)expected["permissible"]!["count"]!, permissible!.Count);
        Assert.Equal((int)expected["permissible"]!["flagged_count"]!, permissible.Flagged);
        Assert.Equal((double)expected["permissible_policy_violation_rate"]!, permissible.Rate);
        Assert.Equal((int)expected["not_permissible"]!["count"]!, notPermissible!.Count);
        Assert.Equal((int)expected["not_permissible"]!["not_applicable_count"]!, notPermissible.NotApplicable);
        Assert.Equal((int)expected["not_permissible"]!["flagged_count"]!, notPermissible.Flagged);
        Assert.Equal((double)expected["not_permissible_policy_violation_rate"]!, notPermissible.Rate);
    }

    [Fact]
    public void ACategory_IsFoundByIndexFirst_ThenByName_AndAnUnknownOneCountsForNothing()
    {
        var taxonomy = Taxonomy(("harm", false), ("help", true));
        var byIndex = Row("t1", Node(0, "help", relevant: true, violated: true));   // index 0 (harm) wins over the name
        var byName = Row("t2", Node(null, "help", relevant: true, violated: true));
        var unknown = Row("t3", Node(null, "elsewhere", relevant: true, violated: true));

        var (permissible, notPermissible) = AssertAiHeadline.Split([byIndex, byName, unknown], taxonomy);

        Assert.Equal((1, 1, 2), (notPermissible!.Flagged, notPermissible.Count, notPermissible.NotApplicable));
        Assert.Equal((1, 1, 2), (permissible!.Flagged, permissible.Count, permissible.NotApplicable));
    }

    [Fact]
    public void AnIrrelevantOrUndecidedCategory_CountsForNothing_ButAMissingRelevantKeyCountsAsRelevant()
    {
        var taxonomy = Taxonomy(("harm", false));
        var irrelevant = Row("t1", Node(0, "harm", relevant: false, violated: null));
        var undecided = Row("t2", new JsonObject { ["node_index"] = 0, ["node_name"] = "harm", ["relevant"] = true, ["violated"] = "yes" });
        var noRelevantKey = Row("t3", new JsonObject { ["node_index"] = 0, ["node_name"] = "harm", ["violated"] = true });

        var (_, notPermissible) = AssertAiHeadline.Split([irrelevant, undecided, noRelevantKey], taxonomy);

        Assert.Equal((1, 1, 2), (notPermissible!.Flagged, notPermissible.Count, notPermissible.NotApplicable));
        Assert.Equal(1.0, notPermissible.Rate);
    }

    [Fact]
    public void ARateWithNoRelevantCase_IsNull_AndATaxonomyWithNoCategories_HasNoRates()
    {
        var (permissible, notPermissible) = AssertAiHeadline.Split([Row("t1", Node(0, "harm", relevant: false, violated: null))], Taxonomy(("harm", false)));
        Assert.Null(notPermissible!.Rate);
        Assert.Null(notPermissible.Interval);
        Assert.Null(permissible!.Rate);

        var (noCategoriesPermissible, noCategoriesNot) = AssertAiHeadline.Split([Row("t1")], AssertAiTaxonomy.FromJson(new JsonObject()));
        Assert.Null(noCategoriesPermissible);
        Assert.Null(noCategoriesNot);
    }

    [Fact]
    public void PromptAndScenarioCases_AreReportedApart_ByTheTesterModel_NotTheType()
    {
        var dir = WriteRun(Taxonomy(("harm", false)).Raw, [
            Row("t1", Node(0, "harm", true, true)).Raw,
            Row("t2", Node(0, "harm", true, false), type: "scenario").Raw,                       // no tester: a prompt row
            Row("t3", Node(0, "harm", true, false), type: "scenario", tester: "azure/gpt-5.4").Raw,
        ]);

        var headlines = AssertAiHeadline.Compute(AssertAiRun.Read(dir));

        Assert.Equal(["prompt", "scenario"], headlines.Select(h => h.Kind));
        Assert.Equal((1, 2), (headlines[0].NotPermissible!.Flagged, headlines[0].NotPermissible!.Count));
        Assert.Equal((0, 1), (headlines[1].NotPermissible!.Flagged, headlines[1].NotPermissible!.Count));
    }

    // ---- ASSERT's status rules ---------------------------------------------------------------------------------

    [Fact]
    public void EveryStatus_ReadsAsAssertReadsIt()
    {
        var rows = ReadRows("scores.constructed.jsonl");

        Assert.Equal(
            [AssertAiJudgeStatus.Ok, AssertAiJudgeStatus.ScoringSkipped, AssertAiJudgeStatus.JudgeFailed, AssertAiJudgeStatus.JudgeFailed],
            rows.Select(r => r.Status));
        Assert.Equal("filter_skipped", rows[3].RawJudgeStatus);   // not a content filter: a refused judge input
    }

    [Theory]
    [InlineData("""{"policy_violation": true}""", null, null, null, "judge_failed")]                    // a dimension missing
    [InlineData("""{"policy_violation": 1, "overrefusal": false}""", null, null, null, "judge_failed")]  // 1 is not a flag
    [InlineData("""{"policy_violation": true, "overrefusal": false}""", null, null, null, "ok")]
    [InlineData("""{"policy_violation": false, "overrefusal": false, "tone": null}""", """["policy_violation","overrefusal","tone"]""", """["tone"]""", """{"tone": false}""", "ok")]
    [InlineData("""{"policy_violation": false, "overrefusal": false, "tone": null}""", """["policy_violation","overrefusal","tone"]""", "[]", """{"tone": false}""", "judge_failed")]
    [InlineData("""{"policy_violation": false, "overrefusal": false, "tone": null}""", """["policy_violation","overrefusal","tone"]""", """["tone"]""", null, "judge_failed")]
    public void AnOkRow_IsOkOnlyWhenItsVerdictHoldsEveryDimension(string dimensions, string? scoreKeys, string? notApplicable, string? applicability, string expected)
    {
        var verdict = new JsonObject { ["dimensions"] = JsonNode.Parse(dimensions), ["node_judgments"] = new JsonArray() };
        if (applicability is not null) verdict["dimension_applicability"] = JsonNode.Parse(applicability);
        var row = new JsonObject { ["type"] = "prompt", ["test_case_id"] = "t", ["judge_status"] = "ok", ["verdict"] = verdict };
        if (scoreKeys is not null) row["score_keys"] = JsonNode.Parse(scoreKeys);
        if (notApplicable is not null) row["not_applicable_score_keys"] = JsonNode.Parse(notApplicable);

        var status = AssertAiScoreRows.Parse(row).Status;

        Assert.Equal(expected == "ok" ? AssertAiJudgeStatus.Ok : AssertAiJudgeStatus.JudgeFailed, status);
    }

    [Theory]
    [InlineData(2, "ok")]
    [InlineData(4, "judge_failed")]   // not a declared grade
    public void AnOrdinalDimension_IsValidOnlyAsADeclaredGrade(int grade, string expected)
    {
        var row = new JsonObject
        {
            ["type"] = "prompt", ["test_case_id"] = "t", ["judge_status"] = "ok",
            ["score_keys"] = new JsonArray("severity"),
            ["dimension_scales"] = JsonNode.Parse("""{"severity": {"type": "ordinal", "values": [{"value": 1, "label": "low"}, {"value": 2, "label": "high"}]}}"""),
            ["verdict"] = new JsonObject { ["dimensions"] = new JsonObject { ["severity"] = grade }, ["node_judgments"] = new JsonArray() },
        };

        Assert.Equal(expected == "ok" ? AssertAiJudgeStatus.Ok : AssertAiJudgeStatus.JudgeFailed, AssertAiScoreRows.Parse(row).Status);
    }

    [Fact]
    public void ARowWithNoStatus_IsOkWhenItsVerdictIs()
    {
        var row = Row("t1", Node(0, "harm", true, false)).Raw;
        row.Remove("judge_status");

        Assert.Equal(AssertAiJudgeStatus.Ok, AssertAiScoreRows.Parse(row).Status);
    }

    // ---- Reading a run directory ---------------------------------------------------------------------------------

    [Fact]
    public void ACaseWithNoScoreRow_IsReported_WithWhatItsAbsenceMeans()
    {
        var dir = WriteRun(
            Taxonomy(("harm", false)).Raw,
            [Row("test_case_000001", Node(0, "harm", true, false)).Raw],
            testSet: ["test_case_000001", "test_case_000002", "test_case_000003"],
            inferenceSet: ["test_case_000001", "test_case_000002"]);

        var run = AssertAiRun.Read(dir);

        Assert.Equal(2, run.Missing.Count);
        Assert.Contains("content filter", run.Missing.Single(m => m.Key.TestCaseId == "test_case_000002").Reason, StringComparison.Ordinal);
        Assert.Contains("was not run", run.Missing.Single(m => m.Key.TestCaseId == "test_case_000003").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTestSetTheManifestNames_IsTheOneComparedAgainst()
    {
        var dir = WriteRun(Taxonomy(("harm", false)).Raw, [Row("test_case_000001", Node(0, "harm", true, false)).Raw], testSet: ["test_case_000001"]);
        var suite = Path.GetDirectoryName(dir)!;
        Directory.CreateDirectory(Path.Combine(suite, "artifacts", "test_set", "v0002"));
        File.WriteAllText(Path.Combine(suite, "artifacts", "test_set", "v0002", "test_set.jsonl"),
            """{"type": "prompt", "test_case_id": "test_case_000001"}""" + "\n" + """{"type": "prompt", "test_case_id": "test_case_000002"}""" + "\n");
        File.WriteAllText(Path.Combine(dir, "manifest.json"), """{"status": "completed", "ended_at": "2026-10-08T10:00:00+00:00", "artifact_versions": {"test_set": {"path": "artifacts/test_set/v0002/test_set.jsonl"}}}""");

        var run = AssertAiRun.Read(dir);

        Assert.EndsWith(Path.Combine("v0002", "test_set.jsonl"), run.TestSetPath, StringComparison.Ordinal);
        Assert.Equal("test_case_000002", Assert.Single(run.Missing).Key.TestCaseId);
        Assert.Equal("completed", run.ManifestStatus);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero), run.FinishedAt);
    }

    [Fact]
    public void TwoRowsForOneCase_AreRefused()
    {
        var dir = WriteRun(Taxonomy(("harm", false)).Raw, [Row("t1").Raw, Row("t1").Raw]);

        var error = Assert.Throws<InvalidDataException>(() => AssertAiRun.Read(dir));
        Assert.Contains("second row for prompt:t1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsLineEndings_BlankLines_AndABom_AreReadAsAssertReadsThem()
    {
        var dir = WriteRun(Taxonomy(("harm", false)).Raw, []);
        var rows = string.Join("\r\n", Row("t1", Node(0, "harm", true, true)).Raw.ToJsonString(), "", Row("t2", Node(0, "harm", true, false)).Raw.ToJsonString()) + "\r\n";
        File.WriteAllText(Path.Combine(dir, "scores.jsonl"), rows, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(2, AssertAiRun.Read(dir).Rows.Count);
    }

    [Fact]
    public void PythonsNonFiniteNumbers_ReadAsNull_AndOtherBadJson_NamesTheLine()
    {
        var dir = WriteRun(Taxonomy(("harm", false)).Raw, [Row("t1").Raw], inferenceSet: []);
        File.WriteAllText(Path.Combine(dir, "inference_set.jsonl"),
            """{"type": "prompt", "test_case_id": "t1", "events": [{"edit": {"tool_args": {"x": NaN, "y": -Infinity, "s": "NaN stays"}}}]}""" + "\n");
        Assert.Empty(AssertAiRun.Read(dir).Missing);

        File.WriteAllText(Path.Combine(dir, "scores.jsonl"), Row("t1").Raw.ToJsonString() + "\n{not json\n");
        var error = Assert.Throws<InvalidDataException>(() => AssertAiRun.Read(dir));
        Assert.Contains("scores.jsonl:2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealAssertTaxonomy_ReadsItsPermissibleCategories_AndARealTestSetItsCases()
    {
        var taxonomy = AssertAiTaxonomy.FromJson(JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "taxonomy.disability_representation.json")))!.AsObject());
        Assert.Equal(26, taxonomy.Categories.Count);
        Assert.Equal(5, taxonomy.Categories.Count(c => c.Permissible));

        var dir = WriteRun(taxonomy.Raw, []);
        File.Copy(Path.Combine(Fixtures, "test_set.disability_representation.jsonl"), Path.Combine(Path.GetDirectoryName(dir)!, "test_set.jsonl"));
        Assert.Equal(["prompt:test_case_000001", "prompt:test_case_000002", "scenario:test_case_000051", "scenario:test_case_000052"],
            AssertAiRun.Read(dir).Missing.Select(m => m.Key.ToString()));
    }

    // ---- ASSERT verdicts as AgentEval results --------------------------------------------------------------------

    [Fact]
    public void EachStatus_BecomesTheAgentEvalResultItMeans_NeverAScoreForAFailedJudge()
    {
        var dir = WriteRun(JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "taxonomy.constructed.json")))!.AsObject(), []);
        File.Copy(Path.Combine(Fixtures, "scores.constructed.jsonl"), Path.Combine(dir, "scores.jsonl"), overwrite: true);

        var results = AssertAiResults.ToEvalResults(AssertAiRun.Read(dir)).Select(r => r.Result).ToList();

        Assert.Equal(["fail", "skipped", "error", "error"], results.Select(r => r.Score.Label));
        Assert.Equal("high", results[0].Score.Severity);   // "Premature line restoration" is not permissible: harm
        Assert.Contains("Premature line restoration", results[0].Details.Summary, StringComparison.Ordinal);
        Assert.Equal(["fail", "pass"], results[0].Details.SubResults!.Select(s => s.Score.Label));   // policy_violation, overrefusal
        Assert.Contains("target_input_refused", results[1].Details.Summary, StringComparison.Ordinal);
        Assert.All(results.Skip(2), r => Assert.Contains("not read as 'no violation'", r.Details.Evidence![0].Message, StringComparison.Ordinal));
        Assert.All(results, r => Assert.False(r.Score.Passed));
    }

    [Fact]
    public void FailingToHelp_FailsAtMedium_NoRelevantCategory_IsInapplicable_AndAClearCase_Passes()
    {
        var taxonomy = Taxonomy(("harm", false), ("help", true));
        var dir = WriteRun(taxonomy.Raw, [
            Row("t1", Node(1, "help", true, true)).Raw,
            Row("t2", Node(0, "harm", false, null), Node(1, "help", false, null)).Raw,
            Row("t3", Node(0, "harm", true, false)).Raw,
        ]);

        var results = AssertAiResults.ToEvalResults(AssertAiRun.Read(dir)).Select(r => r.Result).ToList();

        Assert.Equal(("fail", "medium"), (results[0].Score.Label, results[0].Score.Severity));
        Assert.Equal("inapplicable", results[1].Score.Label);
        Assert.Equal(("pass", true), (results[2].Score.Label, results[2].Score.Passed));
    }

    [Fact]
    public void AnUncalibratedJudge_IsSaidToBeUncalibrated_AndACalibratedOne_CarriesItsNumbers()
    {
        var run = AssertAiRun.Read(WriteRun(Taxonomy(("harm", false)).Raw, [Row("t1", Node(0, "harm", true, false)).Raw]));
        var calibration = new AssertAiJudgeCalibration("azure/gpt-5.4", "golden-hate-unfairness", 20, 0.9, 0.8, 1, 2);

        var plain = AssertAiResults.ToEvalResults(run)[0].Result;
        var calibrated = AssertAiResults.ToEvalResults(run, calibration)[0].Result;

        Assert.Contains("is not calibrated", plain.Details.Evidence!.Single(e => e.Reference == "judge-calibration").Message, StringComparison.Ordinal);
        Assert.Contains("κ 0.800", calibrated.Details.Evidence!.Single(e => e.Reference == "judge-calibration").Message, StringComparison.Ordinal);
        Assert.Equal(calibration, AssertAiJudgeCalibration.FromJson(calibration.ToJson()));
    }

    [Fact]
    public async Task TheVerdictEval_FindsACaseByTypeAndId_ByABareId_OrByTheExportedId_AndSkipsTheRest()
    {
        var dir = WriteRun(Taxonomy(("harm", false)).Raw, [
            Row("t1", Node(0, "harm", true, true)).Raw,
            Row("t2", Node(0, "harm", true, false)).Raw,
            Row("t2", Node(0, "harm", true, true), type: "scenario", tester: "m").Raw,
        ]);
        var eval = new AssertAiVerdictEval(AssertAiRun.Read(dir), new Dictionary<string, AssertAiCaseKey> { ["cal-hu-001"] = new("prompt", "t1") });

        async Task<string> Label(string? id) => (await eval.EvaluateAsync(new EvalInput("q") { CaseId = id })).Score.Label;

        Assert.Equal("fail", await Label("t1"));
        Assert.Equal("fail", await Label("cal-hu-001"));
        Assert.Equal("pass", await Label("prompt:t2"));
        Assert.Equal("fail", await Label("scenario:t2"));
        Assert.Equal("skipped", await Label("t2"));      // two types have it
        Assert.Equal("skipped", await Label("t9"));
        Assert.Equal("skipped", await Label(null));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static List<AssertAiScoreRow> ReadRows(string file) =>
        File.ReadAllLines(Path.Combine(Fixtures, file)).Where(l => l.Trim().Length > 0)
            .Select(l => AssertAiScoreRows.Parse(JsonNode.Parse(l)!.AsObject())).ToList();

    internal static AssertAiTaxonomy Taxonomy(params (string Name, bool Permissible)[] categories)
    {
        var list = new JsonArray();
        foreach (var (name, permissible) in categories)
        {
            list.Add(new JsonObject { ["name"] = name, ["definition"] = name + " definition", ["examples"] = new JsonArray(), ["permissible"] = permissible });
        }

        return AssertAiTaxonomy.FromJson(new JsonObject { ["behavior"] = new JsonObject { ["name"] = "test-behavior", ["definition"] = "d" }, ["behavior_categories"] = list });
    }

    internal static JsonObject Node(int? index, string name, bool relevant, bool? violated)
    {
        var node = new JsonObject { ["node_name"] = name, ["relevant"] = relevant, ["violated"] = violated, ["confidence"] = relevant ? "high" : null, ["evidence_turns"] = new JsonArray(), ["reasoning"] = relevant ? "because" : "" };
        if (index is { } i) node["node_index"] = i;
        return node;
    }

    internal static AssertAiScoreRow Row(string id, params JsonObject[] nodes) => Row(id, "prompt", "", nodes);

    internal static AssertAiScoreRow Row(string id, JsonObject node, string type, string tester = "") => Row(id, type, tester, [node]);

    internal static AssertAiScoreRow Row(string id, string type, string tester, JsonObject[] nodes)
    {
        var violated = nodes.Any(n => n["relevant"]?.GetValue<bool>() != false && n["violated"] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.True);
        var row = new JsonObject
        {
            ["type"] = type, ["test_case_id"] = id, ["behavior"] = "test-behavior", ["judge_model"] = "azure/gpt-5.4", ["target"] = "agent",
            ["tester_model"] = tester, ["score_keys"] = new JsonArray("policy_violation", "overrefusal"), ["not_applicable_score_keys"] = new JsonArray(),
            ["judge_status"] = "ok", ["judge_error"] = null,
            ["verdict"] = new JsonObject
            {
                ["dimensions"] = new JsonObject { ["policy_violation"] = violated, ["overrefusal"] = false },
                ["justification"] = "j",
                ["dimension_justifications"] = new JsonObject { ["policy_violation"] = "pv", ["overrefusal"] = "or" },
                ["node_judgments"] = new JsonArray(nodes.Select(n => (JsonNode)n.DeepClone()).ToArray()),
                ["citations"] = new JsonArray(),
            },
        };
        return AssertAiScoreRows.Parse(row);
    }

    private string WriteRun(JsonObject taxonomy, IEnumerable<JsonObject> rows, IEnumerable<string>? testSet = null, IEnumerable<string>? inferenceSet = null)
    {
        var suite = Path.Combine(_root, "results", "suite-" + Guid.NewGuid().ToString("N")[..8]);
        var run = Path.Combine(suite, "run-1");
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(suite, "taxonomy.json"), taxonomy.ToJsonString());
        File.WriteAllText(Path.Combine(run, "scores.jsonl"), string.Concat(rows.Select(r => r.ToJsonString() + "\n")));
        if (testSet is not null)
        {
            File.WriteAllText(Path.Combine(suite, "test_set.jsonl"), string.Concat(testSet.Select(id => $"{{\"type\": \"prompt\", \"test_case_id\": \"{id}\", \"seed\": {{\"description\": \"x\"}}}}\n")));
        }

        if (inferenceSet is not null)
        {
            File.WriteAllText(Path.Combine(run, "inference_set.jsonl"), string.Concat(inferenceSet.Select(id => $"{{\"type\": \"prompt\", \"test_case_id\": \"{id}\", \"events\": []}}\n")));
        }

        return run;
    }
}
