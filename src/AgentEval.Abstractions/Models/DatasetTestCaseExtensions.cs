// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Core;

namespace AgentEval.Models;

/// <summary>
/// Extension methods for converting <see cref="DatasetTestCase"/> to execution models.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DatasetTestCase"/> is the persistence model (flexible, alias-tolerant, mutable).
/// <see cref="TestCase"/> is the execution model (strict, typed, immutable).
/// These extensions bridge the two without coupling them.
/// </para>
/// <para>See ADR-014 for the architectural rationale behind the two-model design.</para>
/// </remarks>
public static class DatasetTestCaseExtensions
{
    /// <summary>
    /// Converts a <see cref="DatasetTestCase"/> to a <see cref="TestCase"/> for evaluation.
    /// </summary>
    /// <param name="d">The dataset test case to convert.</param>
    /// <param name="groundTruthProjection">
    /// Optional custom projection for <see cref="GroundTruthToolCall"/>. 
    /// By default, structured ground truth is JSON-serialized into <see cref="TestCase.GroundTruth"/>.
    /// Pass a custom function to project differently (e.g., name-only: <c>gt => gt?.Name</c>).
    /// </param>
    /// <returns>A <see cref="TestCase"/> ready for evaluation.</returns>
    public static TestCase ToTestCase(
        this DatasetTestCase d,
        Func<GroundTruthToolCall?, string?>? groundTruthProjection = null)
    {
        ArgumentNullException.ThrowIfNull(d);

        // Fail fast with a clear, Input-focused message. Previously a null Input threw a bare
        // NullReferenceException at the Name slice below, and an empty Input produced an empty Name
        // that later failed TestCaseValidator with a Name-focused message hiding the real cause —
        // the empty Input (BUG-47).
        if (string.IsNullOrWhiteSpace(d.Input))
            throw new ArgumentException(
                "DatasetTestCase.Input must be non-empty to convert to a TestCase.", nameof(d));

        return new TestCase
        {
            Name = string.IsNullOrEmpty(d.Id) ? d.Input[..Math.Min(50, d.Input.Length)] : d.Id,
            Input = d.Input,
            ExpectedOutputContains = d.ExpectedOutput,
            EvaluationCriteria = d.EvaluationCriteria,
            ExpectedTools = d.ExpectedTools,
            GroundTruth = groundTruthProjection != null
                ? groundTruthProjection(d.GroundTruth)
                : (d.GroundTruth is null ? null : JsonSerializer.Serialize(d.GroundTruth)),
            Tags = d.Tags,
            PassingScore = d.PassingScore ?? EvaluationDefaults.DefaultPassingScore,
            Metadata = d.Metadata.Count > 0
                ? d.Metadata
                    .Where(kv => kv.Value is not null)
                    .ToDictionary(kv => kv.Key, kv => kv.Value!)
                : null,
        };
    }

    /// <summary>
    /// Creates an <see cref="EvaluationContext"/> from a <see cref="DatasetTestCase"/> and the agent's actual output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="EvaluationContext.GroundTruth"/> is set from <see cref="DatasetTestCase.ExpectedOutput"/> (text),
    /// not from <see cref="DatasetTestCase.GroundTruth"/> (structured <see cref="GroundTruthToolCall"/>).
    /// </para>
    /// <para>
    /// When only a structured <see cref="GroundTruthToolCall"/> is present and no <see cref="DatasetTestCase.ExpectedOutput"/>,
    /// <see cref="EvaluationContext.GroundTruth"/> will be <c>null</c>. This is by design:
    /// <see cref="EvaluationContext.GroundTruth"/> is consumed by the LLM judge as text.
    /// </para>
    /// <para>
    /// <b>Tool expectations are not carried here.</b> This method does not populate
    /// <see cref="EvaluationContext.ExpectedTools"/> or <see cref="EvaluationContext.ToolUsage"/>, so the
    /// tool-usage metrics have nothing to compare; set both yourself if you want them to run. Note also
    /// that <see cref="TestCase.ExpectedTools"/> (populated by <see cref="ToTestCase"/>) is <b>not enforced
    /// by the MAF evaluation harness</b> — see the remarks on that property (ADR-030 defect D-d, tracker AE-02).
    /// </para>
    /// </remarks>
    /// <param name="d">The dataset test case.</param>
    /// <param name="actualOutput">The agent's actual response text.</param>
    /// <param name="contextSeparator">Separator for joining context documents. Defaults to newline.</param>
    /// <returns>An <see cref="EvaluationContext"/> ready for metric evaluation.</returns>
    public static EvaluationContext ToEvaluationContext(
        this DatasetTestCase d,
        string? actualOutput,
        string contextSeparator = "\n")
    {
        return new EvaluationContext
        {
            Input = d.Input,
            Output = actualOutput ?? "",
            Context = d.Context is null ? null : string.Join(contextSeparator, d.Context),
            GroundTruth = d.ExpectedOutput,
        };
    }
}
