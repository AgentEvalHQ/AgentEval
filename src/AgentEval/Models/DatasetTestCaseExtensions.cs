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
    /// For tool-call accuracy evaluation, use the ToolUsage metrics which compare against ExpectedTools.
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
