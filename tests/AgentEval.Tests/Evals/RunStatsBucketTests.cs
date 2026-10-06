// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// Every check lands in exactly one <see cref="RunStats"/> bucket, so the buckets add up to <c>Total</c> (#203 review,
/// B8). The agentic, GDPR and EU AI Act runners counted four independent predicates — a <c>warn</c> that was not
/// measured was both a warning and skipped, a passed leaf that was not measured both passed and skipped — and the
/// single-composite commands filed a skipped or errored result under Failed. All of them now count through
/// <see cref="EvalScoreExtensions.StatsBucket"/>.
/// </summary>
public class RunStatsBucketTests
{
    private static readonly string[] Labels = ["pass", "fail", "warn", "skipped", "error", "inapplicable", "custom"];
    private static readonly MeasurementState[] States =
        [MeasurementState.Measured, MeasurementState.NotMeasured, MeasurementState.NotApplicable];

    private static EvalScore Score(string label, MeasurementState state, bool passed) =>
        new(passed ? 1.0 : 0.0, null, label, passed, null, "none", null) { Measurement = state };

    // Every label × measurement × passed combination the type can hold. EvalScore refuses Passed with a state other than
    // Measured; the label is a free string (ADR-030 §4.2), so "skipped" or "error" with Passed = true can be stored.
    private static IEnumerable<EvalScore> EveryCombination() =>
        from label in Labels
        from state in States
        from passed in new[] { true, false }
        where state == MeasurementState.Measured || !passed
        select Score(label, state, passed);

    private static EvalResult Leaf(string key, EvalScore score) => new(
        new(key, key, "test", "1.0.0"), score, new(null, null, null, null, null),
        new("atomic-code", null, null, null, null, 0, false), DateTimeOffset.UtcNow);

    private static EvalResult Root(IEnumerable<EvalScore> scores) => new(
        new("root", "Root", "test", "1.0.0"), new EvalScore(0.5, null, "warn", false, null, "none", null),
        new(null, null, null, scores.Select((s, i) => Leaf($"leaf{i}", s)).ToList(), null),
        new("composite", null, null, null, null, 0, false), DateTimeOffset.UtcNow);

    [Fact]
    public void EachCombination_HasTheDocumentedBucket()
    {
        foreach (var score in EveryCombination())
        {
            var expected = score.CensusBucket() != MeasurementState.Measured ? RunStatsBucket.Skipped
                : score.Label == "warn" ? RunStatsBucket.Warnings
                : score.Passed ? RunStatsBucket.Passed
                : RunStatsBucket.Failed;
            Assert.True(expected == score.StatsBucket(), $"{score.Label}/{score.Measurement}/passed={score.Passed}");
        }

        // The cases that matter most, spelled out.
        Assert.Equal(RunStatsBucket.Skipped, Score("warn", MeasurementState.NotMeasured, false).StatsBucket());   // withheld pass
        Assert.Equal(RunStatsBucket.Skipped, Score("error", MeasurementState.Measured, false).StatsBucket());
        Assert.Equal(RunStatsBucket.Skipped, Score("inapplicable", MeasurementState.Measured, false).StatsBucket());
        Assert.Equal(RunStatsBucket.Failed, Score("fail", MeasurementState.Measured, false).StatsBucket());
    }

    public static TheoryData<string> Runners => new() { "agentic", "gdpr", "eu-ai-act" };

    private static RunSummary Summarise(string runner, EvalResult root) => runner switch
    {
        "agentic" => AgentEval.Evals.Agentic.Composition.AgenticBenchmarkRunner.BuildSummary(root, "run"),
        "gdpr" => AgentEval.Compliance.Gdpr.Articles.GdprBenchmarkRunner.BuildSummary(root, "run"),
        "eu-ai-act" => AgentEval.Compliance.EuAiAct.Articles.EuAiActBenchmarkRunner.BuildSummary(root, "run"),
        _ => throw new ArgumentOutOfRangeException(nameof(runner)),
    };

    [Theory]
    [MemberData(nameof(Runners))]
    public void ARunnersBuckets_AddUpToTotal_OverEveryCombination(string runner)
    {
        var scores = EveryCombination().ToList();

        var stats = Summarise(runner, Root(scores)).Stats;

        Assert.Equal(scores.Count, stats.Total);
        Assert.Equal(stats.Total, stats.Passed + stats.Failed + stats.Warnings + stats.Skipped);
        Assert.Equal(scores.ToRunStats(), stats);
    }

    [Theory]
    [MemberData(nameof(Runners))]
    public void TheReviewsExample_FourLeaves_CountFourTimes(string runner)
    {
        // Round 2 L-1: four leaves the old predicates counted six times.
        var stats = Summarise(runner, Root(
        [
            Score("warn", MeasurementState.NotMeasured, false),   // was a warning AND skipped
            Score("skipped", MeasurementState.Measured, true),    // a mislabelled stored result: was passed AND skipped
            Score("pass", MeasurementState.Measured, true),
            Score("fail", MeasurementState.Measured, false),
        ])).Stats;

        Assert.Equal(new RunStats(Total: 4, Passed: 1, Failed: 1, Warnings: 0, Skipped: 2), stats);
    }

    [Fact]
    public void EveryRunStatsBuiltFromCounts_IsAKnownSite_ThatCountsItsOwnOutcomes()
    {
        // A census over src/: a check's EvalScore is counted only through ToRunStats / StatsBucket. The sites below build
        // RunStats from their own domain outcomes (questions, memory categories, test-run counts) or write empty
        // placeholders; each is listed with why. A new `new RunStats(` fails here until it is classified.
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/AgentEval.Abstractions/Evals/EvalScoreExtensions.cs"] = "ToRunStats itself",
            ["src/AgentEval.Core/Benchmarks/BenchmarkRunner.cs"] = "counts through StatsBucket, beside its census",
            ["src/AgentEval.Cli/Commands/BenchLongMemEvalCommand.cs"] = "questions: correct / wrong / unscored (warnings)",
            ["src/AgentEval.Cli/Commands/BenchMemoryCommand.cs"] = "memory categories by score band; skipped categories",
            ["src/AgentEval.Cli/Commands/BenchTypedMemEvalCommand.cs"] = "TypedMemEval outcomes (ADR-026)",
            ["src/AgentEval.DataLoaders/Exporters/DirectoryExporter.cs"] = "a TestRunReport's own counts",
            ["src/AgentEval.DataLoaders/Output/InMemoryOutputStore.cs"] = "PENDING placeholder (zeros)",
            ["src/AgentEval.DataLoaders/Output/NullOutputStore.cs"] = "PENDING placeholder (zeros)",
            ["src/AgentEval.Memory/Reporting/JsonFileBaselineStore.cs"] = "memory baseline categories (B8: Skipped written)",
        };
        var root = RepoRoot();
        var construction = new Regex(@"new\s+(AgentEval\.Output\.)?RunStats\s*\(");

        var sites = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => construction.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();

        var unknown = sites.Where(s => !known.ContainsKey(s)).ToList();
        Assert.True(unknown.Count == 0,
            "RunStats built by hand outside the known sites — count EvalScores with ToRunStats(), or classify the site here: "
            + string.Join(", ", unknown));
        var stale = known.Keys.Where(k => !sites.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.True(stale.Count == 0, "known sites that no longer build RunStats (remove them): " + string.Join(", ", stale));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("AgentEval.sln not found above " + AppContext.BaseDirectory);
    }
}
