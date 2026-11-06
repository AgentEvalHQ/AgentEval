// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365Demo.Tests.TestHelpers;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Integrity;

namespace AgentEval.Microsoft365Demo.Tests;

/// <summary>
/// The benchmark is also written as a sealed AEF run under <c>aef/</c>, with the writer the agenteval CLI's eval uses.
/// Pins: the run verifies intact; it records the provider's shared run settings and the subject
/// <c>agent:&lt;display name&gt;</c>; each case's state is the demo's verdict (PASS is passed, FAIL is failed, ERROR is
/// error, NOT MEASURED is not_measured with its cause), so a case the demo did not measure is never a pass or a fail;
/// each case keeps its turn's evidence document; a benchmark that stopped early closes the run aborted; and the zip
/// carries the run.
/// </summary>
public sealed class AefRunTests : IDisposable
{
    private const string EmptyMarker = "EMPTY-MARKER-4d2b";
    private const string AmbiguousMarker = "AMBIGUOUS-MARKER-8e3f";
    private const string PauseMarker = "PAUSE-MARKER-1c6a";

    private readonly DemoRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task FullRun_WritesASealedAefRun_WithTheSharedRunSettings_TheAgentSubject_AndEachTurnsDocument()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Judge = new FakeJudgeChatClient();

        var run = await _rig.RunAsync(DemoRig.Consent, "--zip");

        run.AssertExit(0);
        var dir = AefRunDirectory(run);
        AssertIntact(dir);
        Assert.Empty(Directory.GetFiles(Path.Combine(run.Folder.FullName, "aef"), "*.lock", SearchOption.AllDirectories));

        // results.json names the same run, and summary.md says how to check it.
        var aef = run.Json("results.json").GetProperty("aef");
        Assert.Equal(Relative(run, dir), aef.GetProperty("run").GetString());
        Assert.False(string.IsNullOrEmpty(aef.GetProperty("runHash").GetString()));
        Assert.Equal(JsonValueKind.Null, aef.GetProperty("abortReason").ValueKind);
        // In the form a tester who follows only the demo can run, and without the run folder's own path.
        Assert.Contains($"{VerifyCommand} \"<run folder>/{Relative(run, dir)}\"", run.Text("summary.md"), StringComparison.Ordinal);
        Assert.DoesNotContain("agenteval aef verify", run.Text("summary.md"), StringComparison.Ordinal);
        Assert.Contains(run.OutLines, l => l.Contains($"from the repository root with: {VerifyCommand} \"", StringComparison.Ordinal));
        Assert.DoesNotContain("agenteval aef verify", run.Out, StringComparison.Ordinal);

        // aef/ holds the answers too and is sealed: the tester deletes it, never edits it.
        Assert.Contains("`aef/` holds the same answers and is sealed: never edit it. Delete it if it holds anything you cannot share, and say so.",
            run.Text("summary.md"), StringComparison.Ordinal);

        var header = Document(dir, "run.json");
        Assert.Equal("completed", (string?)header["status"]);
        Assert.Equal(AefEvalWriter.Ref("agent", "Demo test agent"), (string?)header["subject"]!["ref"]);
        Assert.Equal("agent", (string?)header["subject"]!["kind"]);
        Assert.Equal("mocked", (string?)header["execution"]!["targetMode"]);   // the host supplied the transport
        Assert.Equal("on", (string?)header["contentCapture"]);

        // The run settings are the provider's shared ones (the CLI records the same), with the run's content policy.
        var config = header["config"]!.AsObject();
        var expected = Microsoft365RunSettings.For(
            Microsoft365Config.Load(_rig.ConfigFile), DiagnosticProfile.PublicAnswerOnly, EvidenceContentPolicy.SelectedContent);
        foreach (var (key, value) in expected)
        {
            Assert.True(JsonNode.DeepEquals(value, config[key]), $"config.{key}: expected {value?.ToJsonString()}, got {config[key]?.ToJsonString()}");
        }

        Assert.Equal("1.0", (string?)config["a2aVersion"]);
        Assert.Equal("PublicAnswerOnly", (string?)config["m365Profile"]);
        Assert.Equal("default", (string?)config["m365AgentId"]);
        Assert.Equal("workiq.svc.cloud.microsoft", (string?)config["m365EndpointHost"]);
        Assert.Equal("SelectedContent", (string?)config["m365ContentPolicy"]);
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", header.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("22222222-2222-2222-2222-222222222222", header.ToJsonString(), StringComparison.Ordinal);

        // One root line per case, each passed as the demo says, each with its turn's evidence document and answer.
        var roots = Roots(dir);
        Assert.Equal(DemoRig.DefaultCaseIds, roots.Select(r => (string)r["caseId"]!).ToArray());
        foreach (var root in roots)
        {
            Assert.Equal("passed", (string?)root["state"]);
            var document = JsonNode.Parse(EvidenceText(dir, root, "document"))!;
            Assert.Equal(Microsoft365Evidence.SchemaVersion, (string?)document["schemaVersion"]);
            Assert.Equal((string?)root["caseId"], (string?)document["identity"]!["caseId"]);
            Assert.StartsWith(DemoRig.AnswerStart, EvidenceText(dir, root, "output"), StringComparison.Ordinal);
        }

        // The zip carries the run.
        using var archive = ZipFile.OpenRead(run.Zip.FullName);
        Assert.Contains(archive.Entries, e => e.FullName.Replace('\\', '/').EndsWith(Relative(run, dir) + "/run.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PassFailErrorAndNotMeasured_EachCasesAefStateIsTheDemosVerdict()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Router.RouteSendMessage(EmptyMarker, DemoRig.EmptyAnswer);
        _rig.Router.RouteSendMessage(AmbiguousMarker, DemoRig.AmbiguousAnswer);
        _rig.Router.RouteSendMessage(PauseMarker, DemoRig.InputRequiredConfirmation);
        _rig.Judge = new FakeJudgeChatClient();   // it passes every text it grades, an empty one included
        var dataset = _rig.WriteDataset($$"""
            [
              { "id": "case_pass", "input": "In one sentence, what is on my calendar?", "evaluation_criteria": ["Answers in one sentence"] },
              { "id": "case_fail", "input": "What did the committee decide? {{EmptyMarker}}", "evaluation_criteria": ["Names the decision"] },
              { "id": "case_not_measured", "input": "When does the pilot start? {{AmbiguousMarker}}", "evaluation_criteria": ["Gives one date"] },
              { "id": "case_error", "input": "Describe the kickoff {{PauseMarker}} in one sentence.", "evaluation_criteria": ["Does not create anything"] }
            ]
            """);

        var run = await _rig.RunAsync(DemoRig.Consent, "--dataset", dataset.FullName);

        run.AssertExit(1);   // a case failed
        var verdicts = run.Json("results.json").GetProperty("cases").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("verdict").GetString()!);
        Assert.Equal("PASS", verdicts["case_pass"]);
        Assert.Equal("FAIL", verdicts["case_fail"]);
        Assert.Equal("NOT MEASURED", verdicts["case_not_measured"]);
        Assert.Equal("ERROR", verdicts["case_error"]);

        var dir = AefRunDirectory(run);
        AssertIntact(dir);
        var roots = Roots(dir).ToDictionary(r => (string)r["caseId"]!);
        Assert.Equal(verdicts.Keys.Order(StringComparer.Ordinal), roots.Keys.Order(StringComparer.Ordinal));
        foreach (var (id, verdict) in verdicts)
        {
            Assert.Equal(StateOf(verdict), (string?)roots[id]["state"]);
        }

        // A measured case carries its score; a typed absence carries a reason and no score.
        Assert.NotNull(roots["case_pass"]["scores"]);
        Assert.NotNull(roots["case_fail"]["scores"]);
        foreach (var id in new[] { "case_not_measured", "case_error" })
        {
            Assert.Null(roots[id]["scores"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)roots[id]["reason"]), $"{id} has no reason.");
        }

        // The FAIL says why: its judge passed every criterion, and the check that failed it is a failed assertion that
        // decided the case, never a threshold its score met.
        var failChildren = Children(dir, roots["case_fail"]);
        Assert.Contains(failChildren, c => (string?)c["evaluator"]!["id"] == "agenteval.assertion" && (string?)c["state"] == "failed");
        Assert.All(failChildren.Where(c => (string?)c["evaluator"]!["id"] == "agenteval.judge.criterion"), c => Assert.Equal("passed", (string?)c["state"]));
        Assert.Equal("failure-effect", (string?)roots["case_fail"]["aggregation"]!["rulePath"]);
        Assert.DoesNotContain(Children(dir, roots["case_pass"]), c => (string?)c["state"] == "failed");
        Assert.Equal("threshold", (string?)roots["case_pass"]["aggregation"]!["rulePath"]);

        // The reasons say what happened: nothing threw for the unselected answer, and the paused turn's error is its own.
        Assert.Equal(Microsoft365Evidence.NoAnswerSelectedReason, (string?)roots["case_not_measured"]["reason"]);
        Assert.StartsWith("the evaluation threw ", (string?)roots["case_error"]["reason"], StringComparison.Ordinal);
        Assert.Contains("`not_measured`", run.Text("summary.md"), StringComparison.Ordinal);

        // The empty text of a turn with no selected answer is not recorded as the agent's answer; its document is kept.
        Assert.Null(EvidenceTextOrNull(dir, roots["case_not_measured"], "output"));
        Assert.Equal("case_not_measured", (string?)JsonNode.Parse(EvidenceText(dir, roots["case_not_measured"], "document"))!["identity"]!["caseId"]);

        // The paused turn's evidence document stays with its ERROR case.
        Assert.Equal("TASK_STATE_INPUT_REQUIRED", (string?)JsonNode.Parse(EvidenceText(dir, roots["case_error"], "document"))!["taskState"]);
    }

    [Fact]
    public async Task NoJudge_EveryCaseIsNotMeasured_SoNoAefLineIsAPassOrAFail()
    {
        // Without a judge the harness alone passes every non-empty answer. The demo says NOT MEASURED, and so does its record.
        _rig.WithHealthyEndpoint().WithCompletedTurns();

        var run = await _rig.RunAsync(DemoRig.Consent);

        run.AssertExit(11);
        var dir = AefRunDirectory(run);
        AssertIntact(dir);
        var roots = Roots(dir);
        Assert.Equal(DemoRig.DefaultCaseIds, roots.Select(r => (string)r["caseId"]!).ToArray());
        Assert.All(roots, r =>
        {
            Assert.Equal("not_measured", (string?)r["state"]);
            Assert.Null(r["scores"]);
            Assert.Equal(BenchmarkAef.NoJudgeReason, (string?)r["reason"]);
        });

        // The answers were received and are kept; no judge graded them, so the run names none.
        Assert.StartsWith(DemoRig.AnswerStart, EvidenceText(dir, roots[0], "output"), StringComparison.Ordinal);
        Assert.Null(Document(dir, "run.json")["judges"]);
    }

    [Fact]
    public async Task TheJudgeCallFails_ACompletedCaseIsAnErrorWithItsAnswer_AndAFailIsRecordedWithoutAScore()
    {
        // The judge throws on every call. A case whose checks passed has no verdict (error); a case whose answer is empty
        // failed a check (FAIL) while the judge produced no grade, so its record carries no score.
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Router.RouteSendMessage(EmptyMarker, DemoRig.EmptyAnswer);
        _rig.Judge = new ThrowingJudgeChatClient { Status = System.Net.HttpStatusCode.ServiceUnavailable };
        var dataset = _rig.WriteDataset($$"""
            [
              { "id": "case_judge_down", "input": "In one sentence, what is on my calendar?", "evaluation_criteria": ["Answers in one sentence"] },
              { "id": "case_empty", "input": "What did the committee decide? {{EmptyMarker}}", "evaluation_criteria": ["Names the decision"] }
            ]
            """);

        var run = await _rig.RunAsync(DemoRig.Consent, "--dataset", dataset.FullName);

        run.AssertExit(1);   // a case failed a check
        var dir = AefRunDirectory(run);
        AssertIntact(dir);
        var roots = Roots(dir).ToDictionary(r => (string)r["caseId"]!);

        var down = roots["case_judge_down"];
        Assert.Equal("error", (string?)down["state"]);
        Assert.StartsWith("the judge produced no verdict", (string?)down["reason"], StringComparison.Ordinal);
        Assert.Null(down["scores"]);
        Assert.StartsWith(DemoRig.AnswerStart, EvidenceText(dir, down, "output"), StringComparison.Ordinal);   // the agent answered
        Assert.Equal("case_judge_down", (string?)JsonNode.Parse(EvidenceText(dir, down, "document"))!["identity"]!["caseId"]);

        var empty = roots["case_empty"];
        Assert.Equal("failed", (string?)empty["state"]);
        Assert.Null(empty["scores"]);
        Assert.Null(empty["aggregation"]!["score"]);
        Assert.Null(empty["aggregation"]!["threshold"]);
        Assert.Contains(Children(dir, empty), c => (string?)c["evaluator"]!["id"] == "agenteval.assertion" && (string?)c["state"] == "failed");

        Assert.Contains("`error` when the judge's call failed or gave no verdict", run.Text("summary.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentIdOverride_ForAnotherAgent_TheSubjectNamesTheTestedId_NotTheConfigsAgentName()
    {
        // The config names its own agent; --agent-id tests another one. The sealed record must not merge the two.
        _rig.WriteConfig(
            """{ "tenantId": "11111111-1111-1111-1111-111111111111", "appClientId": "22222222-2222-2222-2222-222222222222", "agentId": "U_a.sales", "agentName": "Sales Bot" }""");
        _rig.WithHealthyEndpoint(DemoRig.AgentCard).WithCompletedTurns();
        _rig.Judge = new FakeJudgeChatClient();

        var run = await _rig.RunAsync(DemoRig.Consent, "--agent-id", DemoRig.AgentId);

        var dir = AefRunDirectory(run);
        var header = Document(dir, "run.json");
        Assert.Equal(AefEvalWriter.Ref("agent", DemoRig.AgentId), (string?)header["subject"]!["ref"]);
        Assert.Equal(DemoRig.AgentId, (string?)header["config"]!["m365AgentId"]);
        Assert.DoesNotContain("Sales", header.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentIdOverride_ForTheConfigsOwnAgent_KeepsItsName()
    {
        _rig.WriteConfig(
            $$"""{ "tenantId": "11111111-1111-1111-1111-111111111111", "appClientId": "22222222-2222-2222-2222-222222222222", "agentId": "{{DemoRig.AgentId}}", "agentName": "Sales Bot" }""");
        _rig.WithHealthyEndpoint(DemoRig.AgentCard).WithCompletedTurns();
        _rig.Judge = new FakeJudgeChatClient();

        var run = await _rig.RunAsync(DemoRig.Consent, "--agent-id", DemoRig.AgentId);

        var header = Document(AefRunDirectory(run), "run.json");
        Assert.Equal(AefEvalWriter.Ref("agent", "Sales Bot"), (string?)header["subject"]!["ref"]);
    }

    [Fact]
    public async Task TurnCapStopsTheBenchmark_TheAefRunClosesAborted_WithALineOnlyForTheCasesThatRan()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Judge = new FakeJudgeChatClient();

        var run = await _rig.RunAsync(DemoRig.Consent, "--max-turns", "3");

        run.AssertExit(11);
        var dir = AefRunDirectory(run);
        AssertIntact(dir);
        var header = Document(dir, "run.json");
        Assert.Equal("aborted", (string?)header["status"]);
        Assert.Equal("turn-cap-reached", (string?)header["abortReason"]);
        var roots = Roots(dir);
        Assert.Equal(DemoRig.DefaultCaseIds.Take(2).ToArray(), roots.Select(r => (string)r["caseId"]!).ToArray());
        Assert.All(roots, r => Assert.Equal("passed", (string?)r["state"]));
        Assert.Equal("turn-cap-reached", run.Json("results.json").GetProperty("aef").GetProperty("abortReason").GetString());
    }

    [Fact]
    public async Task NoBenchmark_WritesNoAefRun()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();

        var run = await _rig.RunAsync(DemoRig.Consent, "--no-benchmark");

        run.AssertExit(0);
        Assert.False(Directory.Exists(Path.Combine(run.Folder.FullName, "aef")), "aef/ is written only when the benchmark runs.");
        Assert.Equal(JsonValueKind.Null, run.Json("results.json").GetProperty("aef").ValueKind);
    }

    [Fact]
    public void SharedRunSettings_RecordTheAgentAndTheProfile_NeverTheTenantOrTheApp_AndNoContentPolicyWithoutOne()
    {
        var file = _rig.WriteConfig(
            """{ "tenantId": "11111111-1111-1111-1111-111111111111", "appClientId": "22222222-2222-2222-2222-222222222222", "agentId": "T_contoso.sales-agent", "endpoint": "https://workiq.contoso.example/a2a/", "trustedHosts": ["workiq.contoso.example"] }""",
            "agent-config.json");
        var config = Microsoft365Config.Load(file);

        var settings = Microsoft365RunSettings.For(config, DiagnosticProfile.Native118Diagnostics, contentPolicy: null);

        Assert.Equal(Microsoft365Evidence.A2AVersion, (string?)settings[Microsoft365RunSettings.A2AVersionKey]);
        Assert.Equal("Native118Diagnostics", (string?)settings[Microsoft365RunSettings.ProfileKey]);
        Assert.Equal(Microsoft365Evidence.NormalizerVersion, (string?)settings[Microsoft365RunSettings.NormalizerVersionKey]);
        Assert.Equal(Microsoft365Evidence.ProviderProfileVersion, (string?)settings[Microsoft365RunSettings.ProviderProfileVersionKey]);
        Assert.Equal(config.ComputeIdentitySha256(), (string?)settings[Microsoft365RunSettings.ConfigSha256Key]);
        Assert.Equal("T_contoso.sales-agent", (string?)settings[Microsoft365RunSettings.AgentIdKey]);
        Assert.Equal("workiq.contoso.example", (string?)settings[Microsoft365RunSettings.EndpointHostKey]);
        Assert.False(settings.ContainsKey(Microsoft365RunSettings.ContentPolicyKey));
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", settings.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("22222222-2222-2222-2222-222222222222", settings.ToJsonString(), StringComparison.Ordinal);
    }

    /// <summary>The command the reports print to check the AEF run (the CLI from this repository).</summary>
    private const string VerifyCommand = "dotnet run --project src/AgentEval.Cli --framework net10.0 -- aef verify";

    /// <summary>The child lines of <paramref name="root"/>.</summary>
    private static List<JsonNode> Children(string directory, JsonNode root) =>
        File.ReadAllLines(Path.Combine(directory, "results.ndjson"))
            .Where(l => l.Length > 0)
            .Select(l => JsonNode.Parse(l)!)
            .Where(l => (string?)l["parentResultId"] == (string?)root["resultId"])
            .ToList();

    /// <summary>The AEF state the demo's verdict maps to.</summary>
    private static string StateOf(string verdict) => verdict switch
    {
        "PASS" => "passed",
        "FAIL" => "failed",
        "NOT MEASURED" => "not_measured",
        _ => "error",
    };

    /// <summary>The one AEF run under the run folder's <c>aef/</c>.</summary>
    private static string AefRunDirectory(DemoRunResult run)
    {
        var aef = Path.Combine(run.Folder.FullName, "aef");
        Assert.True(Directory.Exists(aef), $"The run folder has no aef/.\n{run.Describe()}");
        return Path.GetDirectoryName(Assert.Single(Directory.GetFiles(aef, "run.json", SearchOption.AllDirectories)))!;
    }

    private static string Relative(DemoRunResult run, string directory) =>
        Path.GetRelativePath(run.Folder.FullName, directory).Replace('\\', '/');

    private static void AssertIntact(string directory)
    {
        var report = AefRunVerifier.Verify(directory);
        Assert.Empty(report.Problems);
        Assert.Equal(AefOutcome.Intact, report.Outcome);
    }

    private static JsonObject Document(string directory, string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(directory, name)))!.AsObject();

    /// <summary>The root line of each case (path <c>case</c>, no parent), in the order the run wrote them.</summary>
    private static List<JsonNode> Roots(string directory) =>
        File.ReadAllLines(Path.Combine(directory, "results.ndjson"))
            .Where(l => l.Length > 0)
            .Select(l => JsonNode.Parse(l)!)
            .Where(l => (string?)l["path"] == "case" && l["parentResultId"] is null)
            .ToList();

    /// <summary>The text of the evidence of <paramref name="kind"/> that <paramref name="line"/> references; null when it references none.</summary>
    private static string? EvidenceTextOrNull(string directory, JsonNode line, string kind)
    {
        var ids = line["evidence"]?.AsArray().Select(e => (string)e!).ToHashSet(StringComparer.Ordinal) ?? [];
        var path = Path.Combine(directory, "evidence.ndjson");
        if (ids.Count == 0 || !File.Exists(path))
        {
            return null;
        }

        var entry = File.ReadAllLines(path)
            .Where(l => l.Length > 0)
            .Select(l => JsonNode.Parse(l)!)
            .SingleOrDefault(e => ids.Contains((string)e["evidenceId"]!) && (string?)e["kind"] == kind);
        if (entry is null)
        {
            return null;
        }

        var hex = ((string)entry["link"]!["blob"]!)["sha256:".Length..];
        return File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(directory, "blobs"), hex, SearchOption.AllDirectories)));
    }

    private static string EvidenceText(string directory, JsonNode line, string kind)
    {
        var text = EvidenceTextOrNull(directory, line, kind);
        Assert.True(text is not null, $"The {kind} evidence of case {(string?)line["caseId"]} is missing.");
        return text!;
    }
}
