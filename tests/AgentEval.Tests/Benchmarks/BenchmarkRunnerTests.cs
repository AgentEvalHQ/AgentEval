// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Models;
using AgentEval.Output;
using AgentEval.Tests.Output;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// The runner: admission before observation, one row per (case, check), no orphan manifest, and no
/// floor applied to any verdict.
/// </summary>
public class BenchmarkRunnerTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>A check that reads one literal out of the response. Deterministic, no judge.</summary>
    private sealed class ContainsEval(string key, string needle)
        : AtomicCodeEval(key, $"Response contains '{needle}'", "test", "1.0.0")
    {
        protected override EvalResult Evaluate(EvalInput input)
        {
            if (input.Response is null)
                return NotApplicable("no response was captured, so nothing here can say what it contained.");

            var hit = input.Response.Contains(needle, StringComparison.Ordinal);
            return Build(hit ? 1.0 : 0.0, hit, hit ? "none" : "medium");
        }
    }

    /// <summary>A check that always declines. Its rows must land in `skipped`, never in `failed`.</summary>
    private sealed class AlwaysInapplicableEval()
        : AtomicCodeEval("always_inapplicable", "Always inapplicable", "test", "1.0.0")
    {
        protected override EvalResult Evaluate(EvalInput input) =>
            NotApplicable("this check declines by construction; it is the fixture for the skipped bucket.");
    }

    private static readonly ChanceFloor Floor = ChanceFloor.UniformChoice(4);

    private static TestCase Case(string id, string input = "q") =>
        new() { Id = id, Name = $"case {id}", Input = input };

    private static BenchmarkDefinition Definition(
        IReadOnlyList<TestCase>? cases = null, IReadOnlyList<AdmittedCheck>? checks = null) =>
        new("bench", "1.0.0",
            cases ?? [Case("c1"), Case("c2")],
            checks ?? [new AdmittedCheck(new ContainsEval("k1", "yes"), Floor)]);

    private static BenchmarkArm Answering(string armId, string response) =>
        BenchmarkArm.From(armId, (c, _) => Task.FromResult(new EvalInput(Query: c.Input, Response: response)));

    private static BenchmarkRunner RunnerIn(TempWorkspace temp, string subjectName = "BenchSubject") =>
        new(new FileSystemOutputStore(temp.Path),
            new SubjectIdentity(SubjectKind.Agent, subjectName));

    private static async Task<List<ScenarioResult>> ReadRowsAsync(TempWorkspace temp, string runId)
    {
        var store = new FileSystemOutputStore(temp.Path);
        var rows = new List<ScenarioResult>();
        await foreach (var row in store.GetScenarioResultsAsync(runId)) rows.Add(row);
        return rows;
    }

    // ── The id ────────────────────────────────────────────────────────────────

    [Fact]
    public void ScenarioIdSurvivesSanitizeUnchanged()
    {
        // If the separator were rewritten, Sanitize would ALSO append a hash of the original, making
        // the on-disk name unpredictable — and two arms' directories pair by name.
        var id = BenchmarkRunner.ScenarioId("c1", "travel.book_flight_called");

        Assert.Equal(id, FileSystemLayout.Sanitize(id));
        Assert.Contains(BenchmarkRunner.ScenarioIdSeparator, id, StringComparison.Ordinal);
    }

    // ── Admission happens first ───────────────────────────────────────────────

    [Fact]
    public async Task EveryCheckIsAdmittedBeforeTheFirstCaseIsObserved()
    {
        using var temp = TempWorkspace.Create("BenchAdmitFirst");
        var observed = 0;
        var arm = BenchmarkArm.From("live", (c, _) =>
        {
            Interlocked.Increment(ref observed);
            return Task.FromResult(new EvalInput(Query: c.Input, Response: "yes"));
        });

        // A floorless check. The door refuses it — and the arm must never have been called, because
        // admitting lazily per case would have bought the first case's run before refusing.
        var floorless = Definition(checks: [new AdmittedCheck(new ContainsEval("k1", "yes"), null!)]);

        await Assert.ThrowsAsync<ArgumentException>(() => RunnerIn(temp).RunAsync(floorless, arm));

        Assert.Equal(0, observed);
    }

    [Fact]
    public async Task TheOtherDirection_AFlooredCheckRuns_AndTheArmIsObservedOncePerCase()
    {
        using var temp = TempWorkspace.Create("BenchAdmitOk");
        var observed = 0;
        var arm = BenchmarkArm.From("live", (c, _) =>
        {
            Interlocked.Increment(ref observed);
            return Task.FromResult(new EvalInput(Query: c.Input, Response: "yes"));
        });

        var run = await RunnerIn(temp).RunAsync(Definition(), arm);

        Assert.Equal(2, observed);
        Assert.Equal(2, run.Observations.Count);
        Assert.All(run.Observations, o => Assert.Equal(MeasurementState.Measured, o.Observation.State));
    }

    // ── Two arms pair by construction ─────────────────────────────────────────

    [Fact]
    public async Task TwoArms_ProduceTwoRunDirectories_WhoseScenarioIdsPairByConstruction()
    {
        using var temp = TempWorkspace.Create("BenchTwoArms");
        var definition = Definition(
            cases: [Case("c1"), Case("c2"), Case("c3")],
            checks:
            [
                new AdmittedCheck(new ContainsEval("k1", "yes"), Floor),
                new AdmittedCheck(new ContainsEval("k2", "sure"), Floor),
            ]);

        var good = await RunnerIn(temp).RunAsync(definition, Answering("live", "yes and sure"));
        var bad = await RunnerIn(temp).RunAsync(definition, Answering("broken", "no"));

        Assert.NotEqual(good.RunId, bad.RunId);

        var left = await ReadRowsAsync(temp, good.RunId);
        var right = await ReadRowsAsync(temp, bad.RunId);

        Assert.Equal(6, left.Count);      // 3 cases × 2 checks
        Assert.Equal(6, right.Count);

        // The pairing is the acceptance: no duplicate-id throw, and nothing on either side is orphaned.
        var comparison = RunComparison.Of(left, right);

        Assert.Equal(6, comparison.Scenarios.Count);
        Assert.Empty(comparison.BaselineOnly);
        Assert.Empty(comparison.CandidateOnly);
    }

    // ── The failure path leaves no orphan manifest ────────────────────────────

    [Fact]
    public async Task AThrowingObserve_CompletesTheRun_ThenRethrows()
    {
        using var temp = TempWorkspace.Create("BenchThrow");
        var arm = BenchmarkArm.From("live", (_, _) =>
        {
            throw new InvalidOperationException("the agent fell over");
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunnerIn(temp).RunAsync(Definition(), arm));

        Assert.Equal("the agent fell over", ex.Message);

        // A run directory with a manifest and no summary reads as "still running" forever.
        var store = new FileSystemOutputStore(temp.Path);
        var manifests = new List<RunManifest>();
        await foreach (var m in store.ListRunsAsync(new SubjectIdentity(SubjectKind.Agent, "BenchSubject")))
            manifests.Add(m);

        var manifest = Assert.Single(manifests);
        var summary = await store.GetRunSummaryAsync(manifest.Run.RunId);

        // The summary EXISTS — that is the orphan check. A run directory carrying a manifest and no
        // summary reads as "still running" forever to every consumer in this repository.
        Assert.NotNull(summary);
        Assert.Equal("PENDING", summary!.Verdict);   // nothing was measured — not PASS, and not VOID
        Assert.Equal(0, summary.Stats.Total);
    }

    // ── Buckets ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ANotApplicableRow_CountsAsSkipped_NeverAsFailed()
    {
        using var temp = TempWorkspace.Create("BenchSkipped");
        var definition = Definition(
            cases: [Case("c1"), Case("c2")],
            checks:
            [
                new AdmittedCheck(new ContainsEval("k1", "yes"), Floor),
                new AdmittedCheck(new AlwaysInapplicableEval(), Floor),
            ]);

        var run = await RunnerIn(temp).RunAsync(definition, Answering("live", "yes"));

        var store = new FileSystemOutputStore(temp.Path);
        var summary = await store.GetRunSummaryAsync(run.RunId);

        Assert.NotNull(summary);
        Assert.Equal(4, summary!.Stats.Total);
        Assert.Equal(2, summary.Stats.Passed);
        Assert.Equal(0, summary.Stats.Failed);        // ← the point: an inapplicable row is not a failure
        Assert.Equal(2, summary.Stats.Skipped);
        Assert.Equal("PASS", summary.Verdict);

        // And the buckets reconcile, which is what stops a row being counted twice or lost.
        Assert.Equal(
            summary.Stats.Total,
            summary.Stats.Passed + summary.Stats.Failed + summary.Stats.Warnings + summary.Stats.Skipped);
    }

    [Fact]
    public async Task ARunWhereNothingWasMeasured_IsPENDING_NotPASS()
    {
        using var temp = TempWorkspace.Create("BenchPending");
        var definition = Definition(checks: [new AdmittedCheck(new AlwaysInapplicableEval(), Floor)]);

        var run = await RunnerIn(temp).RunAsync(definition, Answering("live", "yes"));

        var store = new FileSystemOutputStore(temp.Path);
        var summary = await store.GetRunSummaryAsync(run.RunId);

        Assert.Equal("PENDING", summary!.Verdict);
        Assert.Equal(0, summary.Stats.Passed);
        Assert.Equal(2, summary.Stats.Skipped);
    }

    // ── The floor reaches disk ────────────────────────────────────────────────

    [Fact]
    public async Task TheFloorSurvivesToDisk_ReadBackByComparabilityOf()
    {
        // The M1 sample proved this in memory only (§80.9). This reads it off the persisted row.
        using var temp = TempWorkspace.Create("BenchFloorDisk");
        var run = await RunnerIn(temp).RunAsync(Definition(), Answering("live", "yes"));

        var rows = await ReadRowsAsync(temp, run.RunId);
        var row = rows[0];

        Assert.NotNull(row.Comparability);
        Assert.NotNull(row.Comparability!.ChanceFloor);
        Assert.Equal(FloorState.Derived, row.Comparability.ChanceFloor!.State);
        Assert.Equal(0.25, row.Comparability.ChanceFloor.Bar!.Value, 10);
    }

    [Fact]
    public async Task ANotDerivableFloor_ReachesDiskAsAnAbsence_NotAsAZero()
    {
        using var temp = TempWorkspace.Create("BenchFloorAbsent");
        var definition = Definition(checks:
        [
            new AdmittedCheck(
                new ContainsEval("k1", "yes"),
                ChanceFloor.NotDerivable("no draw model exists for a free-text containment check")),
        ]);

        var run = await RunnerIn(temp).RunAsync(definition, Answering("live", "yes"));
        var row = (await ReadRowsAsync(temp, run.RunId))[0];

        Assert.Equal(FloorState.NotDerivable, row.Comparability!.ChanceFloor!.State);
        Assert.Null(row.Comparability.ChanceFloor.Bar);   // ← an absent floor is NOT a floor of 0.0
        Assert.False(string.IsNullOrWhiteSpace(row.Comparability.ChanceFloor.Derivation));
    }

    // ── 3.6: the extension point refuses a smuggled subject ───────────────────

    [Fact]
    public async Task AnArmThatSmugglesAnAgentThroughMetadata_IsRefused_BeforeAnyCheckRuns()
    {
        using var temp = TempWorkspace.Create("BenchSmuggle");
        var ran = 0;

        var counting = new AdmittedCheck(new CountingEval(() => Interlocked.Increment(ref ran)), Floor);
        var arm = BenchmarkArm.From("live", (c, _) => Task.FromResult(
            new EvalInput(Query: c.Input, Response: "yes")
            {
                Metadata = new Dictionary<string, object> { ["agent"] = new SmuggledAgent() },
            }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunnerIn(temp).RunAsync(Definition(checks: [counting]), arm));

        Assert.Contains("Metadata is DATA", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task ADelegateInMetadata_IsAlsoRefused()
    {
        using var temp = TempWorkspace.Create("BenchSmuggleDelegate");
        var arm = BenchmarkArm.From("live", (c, _) => Task.FromResult(
            new EvalInput(Query: c.Input, Response: "yes")
            {
                Metadata = new Dictionary<string, object> { ["run"] = (Func<int>)(() => 1) },
            }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunnerIn(temp).RunAsync(Definition(), arm));

        Assert.Contains("Delegate", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADataRecordInMetadata_IsFine()
    {
        // The other direction, and it matters: Metadata is a legitimate place for DATA. A guard that
        // refused everything would push consumers to smuggle by another route.
        using var temp = TempWorkspace.Create("BenchMetadataOk");
        var arm = BenchmarkArm.From("live", (c, _) => Task.FromResult(
            new EvalInput(Query: c.Input, Response: "yes")
            {
                Metadata = new Dictionary<string, object>
                {
                    ["gate"] = new GateFacts(Blocked: true, Reason: "policy"),
                    ["attempt"] = 3,
                },
            }));

        var run = await RunnerIn(temp).RunAsync(Definition(), arm);

        Assert.Equal(2, run.Observations.Count);
        Assert.All(run.Observations, o => Assert.True(o.Result.Score.Passed));
    }

    private sealed record GateFacts(bool Blocked, string Reason);

    private sealed class SmuggledAgent : IEvaluableAgent
    {
        public string Name => "smuggled";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "the guard must fire before anything can reach this — if this throws, the smuggled subject was RUN");
    }

    private sealed class CountingEval(Action onRun)
        : AtomicCodeEval("counting", "Counting", "test", "1.0.0")
    {
        protected override EvalResult Evaluate(EvalInput input)
        {
            onRun();
            return Build(1.0, true, "none");
        }
    }

    // ── The lane rule ─────────────────────────────────────────────────────────

    [Fact]
    public void BenchmarkRunner_IsNotAnIEval()
    {
        // The census intersection ("the only IEval carrying a floor is the door") must be unchanged
        // by this type existing — which is half of why Q-A was answered "inside the rule".
        Assert.False(typeof(IEval).IsAssignableFrom(typeof(BenchmarkRunner)));
    }
}
