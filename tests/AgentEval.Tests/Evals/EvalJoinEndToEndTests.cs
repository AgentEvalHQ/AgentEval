// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Output;
using AgentEval.Samples.EvalJoin;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Evals;

/// <summary>
/// AE-04's join, proven from a REAL agent run rather than from a hand-built <see cref="TestResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>What is simulated, stated once.</b> Exactly one thing: the model's token generation, by
/// <see cref="ScriptedChatClient"/>. The agent (<see cref="ChatClientAgent"/>), MAF's own
/// function-invocation loop, the tool body, <see cref="MAFAgentAdapter"/>,
/// <see cref="MAFEvaluationHarness"/>, <c>ToolUsageExtractor</c> over the real
/// <c>RawMessages</c>, the projection, the admission door, the runner and
/// <see cref="EvalResultPersistence"/> all execute unmodified.
/// <see cref="TheToolBodyReallyRan_SoTheRunIsARunAndNotAFixture"/>
/// is the control on that claim: it counts side effects the test never produces itself, so a
/// fixture quietly replacing the run would turn it red.
/// </para>
/// <para>
/// The subject is the sample file itself — <c>samples/AgentEval.Samples/EvalJoin/01_EvalWithChanceFloor.cs</c>,
/// compiled into this assembly by the test project — so the runnable example and the proof cannot
/// drift apart.
/// </para>
/// </remarks>
public sealed class EvalJoinEndToEndTests
{
    // ────────────────────────────────────────────────────────────────────────
    //  The join, end to end, over the sample's own run
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheToolBodyReallyRan_SoTheRunIsARunAndNotAFixture()
    {
        var run = await EvalWithChanceFloor.ExecuteAsync(CancellationToken.None);

        // MAF dispatched the call; the counter lives inside the AIFunction the sample handed the
        // agent and nothing in the test or the sample increments it directly.
        Assert.Equal(1, run.LookupCount);

        // ...and the recorder saw it through the real extractor, off the real RawMessages.
        Assert.NotNull(run.Result.ToolUsage);
        Assert.Equal(1, run.Result.ToolUsage!.Count);
        Assert.Equal(EvalWithChanceFloor.ToolName, run.Result.ToolUsage.Calls[0].Name);
        Assert.False(run.Result.HasError);
    }

    [Fact]
    public async Task TheProjectionCarriesTheRunsToolCallIntoTheEvalInput()
    {
        var run = await EvalWithChanceFloor.ExecuteAsync(CancellationToken.None);

        Assert.Equal(run.Case.Input, run.Input.Query);
        Assert.Equal(run.Case.Id, run.Input.CaseId);

        // null would be "no recorder ran"; [] would be "a recorder saw nothing". Neither is this.
        Assert.NotNull(run.Input.ToolCalls);
        var call = Assert.Single(run.Input.ToolCalls!);
        Assert.Equal(EvalWithChanceFloor.ToolName, call.Name);
        Assert.NotNull(call.Arguments);
        Assert.Equal(EvalWithChanceFloor.AskedCity, call.Arguments!["city"]?.ToString());
    }

    [Fact]
    public async Task TheResultComesBackCarryingTheFloorItWasAdmittedUnder()
    {
        var run = await EvalWithChanceFloor.ExecuteAsync(CancellationToken.None);

        var admitted = Assert.Single(run.Results);
        Assert.True(admitted.Score.Passed);

        Assert.NotNull(admitted.Details.Dimensions);
        Assert.Equal(
            1.0 / EvalWithChanceFloor.Cities.Count,
            admitted.Details.Dimensions![ComparabilityFacts.ChanceFloorDimension],
            precision: 10);

        var evidence = Assert.Single(
            admitted.Details.Evidence!,
            e => e.Source == ComparabilityFacts.ChanceFloorEvidenceSource);
        Assert.Equal(ChanceFloor.KindUniformChoice, evidence.Reference);
        Assert.False(string.IsNullOrWhiteSpace(evidence.Message));
    }

    [Fact]
    public async Task TheFloorSurvivesPersistence_ReadBackByTheLibrarysOwnReader()
    {
        var run = await EvalWithChanceFloor.ExecuteAsync(CancellationToken.None);

        var recorded = run.Persisted.Comparability?.ChanceFloor;
        Assert.NotNull(recorded);
        Assert.Equal(FloorState.Derived, recorded!.State);
        Assert.Equal(1.0 / EvalWithChanceFloor.Cities.Count, recorded.Bar!.Value, precision: 10);
        Assert.True(recorded.IsUsableAsABar);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Controls. Each one is the same wiring with ONE thing changed, so the
    //  green above is a fact about the join and not about a benign fixture.
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAgentThatLooksUpTheWrongCity_Fails_AndStillCarriesTheFloor()
    {
        var run = await RunAgainstAsync(scriptedCity: "Basel");

        var result = Assert.Single(run);
        Assert.False(result.Score.Passed);
        Assert.Equal(0.0, result.Score.Value);
        Assert.Equal(MeasurementState.Measured, result.Score.Measurement);

        // The floor is not a reward for passing: a FAIL carries it too, or the number would only
        // ever appear beside results that did not need it.
        Assert.Equal(
            1.0 / EvalWithChanceFloor.Cities.Count,
            result.Details.Dimensions![ComparabilityFacts.ChanceFloorDimension],
            precision: 10);
    }

    [Fact]
    public async Task AnAgentThatCallsNoTool_IsAMeasuredZero_NotUndecidable()
    {
        var run = await RunAgainstAsync(scriptedCity: null);

        var result = Assert.Single(run);
        Assert.False(result.Score.Passed);
        // A recorder RAN and saw nothing. That is decidable, so it is a measurement of zero.
        Assert.Equal(MeasurementState.Measured, result.Score.Measurement);
        Assert.Contains("saw no successful", result.Details.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithTrackingOn_TheHarnessDeclaresARecorder_WhichIsWhatMakesTheZeroMeasured()
    {
        // The direction control for the two undecidable cases below: with tracking on, the harness
        // DOES declare a recorder, and the empty result is a measurement rather than an absence.
        var (testCase, result, lookups) = await RealRunAsync(scriptedCity: null, trackTools: true);

        Assert.Equal(0, lookups);
        Assert.NotNull(result.ToolUsage);
        Assert.NotNull(result.Timeline);
        Assert.Empty(testCase.ToEvalInput(result).ToolCalls!);
    }

    [Fact]
    public async Task ARunThatTHREW_LeavesTheToolQuestionUndecidable_NotACleanZero()
    {
        // 🔴 The commonest way to reach an unrecorded run on DEFAULT options. The agent fails before
        //    extraction; the harness used to hand back an EMPTY ToolCallTimeline anyway, which the
        //    projection documents as "a recorder ran and the agent called nothing" — so a run that
        //    crashed reported a clean sheet on every absence-based safety question.
        var model = new ScriptedChatClient().AddThrow("simulated provider outage");
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions { Name = "WeatherDesk" });

        var testCase = new TestCase { Id = "threw", Name = "threw", Input = "anything", PassingScore = 0 };
        var result = await new MAFEvaluationHarness(verbose: false).RunEvaluationAsync(
            new MAFAgentAdapter(agent),
            testCase,
            new EvaluationOptions { TrackTools = true, EvaluateResponse = false, Verbose = false },
            CancellationToken.None);

        Assert.True(result.HasError);
        Assert.Null(result.ToolUsage);
        Assert.Null(result.Timeline);           // ← the fix; it was an empty timeline before
        Assert.Null(testCase.ToEvalInput(result).ToolCalls);

        // The failure report still carries the timeline: it is a diagnostic bundle, not a
        // declaration any eval reads.
        Assert.NotNull(result.Failure?.Timeline);
    }

    [Fact]
    public async Task WithNoToolRecorder_TheQuestionIsUndecidable_AndUndecidableIsNeverAPass()
    {
        // Same real run, TrackTools off: TestResult carries no ToolUsage and no Timeline, so the
        // projection yields ToolCalls = null — "nobody recorded", not "nothing happened".
        var (testCase, result, _) = await RealRunAsync(scriptedCity: EvalWithChanceFloor.AskedCity, trackTools: false);
        var input = testCase.ToEvalInput(result);
        Assert.Null(input.ToolCalls);

        var runner = await BuildRunnerAsync();
        var scored = Assert.Single(await runner.EvaluateEvalsAsync(input, CancellationToken.None));

        Assert.False(scored.Score.Passed);
        Assert.Equal(MeasurementState.NotApplicable, scored.Score.Measurement);
        Assert.Contains("no tool recorder ran", scored.Details.Summary, StringComparison.Ordinal);

        // Undecidable still carries the floor it was admitted under — the reader must be able to
        // tell "we could not decide, and here is the bar we would have used" from "no bar exists".
        Assert.Equal(
            1.0 / EvalWithChanceFloor.Cities.Count,
            scored.Details.Dimensions![ComparabilityFacts.ChanceFloorDimension],
            precision: 10);
    }

    [Fact]
    public void ADoubleAdmission_Throws_RatherThanLettingTheEvalSupplyItsOwnBar()
    {
        // The gate-self-examination guard, reached through the join rather than in isolation: an
        // eval whose result already carries a chance floor is refused, never merged.
        //
        // RELOCATED, not weakened. This assertion used to sit on EvaluateEvalsAsync, where the
        // self-report check catches the double admission at evaluate time. The door now refuses it
        // at ADMIT time, which is strictly earlier and names the offending eval — so the throw no
        // longer reaches evaluation and the assertion moved to where the defect is now caught. The
        // claim under test is unchanged: a double admission throws rather than letting the outer
        // floor silently win.
        var floor = ChanceFloor.UniformChoice(EvalWithChanceFloor.Cities.Count);
        var inner = FloorAdmittedEval.Admit(new AskedCityWasLookedUpEval(EvalWithChanceFloor.AskedCity), floor);

        var ex = Assert.Throws<ArgumentException>(() =>
            new AgentEvalBuilder().AddEval(inner, ChanceFloor.UniformChoice(2)));

        Assert.Contains("already admitted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDoorRefusesAFloorlessEval_SoTheSampleCouldNotHaveSkippedIt()
    {
        // The sample's step 4 is not decoration: there is no overload that omits the floor, and the
        // one that exists refuses a floor with no derivation.
        Assert.Throws<ArgumentException>(() =>
            new AgentEvalBuilder().AddEval(new AskedCityWasLookedUpEval("Bern"), null!));

        Assert.Throws<ArgumentException>(() =>
            new AgentEvalBuilder().AddEval(
                new AskedCityWasLookedUpEval("Bern"),
                new ChanceFloor(ChanceFloor.KindUniformChoice, FloorState.Derived, 0.5, null, 1, 2, "   ")));

        // And a runner with nothing admitted returns NOTHING — an empty result set is a statement
        // about the registry, never a pass.
        var empty = await new AgentEvalBuilder().BuildAsync(CancellationToken.None);
        Assert.Empty(empty.Evals);
        var (testCase, result, _) = await RealRunAsync(EvalWithChanceFloor.AskedCity, trackTools: true);
        Assert.Empty(await empty.EvaluateEvalsAsync(testCase.ToEvalInput(result), CancellationToken.None));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Helpers — the SAME shape the sample uses, with one knob each.
    // ────────────────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<EvalResult>> RunAgainstAsync(string? scriptedCity)
    {
        var (testCase, result, _) = await RealRunAsync(scriptedCity, trackTools: true);
        var runner = await BuildRunnerAsync();
        return await runner.EvaluateEvalsAsync(testCase.ToEvalInput(result), CancellationToken.None);
    }

    private static async Task<AgentEvalRunner> BuildRunnerAsync() =>
        await new AgentEvalBuilder()
            .AddEval(
                new AskedCityWasLookedUpEval(EvalWithChanceFloor.AskedCity),
                ChanceFloor.UniformChoice(EvalWithChanceFloor.Cities.Count))
            .BuildAsync(CancellationToken.None);

    /// <summary>A real MAF agent run with a scripted model. <paramref name="scriptedCity"/> null ⇒ the model calls no tool.</summary>
    private static async Task<(TestCase Case, TestResult Result, int Lookups)> RealRunAsync(
        string? scriptedCity, bool trackTools)
    {
        int lookups = 0;
        var tool = AIFunctionFactory.Create(
            (string city) => { lookups++; return $"{city}: 14°C, light rain"; },
            EvalWithChanceFloor.ToolName,
            "Look up today's weather for one Swiss city.");

        var model = new ScriptedChatClient();
        if (scriptedCity is not null)
        {
            model.AddToolCall("call-1", EvalWithChanceFloor.ToolName,
                new Dictionary<string, object?> { ["city"] = scriptedCity });
        }

        model.AddText("Here is the forecast.");

        var agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Name = "WeatherDesk",
            ChatOptions = new ChatOptions { Tools = [tool] },
        });

        var testCase = new TestCase
        {
            Id = "weather-control",
            Name = "control",
            Input = $"What is the weather in {EvalWithChanceFloor.AskedCity} today?",
            PassingScore = 0,
        };

        var result = await new MAFEvaluationHarness(verbose: false).RunEvaluationAsync(
            new MAFAgentAdapter(agent),
            testCase,
            new EvaluationOptions { TrackTools = trackTools, EvaluateResponse = false, Verbose = false },
            CancellationToken.None);

        return (testCase, result, lookups);
    }
}
