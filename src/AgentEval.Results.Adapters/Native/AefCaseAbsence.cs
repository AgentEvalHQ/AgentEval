// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Native;

/// <summary>
/// Why a case or trial was not measured, when its host knows it and the harness result cannot say it. The eval writer
/// then records the case in <see cref="State"/> with <see cref="Reason"/>, with no score, no harness annotator and no
/// judge criteria: a typed absence ([RES-1], [RES-2]), never a pass and never a failure.
/// </summary>
/// <remarks>
/// Use it for an absence that no exception explains: for example, a turn that completed with no selected answer. The
/// reason is written to the run as it is, so it must be content-free: it names the cause, never the subject's or the
/// judge's text ([RUN-11]).
/// </remarks>
public sealed record AefCaseAbsence
{
    private const int MaxReasonLength = 1024;

    /// <summary>Creates an absence.</summary>
    /// <param name="state"><see cref="AefState.NotMeasured"/>, <see cref="AefState.Error"/> or <see cref="AefState.Skipped"/>.</param>
    /// <param name="reason">The content-free cause, at most 1,024 characters.</param>
    public AefCaseAbsence(AefState state, string reason)
    {
        if (state is not (AefState.NotMeasured or AefState.Error or AefState.Skipped))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state,
                "A case absence is not_measured, error or skipped: any other state is measured or not final.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > MaxReasonLength)
        {
            throw new ArgumentException($"A case absence's reason is at most {MaxReasonLength} characters.", nameof(reason));
        }

        State = state;
        Reason = reason;
    }

    /// <summary>The case's state: <c>not_measured</c>, <c>error</c> or <c>skipped</c>.</summary>
    public AefState State { get; }

    /// <summary>The content-free cause the run records as the case's <c>reason</c>.</summary>
    public string Reason { get; }
}
