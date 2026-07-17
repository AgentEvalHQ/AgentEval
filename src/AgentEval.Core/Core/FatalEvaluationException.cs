// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>
/// Marker base for an exception representing a STRUCTURAL condition that will recur on every remaining
/// item in a scan/benchmark/eval run — not a per-item transient failure. The generic exception handlers
/// in <c>RedTeamRunner.ExecuteProbeAsync</c>, <c>AgentScenarioEval.EvaluateAsync</c>, and
/// <c>MAFEvaluationHarness.RunEvaluationAsync</c> otherwise swallow any exception into a per-probe/
/// per-scenario/per-test-case error result so a single transient fault doesn't abort an entire run — but
/// they special-case this base type (the same way those same catch chains already special-case
/// <see cref="OperationCanceledException"/>) and let it propagate instead, so a condition that can never
/// resolve itself mid-run (e.g. an exhausted spend cap) stops the whole run once, rather than re-reporting
/// the identical root cause as a separate "execution error" for every remaining item.
/// </summary>
public abstract class FatalEvaluationException : Exception
{
    /// <summary>Creates a new <see cref="FatalEvaluationException"/> with the given message.</summary>
    protected FatalEvaluationException(string message) : base(message)
    {
    }

    /// <summary>Creates a new <see cref="FatalEvaluationException"/> with the given message and inner exception.</summary>
    protected FatalEvaluationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
