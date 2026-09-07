// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Evals.Meta;
using AgentEval.Output;
using AgentEval.Samples.EvalJoin;
using AgentEval.Tests.Output;
using Xunit;

namespace AgentEval.Tests.Samples;

/// <summary>
/// Drives the SAMPLE FILE, not a copy of it, so "the runnable example" and "the thing the test
/// proves" cannot drift apart.
/// </summary>
public class DeterministicBenchmarkSampleTests
{
    private static string CheckKeyOf(DeterministicBenchmark.BenchmarkOutcome o) =>
        o.Definition.Checks[0].Eval.Key;

    [Fact]
    public async Task TheSampleProducesTwoArms_TwoRepsEach_AsSeparateRunDirectories()
    {
        using var temp = TempWorkspace.Create("BenchSample");

        var outcome = await DeterministicBenchmark.ExecuteAsync(temp.Path);

        Assert.Equal(DeterministicBenchmark.Reps, outcome.Live.Count);
        Assert.Equal(DeterministicBenchmark.Reps, outcome.Broken.Count);

        // Four distinct runs. Reps in ONE run would collide scenario ids; arms in one run would hide
        // the arm from `compare`, which pairs directories.
        var runIds = outcome.Live.Concat(outcome.Broken).Select(r => r.RunId).ToList();
        Assert.Equal(4, runIds.Distinct(StringComparer.Ordinal).Count());

        Assert.All(outcome.Live, r => Assert.Equal(DeterministicBenchmark.LiveArm, r.ArmId));
        Assert.All(outcome.Broken, r => Assert.Equal(DeterministicBenchmark.BrokenArm, r.ArmId));
    }

    [Fact]
    public async Task TheLiveArmClearsItsFloor_AndTheFloorCameFromTheRoster()
    {
        using var temp = TempWorkspace.Create("BenchSampleFloor");

        var outcome = await DeterministicBenchmark.ExecuteAsync(temp.Path);
        var comparison = BenchmarkScore.AgainstFloor(outcome.Live, RepCollapse.All)
            .Single(c => c.CheckKey == CheckKeyOf(outcome)).Comparison;

        // 1 of 8 cities: the floor is derived from the ROSTER, before anything ran, never from the
        // arm's own output.
        Assert.Equal(1.0 / EvalWithChanceFloor.Cities.Count, comparison.FloorUsed, 10);
        Assert.Equal(outcome.Definition.Cases.Count, comparison.Trials);
        Assert.Equal(outcome.Definition.Cases.Count, comparison.Successes);
        Assert.True(comparison.AboveFloor);
    }

    [Fact]
    public async Task TheBrokenArmIsTheControl_AndItLosesEveryCase()
    {
        using var temp = TempWorkspace.Create("BenchSampleControl");

        var outcome = await DeterministicBenchmark.ExecuteAsync(temp.Path);
        var paired = BenchmarkScore.AgainstReference(outcome.Broken, outcome.Live, RepCollapse.All)
            .Single(c => c.CheckKey == CheckKeyOf(outcome)).Comparison;

        Assert.Equal(outcome.Definition.Cases.Count, paired.Wins);
        Assert.Equal(0, paired.Losses);
        Assert.Equal(0, paired.Ties);

        // n is the CASE count, never cases × reps. This is the whole reason reps collapse first.
        Assert.Equal(outcome.Definition.Cases.Count, paired.EffectiveN);
        Assert.Equal(outcome.Definition.Cases.Count, paired.Unit.Cases);
        Assert.Equal(
            outcome.Definition.Cases.Count * DeterministicBenchmark.Reps * 2,
            paired.Unit.TotalReps);
    }

    [Fact]
    public async Task TwoRepOneDirectories_PairByName_WithoutADuplicateIdThrow()
    {
        // The acceptance `agenteval compare` performs: index both runs by scenario id and match. A
        // duplicate id would throw here rather than exiting 0 or 13.
        using var temp = TempWorkspace.Create("BenchSampleCompare");

        var outcome = await DeterministicBenchmark.ExecuteAsync(temp.Path);
        var store = new FileSystemOutputStore(temp.Path);

        var baseline = await ReadAsync(store, outcome.Broken[0].RunId);
        var candidate = await ReadAsync(store, outcome.Live[0].RunId);

        var comparison = RunComparison.Of(baseline, candidate);

        Assert.Equal(outcome.Definition.Cases.Count, comparison.Scenarios.Count);
        Assert.Empty(comparison.BaselineOnly);
        Assert.Empty(comparison.CandidateOnly);
    }

    [Fact]
    public async Task TheCensusIsFull_SoNoRowWentMissing()
    {
        using var temp = TempWorkspace.Create("BenchSampleCensus");

        var outcome = await DeterministicBenchmark.ExecuteAsync(temp.Path);
        var census = BenchmarkScore.Census(outcome.Live)
            .Single(c => c.CheckKey == CheckKeyOf(outcome)).Census;

        Assert.Equal(outcome.Definition.Cases.Count, census.Total);
        Assert.Equal(outcome.Definition.Cases.Count, census.Measured);
        Assert.False(census.Void);
    }

    [Fact]
    public async Task TheSampleUsesTheSHIPPEDEval_Unchanged()
    {
        // The demonstration is "your existing eval becomes a definition with no changes". If the
        // sample quietly swapped in a bespoke copy, that claim would be false.
        using var temp = TempWorkspace.Create("BenchSampleShipped");

        var outcome = await DeterministicBenchmark.ExecuteAsync(temp.Path);
        var check = Assert.Single(outcome.Definition.Checks);

        Assert.IsType<AskedCityWasLookedUpEval>(check.Eval);
        Assert.Equal("asked_city_was_looked_up", check.Eval.Key);
    }

    private static async Task<List<ScenarioResult>> ReadAsync(FileSystemOutputStore store, string runId)
    {
        var rows = new List<ScenarioResult>();
        await foreach (var row in store.GetScenarioResultsAsync(runId)) rows.Add(row);
        return rows;
    }
}
