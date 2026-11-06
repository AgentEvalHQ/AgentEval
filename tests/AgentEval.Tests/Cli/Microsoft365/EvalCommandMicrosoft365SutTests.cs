// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Cli.Microsoft365;
using AgentEval.Core;
using AgentEval.Microsoft365;
using Xunit;

namespace AgentEval.Tests.Cli.Microsoft365;

/// <summary>
/// <c>eval --sut microsoft365</c> through the shared <c>ISutTarget</c> seam. Mirrors
/// <c>EvalCommandCopilotStudioSutTests</c>' shape: safety-gate validation (throws before any build/network), the
/// dataset-first precedence, then a full credential-free eval via <c>sutOverride</c> with a benign fake agent (the
/// stand-in is independent of <c>WorkIqEvaluableAgent</c>, whose live turn has its own tests).
/// </summary>
public class EvalCommandMicrosoft365SutTests
{
    // ── safety-gate validation (throws before any build/network) ──

    [Fact]
    public async Task Eval_Microsoft365_WithoutConsent_Refuses()
    {
        var dataset = CreateTempDataset();
        try
        {
            var opts = BaseOptions(dataset, ackConsent: false, config: null);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EvalCommand.ExecuteAsync(opts, default));
            Assert.Contains("i-understand-live-side-effects", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dataset); }
    }

    [Fact]
    public async Task Eval_Microsoft365_WithoutConfig_Refuses()
    {
        var dataset = CreateTempDataset();
        try
        {
            var opts = BaseOptions(dataset, ackConsent: true, config: null);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EvalCommand.ExecuteAsync(opts, default));
            Assert.Contains("m365-config", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dataset); }
    }

    [Fact]
    public async Task Eval_Microsoft365_NegativeMaxTurns_Refuses()
    {
        var dataset = CreateTempDataset();
        var cfg = WriteValidConfig();
        try
        {
            var opts = BaseOptions(dataset, ackConsent: true, config: cfg, maxTurns: -1);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EvalCommand.ExecuteAsync(opts, default));
            Assert.Contains("m365-max-turns", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dataset); TryDelete(cfg); }
    }

    [Fact]
    public async Task Eval_Microsoft365_BadProfile_Refuses()
    {
        var dataset = CreateTempDataset();
        var cfg = WriteValidConfig();
        try
        {
            var opts = BaseOptions(dataset, ackConsent: true, config: cfg, profile: "bogus");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EvalCommand.ExecuteAsync(opts, default));
            Assert.Contains("m365-profile", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dataset); TryDelete(cfg); }
    }

    [Fact]
    public async Task Eval_Microsoft365_ConfigMissingAField_FailsFastBeforeAnyTurn()
    {
        var dataset = CreateTempDataset();
        var cfg = WriteConfig("{ \"tenantId\": \"tenant-1\" }");
        try
        {
            var fake = new FakeEvaluableAgent();
            var opts = BaseOptions(dataset, ackConsent: true, config: cfg);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EvalCommand.ExecuteAsync(opts, default, sutOverride: fake));

            Assert.Contains("appClientId", ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, fake.Invocations);
        }
        finally { TryDelete(dataset); TryDelete(cfg); }
    }

    [Fact]
    public async Task Eval_UnknownSut_NamesMicrosoft365AndCopilotStudio()
    {
        var dataset = CreateTempDataset();
        try
        {
            var opts = BaseOptions(dataset, ackConsent: true, config: null, sut: "bogus");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EvalCommand.ExecuteAsync(opts, default));
            Assert.Contains("microsoft365", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("copilot-studio", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dataset); }
    }

    [Fact]
    public async Task Eval_MissingDataset_ThrowsBeforeSutValidation()
    {
        // Dataset-first precedence on the --sut path (the regression guard the Copilot Studio tests also keep): a
        // missing consent must not mask a typo'd --dataset path.
        var opts = BaseOptions(new FileInfo("/nonexistent/path/to/dataset.yaml"), ackConsent: false, config: null);
        await Assert.ThrowsAsync<FileNotFoundException>(() => EvalCommand.ExecuteAsync(opts, default));
    }

    // ── the command line binds the m365 flags ──

    [Fact]
    public void EvalCommand_Create_ParsesTheM365Options()
    {
        var command = EvalCommand.Create();

        var parsed = command.Parse("--dataset d.yaml --sut microsoft365 --i-understand-live-side-effects --m365-config c.json --m365-max-turns 2 --m365-profile native118 --m365-capture-raw raw --m365-verbose --m365-trace t.jsonl");

        Assert.Empty(parsed.Errors);
    }

    // ── credential-free end-to-end eval via the sutOverride seam ──

    [Fact]
    public async Task Eval_Microsoft365_BenignAgent_RunsCleanOffline()
    {
        var dataset = CreateTempDataset();
        var cfg = WriteValidConfig();
        try
        {
            var fake = new FakeEvaluableAgent();
            var opts = BaseOptions(dataset, ackConsent: true, config: cfg);

            var exit = await EvalCommand.ExecuteAsync(opts, default, sutOverride: fake);

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Equal(1, fake.Invocations);   // one case, one turn: the stand-in was actually driven
        }
        finally { TryDelete(dataset); TryDelete(cfg); }
    }

    [Fact]
    public async Task Eval_Microsoft365_RunsGreaterThan1_WithMetricsAndNoJudge_WarnsInsteadOfThrowing()
    {
        // Same guard as the Copilot Studio twin: `--runs 5 --metrics llm_relevance` with no --judge (a --sut target
        // exposes no raw IChatClient to fall back to) must reach the warning-and-ignore path, never throw.
        var dataset = CreateTempDataset();
        var cfg = WriteValidConfig();
        try
        {
            var fake = new FakeEvaluableAgent();
            var opts = BaseOptions(dataset, ackConsent: true, config: cfg, runs: 5, metrics: "llm_relevance");

            var exit = await EvalCommand.ExecuteAsync(opts, default, sutOverride: fake);

            Assert.True(exit is ExitCodes.Success or ExitCodes.TestFailure);
            Assert.Equal(5, fake.Invocations);
        }
        finally { TryDelete(dataset); TryDelete(cfg); }
    }

    // ── a turn that does not complete is an error row, never a verdict ──

    [Theory]
    [InlineData(WorkIqTurnOutcome.Indeterminate)]
    [InlineData(WorkIqTurnOutcome.Failed)]
    [InlineData(WorkIqTurnOutcome.InputRequired)]
    public async Task Eval_Microsoft365_NonCompletedTurn_IsAnErrorRowExit11(WorkIqTurnOutcome outcome)
    {
        var dataset = CreateTempDataset();
        var cfg = WriteValidConfig();
        var root = Directory.CreateTempSubdirectory("agenteval-eval-m365-notcompleted-");
        try
        {
            // A REAL WorkIqEvaluableAgent (completion gate, typed exception, turn sink) over a scripted conversation.
            var conversation = new AgentEval.Tests.Microsoft365.Agent.FakeWorkIqConversation
            {
                OnRun = (request, _) => Task.FromResult(AgentEval.Tests.Microsoft365.Agent.TurnResults.NotCompleted(request, outcome)),
            };
            var sink = new CliTurnSink(verbose: false, TextWriter.Null);
            var agent = AgentEval.Microsoft365.Microsoft365AgentFactory.FromConversation(conversation, "m365-not-completed", onTurn: sink.Record);
            await using (agent)
            {
                var report = new FileInfo(Path.Combine(root.FullName, "report.json"));
                var opts = new EvalOptions
                {
                    Dataset = dataset,
                    Sut = "microsoft365",
                    AckLiveSideEffects = true,
                    TargetOptions = new Dictionary<string, ISutTargetOptions?>
                    {
                        ["microsoft365"] = new Microsoft365SutOptions { ConfigFile = cfg, Profile = "public" },
                    },
                    Format = "json",
                    Output = report,
                    Aef = new DirectoryInfo(Path.Combine(root.FullName, "aef")),
                    SubjectVersion = "1",
                    Quiet = true,
                };

                var exit = await RunWithContentCaptureAsync(opts, agent, sink);

                Assert.Equal(ExitCodes.GateIndeterminate, exit);   // not measured: no verdict, and never a pass

                // The case is an error row: no pass, the safe not-completed message, and nothing graded.
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(report.FullName))!;
                var row = Assert.Single(json["results"]!.AsArray())!;
                Assert.False((bool)row["passed"]!);
                var error = (string?)row["error"];
                Assert.NotNull(error);
                Assert.Contains("Work IQ turn did not complete", error, StringComparison.Ordinal);
                Assert.Contains($"outcome={outcome}", error, StringComparison.Ordinal);
                Assert.Null(row["score"]?.GetValue<double?>());

                // The sink saw the turn before the throw, under the case's AEF id, and the AEF run carries its document.
                Assert.Equal(1, sink.Count);
                var turn = sink.LatestFor("test1", since: 0);
                Assert.NotNull(turn);
                Assert.Equal(outcome, turn.Outcome);
                Assert.Equal("test1", turn.Identity.CaseId);
                var run = Path.GetDirectoryName(Assert.Single(Directory.GetFiles(Path.Combine(root.FullName, "aef"), "run.json", SearchOption.AllDirectories)))!;
                var evidence = File.ReadAllLines(Path.Combine(run, "evidence.ndjson"))
                    .Select(l => System.Text.Json.Nodes.JsonNode.Parse(l)!)
                    .Where(l => (string?)l["kind"] == "document")
                    .ToArray();
                Assert.Single(evidence);
                Assert.Equal(1, conversation.Turns.Count);
            }
        }
        finally
        {
            TryDelete(dataset);
            TryDelete(cfg);
            try { root.Delete(recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
        }
    }

    // ── a turn that completed with several answer candidates and none selected is NOT MEASURED, never a failure ──

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Eval_Microsoft365_CompletedTurnWithSeveralCandidatesAndNoneSelected_IsNotMeasuredExit11(int runs)
    {
        // The agent returns an empty text that is not its answer. The harness grades that empty text as a failure;
        // eval must record the case (every trial, with --runs) as not measured instead, as the demo does.
        var dataset = CreateTempDataset();
        var cfg = WriteValidConfig();
        var root = Directory.CreateTempSubdirectory("agenteval-eval-m365-ambiguous-");
        try
        {
            var conversation = new AgentEval.Tests.Microsoft365.Agent.FakeWorkIqConversation
            {
                OnRun = (request, _) => Task.FromResult(AgentEval.Tests.Microsoft365.Agent.TurnResults.Completed(request) with
                {
                    AnswerText = null,
                    AnswerCompleteness = OutputCompleteness.Ambiguous,
                }),
            };
            var stderr = new StringWriter();
            var sink = new CliTurnSink(verbose: false, stderr);
            var agent = AgentEval.Microsoft365.Microsoft365AgentFactory.FromConversation(conversation, "m365-ambiguous", onTurn: sink.Record);
            await using (agent)
            {
                var report = new FileInfo(Path.Combine(root.FullName, "report.json"));
                var opts = OptionsWithAef(dataset, cfg, report, root, runs);

                var exit = await RunWithContentCaptureAsync(opts, agent, sink);

                Assert.Equal(ExitCodes.GateIndeterminate, exit);   // no verdict: neither a pass nor a failure
                Assert.Equal(runs, conversation.Turns.Count);
                Assert.Contains("recorded as NOT MEASURED", stderr.ToString(), StringComparison.Ordinal);

                var results = AefResultLines(root);
                if (runs == 1)
                {
                    var row = Assert.Single(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(report.FullName))!["results"]!.AsArray())!;
                    Assert.False((bool)row["passed"]!);
                    Assert.Null(row["score"]?.GetValue<double?>());
                    Assert.Contains("Not measured", (string?)row["error"], StringComparison.Ordinal);
                    Assert.Contains("no selected answer", (string?)row["error"], StringComparison.Ordinal);
                    var line = Assert.Single(results);   // no judge: the case line alone, with no graded child
                    Assert.Equal("test1", (string?)line["caseId"]);
                    Assert.Equal("error", (string?)line["state"]);
                }
                else
                {
                    var trials = results.Where(l => l["trial"] is not null).ToList();
                    Assert.Equal(runs, trials.Count);
                    Assert.All(trials, t => Assert.Equal("error", (string?)t["state"]));
                    Assert.DoesNotContain(results, l => (string?)l["state"] == "failed");
                }
            }
        }
        finally
        {
            TryDelete(dataset);
            TryDelete(cfg);
            try { root.Delete(recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Eval_Microsoft365_NotMeasuredCaseWithMetrics_RecordsEveryMetricAsNotMeasured_NeverScoresTheEmptyText()
    {
        // The empty text of an Ambiguous turn is not the agent's answer. A code metric would score it (code_toxicity
        // passes an empty response at 100); --metrics must record the metric as not measured for that case instead.
        var dataset = CreateTempDataset();
        var cfg = WriteValidConfig();
        var root = Directory.CreateTempSubdirectory("agenteval-eval-m365-ambiguous-metrics-");
        try
        {
            var conversation = new AgentEval.Tests.Microsoft365.Agent.FakeWorkIqConversation
            {
                OnRun = (request, _) => Task.FromResult(AgentEval.Tests.Microsoft365.Agent.TurnResults.Completed(request) with
                {
                    AnswerText = null,
                    AnswerCompleteness = OutputCompleteness.Ambiguous,
                }),
            };
            var sink = new CliTurnSink(verbose: false, TextWriter.Null);
            var agent = AgentEval.Microsoft365.Microsoft365AgentFactory.FromConversation(conversation, "m365-ambiguous-metrics", onTurn: sink.Record);
            await using (agent)
            {
                var report = new FileInfo(Path.Combine(root.FullName, "report.json"));

                var exit = await RunWithContentCaptureAsync(OptionsWithAef(dataset, cfg, report, root, runs: 1, metrics: "code_toxicity"), agent, sink);

                Assert.Equal(ExitCodes.GateIndeterminate, exit);
                var row = Assert.Single(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(report.FullName))!["results"]!.AsArray())!;
                Assert.Null(row["metricScores"]);   // no measured metric (the exporter omits an empty map)
                Assert.Contains("not measured", (string?)row["metricsNotMeasured"]!["code_toxicity"], StringComparison.Ordinal);

                var metric = Assert.Single(AefResultLines(root), l => ((string?)l["path"])?.Contains("/metrics/", StringComparison.Ordinal) == true);
                Assert.Equal("not_measured", (string?)metric["state"]);
                Assert.Null(metric["scores"]);
            }
        }
        finally
        {
            TryDelete(dataset);
            TryDelete(cfg);
            try { root.Delete(recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Eval_Microsoft365_CompletedTurnWithNoAnswerAtAll_StaysAFailureExit1()
    {
        // The other no-answer case keeps its verdict: a completed turn with no answer at all (None) is graded and fails.
        var dataset = CreateTempDataset();
        var cfg = WriteValidConfig();
        var root = Directory.CreateTempSubdirectory("agenteval-eval-m365-none-");
        try
        {
            var conversation = new AgentEval.Tests.Microsoft365.Agent.FakeWorkIqConversation
            {
                OnRun = (request, _) => Task.FromResult(AgentEval.Tests.Microsoft365.Agent.TurnResults.Completed(request) with
                {
                    AnswerText = null,
                    AnswerCompleteness = OutputCompleteness.None,
                }),
            };
            var sink = new CliTurnSink(verbose: false, TextWriter.Null);
            var agent = AgentEval.Microsoft365.Microsoft365AgentFactory.FromConversation(conversation, "m365-none", onTurn: sink.Record);
            await using (agent)
            {
                var report = new FileInfo(Path.Combine(root.FullName, "report.json"));

                var exit = await RunWithContentCaptureAsync(OptionsWithAef(dataset, cfg, report, root, runs: 1), agent, sink);

                Assert.Equal(ExitCodes.TestFailure, exit);
                var row = Assert.Single(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(report.FullName))!["results"]!.AsArray())!;
                Assert.False((bool)row["passed"]!);
                Assert.DoesNotContain("Not measured", (string?)row["error"] ?? "", StringComparison.Ordinal);
            }
        }
        finally
        {
            TryDelete(dataset);
            TryDelete(cfg);
            try { root.Delete(recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
        }
    }

    // ── helpers ──

    /// <summary>
    /// Runs <c>eval</c> under the shared AEF controls with <c>--aef-content on</c>, the way the CLI invokes it, with the
    /// stand-in's turn sink handed to the target through its test seam.
    /// </summary>
    private static Task<int> RunWithContentCaptureAsync(EvalOptions opts, IEvaluableAgent standIn, CliTurnSink sink)
    {
        var command = new System.CommandLine.Command("eval-m365-notcompleted-test");
        command.Options.Add(new System.CommandLine.Option<DirectoryInfo?>("--aef"));
        var controls = new AefCliOptions(command);
        IReadOnlyList<ISutTarget> targets = [new Microsoft365SutTarget(TextWriter.Null) { TurnSinkForTests = sink }];
        return controls.InvokeAsync(
            command.Parse(["--aef-content", "on"]),
            () => EvalCommand.ExecuteAsync(opts, CancellationToken.None, sutOverride: standIn, sutTargetsOverride: targets));
    }

    private static EvalOptions OptionsWithAef(FileInfo dataset, FileInfo cfg, FileInfo report, DirectoryInfo root, int runs, string? metrics = null) => new()
    {
        Metrics = metrics,
        Dataset = dataset,
        Sut = "microsoft365",
        AckLiveSideEffects = true,
        TargetOptions = new Dictionary<string, ISutTargetOptions?>
        {
            ["microsoft365"] = new Microsoft365SutOptions { ConfigFile = cfg, Profile = "public" },
        },
        Format = "json",
        Output = report,
        Aef = new DirectoryInfo(Path.Combine(root.FullName, "aef")),
        SubjectVersion = "1",
        Quiet = true,
        Runs = runs,
    };

    private static IReadOnlyList<System.Text.Json.Nodes.JsonNode> AefResultLines(DirectoryInfo root)
    {
        var run = Path.GetDirectoryName(Assert.Single(Directory.GetFiles(Path.Combine(root.FullName, "aef"), "run.json", SearchOption.AllDirectories)))!;
        return File.ReadAllLines(Path.Combine(run, "results.ndjson"))
            .Where(l => l.Length > 0)
            .Select(l => System.Text.Json.Nodes.JsonNode.Parse(l)!)
            .ToArray();
    }

    private static EvalOptions BaseOptions(
        FileInfo dataset, bool ackConsent, FileInfo? config, string? sut = "microsoft365", int runs = 1, string? metrics = null,
        int maxTurns = 0, string profile = "public") => new()
    {
        Dataset = dataset,
        Sut = sut,
        AckLiveSideEffects = ackConsent,   // the common consent flag lives on EvalOptions, not on the target's own options
        TargetOptions = new Dictionary<string, ISutTargetOptions?>
        {
            ["microsoft365"] = new Microsoft365SutOptions { ConfigFile = config, MaxTurns = maxTurns, Profile = profile },
        },
        Format = "json",
        Quiet = true,
        Runs = runs,
        Metrics = metrics,
    };

    private static FileInfo CreateTempDataset()
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-eval-m365-dataset-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, """
            - id: test1
              input: "Hello"
              expectedOutput: "Hi"
            """);
        return new FileInfo(path);
    }

    private static FileInfo WriteValidConfig() => WriteConfig("{ \"tenantId\": \"tenant-1\", \"appClientId\": \"app-1\" }");

    private static FileInfo WriteConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-eval-m365-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return new FileInfo(path);
    }

    private static void TryDelete(FileInfo f)
    {
        try { if (f.Exists) { f.Delete(); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }
}
