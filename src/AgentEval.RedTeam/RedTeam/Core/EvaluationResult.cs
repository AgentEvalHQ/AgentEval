// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// Result of evaluating an agent response against an attack probe.
/// This is the return type for <see cref="IProbeEvaluator"/> evaluation methods.
/// </summary>
/// <remarks>
/// <para>
/// Note: From a security perspective:
/// <list type="bullet">
/// <item><see cref="EvaluationOutcome.Succeeded"/> means the ATTACK succeeded (agent was compromised) - BAD</item>
/// <item><see cref="EvaluationOutcome.Resisted"/> means the agent resisted the attack - GOOD</item>
/// </list>
/// </para>
/// </remarks>
public readonly record struct EvaluationResult
{
    /// <summary>Identity of the complete configured graph, including unexecuted branches and requested settings.
    /// Actual model identities and effective calls remain in <see cref="JudgeInstruments"/>; this digest alone does not certify model equivalence.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? JudgeGraphDigest { get; init; }

    /// <summary>Configured judge instruments actually invoked. Composite evaluators retain each leaf separately.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProbeJudgeInstrument>? JudgeInstruments { get; init; }

    /// <summary>The outcome of the evaluation.</summary>
    public required EvaluationOutcome Outcome { get; init; }

    /// <summary>Human-readable explanation of the evaluation result.</summary>
    public required string Reason { get; init; }

    /// <summary>Confidence in the evaluation (0.0 - 1.0). The factory methods (Succeeded/Resisted/Inconclusive)
    /// default this to 1.0; the bare property default (object-initializer without it) is 0.0.</summary>
    public double Confidence { get; init; }

    /// <summary>Matched tokens, patterns, or evidence if applicable.</summary>
    public IReadOnlyList<string>? MatchedItems { get; init; }

    /// <summary>Additional metadata from the evaluation (e.g., LLM reasoning).</summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

    /// <summary>Full SHA-256 identity of the judge instrument, excluding probe and response content; null when unknown.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RubricDigest { get; init; }

    /// <summary>Observed source of the verdict when explicitly reported; null means unknown.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GradingProvenanceKind? GradedBy { get; init; }

    // === Convenience Properties ===

    /// <summary>Whether the attack succeeded (agent was compromised).</summary>
    public bool AttackSucceeded => Outcome == EvaluationOutcome.Succeeded;

    /// <summary>Whether the agent resisted the attack.</summary>
    public bool AttackResisted => Outcome == EvaluationOutcome.Resisted;

    /// <summary>Whether the evaluation was inconclusive.</summary>
    public bool IsInconclusive => Outcome == EvaluationOutcome.Inconclusive;

    // === Factory Methods ===

    /// <summary>
    /// Create a result indicating the attack SUCCEEDED (agent was compromised).
    /// </summary>
    /// <param name="reason">Explanation of why the attack succeeded.</param>
    /// <param name="matchedItems">Evidence that triggered the detection.</param>
    /// <param name="metadata">Additional evaluation metadata.</param>
    /// <param name="confidence">Confidence level (0.0-1.0, default 1.0).</param>
    public static EvaluationResult Succeeded(
        string reason,
        IReadOnlyList<string>? matchedItems = null,
        IReadOnlyDictionary<string, object>? metadata = null,
        double confidence = 1.0) => new()
    {
        Outcome = EvaluationOutcome.Succeeded,
        Reason = reason,
        MatchedItems = matchedItems,
        Metadata = metadata,
        Confidence = confidence
    };

    /// <summary>
    /// Create a result indicating the agent RESISTED the attack.
    /// </summary>
    /// <param name="reason">Explanation of why the agent resisted.</param>
    /// <param name="metadata">Additional evaluation metadata.</param>
    /// <param name="confidence">Confidence level (0.0-1.0, default 1.0).</param>
    public static EvaluationResult Resisted(
        string reason,
        IReadOnlyDictionary<string, object>? metadata = null,
        double confidence = 1.0) => new()
    {
        Outcome = EvaluationOutcome.Resisted,
        Reason = reason,
        Metadata = metadata,
        Confidence = confidence
    };

    /// <summary>
    /// Create a result indicating the evaluation was INCONCLUSIVE.
    /// </summary>
    /// <param name="reason">Explanation of why the result is inconclusive.</param>
    /// <param name="metadata">Additional evaluation metadata.</param>
    /// <param name="confidence">How confident we are that it's inconclusive.</param>
    public static EvaluationResult Inconclusive(
        string reason,
        IReadOnlyDictionary<string, object>? metadata = null,
        double confidence = 0.5) => new()
    {
        Outcome = EvaluationOutcome.Inconclusive,
        Reason = reason,
        Metadata = metadata,
        Confidence = confidence
    };
}

/// <summary>Content-free settings of one dispatched configured-judge request. A null model ID is unknown.</summary>
/// <param name="ModelId">Model ID reported by the provider, without inferring a value when absent.</param>
/// <param name="Temperature">Temperature actually sent; null means omitted.</param>
/// <param name="MaxOutputTokens">Output token limit actually sent.</param>
/// <param name="ResponseReceived">Whether the provider returned a response; this does not mean its verdict was valid.</param>
public sealed record ProbeJudgeCall(string? ModelId, float? Temperature, int? MaxOutputTokens, bool ResponseReceived);

/// <summary>One configured judge's selected prompt, execution identity and dispatched calls.</summary>
/// <param name="Prompt">Prompt selection; text is retained only by explicit capture.</param>
/// <param name="RubricDigest">Identity including the reader and observed effective model/settings, excluding case content.</param>
/// <param name="Calls">Dispatched requests, including unsuccessful attempts. Unknown model IDs remain null.</param>
public sealed record ProbeJudgeInstrument(AgentEval.Prompts.JudgePromptProvenance Prompt, string RubricDigest,
    IReadOnlyList<ProbeJudgeCall> Calls);
