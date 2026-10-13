// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Evals;

/// <summary>Content-free, pre-execution declaration of an atomic LLM evaluation instrument.</summary>
/// <remarks>Implementation and transport configuration must also be bound by the producer. This snapshot
/// describes the leaf; it does not by itself prove that a whole suite is completely identified.</remarks>
public sealed record AtomicLlmDefinition
{
    /// <summary>Digest of criteria, prompt material and judge input framing.</summary>
    public required string RubricDigest { get; init; }
    /// <summary>Whether the evaluator explicitly identifies its prompt material.</summary>
    public required bool PromptIdentified { get; init; }
    /// <summary>Configured pass threshold, on the unit scale.</summary>
    public required double PassThreshold { get; init; }
    /// <summary>Configured severity override, or null for the default/rubric policy.</summary>
    public string? FailureSeverity { get; init; }
    /// <summary>Configured judge model, when known.</summary>
    public string? JudgeModel { get; init; }
    /// <summary>Configured judge provider, when known.</summary>
    public string? JudgeProvider { get; init; }
    /// <summary>Tool data included in the judge input.</summary>
    public required JudgeToolData ToolData { get; init; }
    /// <summary>Digest of the bound rubric's score scale, review boundary and severity policy.</summary>
    public required string ScorePolicyDigest { get; init; }
    /// <summary>Evaluator implementation type, for producer-side assembly identity binding.</summary>
    public required Type EvaluatorType { get; init; }
}
