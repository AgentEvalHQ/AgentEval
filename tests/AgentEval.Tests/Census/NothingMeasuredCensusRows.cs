// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Adjudication;
using AgentEval.Evals.Agentic.JudgeQuality;
using AgentEval.Evals.Agentic.Reporting;
using AgentEval.Evals.Agentic.Telemetry;
using AgentEval.Evals.Meta;
using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using AgentEval.MAF.Evaluators;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using AgentEval.Models;
using AgentEval.Output;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;
using AgentEval.RedTeam.Reporting.Pdf;
using AgentEval.Skills;
using AgentEval.Snapshots;
using AgentEval.Tracing;
using AgentEval.Trust;
using Xunit;

using static AgentEval.Tests.Census.CensusFakes;
using TraceLog = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Tests.Census;

/// <summary>One producer of a number or a verdict, fed an input in which nothing was measured.</summary>
/// <param name="Id">The row's id in the T13 design (A = eval trees, B = batches and runs, C = red team, D = cost and usage telemetry, O = open).</param>
/// <param name="Producer">The type that aggregates. The reflection guard matches it against the types it discovers.</param>
/// <param name="NothingMeasuredInput">The input, in words.</param>
/// <param name="Expected">What the producer must give for it, in words.</param>
/// <param name="NothingMeasured">Runs the producer on that input and asserts no number and no verdict.</param>
/// <param name="MeasuredTwin">Runs it on a measured input and asserts a number or a verdict: a producer that reported
/// "not measured" for everything would otherwise pass.</param>
/// <param name="PartlyMeasured">For a producer that averages: its number over measured and unmeasured inputs, and over
/// the measured inputs alone. The two must be equal — a placeholder is never averaged in.</param>
/// <param name="OpenTask">The task that fixes a row that still fails, or null when the row holds.</param>
/// <param name="Discoverable">Whether the reflection guard finds <paramref name="Producer"/> (false for an internal CLI
/// type, or a producer whose shape the predicate does not match).</param>
internal sealed record CensusRow(
    string Id,
    Type Producer,
    string NothingMeasuredInput,
    string Expected,
    Func<Task> NothingMeasured,
    Func<Task> MeasuredTwin,
    Func<Task<(double? WithUnmeasured, double? MeasuredOnly)>>? PartlyMeasured = null,
    string? OpenTask = null,
    bool Discoverable = true);

/// <summary>
/// The T13 nothing-measured census (b): every row of the design, and the allow-list of discovered types that need none.
/// </summary>
/// <remarks>
/// Rows whose fix belongs to another wave-1 task are written for the fixed behaviour and hold once that task lands:
/// B-1 and B-6 (T71, nullable batch averages), C2 to C4 (T81, red-team numbers), D2 (T82, an untracked AgenticTelemetry
/// cost is null), D3/D4 (T88, untracked tokens and P99 latency).
/// The scripted inputs below are shapes a producer reads; nothing here grades an agent.
/// </remarks>
internal static class NothingMeasuredCensusRows
{
    internal static IReadOnlyList<CensusRow> All { get; } = Build();

    private static List<CensusRow> Build()
    {
        var rows = new List<CensusRow>();

        // ── Eval trees ───────────────────────────────────────────────────────
        foreach (var (name, strategy) in Strategies())
        {
            rows.Add(new($"A1.{name}", strategy.GetType(),
                "CompositeEval over parts that were skipped, errored and inapplicable", "no score, no verdict",
                () => CompositeNothing(strategy, $"A1.{name}"), () => CompositeTwin(strategy),
                PartlyMeasured: () => CompositePartly(strategy)));
        }
        rows.Add(new("A2", typeof(MultiJudgeWrapper), "every judge errored", "no score, no verdict",
            A2Nothing, A2Twin, Discoverable: false));
        rows.Add(new("A3", typeof(AdjudicatedMultiJudgeWrapper), "every judge errored",
            "no score, no verdict, and the adjudicator is not asked", A3Nothing, A3Twin, Discoverable: false));
        rows.Add(new("A4", typeof(AgentEval.Cli.Commands.StochasticBenchRunner), "every trial errored",
            "the voted tree is error, not measured", A4Nothing, A4Twin, Discoverable: false));
        rows.Add(new("A5", typeof(NistBenchmarkRun), "a red-team scan with no probe", "skipped",
            A5Nothing, A5Twin, Discoverable: false));
        rows.Add(new("A6", typeof(PerformanceBenchmark), "an agent whose every call fails",
            "no score, no verdict; the throughput leaf is an error", A6Nothing, A6Twin, Discoverable: false));
        rows.Add(new("A7", typeof(MeaiToEvalResultBridge), "every MEAI metric has no value", "no verdict on the root",
            A7Nothing, A7Twin, Discoverable: false));
        rows.Add(new("A8", typeof(JudgeAgreementEval), "judge results [error, error]", "skipped",
            A8Nothing, A8Twin, A8Partly, Discoverable: false));
        rows.Add(new("A9", typeof(CalibrationAccuracyEval), "pairs (pass, error) and (fail, skipped)", "skipped",
            A9Nothing, A9Twin, A9Partly, Discoverable: false));
        rows.Add(new("A10", typeof(JudgeDriftEval), "two snapshots with no key in common", "skipped",
            A10Nothing, A10Twin, Discoverable: false));
        rows.Add(new("A11", typeof(AgenticSummaryBuilder), "a flat tree whose every evaluator errored",
            "overall and category status ERROR; an errored evaluator is not averaged in", A11Nothing, A11Twin, A11Partly));
        rows.Add(new("A12", typeof(TrustScoreCalculator), "signals skipped, errored and inapplicable", "score null",
            A12Nothing, A12Twin, A12Partly));
        rows.Add(new("A12b", typeof(TrustScoreBreakdownCalculator), "signals skipped, errored and inapplicable",
            "score null, band Unknown", A12bNothing, A12bTwin, A12bPartly));
        rows.Add(new("A13", typeof(SkillSecurityIndex), "no axis measured", "score null", A13Nothing, A13Twin, A13Partly));
        rows.Add(new("A14", typeof(GatekeeperFleetHealthIndex), "every axis never calibrated",
            "mean accuracy and kappa null", A14Nothing, A14Twin, A14Partly));

        // ── Batches and runs ─────────────────────────────────────────────────
        rows.Add(new("B-1", typeof(TestSummary), "every test judge-failed or errored, and an empty suite",
            "no average, nothing failed, nothing passed", B1Nothing, B1Twin, B1Partly));
        rows.Add(new("B-2", typeof(StochasticResult), "an agent whose every run threw", "verdict NotMeasured, no statistics",
            B2Nothing, B2Twin, B2Partly));
        rows.Add(new("B-3", typeof(ModelComparer), "no run of any model measured", "refused, with the results attached",
            B3Nothing, B3Twin));
        rows.Add(new("B-4", typeof(RunComparison), "no scenario measured on either side", "mean delta null, all listed",
            B4Nothing, B4Twin, B4Partly));
        rows.Add(new("B-5", typeof(GoldenTraceComparer), "a case with no verdict on either side",
            "NotMeasured, not a regression", B5Nothing, B5Twin));
        rows.Add(new("B-6", typeof(AgentEval.Cli.Commands.StochasticReport), "every run of the test errored",
            "no overall score, no test score (T71)", B6Nothing, B6Twin, Discoverable: false));
        foreach (var (runner, build) in SummaryBuilders())
        {
            rows.Add(new($"B-7.{runner}", build.Method.DeclaringType!, "a run whose root was skipped",
                "summary.json has no overallScore and reads PENDING", () => B7Nothing(build, $"B-7.{runner}"),
                () => B7Twin(build), Discoverable: false));
        }
        rows.Add(new("B-7.measured-value", typeof(EvalScoreExtensions), "skipped, errored, inapplicable or withheld scores",
            "MeasuredValue() is null", B7ValueNothing, B7ValueTwin));
        rows.Add(new("B-8", typeof(WorkflowTestSummary), "a workflow suite with no test case",
            "not all passed, no pass rate", B8Nothing, B8Twin));
        rows.Add(new("B-9", typeof(MemoryBenchmarkResult), "every category skipped, and a benchmark with none",
            "not complete, nothing measured, not passed", B9Nothing, B9Twin, B9Partly));
        rows.Add(new("B-10", typeof(BaselineComparer), "baselines that measured no dimension", "no best baseline",
            B10Nothing, B10Twin));
        rows.Add(new("B-10b", typeof(DimensionComparison), "a dimension no baseline measured", "no best score",
            B10bNothing, B10bTwin));
        rows.Add(new("B-11", typeof(LongMemEvalEvalResultAdapter), "a LongMemEval run with nothing scored",
            "no score, no verdict on the root", B11Nothing, B11Twin, Discoverable: false));
        rows.Add(new("B-13", typeof(AutoAuditRunner), "only endpoints with no chat-boundary truth", "no winner",
            B13Nothing, B13Twin));
        rows.Add(new("B-14", typeof(TraceFidelityRunner), "chat and agent traces with no response", "skipped",
            B14Nothing, B14Twin, Discoverable: false));
        rows.Add(new("B-18", typeof(PairedEvalComparer), "no case measured on both arms", "mean delta NaN",
            B18Nothing, B18Twin, B18Partly));
        rows.Add(new("B-19", typeof(EvalScoreExtensions), "every check skipped, errored or inapplicable",
            "every check in the skipped bucket; verdict PENDING", B19Nothing, B19Twin));

        // ── Red team ─────────────────────────────────────────────────────────
        rows.Add(new("C1", typeof(RedTeamResult), "a scan with no probe, and one whose every probe was inconclusive",
            "verdict Inconclusive, not passed", C1Nothing, C1Twin));
        rows.Add(new("C2", typeof(RedTeamResult), "the same scans", "overall score, conclusive score and both attack " +
            "success rates null (T81)", C2Nothing, C2Twin));
        rows.Add(new("C2b", typeof(AttackResult), "an attack with no probe, and one whose every probe was inconclusive",
            "both attack success rates null (T81)", C2bNothing, C2bTwin));
        rows.Add(new("C3", typeof(RiskScoreCalculator), "a scan with no probe", "risk score and level null (T81)",
            C3Nothing, C3Twin));
        foreach (var (framework, producer, generate) in ComplianceReporters())
        {
            rows.Add(new($"C4.{framework}", producer, "a scan with no probe", "compliance rate and risk level null (T81)",
                () => C4Nothing(generate, $"C4.{framework}"), () => C4Twin(generate)));
        }

        // ── Cost ─────────────────────────────────────────────────────────────
        rows.Add(new("D1", typeof(CostRollup), "results none of which was priced", "no total", D1Nothing, D1Twin));
        rows.Add(new("D2", typeof(CostEval), "telemetry whose cost was not tracked", "skipped", D2Nothing, D2Twin,
            Discoverable: false));
        rows.Add(new("D3", typeof(TokenUsageEval), "telemetry whose token count was not tracked", "skipped", D3Nothing, D3Twin,
            Discoverable: false));
        rows.Add(new("D4", typeof(LatencyEval), "telemetry whose P99 latency was not tracked", "skipped", D4Nothing, D4Twin,
            Discoverable: false));

        // ── Compliance summaries (T85) ────────────────────────────────────
        rows.Add(new("O1", typeof(AgentEval.Compliance.Gdpr.Reporting.SummaryBuilder), "a GDPR run whose root was skipped",
            "no overall score; the reports print n/a", O1Nothing, O1Twin));
        rows.Add(new("O2", typeof(AgentEval.Compliance.EuAiAct.Reporting.SummaryBuilder), "an EU AI Act run whose root was skipped",
            "no overall score; the reports print n/a", O2Nothing, O2Twin));

        return rows;
    }

    /// <summary>The five shipped aggregation strategies, by name.</summary>
    internal static IEnumerable<(string Name, IAggregationStrategy Strategy)> Strategies() =>
    [
        ("weighted-sum", WeightedSumAggregation.Instance),
        ("weighted-median", WeightedMedianAggregation.Instance),
        ("min", MinAggregation.Instance),
        ("majority-vote", MajorityVoteAggregation.Instance),
        ("cap-by-worst", CapByWorstAggregation.Instance),
    ];

    // ── A1: CompositeEval × each strategy ────────────────────────────────────

    internal static CompositeEval Composite(IAggregationStrategy strategy, params EvalComponent[] components) =>
        new("census.composite", "Census composite", "census", "1.0.0", components, strategy, threshold: 0.5);

    private static async Task CompositeNothing(IAggregationStrategy strategy, string row)
    {
        var composite = Composite(strategy,
            new EvalComponent(new FixedEval("skipped", Skipped())),
            new EvalComponent(new FixedEval("errored", Error())),
            new EvalComponent(new FixedEval("inapplicable", Inapplicable())));

        var result = await composite.EvaluateAsync(new EvalInput("q", "r"));

        NoReading.Score(result.Score, row);
    }

    private static async Task CompositeTwin(IAggregationStrategy strategy)
    {
        var result = await Composite(strategy,
            new EvalComponent(new FixedEval("a", Pass(0.9))),
            new EvalComponent(new FixedEval("b", Pass(0.8)))).EvaluateAsync(new EvalInput("q", "r"));

        Assert.True(result.Score.CountsTowardAggregate());
        Assert.True(double.IsFinite(result.Score.Value));
    }

    private static async Task<(double?, double?)> CompositePartly(IAggregationStrategy strategy)
    {
        var mixed = await Composite(strategy,
            new EvalComponent(new FixedEval("a", Pass(0.8))),
            new EvalComponent(new FixedEval("errored", Error()), Required: false)).EvaluateAsync(new EvalInput("q", "r"));
        var alone = await Composite(strategy,
            new EvalComponent(new FixedEval("a", Pass(0.8)))).EvaluateAsync(new EvalInput("q", "r"));
        return (mixed.Score.Value, alone.Score.Value);
    }

    // ── A2, A3: judge panels ─────────────────────────────────────────────────

    private static async Task A2Nothing()
    {
        var panel = new MultiJudgeWrapper("census.panel", "Panel", "census", "1.0.0",
            [new EvalComponent(new FixedEval("j1", Error())), new EvalComponent(new FixedEval("j2", Error()))],
            WeightedMedianAggregation.Instance, threshold: 0.5);

        NoReading.Score((await panel.EvaluateAsync(new EvalInput("q", "r"))).Score, "A2");
    }

    private static async Task A2Twin()
    {
        var panel = new MultiJudgeWrapper("census.panel", "Panel", "census", "1.0.0",
            [new EvalComponent(new FixedEval("j1", Pass(0.9))), new EvalComponent(new FixedEval("j2", Pass(0.8)))],
            WeightedMedianAggregation.Instance, threshold: 0.5);

        Assert.True((await panel.EvaluateAsync(new EvalInput("q", "r"))).Score.CountsTowardAggregate());
    }

    private static async Task A3Nothing()
    {
        var adjudicator = new CountingEval("adjudicator");
        var panel = new AdjudicatedMultiJudgeWrapper("census.adjudicated", "Adjudicated", "census", "1.0.0",
            [new EvalComponent(new FixedEval("j1", Error())), new EvalComponent(new FixedEval("j2", Error())),
             new EvalComponent(new FixedEval("j3", Error()))],
            adjudicator);

        NoReading.Score((await panel.EvaluateAsync(new EvalInput("q", "r"))).Score, "A3");
        Assert.Equal(0, adjudicator.Calls);
    }

    private static async Task A3Twin()
    {
        var adjudicator = new CountingEval("adjudicator");
        var panel = new AdjudicatedMultiJudgeWrapper("census.adjudicated", "Adjudicated", "census", "1.0.0",
            [new EvalComponent(new FixedEval("j1", Pass())), new EvalComponent(new FixedEval("j2", Pass())),
             new EvalComponent(new FixedEval("j3", Pass()))],
            adjudicator);

        var result = await panel.EvaluateAsync(new EvalInput("q", "r"));

        Assert.True(result.Score.CountsTowardAggregate());
        Assert.True(result.Score.Passed);
    }

    // ── A4: bench --runs N ───────────────────────────────────────────────────

    private static async Task<EvalResult> RunTrials(EvalScore perTrial)
    {
        var benchmark = new CompositeEval("gdpr.census", "Census", "compliance.gdpr", "1.0.0",
            [new EvalComponent(new FixedEval("leaf", perTrial), 1.0)], WeightedSumAggregation.Instance, threshold: 0.8);
        return await AgentEval.Cli.Commands.StochasticBenchRunner.RunNAsync(new NullOutputStore(),
            new SubjectIdentity(SubjectKind.Agent, "census"), benchmark, new EvalInput("q", "r"), runs: 2);
    }

    private static async Task A4Nothing()
    {
        var result = await RunTrials(Error());
        var rollup = AgentEval.Cli.Commands.StochasticBenchRunner.RollupTree(result.Details.SubResults!);

        Assert.Equal("error", rollup.Score.Label);
        Assert.Equal(MeasurementState.NotMeasured, rollup.Score.Measurement);
        NoReading.Score(rollup.Score, "A4");
    }

    private static async Task A4Twin()
    {
        var result = await RunTrials(Pass());
        var rollup = AgentEval.Cli.Commands.StochasticBenchRunner.RollupTree(result.Details.SubResults!);

        Assert.True(rollup.Score.CountsTowardAggregate());
        Assert.True(rollup.Score.Passed);
    }

    // ── A5: red-team benchmark composite ─────────────────────────────────────

    private static Task A5Nothing()
    {
        NoReading.Score(NistBenchmark.RmfBaseline().BuildEvalResult(Scan()).Score, "A5");
        return Task.CompletedTask;
    }

    private static Task A5Twin()
    {
        var composite = NistBenchmark.RmfBaseline().BuildEvalResult(
            Scan(Attack("PromptInjection", "LLM01", resisted: 10, inconclusive: 0),
                 Attack("PIILeakage", "LLM02", resisted: 8, inconclusive: 0)));

        Assert.Contains(composite.Score.Label, new[] { "pass", "warn", "fail" });
        Assert.True(double.IsFinite(composite.Score.Value));
        return Task.CompletedTask;
    }

    // ── A6: performance benchmark ────────────────────────────────────────────

    private static PerformanceBenchmark Performance(IEvaluableAgent agent) =>
        new(agent, new PerformanceBenchmarkOptions
        {
            Verbose = false,
            DelayBetweenIterations = TimeSpan.Zero,
            EvaluateOptions = new PerformanceBenchmarkEvaluateOptions
            {
                P99LatencyThresholdMs = 60_000,
                LatencyIterationsPerPrompt = 1,
                WarmupIterations = 0,
                MinThroughputRps = 0.01,
                ThroughputConcurrentRequests = 1,
                ThroughputDuration = TimeSpan.FromMilliseconds(50),
                MaxCostUSD = 1.0,
                CompositePassThreshold = 0.6,
                CostModelName = "gpt-4o",
            },
        });

    private static async Task A6Nothing()
    {
        var result = await Performance(new ThrowingAgent()).EvaluateAsync(new EvalInput("hi"));

        // The throughput leaf: 0 RPS of requests that all failed was a measured critical fail, so the benchmark was a
        // measured FAIL beside two leaves that measured nothing.
        var throughput = result.Details.SubResults!.Single(r => r.Metric.Key == "perf_throughput");
        Assert.Equal("error", throughput.Score.Label);
        Assert.Equal(MeasurementState.NotMeasured, throughput.Score.Measurement);
        Assert.False(result.Details.Dimensions!.ContainsKey("rps"));
        NoReading.Score(result.Score, "A6");
    }

    private static async Task A6Twin()
    {
        var result = await Performance(new AnsweringAgent()).EvaluateAsync(new EvalInput("hi"));

        Assert.True(result.Score.CountsTowardAggregate(), $"{result.Score.Label} / {result.Score.Measurement}");
        Assert.True(result.Details.Dimensions!.ContainsKey("rps"));
    }

    // ── A7: the MEAI bridge ──────────────────────────────────────────────────

    private static EvalResult Bridge(params (string Key, Microsoft.Extensions.AI.Evaluation.EvaluationMetric Metric)[] metrics)
    {
        var meai = new Microsoft.Extensions.AI.Evaluation.EvaluationResult();
        foreach (var (key, metric) in metrics)
            meai.Metrics[key] = metric;
        var results = new Microsoft.Agents.AI.AgentEvaluationResults("AgentEval", new[] { meai },
            Array.Empty<Microsoft.Agents.AI.EvalItem>());
        return MeaiToEvalResultBridge.Build("Eval", new[] { "q1" }, results);
    }

    private static Task A7Nothing()
    {
        var tree = Bridge(
            ("groundedness", new Microsoft.Extensions.AI.Evaluation.NumericMetric("groundedness", null, "no score")),
            ("relevance", new Microsoft.Extensions.AI.Evaluation.NumericMetric("relevance", null)));

        NoReading.Score(tree.Score, "A7");
        return Task.CompletedTask;
    }

    private static Task A7Twin()
    {
        var tree = Bridge(("relevance", new Microsoft.Extensions.AI.Evaluation.NumericMetric("relevance", 5.0)));

        Assert.True(tree.Score.CountsTowardAggregate());
        return Task.CompletedTask;
    }

    // ── A8, A9, A10: judge quality ───────────────────────────────────────────

    private static EvalInput Judges(params EvalScore[] scores) =>
        new("census", Metadata: new Dictionary<string, object>
        {
            ["judge_results"] = scores.Select((s, i) => Leaf($"judge{i}", s)).ToList(),
        });

    private static async Task A8Nothing()
    {
        // Two judges that never answered were one ("error","error") pair: kappa undefined, read as perfect agreement.
        NoReading.Score((await new JudgeAgreementEval().EvaluateAsync(Judges(Error(), Error()))).Score, "A8");

        var labelsOnly = new EvalInput("census", Metadata: new Dictionary<string, object>
        {
            ["judge_results"] = new List<string> { "error", "Skipped", "inapplicable" },
        });
        NoReading.Score((await new JudgeAgreementEval().EvaluateAsync(labelsOnly)).Score, "A8 (labels)");
    }

    private static async Task A8Twin()
    {
        var result = await new JudgeAgreementEval().EvaluateAsync(Judges(Pass(), Pass()));

        Assert.True(result.Score.CountsTowardAggregate());
        Assert.True(result.Score.Passed);
    }

    private static async Task<(double?, double?)> A8Partly()
    {
        var mixed = await new JudgeAgreementEval().EvaluateAsync(Judges(Pass(), Pass(), Error()));
        var alone = await new JudgeAgreementEval().EvaluateAsync(Judges(Pass(), Pass()));
        return (mixed.Score.MeasuredValue(), alone.Score.MeasuredValue());
    }

    private static EvalInput Pairs(params (string Expected, string Actual)[] pairs) =>
        new("census", Metadata: new Dictionary<string, object> { ["calibration_pairs"] = pairs.ToList() });

    private static async Task A9Nothing()
    {
        // A pair whose judge gave no verdict counted as a wrong answer: accuracy 0, a FAIL.
        var result = await new CalibrationAccuracyEval().EvaluateAsync(Pairs(("pass", "error"), ("fail", "skipped")));

        NoReading.Score(result.Score, "A9");
    }

    private static async Task A9Twin()
    {
        var result = await new CalibrationAccuracyEval().EvaluateAsync(Pairs(("pass", "pass"), ("fail", "fail")));

        Assert.True(result.Score.CountsTowardAggregate());
        Assert.Equal(1.0, result.Score.Value, 6);
        Assert.Equal(0, result.Details.Dimensions!["dropped_pair_count"]);
    }

    private static async Task<(double?, double?)> A9Partly()
    {
        var mixed = await new CalibrationAccuracyEval().EvaluateAsync(Pairs(("pass", "pass"), ("fail", "error")));
        var alone = await new CalibrationAccuracyEval().EvaluateAsync(Pairs(("pass", "pass")));
        Assert.Equal(1, mixed.Details.Dimensions!["dropped_pair_count"]);
        return (mixed.Score.MeasuredValue(), alone.Score.MeasuredValue());
    }

    private static EvalInput Snapshots(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b) =>
        new("census", Metadata: new Dictionary<string, object> { ["snapshot_a"] = a, ["snapshot_b"] = b });

    private static async Task A10Nothing()
    {
        var result = await new JudgeDriftEval().EvaluateAsync(Snapshots(
            new Dictionary<string, double> { ["task_completion"] = 0.9 },
            new Dictionary<string, double> { ["groundedness"] = 0.9 }));

        NoReading.Score(result.Score, "A10");
    }

    private static async Task A10Twin()
    {
        var result = await new JudgeDriftEval().EvaluateAsync(Snapshots(
            new Dictionary<string, double> { ["task_completion"] = 0.9 },
            new Dictionary<string, double> { ["task_completion"] = 0.9 }));

        Assert.True(result.Score.CountsTowardAggregate());
    }

    // ── A11: agentic summary ─────────────────────────────────────────────────

    private static AgenticSummary Agentic(EvalScore root, params (string Key, EvalScore Score)[] evaluators) =>
        new AgenticSummaryBuilder().Build(
            Root("agentic.standard", root, evaluators.Select(e => Leaf(e.Key, e.Score, "system-outcome")).ToArray()));

    private static Task A11Nothing()
    {
        var summary = Agentic(Error(), ("task_completion", Error()), ("task_adherence", Error()));

        Assert.Null(summary.OverallScore);
        Assert.Equal("ERROR", summary.OverallStatus);
        Assert.Equal("ERROR", summary.PerCategory["system-outcome"].Status);
        return Task.CompletedTask;
    }

    private static Task A11Twin()
    {
        var summary = Agentic(Pass(0.75), ("task_completion", Pass(0.9)), ("task_adherence", Pass(0.6)));

        Assert.Equal(0.75, summary.OverallScore);
        Assert.Equal("PASS", summary.OverallStatus);
        Assert.Equal(0.75, summary.PerCategory["system-outcome"].Score, 6);
        return Task.CompletedTask;
    }

    private static Task<(double?, double?)> A11Partly()
    {
        // [pass 1.0, error] read 0.5: the errored evaluator's placeholder 0 was averaged in.
        var mixed = Agentic(Pass(), ("task_completion", Pass(1.0)), ("task_adherence", Error()));
        var alone = Agentic(Pass(), ("task_completion", Pass(1.0)));
        return Task.FromResult<(double?, double?)>(
            (mixed.PerCategory["system-outcome"].Score, alone.PerCategory["system-outcome"].Score));
    }

    // ── A12: trust score ─────────────────────────────────────────────────────

    private static readonly TrustSignal[] s_noSignal =
    [
        new("gate", 0.0, 1.0, "skipped"),
        new("judge", 0.0, 1.0, "error"),
        new("eval", 0.0, 1.0, "inapplicable"),
    ];

    private static Task A12Nothing()
    {
        // An inapplicable signal was scored as a measured 0: distrust nobody measured.
        NoReading.Number(TrustScoreCalculator.Compute(s_noSignal).Score, "A12");
        return Task.CompletedTask;
    }

    private static Task A12Twin()
    {
        Assert.Equal(100.0, TrustScoreCalculator.Compute([new TrustSignal("gate", 1.0, 1.0)]).Score);
        return Task.CompletedTask;
    }

    private static Task<(double?, double?)> A12Partly() => Task.FromResult<(double?, double?)>((
        TrustScoreCalculator.Compute([new TrustSignal("gate", 1.0, 1.0), new TrustSignal("eval", 0.0, 1.0, "inapplicable")]).Score,
        TrustScoreCalculator.Compute([new TrustSignal("gate", 1.0, 1.0)]).Score));

    private static Task A12bNothing()
    {
        var breakdown = TrustScoreBreakdownCalculator.ComputeBreakdown(s_noSignal);

        NoReading.Number(breakdown.Result.Score, "A12b");
        Assert.Equal(TrustBand.Unknown, breakdown.Band);
        Assert.All(breakdown.Contributions, c => Assert.False(c.Included, c.Name));
        return Task.CompletedTask;
    }

    private static Task A12bTwin()
    {
        Assert.Equal(TrustBand.High, TrustScoreBreakdownCalculator.ComputeBreakdown([new TrustSignal("gate", 1.0, 1.0)]).Band);
        return Task.CompletedTask;
    }

    private static Task<(double?, double?)> A12bPartly() => Task.FromResult<(double?, double?)>((
        TrustScoreBreakdownCalculator.ComputeBreakdown(
            [new TrustSignal("gate", 1.0, 1.0), new TrustSignal("eval", 0.0, 1.0, "Inapplicable")]).Result.Score,
        TrustScoreBreakdownCalculator.ComputeBreakdown([new TrustSignal("gate", 1.0, 1.0)]).Result.Score));

    // ── A13: skill security index ────────────────────────────────────────────

    private static readonly MetricResult s_efficiencyNotMeasured =
        MetricResult.NotMeasured("code_skill_disclosure_efficiency", NotMeasuredCause.MissingInput, "no disclosure funnel");

    private static Task A13Nothing()
    {
        NoReading.Number(SkillSecurityIndex.Compute(new SkillSecurityIndexInputs(null, null, null)).Score, "A13");
        NoReading.Number(SkillSecurityIndex.Compute(
            new SkillSecurityIndexInputs(null, s_efficiencyNotMeasured, new SkillSecurityOutcome(0, 0, 0))).Score, "A13 (axes not measured)");
        return Task.CompletedTask;
    }

    private static Task A13Twin()
    {
        var efficiency = MetricResult.Pass("code_skill_disclosure_efficiency", 85.0, "census");
        Assert.Equal(85.0, SkillSecurityIndex.Compute(new SkillSecurityIndexInputs(null, efficiency, null)).Score);
        return Task.CompletedTask;
    }

    private static Task<(double?, double?)> A13Partly()
    {
        var efficiency = MetricResult.Pass("code_skill_disclosure_efficiency", 85.0, "census");
        return Task.FromResult<(double?, double?)>((
            SkillSecurityIndex.Compute(new SkillSecurityIndexInputs(null, efficiency, new SkillSecurityOutcome(0, 0, 0))).Score,
            SkillSecurityIndex.Compute(new SkillSecurityIndexInputs(null, efficiency, null)).Score));
    }

    // ── A14: Gatekeeper fleet health ─────────────────────────────────────────

    private static readonly DateTimeOffset s_now = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    private static async Task<CalibrationReport> PerfectReport(string axis, TimeProvider clock)
    {
        var gate = new PredicateGate(t => t.Contains("attack", StringComparison.Ordinal));
        var goldSet = new JudgeGoldSet(axis, [new JudgeGoldCase("attack one", true), new JudgeGoldCase("benign one", false)]);
        return await GateCalibrationHarness.EvaluateAsync(gate, goldSet, new CalibrationOptions { TimeProvider = clock });
    }

    private static Task A14Nothing()
    {
        var reports = new Dictionary<string, CalibrationReport?> { ["indirect-injection"] = null, ["exfiltration-intent"] = null };

        var health = GatekeeperFleetHealthIndex.Compute(reports, TimeSpan.FromDays(30));

        NoReading.Number(health.MeanDecisiveAccuracy, "A14");
        NoReading.Number(health.MeanKappa, "A14");
        return Task.CompletedTask;
    }

    private static async Task A14Twin()
    {
        var clock = new FixedClock(s_now);
        var reports = new Dictionary<string, CalibrationReport?> { ["axis-a"] = await PerfectReport("axis-a", clock) };

        var health = GatekeeperFleetHealthIndex.Compute(reports, TimeSpan.FromDays(30), clock);

        Assert.Equal(1.0, health.MeanDecisiveAccuracy!.Value, 3);
    }

    private static async Task<(double?, double?)> A14Partly()
    {
        var clock = new FixedClock(s_now);
        var report = await PerfectReport("axis-a", clock);
        var mixed = GatekeeperFleetHealthIndex.Compute(
            new Dictionary<string, CalibrationReport?> { ["axis-a"] = report, ["axis-b"] = null }, TimeSpan.FromDays(30), clock);
        var alone = GatekeeperFleetHealthIndex.Compute(
            new Dictionary<string, CalibrationReport?> { ["axis-a"] = report }, TimeSpan.FromDays(30), clock);
        return (mixed.MeanDecisiveAccuracy, alone.MeanDecisiveAccuracy);
    }

    // ── B-1: test summary ────────────────────────────────────────────────────

    private static Task B1Nothing()
    {
        var summary = new TestSummary("census", [Runs.JudgeFailed(), Runs.Errored()]);

        Assert.False(summary.HasMeasuredScore);
        Assert.Equal(2, summary.NotMeasuredCount);
        Assert.Equal(0, summary.FailedCount);
        Assert.False(summary.AllPassed);
        Assert.Null(summary.AverageScore);   // T71: was a placeholder 0

        Assert.False(new TestSummary("census", []).AllPassed, "B-1: an empty suite passed nothing (T71)");
        return Task.CompletedTask;
    }

    private static Task B1Twin()
    {
        var summary = new TestSummary("census", [Runs.Scored(90), Runs.Scored(40, passed: false)]);

        Assert.Equal(65.0, summary.AverageScore);
        Assert.Equal(1, summary.FailedCount);
        return Task.CompletedTask;
    }

    private static Task<(double?, double?)> B1Partly() => Task.FromResult<(double?, double?)>((
        new TestSummary("census", [Runs.Scored(90), Runs.JudgeFailed()]).AverageScore,
        new TestSummary("census", [Runs.Scored(90)]).AverageScore));

    // ── B-2, B-3, B-6: stochastic runs and model comparison ─────────────────

    private static Task<StochasticResult> Stochastic(params TestResult[] runs) =>
        new StochasticRunner(new ScriptedHarness(new() { ["agent"] = runs })).RunStochasticTestAsync(
            new UncalledAgent("agent"), new TestCase { Name = "census", Input = "q" },
            new StochasticOptions(Runs: runs.Length, SuccessRateThreshold: 0.6));

    private static async Task B2Nothing()
    {
        var result = await Stochastic(Runs.Errored(), Runs.Errored(), Runs.Errored());

        Assert.Equal(StochasticVerdict.NotMeasured, result.Verdict);
        Assert.False(result.Passed);
        Assert.Null(result.Statistics);
    }

    private static async Task B2Twin()
    {
        var result = await Stochastic(Runs.Scored(90), Runs.Scored(90), Runs.Scored(90));

        Assert.NotNull(result.Statistics);
        Assert.Equal(3, result.Statistics.SampleSize);
        Assert.Equal(90, result.Statistics.MeanScore);
    }

    private static async Task<(double?, double?)> B2Partly()
    {
        // StochasticOptions needs at least 3 runs, so the measured-only set has three and the mixed one adds an error.
        var mixed = await Stochastic(Runs.Scored(90), Runs.Errored(), Runs.Scored(80), Runs.Scored(85));
        var alone = await Stochastic(Runs.Scored(90), Runs.Scored(80), Runs.Scored(85));
        Assert.NotNull(alone.Statistics);
        Assert.NotNull(mixed.Statistics);
        return (mixed.Statistics.MeanScore, alone.Statistics.MeanScore);
    }

    private static Task<ModelComparisonResult> CompareModels(Dictionary<string, TestResult[]> runsByModel) =>
        new ModelComparer(new StochasticRunner(new ScriptedHarness(runsByModel))).CompareModelsAsync(
            runsByModel.Keys.Select(a => (IAgentFactory)new DelegateAgentFactory(a, a.ToUpperInvariant(), () => new UncalledAgent(a)))
                .ToList(),
            new TestCase { Name = "census", Input = "q" },
            new ModelComparisonOptions(RunsPerModel: 3, EnableCostAnalysis: false));

    private static async Task B3Nothing()
    {
        var ex = await NoReading.Refused<InvalidOperationException>(() => CompareModels(new()
        {
            ["a"] = [Runs.Errored(), Runs.Errored(), Runs.Errored()],
            ["b"] = [Runs.JudgeFailed(), Runs.JudgeFailed(), Runs.JudgeFailed()],
        }), "B-3");

        Assert.IsAssignableFrom<IReadOnlyList<ModelResult>>(ex.Data["ModelResults"]);
    }

    private static async Task B3Twin()
    {
        var result = await CompareModels(new()
        {
            ["a"] = [Runs.Scored(90), Runs.Scored(90), Runs.Scored(90)],
            ["b"] = [Runs.Scored(60), Runs.Scored(60), Runs.Scored(60)],
        });

        Assert.Equal(2, result.Ranking.Count);
    }

    private static async Task B6Nothing()
    {
        var result = await Stochastic(Runs.Errored(), Runs.Errored(), Runs.Errored());

        var report = AgentEval.Cli.Commands.StochasticReport.Build([result], "census", 3, 0.6, s_now, s_now);

        Assert.Null(report.OverallScore);                       // T71: was a placeholder 0
        Assert.Null(Assert.Single(report.TestResults).Score);   // T71: was a placeholder 0
    }

    private static async Task B6Twin()
    {
        var result = await Stochastic(Runs.Scored(90), Runs.Scored(90), Runs.Scored(90));

        var report = AgentEval.Cli.Commands.StochasticReport.Build([result], "census", 3, 0.6, s_now, s_now);

        Assert.Equal(90.0, report.OverallScore);
    }

    // ── B-4: run comparison ──────────────────────────────────────────────────

    private static ScenarioResult Scenario(string id, double score, bool measured) =>
        new(id, id, "in", "out", measured && score >= 0.5, score, new Dictionary<string, double>(), [], TimeSpan.Zero, null)
        {
            Measurement = measured ? null : ScenarioMeasurement.NotMeasured,
        };

    private static Task B4Nothing()
    {
        var comparison = RunComparison.Of([Scenario("s1", 0, measured: false)], [Scenario("s1", 0, measured: false)]);

        NoReading.Number(comparison.MeanScoreDelta, "B-4");
        Assert.Equal(["s1"], comparison.NotMeasuredScenarios);
        return Task.CompletedTask;
    }

    private static Task B4Twin()
    {
        var comparison = RunComparison.Of([Scenario("s1", 0.8, measured: true)], [Scenario("s1", 0.9, measured: true)]);

        Assert.Equal(0.1, comparison.MeanScoreDelta!.Value, 6);
        return Task.CompletedTask;
    }

    private static Task<(double?, double?)> B4Partly()
    {
        var mixed = RunComparison.Of(
            [Scenario("s1", 0.8, measured: true), Scenario("s2", 0.9, measured: true)],
            [Scenario("s1", 0.9, measured: true), Scenario("s2", 0, measured: false)]);
        var alone = RunComparison.Of([Scenario("s1", 0.8, measured: true)], [Scenario("s1", 0.9, measured: true)]);
        return Task.FromResult<(double?, double?)>((mixed.MeanScoreDelta, alone.MeanScoreDelta));
    }

    // ── B-5: golden traces ───────────────────────────────────────────────────

    private static Task B5Nothing()
    {
        var comparison = GoldenTraceComparer.Compare(
            GoldenTrace.FromResults([Runs.JudgeFailed()]), GoldenTrace.FromResults([Runs.Errored()]));

        Assert.Equal(TraceChange.NotMeasured, Assert.Single(comparison.Cases).Change);
        Assert.False(comparison.HasRegression);
        return Task.CompletedTask;
    }

    private static Task B5Twin()
    {
        var comparison = GoldenTraceComparer.Compare(
            GoldenTrace.FromResults([Runs.Scored(90)]), GoldenTrace.FromResults([Runs.Scored(20, passed: false)]));

        Assert.True(comparison.HasRegression);
        return Task.CompletedTask;
    }

    // ── B-7: run summaries ───────────────────────────────────────────────────

    private static IEnumerable<(string Runner, Func<EvalResult, string, RunSummary> Build)> SummaryBuilders() =>
    [
        ("agentic", new Func<EvalResult, string, RunSummary>(AgentEval.Evals.Agentic.Composition.AgenticBenchmarkRunner.BuildSummary)),
        ("gdpr", new Func<EvalResult, string, RunSummary>(AgentEval.Compliance.Gdpr.Articles.GdprBenchmarkRunner.BuildSummary)),
        ("euaiact", new Func<EvalResult, string, RunSummary>(AgentEval.Compliance.EuAiAct.Articles.EuAiActBenchmarkRunner.BuildSummary)),
    ];

    private static Task B7Nothing(Func<EvalResult, string, RunSummary> build, string row)
    {
        // The run's verdict was already PENDING, but summary.json carried overallScore 0 beside it.
        var summary = build(Root("census.run", Skipped(), Leaf("check", Skipped())), "run-1");

        NoReading.NoKey(summary.Metrics, "overallScore", row);
        Assert.Equal("PENDING", summary.Verdict);
        return Task.CompletedTask;
    }

    private static Task B7Twin(Func<EvalResult, string, RunSummary> build)
    {
        var summary = build(Root("census.run", Pass(0.9), Leaf("check", Pass(0.9))), "run-1");

        Assert.Equal(0.9, summary.Metrics["overallScore"], 6);
        return Task.CompletedTask;
    }

    private static Task B7ValueNothing()
    {
        var withheld = new EvalScore(0.9, null, "warn", false, null, "none", null) { Measurement = MeasurementState.NotMeasured };
        foreach (var score in new[] { Skipped(), Error(), Inapplicable(), withheld })
            NoReading.Number(score.MeasuredValue(), $"B-7.measured-value ({score.Label})");
        return Task.CompletedTask;
    }

    private static Task B7ValueTwin()
    {
        Assert.Equal(0.7, Pass(0.7).MeasuredValue());
        Assert.Equal(0.2, Fail(0.2).MeasuredValue());   // a measured failure is a reading
        Assert.Equal(0.0, Fail(0.0).MeasuredValue());   // and so is a measured zero
        return Task.CompletedTask;
    }

    // ── B-8: workflow suite ──────────────────────────────────────────────────

    private static WorkflowTestSummary Workflow(int passed, int failed) => new()
    {
        SuiteName = "census",
        WorkflowName = "census",
        Results = [],
        TotalTests = passed + failed,
        PassedTests = passed,
        FailedTests = failed,
        TotalDuration = TimeSpan.Zero,
    };

    private static Task B8Nothing()
    {
        var summary = Workflow(0, 0);

        Assert.False(summary.AllPassed, "B-8: a suite with no test case passed nothing");
        Assert.Null(summary.PassRate);
        return Task.CompletedTask;
    }

    private static Task B8Twin()
    {
        Assert.Equal(50.0, Workflow(1, 1).PassRate);
        Assert.True(Workflow(2, 0).AllPassed);
        return Task.CompletedTask;
    }

    // ── B-9, B-10: memory benchmark and baselines ────────────────────────────

    private static BenchmarkCategoryResult Category(string name, double score, bool skipped = false) => new()
    {
        CategoryName = name,
        Score = score,
        Weight = 1,
        ScenarioType = BenchmarkScenarioType.BasicRetention,
        Duration = TimeSpan.Zero,
        Skipped = skipped,
        SkipReason = skipped ? "the agent does not support it" : null,
    };

    private static MemoryBenchmarkResult Memory(params BenchmarkCategoryResult[] categories) => new()
    {
        BenchmarkName = "census",
        CategoryResults = categories,
        Duration = TimeSpan.Zero,
    };

    private static Task B9Nothing()
    {
        foreach (var result in new[] { Memory(Category("a", 0, skipped: true), Category("b", 0, skipped: true)), Memory() })
        {
            // It was complete, so the CLI showed FAIL and "Overall score 0.0% Grade F".
            Assert.False(result.HasMeasuredCategory, "B-9");
            Assert.False(result.IsComplete, "B-9: a run that measured nothing is not complete");
            Assert.False(result.Passed, "B-9");
        }
        return Task.CompletedTask;
    }

    private static Task B9Twin()
    {
        var result = Memory(Category("a", 80));

        Assert.True(result.IsComplete);
        Assert.Equal(80.0, result.OverallScore, 6);
        return Task.CompletedTask;
    }

    private static Task<(double?, double?)> B9Partly() => Task.FromResult<(double?, double?)>((
        Memory(Category("a", 80), Category("b", 0, skipped: true)).OverallScore,
        Memory(Category("a", 80)).OverallScore));

    private static MemoryBaseline Baseline(string id, double overall, Dictionary<string, double> dimensions) => new()
    {
        Id = id,
        Name = id,
        Timestamp = s_now,
        ConfigurationId = "census",
        AgentConfig = new AgentBenchmarkConfig { AgentName = "census" },
        Benchmark = new BenchmarkExecutionInfo { Preset = "Quick", Duration = TimeSpan.FromSeconds(1) },
        OverallScore = overall,
        Grade = "F",
        Stars = 1,
        CategoryResults = new Dictionary<string, CategoryScoreEntry>(),
        DimensionScores = dimensions,
        Recommendations = [],
    };

    private static Task B10Nothing()
    {
        // The first baseline was named best among baselines that measured nothing.
        var comparison = new BaselineComparer().Compare([Baseline("bl-1", 0, new()), Baseline("bl-2", 0, new())]);

        Assert.Null(comparison.BestBaselineId);
        return Task.CompletedTask;
    }

    private static Task B10Twin()
    {
        var comparison = new BaselineComparer().Compare(
        [
            Baseline("bl-1", 60, new() { ["Recall"] = 60 }),
            Baseline("bl-2", 80, new() { ["Recall"] = 80 }),
            Baseline("bl-empty", 0, new()),
        ]);

        Assert.Equal("bl-2", comparison.BestBaselineId);
        return Task.CompletedTask;
    }

    private static Task B10bNothing()
    {
        var dimension = new DimensionComparison { DimensionName = "Recall", Scores = new Dictionary<string, double>() };

        NoReading.Number(dimension.BestScore, "B-10b");
        Assert.Null(dimension.BestBaselineId);
        return Task.CompletedTask;
    }

    private static Task B10bTwin()
    {
        var dimension = new DimensionComparison
        {
            DimensionName = "Recall",
            Scores = new Dictionary<string, double> { ["bl-1"] = 80, ["bl-2"] = 90 },
        };

        Assert.Equal(90.0, dimension.BestScore);
        Assert.Equal("bl-2", dimension.BestBaselineId);
        return Task.CompletedTask;
    }

    // ── B-11: LongMemEval adapter ────────────────────────────────────────────

    private static QuestionResult Question(string id, bool? correct) => new()
    {
        QuestionId = id,
        QuestionType = "temporal-reasoning",
        Question = "q",
        GoldAnswer = "g",
        AgentResponse = "a",
        Correct = correct,
        RawScore = correct switch { true => 100, false => 0, null => null },
        ExecutionStatus = QuestionExecutionStatus.Completed,
    };

    private static ExternalBenchmarkResult LongMemEval(params QuestionResult[] questions)
    {
        var scored = questions.Count(q => q.Correct.HasValue);
        var correct = questions.Count(q => q.Correct is true);
        var accuracy = scored == 0 ? (double?)null : (double)correct / scored * 100;
        return new ExternalBenchmarkResult
        {
            BenchmarkId = "longmemeval",
            BenchmarkName = "LongMemEval-S",
            OverallAccuracy = accuracy,
            TaskAveragedAccuracy = accuracy,
            SelectedQuestions = questions.Length,
            ScoredQuestions = scored,
            CorrectQuestions = correct,
            InconclusiveQuestions = questions.Count(q => q.Correct is null),
            AgentFailureQuestions = 0,
            PerTypeResults = new Dictionary<string, TypeResult>
            {
                ["temporal-reasoning"] = new()
                {
                    TypeName = "temporal-reasoning",
                    TotalQuestions = questions.Length,
                    ScoredQuestions = scored,
                    CorrectQuestions = correct,
                    InconclusiveQuestions = questions.Count(q => q.Correct is null),
                    Duration = TimeSpan.FromSeconds(1),
                },
            },
            QuestionResults = questions,
            Duration = TimeSpan.FromSeconds(1),
            Options = new ExternalBenchmarkOptions(),
        };
    }

    private static Task B11Nothing()
    {
        NoReading.Score(LongMemEvalEvalResultAdapter.ToEvalResult(LongMemEval(Question("q1", null)), "Quick").Score, "B-11");
        return Task.CompletedTask;
    }

    private static Task B11Twin()
    {
        Assert.True(LongMemEvalEvalResultAdapter.ToEvalResult(LongMemEval(Question("q1", true)), "Quick").Score.CountsTowardAggregate());
        return Task.CompletedTask;
    }

    // ── B-13, B-14: auto-audit and trace fidelity ────────────────────────────

    private static Task B13Nothing()
    {
        var report = AutoAuditRunner.Compare(new[]
        {
            new AutoAuditEndpointResult("no-recorder", 1.00, 0, 0, 0, 5, true, Array.Empty<string>()) { FidelityMeasured = false },
        });

        Assert.Null(report.Winner);
        return Task.CompletedTask;
    }

    private static Task B13Twin()
    {
        var report = AutoAuditRunner.Compare(new[]
        {
            new AutoAuditEndpointResult("no-recorder", 1.00, 0, 0, 0, 5, true, Array.Empty<string>()) { FidelityMeasured = false },
            new AutoAuditEndpointResult("measured", 0.80, 0, 100, 50, 10, true, new[] { "hidden_retries ×1" }),
        });

        Assert.Equal("measured", report.Winner!.Endpoint);
        return Task.CompletedTask;
    }

    private static Task B14Nothing()
    {
        NoReading.Score(new TraceFidelityRunner().ReconcileToEvalResult(new TraceLog(), new TraceLog()).Score, "B-14");
        return Task.CompletedTask;
    }

    private static Task B14Twin()
    {
        // The clean pair of TraceFidelityTests: the agent's account and the chat boundary agree.
        var agent = new TraceLog
        {
            Version = "1.0",
            Performance = new TracePerformance { TotalPromptTokens = 10, TotalCompletionTokens = 5 },
            Entries = new List<TraceEntry>
            {
                new()
                {
                    Type = TraceEntryType.Response,
                    Index = 0,
                    ToolCalls = [new TraceToolCall { Name = "Book", Arguments = "{\"x\":1}" }],
                },
            },
        };
        var chat = new TraceLog
        {
            Version = "1.1",
            Entries = new List<TraceEntry>
            {
                TraceEntry.ForChatResponse(0, "inv-1", "…", 10, new TraceTokenUsage { PromptTokens = 10, CompletionTokens = 5 },
                    [new TraceToolCall { Name = "Book", Arguments = "{\"x\":1}" }], "stop", null),
            },
        };

        var result = new TraceFidelityRunner().ReconcileToEvalResult(agent, chat);

        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.CountsTowardAggregate());
        return Task.CompletedTask;
    }

    // ── B-18: paired comparison ──────────────────────────────────────────────

    private static double PairedDelta(params Observation[] observations)
    {
        var comparer = new PairedEvalComparer(RepCollapse.Mean);
        foreach (var observation in observations)
            comparer.Record(observation);
        return comparer.Compare("ref", "new", ruleHash: "sha256:deadbeef").MeanDelta;
    }

    private static Task B18Nothing()
    {
        NoReading.NaN(PairedDelta(Observation.NotMeasured("c0", "ref"), Observation.NotMeasured("c0", "new")), "B-18");
        return Task.CompletedTask;
    }

    private static Task B18Twin()
    {
        Assert.Equal(1.0, PairedDelta(Observation.Measured("c0", "ref", 0.0), Observation.Measured("c0", "new", 1.0)), 12);
        return Task.CompletedTask;
    }

    private static Task<(double?, double?)> B18Partly() => Task.FromResult<(double?, double?)>((
        PairedDelta(Observation.Measured("c0", "ref", 0.0), Observation.Measured("c0", "new", 1.0),
                    Observation.NotMeasured("c1", "ref"), Observation.Measured("c1", "new", 1.0)),
        PairedDelta(Observation.Measured("c0", "ref", 0.0), Observation.Measured("c0", "new", 1.0))));

    // ── B-19: run stats and verdict ──────────────────────────────────────────

    private static Task B19Nothing()
    {
        var stats = new[] { Skipped(), Error(), Inapplicable() }.ToRunStats();

        Assert.Equal(new RunStats(3, 0, 0, 0, 3), stats);
        Assert.Equal("PENDING", Skipped().RunVerdict(stats));
        return Task.CompletedTask;
    }

    private static Task B19Twin()
    {
        var stats = new[] { Pass(), Fail() }.ToRunStats();

        Assert.Equal(new RunStats(2, 1, 1, 0, 0), stats);
        Assert.Equal("FAIL", Fail().RunVerdict(stats));
        return Task.CompletedTask;
    }

    // ── C: red team ──────────────────────────────────────────────────────────

    private static AttackResult Attack(string name, string owasp, int resisted, int inconclusive)
    {
        var probes = Enumerable.Range(0, resisted)
            .Select(i => new ProbeResult { ProbeId = $"{name}-r{i}", Prompt = "p", Response = "no", Outcome = EvaluationOutcome.Resisted, Reason = "refused" })
            .Concat(Enumerable.Range(0, inconclusive)
                .Select(i => new ProbeResult { ProbeId = $"{name}-i{i}", Prompt = "p", Response = "?", Outcome = EvaluationOutcome.Inconclusive, Reason = "undecidable" }))
            .ToList();
        return new AttackResult
        {
            AttackName = name, OwaspId = owasp, ProbeResults = probes, ResistedCount = resisted, InconclusiveCount = inconclusive,
            // The MITRE ATLAS ids the real attacks carry (PromptInjectionAttack, PIILeakageAttack): the MITRE reporter
            // maps by them, so without them a measured scan tests no technique and has no compliance rate.
            MitreAtlasIds = name switch
            {
                "PromptInjection" => ["AML.T0051"],
                "PIILeakage" => ["AML.T0037", "AML.T0057"],
                _ => [],
            },
        };
    }

    private static RedTeamResult Scan(params AttackResult[] attacks) => new()
    {
        AgentName = "census",
        AttackResults = attacks,
        TotalProbes = attacks.Sum(a => a.TotalCount),
        ResistedProbes = attacks.Sum(a => a.ResistedCount),
        InconclusiveProbes = attacks.Sum(a => a.InconclusiveCount),
    };

    private static RedTeamResult[] NothingConclusive() =>
        [Scan(), Scan(Attack("PromptInjection", "LLM01", resisted: 0, inconclusive: 5))];

    private static RedTeamResult Resisted() =>
        Scan(Attack("PromptInjection", "LLM01", resisted: 10, inconclusive: 0), Attack("PIILeakage", "LLM02", resisted: 8, inconclusive: 0));

    private static Task C1Nothing()
    {
        foreach (var scan in NothingConclusive())
        {
            Assert.Equal(Verdict.Inconclusive, scan.Verdict);
            Assert.False(scan.Passed, "C1");
        }
        return Task.CompletedTask;
    }

    private static Task C1Twin()
    {
        Assert.NotEqual(Verdict.Inconclusive, Resisted().Verdict);
        return Task.CompletedTask;
    }

    private static Task C2Nothing()
    {
        // An attack success rate of 0.0 reads "fully secure" by its own doc.
        foreach (var scan in NothingConclusive())
        {
            Assert.Null(scan.OverallScore);
            Assert.Null(scan.ConclusiveScore);
            Assert.Null(scan.AttackSuccessRate);
            Assert.Null(scan.ConclusiveAttackSuccessRate);
        }
        return Task.CompletedTask;
    }

    private static Task C2Twin()
    {
        var scan = Resisted();

        Assert.Equal(100.0, scan.OverallScore);
        Assert.Equal(0.0, scan.AttackSuccessRate);   // a measured zero stays real
        return Task.CompletedTask;
    }

    private static Task C2bNothing()
    {
        foreach (var attack in new[] { Attack("PromptInjection", "LLM01", 0, 0), Attack("PromptInjection", "LLM01", 0, 5) })
        {
            Assert.Null(attack.AttackSuccessRate);
            Assert.Null(attack.ConclusiveAttackSuccessRate);
        }
        return Task.CompletedTask;
    }

    private static Task C2bTwin()
    {
        Assert.Equal(0.0, Attack("PromptInjection", "LLM01", 10, 0).AttackSuccessRate);
        return Task.CompletedTask;
    }

    private static Task C3Nothing()
    {
        // The PDF rated a scan that measured nothing 100 / Low risk.
        var calculator = new RiskScoreCalculator();

        Assert.Null(calculator.CalculateScore(Scan()));
        var summary = calculator.GetSummary(Scan());
        Assert.Null(summary.Score);
        Assert.Null(summary.Level);
        return Task.CompletedTask;
    }

    private static Task C3Twin()
    {
        Assert.True(new RiskScoreCalculator().CalculateScore(Resisted()) is int score && score is >= 0 and <= 100);
        return Task.CompletedTask;
    }

    private static IEnumerable<(string Framework, Type Producer, Func<RedTeamResult, IComplianceReport> Generate)> ComplianceReporters() =>
    [
        ("owasp", typeof(OWASPComplianceReport), scan => new OWASPComplianceReporter().GenerateReport(scan)),
        ("mitre", typeof(MITREATLASReport), scan => new MITREATLASReporter().GenerateReport(scan)),
        ("nist", typeof(NistAiRmfComplianceReport), scan => new NistAiRmfComplianceReporter().GenerateReport(scan)),
        ("soc2", typeof(SOC2ComplianceReport), scan => new SOC2ComplianceReporter().GenerateReport(scan)),
        ("iso27001", typeof(ISO27001ComplianceReport), scan => new ISO27001ComplianceReporter().GenerateReport(scan)),
    ];

    private static Task C4Nothing(Func<RedTeamResult, IComplianceReport> generate, string row)
    {
        // A rate of 0.0% and a Low (or Moderate) risk level for nothing evaluated.
        var report = generate(Scan());

        Assert.True(report.ComplianceRate is null, $"{row}: compliance rate {report.ComplianceRate}");
        Assert.True(report.RiskLevel is null, $"{row}: risk level {report.RiskLevel}");
        if (report is OWASPComplianceReport owasp)
            Assert.Null(owasp.Summary.OverallPassRate);
        return Task.CompletedTask;
    }

    private static Task C4Twin(Func<RedTeamResult, IComplianceReport> generate)
    {
        var report = generate(Resisted());

        Assert.True(report.ComplianceRate is double rate && double.IsFinite(rate), $"compliance rate {report.ComplianceRate}");
        return Task.CompletedTask;
    }

    // ── D: cost ──────────────────────────────────────────────────────────────

    private static EvalResult Priced(string key, double? cost) =>
        Leaf(key, Pass()) with { Provenance = new("atomic-llm", "judge", null, null, null, cost, false) };

    private static Task D1Nothing()
    {
        var (cost, _, unpriced) = CostRollup.Aggregate([Priced("a", null), Priced("b", null)]);

        NoReading.Number(cost, "D1");
        Assert.Equal(2, unpriced.Count);
        return Task.CompletedTask;
    }

    private static Task D1Twin()
    {
        var (cost, _, unpriced) = CostRollup.Aggregate([Priced("a", 0.01), Priced("b", 0.02)]);

        Assert.Equal(0.03, cost!.Value, 10);
        Assert.Empty(unpriced);
        return Task.CompletedTask;
    }

    private static AgenticTelemetry Telemetry(double? costUsd) =>
        new(800, 1200, 2000, 4200, costUsd, 0, 10, 0, new Dictionary<string, double>());

    private static async Task D2Nothing()
    {
        NoReading.Score((await new CostEval(Telemetry(null)).EvaluateAsync(new EvalInput("q"))).Score, "D2");
    }

    private static async Task D2Twin()
    {
        var result = await new CostEval(Telemetry(0.08)).EvaluateAsync(new EvalInput("q"));

        Assert.True(result.Score.CountsTowardAggregate());
        Assert.True(result.Score.Passed);
    }

    private static async Task D3Nothing() => NoReading.Score(
        (await new TokenUsageEval(Telemetry(0.08) with { TokensUsed = null }).EvaluateAsync(new EvalInput("q"))).Score, "D3");

    private static async Task D3Twin()
    {
        var result = await new TokenUsageEval(Telemetry(0.08)).EvaluateAsync(new EvalInput("q"));
        Assert.True(result.Score.CountsTowardAggregate());
        Assert.True(result.Score.Passed);
    }

    private static async Task D4Nothing() => NoReading.Score(
        (await new LatencyEval(Telemetry(0.08) with { LatencyMsP99 = null }).EvaluateAsync(new EvalInput("q"))).Score, "D4");

    private static async Task D4Twin()
    {
        var result = await new LatencyEval(Telemetry(0.08)).EvaluateAsync(new EvalInput("q"));
        Assert.True(result.Score.CountsTowardAggregate());
        Assert.True(result.Score.Passed);
    }

    // ── O: open ──────────────────────────────────────────────────────────────

    private static Task O1Nothing()
    {
        var registry = new AgentEval.Compliance.Gdpr.Articles.ArticlesRegistry(
            new AgentEval.Compliance.Gdpr.Articles.Loading.ArticleScenarioYamlLoader(),
            new AgentEval.Compliance.Gdpr.Articles.Building.ArticleCompositeBuilder(
                new AgentEval.Compliance.Gdpr.Articles.Building.ScenarioToAtomicEval(new NeverCalledJudge(), judgeModel: "census")));
        var summary = new AgentEval.Compliance.Gdpr.Reporting.SummaryBuilder(registry).Build(Root("gdpr.census", Skipped()));

        // OverallStatus and score now agree: skipped is not a measured zero.
        Assert.Null(summary.OverallScore);
        return Task.CompletedTask;
    }

    private static Task O1Twin()
    {
        var registry = new AgentEval.Compliance.Gdpr.Articles.ArticlesRegistry(
            new AgentEval.Compliance.Gdpr.Articles.Loading.ArticleScenarioYamlLoader(),
            new AgentEval.Compliance.Gdpr.Articles.Building.ArticleCompositeBuilder(
                new AgentEval.Compliance.Gdpr.Articles.Building.ScenarioToAtomicEval(new NeverCalledJudge(), judgeModel: "census")));
        var summary = new AgentEval.Compliance.Gdpr.Reporting.SummaryBuilder(registry).Build(Root("gdpr.census", Pass(0.9)));

        Assert.Equal(0.9, summary.OverallScore);
        return Task.CompletedTask;
    }

    private static Task O2Nothing()
    {
        var registry = new AgentEval.Compliance.EuAiAct.Articles.EuAiActArticlesRegistry(
            new AgentEval.Compliance.EuAiAct.Articles.Loading.ArticleScenarioYamlLoader(),
            new AgentEval.Compliance.EuAiAct.Articles.Building.ArticleCompositeBuilder(
                new AgentEval.Compliance.EuAiAct.Articles.Building.ScenarioToAtomicEval(new NeverCalledJudge(), judgeModel: "census")));
        var summary = new AgentEval.Compliance.EuAiAct.Reporting.SummaryBuilder(registry).Build(Root("euaiact.census", Skipped()));

        Assert.Null(summary.OverallScore);
        return Task.CompletedTask;
    }

    private static Task O2Twin()
    {
        var registry = new AgentEval.Compliance.EuAiAct.Articles.EuAiActArticlesRegistry(
            new AgentEval.Compliance.EuAiAct.Articles.Loading.ArticleScenarioYamlLoader(),
            new AgentEval.Compliance.EuAiAct.Articles.Building.ArticleCompositeBuilder(
                new AgentEval.Compliance.EuAiAct.Articles.Building.ScenarioToAtomicEval(new NeverCalledJudge(), judgeModel: "census")));
        var summary = new AgentEval.Compliance.EuAiAct.Reporting.SummaryBuilder(registry).Build(Root("euaiact.census", Pass(0.9)));

        Assert.Equal(0.9, summary.OverallScore);
        return Task.CompletedTask;
    }

    // ── Stand-ins ────────────────────────────────────────────────────────────

    /// <summary>A judge the summary builders hold but never call: they read a finished tree.</summary>
    private sealed class NeverCalledJudge : IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the census builds summaries from given trees; no scenario runs");
    }

    /// <summary>A gate that blocks the texts <paramref name="block"/> picks: the calibration input, not a grade.</summary>
    private sealed class PredicateGate(Func<string, bool> block) : IChatGate
    {
        public string PolicyName => "census";

        public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default) =>
            new(block(text) ? GateVerdict.Block(PolicyName, "blocked") : GateVerdict.Allow(PolicyName));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
