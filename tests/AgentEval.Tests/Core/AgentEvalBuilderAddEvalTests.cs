// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Models;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Core;

/// <summary>
/// AE-04 end to end: agent run → <c>EvalInput</c> → floor-gated <c>IEval</c> → <c>EvalResult</c>
/// carrying its floor → read back by the library's own persistence reader.
/// </summary>
public class AgentEvalBuilderAddEvalTests
{
    private sealed class StubEval : IEval
    {
        public StubEval(string key = "stub_eval") => Key = key;

        public string Key { get; }
        public string Name => "Stub Eval";
        public string Category => "test";
        public string Version => "1.0.0";
        public EvalInput? LastInput { get; private set; }

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
        {
            LastInput = input;
            return Task.FromResult(new EvalResult(
                Metric: new(Key, Name, Category, Version),
                Score: new(0.9, null, "pass", true, 0.7, "none", null),
                Details: new(null, null, null, null, null),
                Provenance: new("atomic-code", null, null, null, null, 0.0, false),
                EvaluatedAt: DateTimeOffset.Parse("2026-09-07T00:00:00Z")));
        }
    }

    private static AgentEvalBuilder NewBuilder() => AgentEvalBuilder.Create().WithNoLogging();

    // ══════════════════════════════════════════════════════════════════════════
    // The door
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AddEval_RefusesAnEvalWithNoFloor_AndNamesIt()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => NewBuilder().AddEval(new StubEval("task_completion"), null!));

        Assert.Contains("task_completion", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddEval_RefusesANotDerivableFloorCarryingNoReason()
    {
        var floorless = new ChanceFloor(
            ChanceFloor.KindNotDerivable, FloorState.NotDerivable, double.NaN, null, 0, 0, "   ");

        Assert.Throws<ArgumentException>(() => NewBuilder().AddEval(new StubEval(), floorless));
    }

    [Fact]
    public void AddEval_AdmitsANotDerivableFloorThatStatesItsReason()
    {
        var runner = NewBuilder()
            .AddEval(new StubEval(), ChanceFloor.NotDerivable("free text over an unbounded space"))
            .Build();

        Assert.Single(runner.Evals);
    }

    [Fact]
    public void AddEval_RefusesADuplicateKey()
    {
        var builder = NewBuilder().AddEval(new StubEval("dup"), ChanceFloor.UniformChoice(2));

        var ex = Assert.Throws<ArgumentException>(
            () => builder.AddEval(new StubEval("dup"), ChanceFloor.UniformChoice(8)));

        Assert.Contains("dup", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddEval_IsFluentAndOrderPreserving()
    {
        var runner = NewBuilder()
            .AddEval(new StubEval("first"), ChanceFloor.UniformChoice(2))
            .AddEval(new StubEval("second"), ChanceFloor.NotDerivable("no pool"))
            .Build();

        Assert.Equal(new[] { "first", "second" }, runner.Evals.Select(e => e.Key));
    }

    [Fact]
    public void EveryAdmittedEvalCarriesItsFloor_AndTheSetIsNotEmpty()
    {
        // The NotEmpty is the positive control: "every admitted eval carries a floor" is trivially
        // true of an empty registry, so the claim is only worth making over a non-empty one.
        var runner = NewBuilder()
            .AddEval(new StubEval("first"), ChanceFloor.UniformChoice(2))
            .AddEval(new StubEval("second"), ChanceFloor.NotDerivable("no pool"))
            .Build();

        Assert.NotEmpty(runner.Evals);
        Assert.All(runner.Evals, e => Assert.False(string.IsNullOrWhiteSpace(e.Floor.Derivation)));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ABLATION 4 — VACUITY: the gate must not "pass" when given nothing
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WithZeroEvalsRegistered_TheRunnerProducesZeroResults_AndSaysSo()
    {
        var runner = NewBuilder().Build();

        var results = await runner.EvaluateEvalsAsync(new EvalInput("q"));

        // An empty registry yields an empty result set — a statement about the REGISTRY, never a
        // pass. Nothing here reads as "all evals cleared their floor".
        Assert.Empty(runner.Evals);
        Assert.Empty(results);
    }

    [Fact]
    public async Task TheAllCarryAFloorAssertion_FailsWhenSomethingWithoutAFloorIsForcedIn()
    {
        // Direction check on the assertion used above: over a NON-empty set of results it is capable
        // of failing. An unadmitted eval's result carries no chance-floor evidence at all.
        var unadmitted = new StubEval("never_admitted");
        var results = new[] { await unadmitted.EvaluateAsync(new EvalInput("q")) };

        Assert.NotEmpty(results);
        Assert.DoesNotContain(
            results,
            r => r.Details.Evidence?.Any(e => e.Source == ComparabilityFacts.ChanceFloorEvidenceSource) == true);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The join, end to end — and the floor read back off a real result
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AnAgentRunReachesAnIEval_AndTheResultCarriesTheFloorItWasAdmittedUnder()
    {
        var testCase = new TestCase
        {
            Id = "case-7",
            Name = "Refund flow",
            Input = "I want a refund for order 12345.",
            ExpectedTools = new[] { "lookup_order" },
            GroundTruth = "Order 12345 is refundable.",
        };
        var usage = new ToolUsageReport();
        usage.AddCall(new ToolCallRecord { Name = "lookup_order", CallId = "c1", Order = 1, Result = "found" });
        var runResult = new TestResult
        {
            TestName = "Refund flow",
            Passed = true,
            ActualOutput = "Your refund is on its way.",
            ToolUsage = usage,
        };

        var stub = new StubEval("refund_quality");
        var runner = NewBuilder().AddEval(stub, ChanceFloor.UniformChoice(4)).Build();

        // 1 · the projection
        var input = testCase.ToEvalInput(runResult);
        // 2 · the floor-gated door
        var results = await runner.EvaluateEvalsAsync(input);

        var result = Assert.Single(results);

        // the eval saw the real run
        Assert.Equal("I want a refund for order 12345.", stub.LastInput!.Query);
        Assert.Equal("Your refund is on its way.", stub.LastInput.Response);
        Assert.Equal("case-7", stub.LastInput.CaseId);
        Assert.Equal(new[] { "lookup_order" }, stub.LastInput.ToolCalls!.Select(c => c.Name));
        Assert.Equal(new[] { "lookup_order" }, stub.LastInput.ExpectedActions!.Select(a => a.Description));

        // 3 · the floor reached the result, and the LIBRARY'S OWN reader finds it there —
        //     no new vocabulary, no schema change.
        var scenario = EvalResultPersistence.ToScenarioResult(
            result, "s1", "Refund flow", assertions: Array.Empty<AgentEval.Output.AssertionResult>());

        var recorded = scenario.Comparability!.ChanceFloor;
        Assert.NotNull(recorded);
        Assert.Equal(FloorState.Derived, recorded!.State);
        Assert.Equal(0.25, recorded.Bar!.Value, 10);
        Assert.Equal(ChanceFloor.KindUniformChoice, recorded.Kind);
        Assert.True(recorded.IsUsableAsABar);
        Assert.Equal("refund_quality", scenario.Comparability.EvalKey);
    }

    [Fact]
    public async Task ANotDerivableFloorSurvivesPersistence_AsAReason_NeverAsABar()
    {
        const string Reason = "the response is free text over an unbounded space; there is no pool to draw from";
        var runner = NewBuilder().AddEval(new StubEval("free_text"), ChanceFloor.NotDerivable(Reason)).Build();

        var results = await runner.EvaluateEvalsAsync(new EvalInput("q"));
        var scenario = EvalResultPersistence.ToScenarioResult(
            results[0], "s1", "n", assertions: Array.Empty<AgentEval.Output.AssertionResult>());

        var recorded = scenario.Comparability!.ChanceFloor;
        Assert.NotNull(recorded);
        Assert.Equal(FloorState.NotDerivable, recorded!.State);
        Assert.Null(recorded.Bar);                 // an absent floor is NOT a zero floor
        Assert.False(recorded.IsUsableAsABar);
        Assert.Equal(Reason, recorded.Derivation); // somebody asked, and said why they could not answer
    }

    [Fact]
    public async Task AnEvalThatNeverWentThroughTheDoor_RecordsNoFloorAtAll()
    {
        // The third state, kept distinct from the other two: null Comparability.ChanceFloor is
        // "nobody derived one", which is neither a bar nor a stated reason. This is what all 74
        // existing IEval implementations look like today, and admitting one is the only thing that
        // changes it.
        var result = await new StubEval("unadmitted").EvaluateAsync(new EvalInput("q"));

        var scenario = EvalResultPersistence.ToScenarioResult(
            result, "s1", "n", assertions: Array.Empty<AgentEval.Output.AssertionResult>());

        Assert.Null(scenario.Comparability!.ChanceFloor);
    }

    [Fact]
    public async Task EvaluateEvalsAsync_RefusesANullInput()
    {
        var runner = NewBuilder().Build();

        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.EvaluateEvalsAsync(null!));
    }
}
