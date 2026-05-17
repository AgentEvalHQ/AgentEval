// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.System;

/// <summary>
/// Evaluates the efficiency of the action path taken by an AI agent to complete a task.
/// <para>
/// Implemented as a <see cref="CompositeEval"/> with two equal-weight components:
/// </para>
/// <list type="bullet">
///   <item>
///     <term>path_edit_distance</term>
///     <description>
///       Deterministic component. Computes a normalised Levenshtein edit-distance between
///       the actual action sequence (<see cref="EvalInput.ToolCalls"/>) and the expected
///       optimal action sequence (<see cref="EvalInput.ExpectedActions"/>).
///       Score = 1 − (edit_distance / max(actual_len, expected_len)).
///     </description>
///   </item>
///   <item>
///     <term>path_quality</term>
///     <description>
///       LLM component. Judges the qualitative quality of the path — were redundant steps
///       taken? Did the agent backtrack unnecessarily? Was the ordering sensible?
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Input contract</b>: actions are read from <see cref="EvalInput.ToolCalls"/> (actual)
/// and <see cref="EvalInput.ExpectedActions"/> (expected). The
/// <see cref="EvalInput.ExpectedActions"/> list contains one entry per expected action step;
/// the <c>Description</c> field is used as the string label for edit-distance comparison.
/// If either list is absent, the deterministic component returns score=0 with a note in evidence.
/// </para>
/// <para>
/// Source (LLM component): new AgentEval evaluator; no direct Foundry equivalent.
/// The deterministic component uses standard Levenshtein distance.
/// </para>
/// </summary>
public sealed class TaskNavigationEfficiencyEval : IEval
{
    private readonly CompositeEval _inner;

    /// <inheritdoc/>
    public string Key => _inner.Key;

    /// <inheritdoc/>
    public string Name => _inner.Name;

    /// <inheritdoc/>
    public string Category => _inner.Category;

    /// <inheritdoc/>
    public string Version => _inner.Version;

    /// <summary>
    /// Initialises a new <see cref="TaskNavigationEfficiencyEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used for the path-quality sub-dimension.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the composite passes. Defaults to 0.70.</param>
    public TaskNavigationEfficiencyEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.70)
    {
        ArgumentNullException.ThrowIfNull(judge);

        var components = new[]
        {
            new EvalComponent(
                Eval: new ActionSequenceEditDistanceEval(passThreshold),
                Weight: 0.50),

            new EvalComponent(
                Eval: new AtomicLlmEval(
                    evaluator: judge,
                    key: "path_quality",
                    name: "Path Quality",
                    category: "system-outcome",
                    version: "1.0.0",
                    criteria: new[]
                    {
                        "The agent did not take unnecessary detours or redundant steps to complete the task",
                        "The agent did not backtrack or repeat failed actions without a corrective strategy",
                        "The ordering of actions was logical and efficient given the task requirements",
                    },
                    passThreshold: passThreshold,
                    judgeModel: judgeModel,
                    promptId: "agenteval.task_navigation_efficiency.v1",
                    failureSeverity: "medium"),
                Weight: 0.50),
        };

        _inner = new CompositeEval(
            key: "task_navigation_efficiency",
            name: "Task Navigation Efficiency",
            category: "system-outcome",
            version: "1.0.0",
            components: components,
            aggregation: WeightedSumAggregation.Instance,
            threshold: passThreshold);
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);
}

/// <summary>
/// Deterministic sub-component of <see cref="TaskNavigationEfficiencyEval"/>.
/// Computes a normalised Levenshtein edit-distance score between the actual tool-call
/// sequence and the expected action sequence.
/// </summary>
internal sealed class ActionSequenceEditDistanceEval : AtomicCodeEval
{
    private readonly double _passThreshold;

    internal ActionSequenceEditDistanceEval(double passThreshold)
        : base("path_edit_distance", "Path Edit Distance", "system-outcome", "1.0.0")
    {
        _passThreshold = passThreshold;
    }

    /// <inheritdoc/>
    protected override EvalResult Evaluate(EvalInput input)
    {
        // Derive action-name sequences from EvalInput primitives.
        // actual  = tool call names from EvalInput.ToolCalls
        // expected = descriptions from EvalInput.ExpectedActions
        var actual = input.ToolCalls?.Select(tc => tc.Name).ToList()
                     ?? new List<string>();
        var expected = input.ExpectedActions?.Select(ea => ea.Description).ToList()
                       ?? new List<string>();

        if (actual.Count == 0 && expected.Count == 0)
        {
            // Both empty — trivially perfect (nothing expected, nothing done).
            return Build(
                value: 1.0,
                passed: true,
                severity: "none",
                evidence: new[] { new EvalEvidence("deterministic", "edit_distance", "Both actual and expected action sequences are empty; score is trivially 1.0.") });
        }

        if (actual.Count == 0 || expected.Count == 0)
        {
            var msg = actual.Count == 0
                ? "EvalInput.ToolCalls is empty; cannot compute edit distance. Score = 0."
                : "EvalInput.ExpectedActions is empty; cannot compute edit distance. Score = 0.";
            return Build(
                value: 0.0,
                passed: false,
                severity: "medium",
                evidence: new[] { new EvalEvidence("deterministic", "edit_distance", msg) });
        }

        var editDistance = LevenshteinDistance(actual, expected);
        var maxLen = Math.Max(actual.Count, expected.Count);
        var normalised = 1.0 - (double)editDistance / maxLen;
        var passed = normalised >= _passThreshold;
        var severity = passed ? "none" : (normalised < 0.40 ? "high" : "medium");

        return Build(
            value: normalised,
            passed: passed,
            severity: severity,
            dimensions: new Dictionary<string, double>
            {
                ["edit_distance_raw"] = editDistance,
                ["actual_length"] = actual.Count,
                ["expected_length"] = expected.Count,
            },
            evidence: new[]
            {
                new EvalEvidence(
                    Source: "deterministic",
                    Reference: "edit_distance",
                    Message: $"Levenshtein distance {editDistance} over max({actual.Count},{expected.Count})={maxLen} steps → normalised score {normalised:F3}."),
            });
    }

    /// <summary>
    /// Standard Levenshtein edit distance between two string sequences.
    /// O(m×n) time, O(n) space (rolling row).
    /// </summary>
    private static int LevenshteinDistance(IList<string> source, IList<string> target)
    {
        int m = source.Count, n = target.Count;
        var prev = new int[n + 1];
        var curr = new int[n + 1];

        for (int j = 0; j <= n; j++) prev[j] = j;

        for (int i = 1; i <= m; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= n; j++)
            {
                var cost = string.Equals(source[i - 1], target[j - 1], StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(prev[j] + 1, curr[j - 1] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[n];
    }
}
