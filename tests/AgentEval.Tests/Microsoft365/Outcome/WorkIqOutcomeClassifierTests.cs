// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using A2A;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Protocol;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Outcome;

/// <summary>
/// <see cref="WorkIqOutcomeClassifier"/>: the strict wire-name mapping cross-checked against the SDK's own
/// serialisation, the outcome table over every state × transport, the failure-driven cases (known reply before a
/// task id, timeout, local cancel, failure after a task id), the answer completeness rules and the telemetry rules.
/// </summary>
public sealed class WorkIqOutcomeClassifierTests
{
    private static readonly WorkIqTaskState?[] AllStates =
    [
        null,
        WorkIqTaskState.Unknown,
        WorkIqTaskState.Submitted,
        WorkIqTaskState.Working,
        WorkIqTaskState.Completed,
        WorkIqTaskState.Failed,
        WorkIqTaskState.Canceled,
        WorkIqTaskState.Rejected,
        WorkIqTaskState.InputRequired,
        WorkIqTaskState.AuthRequired,
    ];

    private static readonly TransportOutcome[] AllTransports = Enum.GetValues<TransportOutcome>();

    private static WorkIqFailure Failure(WorkIqFailureCategory category, OutcomeCertainty certainty, WorkIqOperation operation = WorkIqOperation.Send) =>
        new(category, "safe", certainty, operation);

    private static WorkIqAnswerSelection Selection(OutputCompleteness completeness, string? text = "answer") =>
        new(text, completeness, ImmutableArray<string>.Empty, ImmutableArray<SourceRef>.Empty);

    // ---------------------------------------------------------------------------------------------------------
    // ToTaskState
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("TASK_STATE_SUBMITTED", WorkIqTaskState.Submitted)]
    [InlineData("TASK_STATE_WORKING", WorkIqTaskState.Working)]
    [InlineData("TASK_STATE_COMPLETED", WorkIqTaskState.Completed)]
    [InlineData("TASK_STATE_FAILED", WorkIqTaskState.Failed)]
    [InlineData("TASK_STATE_CANCELED", WorkIqTaskState.Canceled)]
    [InlineData("TASK_STATE_REJECTED", WorkIqTaskState.Rejected)]
    [InlineData("TASK_STATE_INPUT_REQUIRED", WorkIqTaskState.InputRequired)]
    [InlineData("TASK_STATE_AUTH_REQUIRED", WorkIqTaskState.AuthRequired)]
    [InlineData("TASK_STATE_UNSPECIFIED", WorkIqTaskState.Unknown)]
    [InlineData(null, WorkIqTaskState.Unknown)]
    [InlineData("", WorkIqTaskState.Unknown)]
    [InlineData("completed", WorkIqTaskState.Unknown)]
    [InlineData("Completed", WorkIqTaskState.Unknown)]
    [InlineData("task_state_completed", WorkIqTaskState.Unknown)]
    [InlineData(" TASK_STATE_COMPLETED", WorkIqTaskState.Unknown)]
    [InlineData("TASK_STATE_DONE", WorkIqTaskState.Unknown)]
    public void ToTaskState_RawString_MapsOnlyExactWireNames(string? raw, WorkIqTaskState expected) =>
        Assert.Equal(expected, WorkIqOutcomeClassifier.ToTaskState(raw));

    [Fact]
    public void ToTaskState_SdkEnum_AgreesWithTheSdkWireNames()
    {
        foreach (var sdk in Enum.GetValues<TaskState>())
        {
            // The SDK enum carries its own [JsonConverter] with the wire member names, so plain serialisation yields them.
            var wire = JsonSerializer.Serialize(sdk).Trim('"');
            Assert.StartsWith(WorkIqOutcomeClassifier.RawStatePrefix, wire, StringComparison.Ordinal);

            var fromEnum = WorkIqOutcomeClassifier.ToTaskState(sdk);
            var fromWire = WorkIqOutcomeClassifier.ToTaskState(wire);
            Assert.Equal(fromEnum, fromWire);

            if (sdk == TaskState.Unspecified)
            {
                Assert.Equal(WorkIqTaskState.Unknown, fromEnum);
            }
            else
            {
                Assert.Equal(Enum.Parse<WorkIqTaskState>(sdk.ToString()), fromEnum);
                Assert.Equal(wire, WorkIqOutcomeClassifier.ToRawState(fromEnum));
            }
        }
    }

    [Fact]
    public void ToRawState_EveryKnownState_RoundTrips()
    {
        foreach (var state in Enum.GetValues<WorkIqTaskState>())
        {
            var raw = WorkIqOutcomeClassifier.ToRawState(state);
            if (state == WorkIqTaskState.Unknown)
            {
                Assert.Null(raw);
            }
            else
            {
                Assert.NotNull(raw);
                Assert.Equal(state, WorkIqOutcomeClassifier.ToTaskState(raw));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Classify: the state × transport table (no failure)
    // ---------------------------------------------------------------------------------------------------------

    public static IEnumerable<object?[]> StateTransportTable()
    {
        foreach (var state in AllStates)
        {
            foreach (var transport in AllTransports)
            {
                foreach (var taskIdKnown in new[] { false, true })
                {
                    yield return [state, transport, taskIdKnown];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(StateTransportTable))]
    public void Classify_EveryStateAndTransportWithoutFailure_FollowsTheTable(WorkIqTaskState? state, TransportOutcome transport, bool taskIdKnown)
    {
        var (outcome, certainty) = WorkIqOutcomeClassifier.Classify(state, hasDirectMessage: false, transport, failure: null, taskIdKnown);

        var (expectedOutcome, expectedCertainty) = state switch
        {
            WorkIqTaskState.Completed => (WorkIqTurnOutcome.Completed, OutcomeCertainty.Known),
            WorkIqTaskState.Failed => (WorkIqTurnOutcome.Failed, OutcomeCertainty.Known),
            WorkIqTaskState.Canceled => (WorkIqTurnOutcome.Canceled, OutcomeCertainty.Known),
            WorkIqTaskState.Rejected => (WorkIqTurnOutcome.Rejected, OutcomeCertainty.Known),
            WorkIqTaskState.InputRequired => (WorkIqTurnOutcome.InputRequired, OutcomeCertainty.Known),
            WorkIqTaskState.AuthRequired => (WorkIqTurnOutcome.AuthRequired, OutcomeCertainty.Known),
            _ => (WorkIqTurnOutcome.Indeterminate, OutcomeCertainty.Indeterminate),
        };
        Assert.Equal(expectedOutcome, outcome);
        Assert.Equal(expectedCertainty, certainty);
    }

    [Theory]
    [MemberData(nameof(StateTransportTable))]
    public void Classify_EveryStateAndTransportWithADirectMessage_NonTerminalBecomesCompleted(WorkIqTaskState? state, TransportOutcome transport, bool taskIdKnown)
    {
        var (outcome, certainty) = WorkIqOutcomeClassifier.Classify(state, hasDirectMessage: true, transport, failure: null, taskIdKnown);

        var expected = state switch
        {
            WorkIqTaskState.Failed => WorkIqTurnOutcome.Failed,
            WorkIqTaskState.Canceled => WorkIqTurnOutcome.Canceled,
            WorkIqTaskState.Rejected => WorkIqTurnOutcome.Rejected,
            WorkIqTaskState.InputRequired => WorkIqTurnOutcome.InputRequired,
            WorkIqTaskState.AuthRequired => WorkIqTurnOutcome.AuthRequired,
            _ => WorkIqTurnOutcome.Completed,
        };
        Assert.Equal(expected, outcome);
        Assert.Equal(OutcomeCertainty.Known, certainty);
    }

    [Fact]
    public void Classify_Always_IndeterminateOutcomeMeansIndeterminateCertainty()
    {
        foreach (var state in AllStates)
        {
            foreach (var transport in AllTransports)
            {
                foreach (var failure in new WorkIqFailure?[] { null, Failure(WorkIqFailureCategory.Timeout, OutcomeCertainty.Indeterminate), Failure(WorkIqFailureCategory.Unauthorized, OutcomeCertainty.Known), Failure(WorkIqFailureCategory.Canceled, OutcomeCertainty.Indeterminate) })
                {
                    foreach (var taskIdKnown in new[] { false, true })
                    {
                        var (outcome, certainty) = WorkIqOutcomeClassifier.Classify(state, false, transport, failure, taskIdKnown);
                        Assert.Equal(outcome == WorkIqTurnOutcome.Indeterminate, certainty == OutcomeCertainty.Indeterminate);
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Classify: failures
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(WorkIqFailureCategory.Unauthorized)]
    [InlineData(WorkIqFailureCategory.Forbidden)]
    [InlineData(WorkIqFailureCategory.NotFound)]
    [InlineData(WorkIqFailureCategory.RateLimited)]
    [InlineData(WorkIqFailureCategory.RemoteError)]
    [InlineData(WorkIqFailureCategory.ProtocolVersionMismatch)]
    [InlineData(WorkIqFailureCategory.UntrustedHost)]
    public void Classify_FailedTransportBeforeTaskIdWithKnownFailure_FailedAndKnown(WorkIqFailureCategory category)
    {
        var (outcome, certainty) = WorkIqOutcomeClassifier.Classify(null, false, TransportOutcome.Failed, Failure(category, OutcomeCertainty.Known), taskIdKnown: false);

        Assert.Equal(WorkIqTurnOutcome.Failed, outcome);
        Assert.Equal(OutcomeCertainty.Known, certainty);
    }

    [Theory]
    [InlineData(WorkIqFailureCategory.Timeout)]
    [InlineData(WorkIqFailureCategory.Transport)]
    [InlineData(WorkIqFailureCategory.Malformed)]
    [InlineData(WorkIqFailureCategory.Unknown)]
    public void Classify_FailedTransportBeforeTaskIdWithIndeterminateFailure_Indeterminate(WorkIqFailureCategory category)
    {
        var (outcome, certainty) = WorkIqOutcomeClassifier.Classify(null, false, TransportOutcome.Failed, Failure(category, OutcomeCertainty.Indeterminate), taskIdKnown: false);

        Assert.Equal(WorkIqTurnOutcome.Indeterminate, outcome);
        Assert.Equal(OutcomeCertainty.Indeterminate, certainty);
    }

    [Fact]
    public void Classify_FailedTransportWithoutAFailureObject_Indeterminate()
    {
        var (outcome, certainty) = WorkIqOutcomeClassifier.Classify(null, false, TransportOutcome.Failed, null, taskIdKnown: false);

        Assert.Equal(WorkIqTurnOutcome.Indeterminate, outcome);
        Assert.Equal(OutcomeCertainty.Indeterminate, certainty);
    }

    [Theory]
    [InlineData(WorkIqTaskState.Submitted)]
    [InlineData(WorkIqTaskState.Working)]
    [InlineData(WorkIqTaskState.Unknown)]
    [InlineData(null)]
    public void Classify_FailedTransportAfterTaskIdWithNonTerminalState_Indeterminate(WorkIqTaskState? state)
    {
        var (outcome, certainty) = WorkIqOutcomeClassifier.Classify(state, false, TransportOutcome.Failed, Failure(WorkIqFailureCategory.NotFound, OutcomeCertainty.Known, WorkIqOperation.GetTask), taskIdKnown: true);

        Assert.Equal(WorkIqTurnOutcome.Indeterminate, outcome);
        Assert.Equal(OutcomeCertainty.Indeterminate, certainty);
    }

    [Theory]
    [InlineData(TransportOutcome.Interrupted)]
    [InlineData(TransportOutcome.Recovered)]
    [InlineData(TransportOutcome.Completed)]
    public void Classify_NonFailedTransportWithKnownFailureAndNoState_Indeterminate(TransportOutcome transport)
    {
        var (outcome, _) = WorkIqOutcomeClassifier.Classify(null, false, transport, Failure(WorkIqFailureCategory.Unauthorized, OutcomeCertainty.Known), taskIdKnown: false);

        Assert.Equal(WorkIqTurnOutcome.Indeterminate, outcome);
    }

    [Theory]
    [InlineData(TransportOutcome.Failed, OutcomeCertainty.Known, false)]
    [InlineData(TransportOutcome.Failed, OutcomeCertainty.Indeterminate, false)]
    [InlineData(TransportOutcome.Interrupted, OutcomeCertainty.Indeterminate, true)]
    [InlineData(TransportOutcome.Interrupted, OutcomeCertainty.Indeterminate, false)]
    public void Classify_LocalCancellation_NeverCanceled(TransportOutcome transport, OutcomeCertainty certainty, bool taskIdKnown)
    {
        foreach (var state in new WorkIqTaskState?[] { null, WorkIqTaskState.Submitted, WorkIqTaskState.Working })
        {
            var (outcome, outcomeCertainty) = WorkIqOutcomeClassifier.Classify(state, false, transport, Failure(WorkIqFailureCategory.Canceled, certainty), taskIdKnown);

            Assert.Equal(WorkIqTurnOutcome.Indeterminate, outcome);
            Assert.Equal(OutcomeCertainty.Indeterminate, outcomeCertainty);
        }
    }

    [Fact]
    public void Classify_ServiceConfirmedCancel_CanceledAndKnownEvenWithALocalFailure()
    {
        var (outcome, certainty) = WorkIqOutcomeClassifier.Classify(WorkIqTaskState.Canceled, false, TransportOutcome.Recovered, Failure(WorkIqFailureCategory.Canceled, OutcomeCertainty.Indeterminate), taskIdKnown: true);

        Assert.Equal(WorkIqTurnOutcome.Canceled, outcome);
        Assert.Equal(OutcomeCertainty.Known, certainty);
    }

    [Theory]
    [InlineData(WorkIqTaskState.Completed, TransportOutcome.Recovered, WorkIqTurnOutcome.Completed)]
    [InlineData(WorkIqTaskState.Completed, TransportOutcome.Interrupted, WorkIqTurnOutcome.Completed)]
    [InlineData(WorkIqTaskState.Failed, TransportOutcome.Interrupted, WorkIqTurnOutcome.Failed)]
    [InlineData(WorkIqTaskState.Rejected, TransportOutcome.Failed, WorkIqTurnOutcome.Rejected)]
    [InlineData(WorkIqTaskState.InputRequired, TransportOutcome.Recovered, WorkIqTurnOutcome.InputRequired)]
    [InlineData(WorkIqTaskState.AuthRequired, TransportOutcome.Failed, WorkIqTurnOutcome.AuthRequired)]
    public void Classify_TerminalOrSuspendedStateWithAFailedTransport_StateWins(WorkIqTaskState state, TransportOutcome transport, WorkIqTurnOutcome expected)
    {
        var (outcome, certainty) = WorkIqOutcomeClassifier.Classify(state, false, transport, Failure(WorkIqFailureCategory.Transport, OutcomeCertainty.Indeterminate), taskIdKnown: true);

        Assert.Equal(expected, outcome);
        Assert.Equal(OutcomeCertainty.Known, certainty);
    }

    [Fact]
    public void Classify_UnknownState_NeverCompleted()
    {
        foreach (var transport in AllTransports)
        {
            var (outcome, _) = WorkIqOutcomeClassifier.Classify(WorkIqTaskState.Unknown, false, transport, null, taskIdKnown: true);
            Assert.Equal(WorkIqTurnOutcome.Indeterminate, outcome);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // AnswerCompletenessFor
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(OutputCompleteness.Complete)]
    [InlineData(OutputCompleteness.Partial)]
    [InlineData(OutputCompleteness.Empty)]
    [InlineData(OutputCompleteness.Ambiguous)]
    [InlineData(OutputCompleteness.None)]
    public void AnswerCompletenessFor_Completed_UsesTheSelection(OutputCompleteness completeness) =>
        Assert.Equal(completeness, WorkIqOutcomeClassifier.AnswerCompletenessFor(WorkIqTurnOutcome.Completed, Selection(completeness)));

    [Fact]
    public void AnswerCompletenessFor_CompletedWithoutSelection_None() =>
        Assert.Equal(OutputCompleteness.None, WorkIqOutcomeClassifier.AnswerCompletenessFor(WorkIqTurnOutcome.Completed, null));

    [Theory]
    [InlineData(OutputCompleteness.Complete, OutputCompleteness.Partial)]
    [InlineData(OutputCompleteness.Partial, OutputCompleteness.Partial)]
    [InlineData(OutputCompleteness.Ambiguous, OutputCompleteness.Partial)]
    [InlineData(OutputCompleteness.Empty, OutputCompleteness.None)]
    [InlineData(OutputCompleteness.None, OutputCompleteness.None)]
    public void AnswerCompletenessFor_NotCompleted_TextIsPartialNeverComplete(OutputCompleteness selected, OutputCompleteness expected)
    {
        foreach (var outcome in Enum.GetValues<WorkIqTurnOutcome>().Where(o => o != WorkIqTurnOutcome.Completed))
        {
            Assert.Equal(expected, WorkIqOutcomeClassifier.AnswerCompletenessFor(outcome, Selection(selected)));
            Assert.Equal(OutputCompleteness.None, WorkIqOutcomeClassifier.AnswerCompletenessFor(outcome, null));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // TelemetryFor
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(DiagnosticProfile.PublicAnswerOnly, false, TelemetryCompleteness.NotRequested)]
    [InlineData(DiagnosticProfile.PublicAnswerOnly, true, TelemetryCompleteness.NotRequested)]
    [InlineData(DiagnosticProfile.Native118Diagnostics, false, TelemetryCompleteness.Unknown)]
    [InlineData(DiagnosticProfile.Native118Diagnostics, true, TelemetryCompleteness.Partial)]
    public void TelemetryFor_Profiles_NeverComplete(DiagnosticProfile profile, bool observed, TelemetryCompleteness expected)
    {
        var result = WorkIqOutcomeClassifier.TelemetryFor(profile, observed);

        Assert.Equal(expected, result);
        Assert.NotEqual(TelemetryCompleteness.Complete, result);
    }
}
