// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using AgentEval.Evals;
using AgentEval.Models;

namespace AgentEval.TravelDemo.Evals;

/// <summary>
/// Offline invariants for <see cref="BookFlightWasCalledEval"/>, runnable with no credentials.
/// </summary>
/// <remarks>
/// The fixtures are built the way the harness builds them — <see cref="ToolUsageReport.AddCall"/>
/// with an <c>Order</c> and <c>WasExecuted</c> — rather than hand-made timelines, because a fixture
/// kinder than reality proves nothing about reality.
/// </remarks>
public static class BookFlightSelfTest
{
    public static async Task<int> RunAsync()
    {
        var runner = await new AgentEvalBuilder()
            .AddEval(new BookFlightWasCalledEval(), BookFlightWasCalledEval.DeclaredFloor)
            .BuildAsync(CancellationToken.None);

        var testCase = new TestCase { Name = "selftest", Input = "book me a flight" };

        // 1 · a recorder that saw an executed BookFlight ⇒ pass
        var booked = new ToolUsageReport();
        booked.AddCall(new ToolCallRecord
        { Name = BookFlightWasCalledEval.ToolName, CallId = "c1", Order = 1, WasExecuted = true, Result = "CONF-AE-205-9931" });
        var passResult = (await runner.EvaluateEvalsAsync(
            testCase.ToEvalInput(new TestResult { TestName = "booked", ToolUsage = booked }),
            CancellationToken.None))[0];

        // 2 · a recorder that admits it dropped approval-gated calls ⇒ UNDECIDABLE, never a fail
        var blind = new ToolUsageReport { DroppedApprovalRequestCount = 1 };
        var blindResult = (await runner.EvaluateEvalsAsync(
            testCase.ToEvalInput(new TestResult { TestName = "blind", ToolUsage = blind }),
            CancellationToken.None))[0];

        var okPass = passResult.Score.Passed;
        var okBlind = !blindResult.Score.Passed
                   && blindResult.Score.CensusBucket() == AgentEval.Evals.Meta.MeasurementState.NotApplicable;

        Console.WriteLine($"  executed BookFlight        → {passResult.Score.Label} (expect pass)     {(okPass ? "OK" : "FAIL")}");
        Console.WriteLine($"  blind recorder             → {blindResult.Score.Label} / {blindResult.Score.CensusBucket()} "
                        + $"(expect inapplicable)  {(okBlind ? "OK" : "FAIL")}");

        var ok = okPass && okBlind;
        Console.WriteLine(ok ? "  selftest OK" : "  selftest FAILED");
        return ok ? 0 : 1;
    }
}
