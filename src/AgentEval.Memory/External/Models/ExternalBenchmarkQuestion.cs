// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.External.Models;

/// <summary>
/// A question from an external benchmark, passed to the judge for scoring.
/// </summary>
public class ExternalBenchmarkQuestion
{
    /// <summary>Unique question identifier from the dataset.</summary>
    public required string QuestionId { get; init; }

    /// <summary>Question type/category (e.g., "single-session-user", "temporal-reasoning").</summary>
    public required string QuestionType { get; init; }

    /// <summary>The question text.</summary>
    public required string Question { get; init; }

    /// <summary>The gold/expected answer.</summary>
    public required string GoldAnswer { get; init; }

    /// <summary>Optional date context for temporal queries.</summary>
    public string? QuestionDate { get; init; }

    /// <summary>Whether this is an abstention question (agent should say "I don't know").</summary>
    public bool IsAbstention { get; init; }
}
