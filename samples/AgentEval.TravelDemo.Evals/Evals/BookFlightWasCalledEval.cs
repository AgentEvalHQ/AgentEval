// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using AgentEval.Evals;
using AgentEval.Evals.Meta;

namespace AgentEval.TravelDemo.Evals;

/// <summary>
/// Did the agent actually book a flight? One deterministic measurement, admitted through the
/// library's floor-gated door — the thin shape a sample should have.
/// </summary>
/// <remarks>
/// <para>
/// This replaces two reads of the form <c>result.ToolUsage?.Calls.Count(…) ?? 0</c>. That
/// <c>?? 0</c> is the defect this whole lane exists to prevent: it renders "no recorder ran, so
/// nobody knows" and "a recorder ran and saw nothing" as the same number, and the number it picks is
/// the one that reads as a clean negative result.
/// </para>
/// <para>
/// A call only counts if it was actually EXECUTED. A <c>BookFlight</c> that threw, or that never ran
/// because it was rejected at an approval gate, is not a booking — so the two markers on
/// <see cref="ToolCall.Result"/> are checked rather than the mere presence of a call by that name.
/// </para>
/// </remarks>
public sealed class BookFlightWasCalledEval()
    : AtomicCodeEval("travel.book_flight_called", "BookFlight was called", "travel", "1.0.0")
{
    /// <summary>The tool whose execution is the measurement.</summary>
    public const string ToolName = "BookFlight";

    /// <summary>
    /// The chance floor this eval is admitted with — <b>not derivable</b>, and the reason is the
    /// point.
    /// </summary>
    /// <remarks>
    /// A floor answers "what would an arm that understood nothing score?", and that needs a draw
    /// budget k. TravelDemo declares none: <c>grep 'MaxToolCalls|ToolCallCap|maxItems|MaxIterations'</c>
    /// over both TravelDemo projects returns <b>0</b> — no cap, no schema limit, no prompt
    /// constraint. Taking k from the observed call count instead would let the arm size its own null,
    /// which is the recorded defect in <see cref="ArmProfile"/>'s remarks: a deliberately implausible
    /// stub read ABOVE its own floor on 3 of 12 personas while a real arm at the identical rate read
    /// BELOW at k = 12.
    /// <para>
    /// If a budget is ever declared, this becomes
    /// <c>ChanceFloor.AtLeastOneHit(poolSize: 9, favourable: 1, draws: k)</c> — 9 being the tool count
    /// on the agent, which must be re-taken from
    /// <c>grep -c 'AIFunctionFactory.Create' TravelAgentFactory.cs</c> rather than copied from here.
    /// </para>
    /// </remarks>
    public static ChanceFloor DeclaredFloor => ChanceFloor.NotDerivable(
        "TravelDemo declares no tool-call budget: no cap, no maxItems, no prompt constraint. k cannot "
        + "be taken from the observed call count without letting the arm size its own null.");

    /// <inheritdoc/>
    protected override EvalResult Evaluate(EvalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.ToolCalls is null)
        {
            var reason =
                "no tool recorder saw this run, so nothing here can say whether a flight was booked. "
                + "An absent record is not an empty one, and an empty one is not a failure.";
            return NotApplicable(reason, new EvalEvidence("tool-calls", ToolName, reason));
        }

        var executed = input.ToolCalls
            .Where(c => string.Equals(c.Name, ToolName, StringComparison.Ordinal))
            .Where(c => c.Result is null
                || (!c.Result.StartsWith(TestRunEvalProjection.ToolErrorResultPrefix, StringComparison.Ordinal)
                 && !c.Result.StartsWith(TestRunEvalProjection.ToolNotExecutedResultPrefix, StringComparison.Ordinal)))
            .ToList();

        var passed = executed.Count >= 1;

        return Build(
            passed ? 1.0 : 0.0,
            passed,
            severity: passed ? "none" : "high",
            evidence:
            [
                new EvalEvidence(
                    "tool-calls",
                    ToolName,
                    passed
                        ? $"{executed.Count} executed {ToolName} call(s) in a record of {input.ToolCalls.Count}"
                        : $"a recorder saw {input.ToolCalls.Count} call(s) and none was an executed {ToolName}"),
            ]);
    }
}
