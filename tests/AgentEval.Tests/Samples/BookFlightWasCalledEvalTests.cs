// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Models;
using AgentEval.TravelDemo.Evals;

namespace AgentEval.Tests.Samples;

/// <summary>
/// Task 2.1. TravelDemo's first deterministic eval, replacing two
/// <c>result.ToolUsage?.Calls.Count(…) ?? 0</c> reads that rendered "no recorder ran" and "a recorder
/// saw nothing" as the same number.
/// </summary>
public class BookFlightWasCalledEvalTests
{
    private static readonly TestCase Case = new() { Name = "c", Input = "book me a flight" };

    private static EvalInput Project(ToolUsageReport? usage) =>
        Case.ToEvalInput(new TestResult { TestName = "t", ToolUsage = usage });

    private static ToolUsageReport Recorder(params ToolCallRecord[] calls)
    {
        var r = new ToolUsageReport();
        foreach (var c in calls) r.AddCall(c);
        return r;
    }

    [Fact]
    public async Task NullToolCalls_IsNotApplicable_NeverAPass()
    {
        // The whole point of the task: with no recorder at all, the projection yields null and the
        // eval must decline to answer rather than report a measured zero.
        var result = await new BookFlightWasCalledEval().EvaluateAsync(Project(usage: null));

        Assert.Equal(MeasurementState.NotApplicable, result.Score.CensusBucket());
        Assert.False(result.Score.Passed);
        Assert.Contains("absent record is not an empty one", result.Details.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlindRecorder_IsAlsoNotApplicable()
    {
        var blind = new ToolUsageReport { DroppedApprovalRequestCount = 1 };

        var result = await new BookFlightWasCalledEval().EvaluateAsync(Project(blind));

        Assert.Equal(MeasurementState.NotApplicable, result.Score.CensusBucket());
    }

    [Fact]
    public async Task OneExecutedBookFlight_Passes()
    {
        var usage = Recorder(new ToolCallRecord
        { Name = "BookFlight", CallId = "c1", Order = 1, WasExecuted = true, Result = "CONF-AE-205-9931" });

        var result = await new BookFlightWasCalledEval().EvaluateAsync(Project(usage));

        Assert.True(result.Score.Passed);
        Assert.Equal(MeasurementState.Measured, result.Score.CensusBucket());
    }

    [Fact]
    public async Task AnErroredBookFlight_DoesNotCount()
    {
        // A call that threw is not a booking. Without the marker check this would pass on the mere
        // presence of a call named BookFlight.
        var usage = Recorder(new ToolCallRecord
        {
            Name = "BookFlight", CallId = "c1", Order = 1, WasExecuted = true,
            Exception = new InvalidOperationException("carrier rejected the booking"),
        });

        var result = await new BookFlightWasCalledEval().EvaluateAsync(Project(usage));

        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.Measured, result.Score.CensusBucket());   // measured, not undecidable
    }

    [Fact]
    public async Task ANotExecutedBookFlight_DoesNotCount()
    {
        var usage = Recorder(new ToolCallRecord
        {
            Name = "BookFlight", CallId = "c1", Order = 1,
            ApprovalState = ToolCallRecord.ApprovalRejected,
        });

        var result = await new BookFlightWasCalledEval().EvaluateAsync(Project(usage));

        Assert.False(result.Score.Passed);
    }

    [Fact]
    public async Task ARecordedZero_IsAMeasurement_NotAnAbsence()
    {
        // The other half of the distinction: a recorder that ran and saw a different tool is a real
        // measured failure, and must NOT be reported as undecidable.
        var usage = Recorder(new ToolCallRecord
        { Name = "SearchFlights", CallId = "c1", Order = 1, WasExecuted = true, Result = "3 results" });

        var result = await new BookFlightWasCalledEval().EvaluateAsync(Project(usage));

        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.Measured, result.Score.CensusBucket());
    }

    [Fact]
    public void TheFloorIsNotDerivable_AndSaysWhy()
    {
        var floor = BookFlightWasCalledEval.DeclaredFloor;

        Assert.Equal(FloorState.NotDerivable, floor.State);
        Assert.False(string.IsNullOrWhiteSpace(floor.Derivation));
        Assert.Contains("no tool-call budget", floor.Derivation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThroughTheDoor_TheResultCarriesTheReason_NotABar()
    {
        // A NotDerivable floor writes NO number — only the reason. A 0.0 bar would be a bar that
        // everything clears, which is the "a floor of 0.0 is not no-floor" trap.
        var admitted = FloorAdmittedEval.Admit(new BookFlightWasCalledEval(), BookFlightWasCalledEval.DeclaredFloor);
        var usage = Recorder(new ToolCallRecord
        { Name = "BookFlight", CallId = "c1", Order = 1, WasExecuted = true, Result = "ok" });

        var result = await admitted.EvaluateAsync(Project(usage));

        Assert.False(result.Details.Dimensions?.ContainsKey(AgentEval.Output.ComparabilityFacts.ChanceFloorDimension) == true);
        Assert.Contains(result.Details.Evidence!,
            e => string.Equals(e.Source, AgentEval.Output.ComparabilityFacts.ChanceFloorEvidenceSource, StringComparison.Ordinal));
    }
}
