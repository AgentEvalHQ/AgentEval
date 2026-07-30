// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Signals that judging could not produce a binary outcome and the configured
/// <see cref="JudgeFailurePolicy.FailRun"/> policy requires the run to stop.
/// </summary>
public sealed class LongMemEvalJudgeException : Exception
{
    /// <summary>Question whose judgment failed.</summary>
    public string QuestionId { get; }

    /// <summary>Final typed judge outcome.</summary>
    public JudgeOutcomeStatus Status { get; }

    internal LongMemEvalJudgeException(string questionId, JudgeOutcomeStatus status)
        : base($"LongMemEval judge failed with status {status} for question '{questionId}'.")
    {
        QuestionId = questionId;
        Status = status;
    }
}
