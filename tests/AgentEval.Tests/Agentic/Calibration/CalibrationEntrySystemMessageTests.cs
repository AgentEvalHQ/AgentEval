// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Evals.Agentic.Calibration;
using AgentEval.Tests.Agentic.Golden;
using AgentEval.Tests.Interop.AssertAi;
using Xunit;

namespace AgentEval.Tests.Agentic.Calibration;

/// <summary>
/// A calibration entry carries the agent's instructions in a <c>systemMessage</c> column, and the runner and the ASSERT
/// export hand them on as <c>EvalInput.SystemMessage</c> (T73). Before, the entry had no such
/// member: the loader dropped the key without a word, and the four <c>task_adherence</c> judges that grade against the
/// instructions were never measured on a golden. A would-be pass was withheld, and the key left calibration.
/// </summary>
public sealed class CalibrationEntrySystemMessageTests : IDisposable
{
    private const string Instructions = "You write customer emails. Never use bullet points.";

    private const string Line = """
        {"scenarioId":"t-sm-1","evaluatorKey":"task_adherence","systemMessage":"You write customer emails. Never use bullet points.","input":"Draft a customer email.","agentResponse":"Dear Ms. Andersen, your ticket has been escalated.","expectedVerdict":"pass","expectedScoreMin":0.8,"expectedScoreMax":1.0,"rationale":"kept the rule"}
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cal-system-message-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static async Task<CalibrationDataset> LoadAsync(string jsonl)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonl));
        return await new CalibrationDatasetLoader().LoadAsync("system", stream);
    }

    [Fact]
    public async Task AnEntry_CarriesItsSystemMessage_IntoTheEvalInput()
    {
        var input = Assert.Single((await LoadAsync(Line)).Entries).ToEvalInput();

        Assert.Equal(Instructions, input.SystemMessage);
        Assert.Equal("Draft a customer email.", input.Query);   // the rules are not folded into the query
    }

    [Fact]
    public async Task AnEntryWithoutTheColumn_HasNoSystemMessage()
    {
        // Absent stays absent: no placeholder text stands in for instructions nobody wrote. (The tests read the value
        // through ToEvalInput, so this file also compiles against the code before the column, for the red check.)
        var entry = Assert.Single((await LoadAsync(
            """{"scenarioId":"t-sm-2","evaluatorKey":"task_adherence","input":"q","agentResponse":"r","expectedVerdict":"pass","expectedScoreMin":0.5,"expectedScoreMax":1.0,"rationale":"x"}""")).Entries);

        Assert.Null(entry.ToEvalInput().SystemMessage);
    }

    [Fact]
    public async Task TheRunner_ShowsTaskAdherencesRuleJudges_TheEntrysInstructions_SoTheRecordIsMeasured()
    {
        // Without the instructions the four rule leaves skip, the composite withholds its pass, and the runner leaves the
        // whole key out of the category (NotMeasured 1, ExcludedKeys [task_adherence], one judge call: the goal leaf).
        var judge = new RecordingScoreEvaluator(100);
        var runner = new CalibrationRunner(key => key == "task_adherence" ? AgenticEvaluatorFixture.BuildEvaluator(key, judge) : null);

        var report = await runner.RunAsync([await LoadAsync(Line)]);

        var system = report.PerCategory["system"];
        Assert.Equal(0, system.NotMeasured);
        Assert.Empty(system.ExcludedKeys);
        Assert.Equal(1, system.EntryCount);
        Assert.Equal(5, judge.Inputs.Count);
        Assert.Equal(4, judge.Inputs.Count(i => i.Contains("<agent_instructions>", StringComparison.Ordinal)
                                               && i.Contains("Never use bullet points.", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheAssertExport_CarriesAnEntrysSystemMessage_AsItsFirstEvent()
    {
        // The export built its own input from the query, response and tool calls, so the instructions never reached
        // ASSERT's judge. An entry without them still starts with the user's message.
        Directory.CreateDirectory(_root);
        var golden = Path.Combine(_root, "golden-test.jsonl");
        var taxonomy = Path.Combine(_root, "taxonomy.json");
        File.WriteAllLines(golden, [GoldenLine("cal-sm-1", Instructions), GoldenLine("cal-sm-2", systemMessage: null)]);
        File.WriteAllText(taxonomy, AssertAiRunTests.Taxonomy(("harm", false)).Raw.ToJsonString());
        var outDir = Path.Combine(_root, "kit");
        var stderr = new StringWriter();

        var exported = await AssertAiCommand.RunExportAsync([golden], [], taxonomy, "azure/gpt-5.4", outDir, "agenteval", "judge-1", null, TextWriter.Null, stderr);

        Assert.True(exported == ExitCodes.Success, stderr.ToString());
        var rows = File.ReadAllLines(Path.Combine(outDir, "results", "agenteval", "judge-1", "inference_set.jsonl"));
        var withRules = JsonNode.Parse(rows[0])!["events"]!.AsArray();
        Assert.Equal(["set_system_message", "add_message", "add_message"], withRules.Select(e => (string)e!["edit"]!["type"]!));
        Assert.Equal(Instructions, (string)withRules[0]!["edit"]!["message"]!["content"]!);
        var withoutRules = JsonNode.Parse(rows[1])!["events"]!.AsArray();
        Assert.Equal(["add_message", "add_message"], withoutRules.Select(e => (string)e!["edit"]!["type"]!));
    }

    private static string GoldenLine(string id, string? systemMessage)
    {
        var line = new JsonObject
        {
            ["scenarioId"] = id, ["evaluatorKey"] = "task_adherence", ["input"] = "Draft a customer email.", ["agentResponse"] = "Dear Ms. Andersen, …",
            ["expectedVerdict"] = "pass", ["expectedScoreMin"] = 0.8, ["expectedScoreMax"] = 1.0, ["rationale"] = "r",
        };
        if (systemMessage is not null)
        {
            line["systemMessage"] = systemMessage;
        }

        return line.ToJsonString();
    }
}
