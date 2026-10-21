// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.RegularExpressions;
using AgentEval.Evals;
using Xunit;
using Xunit.Sdk;

using static AgentEval.Tests.Census.CensusFakes;

namespace AgentEval.Tests.Census;

/// <summary>
/// T13 census (b): a producer of a number or a verdict that was given nothing measured gives no number and no verdict,
/// still measures a measured input, and never averages a placeholder in. A reflection guard keeps the census complete:
/// every type that looks like an aggregation is a row here or is listed with the reason it needs none.
/// </summary>
/// <remarks>
/// Wave 1 fixed many single aggregations, each with its own tests. This pins them together and keeps the known gaps
/// visible without a Skip: a row with an <see cref="CensusRow.OpenTask"/> must still fail (B3), and once it holds the
/// census says so.
/// </remarks>
public sealed class NothingMeasuredAggregationCensusTests
{
    private static IReadOnlyList<CensusRow> Rows => NothingMeasuredCensusRows.All;

    public static TheoryData<string> HoldingRows()
    {
        var data = new TheoryData<string>();
        foreach (var row in Rows.Where(r => r.OpenTask is null))
            data.Add(row.Id);
        return data;
    }

    public static TheoryData<string> AllRows()
    {
        var data = new TheoryData<string>();
        foreach (var row in Rows)
            data.Add(row.Id);
        return data;
    }

    public static TheoryData<string> PartlyMeasuredRows()
    {
        var data = new TheoryData<string>();
        foreach (var row in Rows.Where(r => r.PartlyMeasured is not null && r.OpenTask is null))
            data.Add(row.Id);
        return data;
    }

    private static CensusRow Row(string id) => Rows.Single(r => r.Id == id);

    // ── B1–B4: the rows ──────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(HoldingRows))]
    public async Task NothingMeasured_GivesNoNumberAndNoVerdict(string id)
    {
        await Row(id).NothingMeasured();
    }

    [Theory]
    [MemberData(nameof(AllRows))]
    public async Task AMeasuredInput_IsStillMeasured(string id)
    {
        await Row(id).MeasuredTwin();
    }

    [Fact]
    public async Task AnOpenRow_StillFails_UntilItsFixLands()
    {
        // A known gap stays visible without a Skip: its row must fail on its own assertion. A crash is not "still
        // failing" — it would hide the gap behind an unrelated error.
        var problems = new List<string>();
        foreach (var row in Rows.Where(r => r.OpenTask is not null))
        {
            try
            {
                await row.NothingMeasured();
                problems.Add($"row {row.Id} now holds: set OpenTask = null ({row.OpenTask})");
            }
            catch (XunitException)
            {
                // Still open, as recorded.
            }
            catch (Exception ex)
            {
                problems.Add($"row {row.Id} crashed instead of failing its assertion: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [MemberData(nameof(PartlyMeasuredRows))]
    public async Task APlaceholderIsNeverAveragedIn(string id)
    {
        var (withUnmeasured, measuredOnly) = await Row(id).PartlyMeasured!();

        Assert.True(measuredOnly is { } value && double.IsFinite(value), $"{id}: the measured inputs alone gave {measuredOnly}");
        Assert.True(withUnmeasured == measuredOnly,
            $"{id}: {withUnmeasured} with the unmeasured inputs, {measuredOnly} without them — a placeholder was averaged in");
    }

    // ── B5: every aggregation strategy ───────────────────────────────────────

    /// <summary>Every <see cref="IAggregationStrategy"/> the product ships, found by reflection.</summary>
    private static IReadOnlyList<IAggregationStrategy> AggregationStrategies() =>
        ProductAssemblies.ExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IAggregationStrategy).IsAssignableFrom(t))
            .Select(t => t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IAggregationStrategy
                         ?? (IAggregationStrategy)Activator.CreateInstance(t)!)
            .OrderBy(s => s.GetType().FullName, StringComparer.Ordinal)
            .ToList();

    public static TheoryData<string> Strategies()
    {
        var data = new TheoryData<string>();
        foreach (var strategy in AggregationStrategies())
            data.Add(strategy.GetType().FullName!);
        return data;
    }

    [Fact]
    public void TheStrategyCensusIsNotVacuous()
    {
        Assert.True(AggregationStrategies().Count >= 5, $"found {AggregationStrategies().Count} aggregation strategies");
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task EveryAggregationStrategy_LeavesNothingMeasuredUnscored(string strategyName)
    {
        var strategy = AggregationStrategies().Single(s => s.GetType().FullName == strategyName);
        var shapes = new (string Name, Func<EvalScore> Score)[]
        {
            ("every part skipped", Skipped),
            ("every part errored", Error),
            ("every part inapplicable", Inapplicable),
        };

        foreach (var (name, score) in shapes)
        {
            var composite = NothingMeasuredCensusRows.Composite(strategy,
                new EvalComponent(new FixedEval("a", score())), new EvalComponent(new FixedEval("b", score())));

            var result = await composite.EvaluateAsync(new EvalInput("q", "r"));

            NoReading.Score(result.Score, $"{strategyName} / {name}");
        }

        // Through the weights-only entry point: an errored part's placeholder 0 is not in the aggregate.
        var mixed = strategy.AggregateWeights([Leaf("a", Pass(0.8)), Leaf("b", Error())], [1.0, 1.0]);
        var alone = strategy.AggregateWeights([Leaf("a", Pass(0.8))], [1.0]);
        Assert.Equal(alone, mixed);
    }

    // ── B6–B8: the reflection guard ──────────────────────────────────────────

    /// <summary>
    /// Discovered types that need no row, each with the reason. A reason is one line: a count (a zero is a true zero),
    /// config, a DTO of a row's producer, a verifier or writer that computes no number, or a placeholder that a flag
    /// already marks as no reading.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Justified = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Abstractions
        ["AgentEval.Comparison.DistributionStatistics"] = "placeholder plus flag: SampleSize 0 marks an empty sample (T71 makes the StochasticResult members that returned one null)",
        ["AgentEval.Comparison.DistributionStatisticsFactory"] = "factory: an empty list gives SampleSize 0, the documented flag",
        ["AgentEval.Comparison.ModelComparisonOptions"] = "config: runs per model and weights",
        ["AgentEval.Comparison.ModelComparisonResult"] = "DTO of ModelComparer (row B-3), built only when a model was measured",
        ["AgentEval.Comparison.ModelRanking"] = "DTO of ModelComparer (row B-3): only measured models are ranked",
        ["AgentEval.Comparison.StochasticStatistics"] = "placeholder plus flag: SampleSize 0, and the result's Verdict is NotMeasured (row B-2)",
        ["AgentEval.Comparison.ToolUsageSummary"] = "DTO of StochasticResult.GetToolUsageSummary; T71 makes its rates null when no run tracked tools",
        ["AgentEval.Core.EvaluationResult"] = "placeholder plus flag: an evaluator's OverallScore with EvaluationFailed set is no reading",
        ["AgentEval.Evals.EvalDetails"] = "data: carries a result's sub-results and computes nothing",
        ["AgentEval.Evals.Meta.FloorComparison"] = "statistical test: with no measured trial its p-value and interval say 'no evidence'; Census counts the excluded",
        ["AgentEval.Evals.Meta.ObservationCensus"] = "count: a zero is a true zero",
        ["AgentEval.Evals.Meta.PairedComparison"] = "DTO of PairedEvalComparer (row B-18)",
        ["AgentEval.Models.EvaluationReport"] = "DTO of TestSummary.ToEvaluationReport; T71 makes OverallScore, PassRate and Duration null when nothing was measured or timed",
        ["AgentEval.Models.TestResult"] = "data: one test's result; JudgeFailed and Error mark it not measured (row B-1)",
        ["AgentEval.Models.TestResultSummary"] = "DTO of EvaluationReport; T71 makes Score and DurationMs null when not measured",
        ["AgentEval.Models.TestSummaryExtensions"] = "maps TestSummary to EvaluationReport; T71 tests it",
        ["AgentEval.Models.ToolStatistics"] = "statistics over a non-empty group of one tool's calls; no call, no entry",
        ["AgentEval.Models.WorkflowTestResult"] = "data: one workflow test's result",
        ["AgentEval.Output.BaselineComparison"] = "DTO of the store's run-vs-baseline deltas, taken only over metrics both summaries carry (rows B-7 keep a placeholder out)",
        ["AgentEval.Output.EvidenceSummary"] = "count: controls passed, warned and failed",
        ["AgentEval.Output.RunSummary"] = "DTO: summary.json; rows B-7 check what the writers put in Metrics",
        ["AgentEval.Output.ScenarioResult"] = "data: one scenario; Measurement marks one not measured (row B-4)",
        ["AgentEval.Snapshots.SemanticComparisonResult"] = "compares one response with one snapshot; no aggregate",
        ["AgentEval.Snapshots.SnapshotComparisonResult"] = "compares one response with one snapshot; no aggregate",

        // Compliance
        ["AgentEval.Compliance.EuAiAct.Reporting.EuAiActArticleSummary"] = "DTO of the EU AI Act SummaryBuilder (row O2); status by ReportStatus",
        ["AgentEval.Compliance.EuAiAct.Reporting.EuAiActComplianceEvidence"] = "evidence document carrying EuAiActSummary (row O2)",
        ["AgentEval.Compliance.EuAiAct.Reporting.EuAiActPillarSummary"] = "DTO of the EU AI Act SummaryBuilder (row O2); status by ReportStatus",
        ["AgentEval.Compliance.EuAiAct.Reporting.EuAiActSummary"] = "DTO of the EU AI Act SummaryBuilder (row O2: its OverallScore is the open gap)",
        ["AgentEval.Compliance.Gdpr.Reporting.GdprArticleSummary"] = "DTO of the GDPR SummaryBuilder (row O1); status by ReportStatus",
        ["AgentEval.Compliance.Gdpr.Reporting.GdprComplianceEvidence"] = "evidence document carrying GdprSummary (row O1)",
        ["AgentEval.Compliance.Gdpr.Reporting.GdprPillarSummary"] = "DTO of the GDPR SummaryBuilder (row O1); status by ReportStatus",
        ["AgentEval.Compliance.Gdpr.Reporting.GdprSummary"] = "DTO of the GDPR SummaryBuilder (row O1: its OverallScore is the open gap)",

        // DataLoaders
        ["AgentEval.Output.AssertionSummary"] = "data: one assertion's outcome in a trace; no number",
        ["AgentEval.Output.ComparisonResultExtensions"] = "prints a StochasticResult through TableFormatter (T71 guards its not-measured rows)",
        ["AgentEval.Output.ExecutionSummary"] = "trace record of one execution: counts, and a cost that is null when not priced",

        // Evals.Agentic
        ["AgentEval.Evals.Agentic.Process.ToolCallAccuracyAggregateEval"] = "wraps a CompositeEval with WeightedSum (rows A1)",
        ["AgentEval.Evals.Agentic.Reporting.AgenticBenchmarkResult"] = "evaluation artifact carrying AgenticSummary (row A11)",
        ["AgentEval.Evals.Agentic.Reporting.AgenticCategorySummary"] = "DTO of AgenticSummaryBuilder (row A11): Score must be a number (agentic-result.schema.json); Status ERROR or SKIPPED says it is no reading",
        ["AgentEval.Evals.Agentic.Reporting.AgenticEvaluatorSummary"] = "DTO of AgenticSummaryBuilder (row A11): the evaluator's own score and status",
        ["AgentEval.Evals.Agentic.Reporting.AgenticSummary"] = "DTO of AgenticSummaryBuilder (row A11): OverallScore must be a number (schema); OverallStatus carries the absence",

        // MAF
        ["AgentEval.MAF.Gatekeeper.GateBlockAggregation"] = "count: refusals by tool, policy, fingerprint, severity and day",
        ["AgentEval.MAF.Gatekeeper.GateReferenceIndexAggregator"] = "count: reads the refusal index into GateBlockAggregation",
        ["AgentEval.MAF.Gatekeeper.GateReferenceLedger+GateReferenceIndexEntry"] = "data: one refusal-index line ('Index' is a name, not an aggregation)",
        ["AgentEval.MAF.Gatekeeper.GateReplayComparison"] = "count: verdicts that differ between two gate configurations on the same traffic",

        // Memory
        ["AgentEval.Memory.External.LongMemEval.LongMemEvalPentagonMapper"] = "averages only the types that have an accuracy; an axis with none is left out",
        ["AgentEval.Memory.External.Models.ExternalBenchmarkResult"] = "DTO: OverallAccuracy is null when nothing was scored (row B-11)",
        ["AgentEval.Memory.External.Models.ExternalJudgmentResult"] = "one judgment: Correct and RawScore are null when the judge gave none",
        ["AgentEval.Memory.External.Models.QuestionResult"] = "data: one question; Correct is null when not scored",
        ["AgentEval.Memory.External.Models.TypedMemEvalAttributionSummary"] = "count: questions attributed to each outcome",
        ["AgentEval.Memory.External.Models.TypedMemEvalCoverageSummary"] = "placeholder plus flag: a share of a vertical with QuestionsTotal 0",
        ["AgentEval.Memory.External.TypedMemEval.TypedMemEvalEvalResultAdapter"] = "maps a TypedMemEval run to an EvalResult tree; nothing scored is not measured (BenchmarkAdaptersNotMeasuredTests)",
        ["AgentEval.Memory.External.TypedMemEval.TypedMemEvalRunSet"] = "count: questions whose outcome flipped between runs",
        ["AgentEval.Memory.External.TypedMemEval.TypedMemEvalRunSetSummary"] = "count: what a set of runs agreed and disagreed about",
        ["AgentEval.Memory.Models.CrossSessionResult"] = "placeholder plus flag: OverallScore with IsMeasured false is no reading",
        ["AgentEval.Memory.Models.MemoryBaselineComparison"] = "DTO of BaselineComparer (row B-10)",
        ["AgentEval.Memory.Models.MemoryEvaluationResult"] = "placeholder plus flag: OverallScore with IsMeasured false is no reading",
        ["AgentEval.Memory.Models.ReachBackResult"] = "placeholder plus flag: IsMeasured",
        ["AgentEval.Memory.Models.ReducerEvaluationResult"] = "placeholder plus flag: FidelityScore with MeasuredFacts 0 is no reading",
        ["AgentEval.Memory.Reporting.PentagonConsolidator"] = "averages only the categories that were not skipped; an axis with none is left out",

        // RedTeam
        ["AgentEval.RedTeam.Baseline.AttackComparison"] = "DTO of RedTeamBaselineComparer: refused when either run has no overall score (T81)",
        ["AgentEval.RedTeam.Baseline.BaselineComparisonExtensions"] = "assertions over a RedTeamComparison; an unmeasured baseline is refused (RedTeamBaselineTests)",
        ["AgentEval.RedTeam.Baseline.ComparisonThresholds"] = "config: regression thresholds",
        ["AgentEval.RedTeam.Baseline.RedTeamBaseline"] = "DTO of a saved RedTeamResult; T81 makes OverallScore null when nothing was conclusive",
        ["AgentEval.RedTeam.Baseline.RedTeamBaselineComparer"] = "refuses scans with different probe sets or no overall score (T81, RedTeamBaselineTests)",
        ["AgentEval.RedTeam.Baseline.RedTeamComparison"] = "DTO of RedTeamBaselineComparer",
        ["AgentEval.RedTeam.MultiTurnResult"] = "one multi-turn conversation folded into one probe verdict; no number",
        ["AgentEval.RedTeam.Reporting.BypassClassBreakdown"] = "per-class attack success with a Wilson interval; only a class that ran has a slice",
        ["AgentEval.RedTeam.Reporting.Compliance.ComplianceSummary"] = "DTO of the OWASP and MITRE reports (rows C4): T81 makes OverallPassRate null when nothing was conclusive",
        ["AgentEval.RedTeam.Reporting.Pdf.RiskSummary"] = "DTO of RiskScoreCalculator (row C3)",

        // Results
        ["AgentEval.Results.Checkpoints.ComparisonRule"] = "config: which runs a checkpoint lane compares",
        ["AgentEval.Results.Requirements.RequirementAggregation"] = "config: how a requirement reads an AEF lane",
        ["AgentEval.Results.Runs.AefSummaryCalculator"] = "AEF summary arithmetic over measured lines; an absence is a typed state (AgentEval.Results.Tests)",
        ["AgentEval.Results.Runs.AefSummaryFigures"] = "DTO of AefSummaryCalculator: figures over the measured lines",
        ["AgentEval.Results.Writing.AefAggregate"] = "config: an AEF entry's aggregation rule",
        ["AgentEval.Results.Writing.AefAggregation"] = "config: an AEF lane's aggregation",
        ["AgentEval.Results.Writing.AefResult"] = "data: one AEF result line; its state types every absence",
        ["AgentEval.Results.Writing.AefSummary"] = "DTO: an AEF run summary written by AefSummaryWriter",
        ["AgentEval.Results.Writing.AefSummaryDecision"] = "DTO: a producer's verdict for an AEF summary lane",
        ["AgentEval.Results.Writing.AefSummaryEntry"] = "DTO: one AEF summary entry",
        ["AgentEval.Results.Writing.AefSummaryLane"] = "DTO: one AEF summary lane",
        ["AgentEval.Results.Writing.AefSummaryWriter"] = "computes summary.json over measured lines only ([SUM-2] to [SUM-9]; AgentEval.Results.Tests)",

        // Results.Adapters
        ["AgentEval.Results.Adapters.EvalPort.EvalPortAggregationContext"] = "verifier context for an eval-port import; produces no score",
        ["AgentEval.Results.Adapters.Native.AefCompositeRunWriter"] = "writer: records each score's state (StateOf, used by NoReading); computes no number",
        ["AgentEval.Results.Adapters.Native.AefEvalCase"] = "data: one case of the AEF eval writer",
        ["AgentEval.Results.Adapters.Native.AefEvalWriter+Session"] = "writer session: records results; computes no number",
        ["AgentEval.Results.Adapters.StoreV1.StoreV1Run"] = "data: a store v1 run being exported to AEF",

        // Core
        ["AgentEval.Benchmarks.AutoAuditReport"] = "DTO of AutoAuditRunner (row B-13)",
        ["AgentEval.Comparison.DefaultStatisticsCalculator"] = "adapter over StatisticsCalculator; the stochastic runner passes measured runs only (row B-2)",
        ["AgentEval.Comparison.ModelComparisonExtensions"] = "Markdown of a ModelComparisonResult; T71 prints n/a for a model with no measured run",
        ["AgentEval.Comparison.StatisticsCalculator"] = "placeholder plus flag: statistics of an empty sample carry SampleSize 0 (row B-2)",
        ["AgentEval.Core.AgentEvalPluginBase"] = "plugin base class; aggregates nothing",
        ["AgentEval.Core.CriterionText"] = "matches a judge's echo of a criterion; produces no score",
        ["AgentEval.Evals.EvalResultPersistence"] = "maps an EvalResult tree to ScenarioResults, carrying not measured as Measurement (row B-4)",
        ["AgentEval.Evals.SeverityRollup"] = "max severity: 'none' over nothing, which no verdict reads alone (CompositeEval guards nothing measured, rows A1)",
        ["AgentEval.Guardrails.Judges.CalibrationReport"] = "calibration of one gate axis over its gold set; the fleet index leaves an absent axis out (row A14)",
        ["AgentEval.Guardrails.Judges.GatekeeperFleetHealthReport"] = "DTO of GatekeeperFleetHealthIndex (row A14)",
        ["AgentEval.Output.ScenarioComparison"] = "DTO of RunComparison (row B-4): BothMeasured gates the delta",
        ["AgentEval.Skills.SkillCoverageSummary"] = "count: skills, resources and scripts scanned",
        ["AgentEval.Skills.SkillSecurityIndexInputs"] = "DTO: the three axes SkillSecurityIndex reads (row A13)",
        ["AgentEval.Skills.SkillSecurityIndexResult"] = "DTO of SkillSecurityIndex (row A13)",
        ["AgentEval.Snapshots.GoldenTrace"] = "DTO of GoldenTraceComparer (row B-5): NotMeasured flags each case",
        ["AgentEval.Snapshots.GoldenTraceComparison"] = "DTO of GoldenTraceComparer (row B-5)",
        ["AgentEval.Snapshots.SnapshotComparer"] = "compares one response with one snapshot; no aggregate",
        ["AgentEval.Snapshots.TraceCaseComparison"] = "DTO of GoldenTraceComparer (row B-5)",
    };

    [Fact]
    public void EveryAggregationInTheProduct_IsInTheCensus()
    {
        var discovered = AggregationDiscovery.Discover();
        var covered = Rows.Select(r => r.Producer.FullName!).Concat(Justified.Keys).ToHashSet(StringComparer.Ordinal);

        var unlisted = discovered.Where(kv => !covered.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();

        Assert.True(unlisted.Count == 0,
            $"{unlisted.Count} type(s) look like an aggregation and are neither a census row's producer nor justified. Add a " +
            "row (NothingMeasuredCensusRows) or a one-line reason (Justified):" + Environment.NewLine + "  " +
            string.Join(Environment.NewLine + "  ", unlisted.Select(kv => $"{kv.Key} [{kv.Value}]")));
    }

    [Fact]
    public void EveryCensusEntry_IsStillAnAggregation()
    {
        // The other direction: an entry for a type that is gone, or no longer looks like an aggregation, is stale; and a
        // row's Discoverable flag says what the guard actually finds.
        var discovered = AggregationDiscovery.Discover();

        var stale = Justified.Keys.Where(k => !discovered.ContainsKey(k)).Select(k => $"justified: {k}");
        var doubled = Justified.Keys.Intersect(Rows.Select(r => r.Producer.FullName!), StringComparer.Ordinal)
            .Select(k => $"both a row's producer and justified: {k}");
        var wrongFlag = Rows.Where(r => r.Discoverable != discovered.ContainsKey(r.Producer.FullName!))
            .Select(r => $"row {r.Id}: Discoverable is {r.Discoverable}, but the guard {(r.Discoverable ? "does not find" : "finds")} {r.Producer.FullName}");
        var problems = stale.Concat(doubled).Concat(wrongFlag).ToList();

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheCensusIsNotVacuous()
    {
        var discovered = AggregationDiscovery.Discover();

        Assert.True(Rows.Count >= 35, $"the census has {Rows.Count} rows");
        Assert.True(discovered.Count >= 50, $"the guard discovered {discovered.Count} types; its predicate measures nothing");
        Assert.Equal(Rows.Count, Rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
        // Each rule finds something, or a rule could silently stop matching.
        foreach (var rule in new[] { "R1", "R2", "R3", "R4" })
            Assert.Contains(discovered.Values, rules => rules.Split(',').Contains(rule));
    }

    // ── B9: run summaries ────────────────────────────────────────────────────

    // An overallScore written into a run summary or a result's details. It must come from MeasuredValue(), or sit under a
    // has-measured guard on one of the three lines above it.
    private static readonly Regex s_overallScoreWrite = new(
        @"\[\s*""overall(Score|_score)""\s*\]\s*=(?!=)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly string[] s_guards = ["MeasuredValue()", "HasMeasuredCategory"];

    [Fact]
    public void NoRunSummaryWritesAPlaceholderOverallScore()
    {
        var root = RepositoryRoot();
        Assert.NotNull(root);

        var sources = Directory.EnumerateFiles(Path.Combine(root!, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f))
            .ToList();
        Assert.True(sources.Count > 500, $"the scan found only {sources.Count} source files, so it measured nothing");

        var writes = new List<string>();
        var offenders = new List<string>();
        foreach (var file in sources)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!s_overallScoreWrite.IsMatch(lines[i]))
                    continue;

                var where = $"{Relative(root!, file)}:{i + 1}";
                writes.Add(where);
                var window = string.Join("\n", lines[Math.Max(0, i - 3)..(i + 1)]);
                if (!s_guards.Any(g => window.Contains(g, StringComparison.Ordinal)) && !IsGuardedMetricDetail(file))
                    offenders.Add($"{where}: {lines[i].Trim()}");
            }
        }

        // Positive control: the run summaries of the agentic, GDPR, EU AI Act, OWASP, NIST, MITRE, perf, stochastic and
        // memory runs.
        Assert.True(writes.Count >= 9, $"found {writes.Count} overallScore writes; the scan would pass vacuously");
        Assert.True(offenders.Count == 0,
            "an overallScore is written without MeasuredValue() or a has-measured guard, so a run that measured nothing writes " +
            "a placeholder 0 beside its PENDING verdict:" + Environment.NewLine + "  " +
            string.Join(Environment.NewLine + "  ", offenders));
    }

    // The memory metrics write overall_score into their details only after returning not measured for an unmeasured
    // result (`if (!memoryResult.IsMeasured) return MetricResult.NotMeasured(...)` at the top of EvaluateAsync).
    private static bool IsGuardedMetricDetail(string file) =>
        file.Replace('\\', '/').Contains("/src/AgentEval.Memory/Metrics/", StringComparison.Ordinal)
        && File.ReadAllText(file).Contains("!memoryResult.IsMeasured", StringComparison.Ordinal);

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string? RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("AgentEval.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}
