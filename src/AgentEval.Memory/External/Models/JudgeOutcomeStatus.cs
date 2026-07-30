// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.External.Models;

/// <summary>Typed outcome returned by an external benchmark judge.</summary>
public enum JudgeOutcomeStatus
{
    /// <summary>The judge explicitly answered yes.</summary>
    Yes,

    /// <summary>The judge explicitly answered no.</summary>
    No,

    /// <summary>The provider returned no usable text.</summary>
    Empty,

    /// <summary>The response was malformed, ambiguous, filtered, or truncated.</summary>
    Invalid,

    /// <summary>The provider call failed.</summary>
    ProviderError
}

/// <summary>Controls behavior after a judge cannot produce a binary outcome.</summary>
public enum JudgeFailurePolicy
{
    /// <summary>Throw after the first inconclusive judge outcome.</summary>
    FailRun,

    /// <summary>Retry within the configured bound, then retain an inconclusive result.</summary>
    RetryThenInconclusive,

    /// <summary>
    /// Retry within the configured bound, then count the inconclusive result as incorrect
    /// for aggregate compatibility while preserving its typed status.
    /// </summary>
    RetryThenIncorrect
}

/// <summary>Controls diagnostic judge evidence retained in benchmark results.</summary>
public enum JudgeEvidenceMode
{
    None,
    Outcome,
    Explanation,
    Raw
}

/// <summary>Describes whether the agent itself completed before judging.</summary>
public enum QuestionExecutionStatus
{
    Completed,
    AgentError
}
