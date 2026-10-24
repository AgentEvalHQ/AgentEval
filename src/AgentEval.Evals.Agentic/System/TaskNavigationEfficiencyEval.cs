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
/// An absent list is not an empty one. With no <see cref="EvalInput.ExpectedActions"/> the deterministic component is
/// not applicable (no path was declared to compare against) and the composite scores on path quality alone. With no
/// <see cref="EvalInput.ToolCalls"/> (nothing recorded what the agent did) neither component measures: the edit distance
/// is skipped when a path was declared, and the path-quality judge, which grades the calls, is not called. The composite
/// then reports no verdict. A recorded empty list is compared and judged like any other sequence. The judge is sent the
/// expected actions, when declared, as the reference its ordering criterion names.
/// </para>
/// <para>
/// Source (LLM component): new AgentEval evaluator. The Azure AI Evaluation SDK's
/// task-navigation-efficiency evaluator is code-only (it matches agent steps against ground-truth
/// steps) and has no prompt, so there is no upstream prompt equivalent.
/// The deterministic component uses standard Levenshtein distance.
/// </para>
/// </summary>
public sealed class TaskNavigationEfficiencyEval : IEvalDefinitionWrapper
{
    private readonly CompositeEval _inner;

    /// <inheritdoc/>
    public IEval DefinitionInner => _inner;

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
                Weight: 0.50) { OnFailure = ComponentFailureEffect.Warn },

            new EvalComponent(
                Eval: new AtomicLlmEval(
                    evaluator: judge,
                    key: "path_quality",
                    name: "Path Quality",
                    category: "system-outcome",
                    // 1.2.0: shown the expected actions when declared; not measured without tool-call data (it used
                    // to grade a path it was never shown).
                    version: "1.2.0",
                    criteria: new[]
                    {
                        "The agent did not take unnecessary detours or redundant steps to complete the task",
                        "The agent did not backtrack or repeat failed actions without a corrective strategy",
                        "The ordering of actions was logical and efficient given the task requirements",
                    },
                    passThreshold: passThreshold,
                    judgeModel: judgeModel,
                    promptId: "agenteval.task_navigation_efficiency.v1",
                    failureSeverity: "medium")
                {
                    JudgeSeesData = JudgeInputData.ToolCalls | JudgeInputData.ExpectedActions,
                    JudgeNeedsData = JudgeInputData.ToolCalls,
                },
                Weight: 0.50) { OnFailure = ComponentFailureEffect.Warn },
        };

        // 1.3.0 (unreleased, one bump for both): path_edit_distance's absent-versus-empty rules (T08) and path_quality's
        // need for the tool calls, so with none captured the composite reports no verdict.
        _inner = new CompositeEval(
            key: "task_navigation_efficiency",
            name: "Task Navigation Efficiency",
            category: "system-outcome",
            version: "1.3.0",
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
internal sealed class ActionSequenceEditDistanceEval : AtomicCodeEval, IEvalConfigurationSource
{
    private readonly double _passThreshold;

    /// <inheritdoc/>
    public global::System.Text.Json.Nodes.JsonObject CaptureConfiguration() => new() { ["passThreshold"] = _passThreshold };

    internal ActionSequenceEditDistanceEval(double passThreshold)
        : base("path_edit_distance", "Path Edit Distance", "system-outcome", "1.1.0")
    {
        _passThreshold = passThreshold;
    }

    /// <inheritdoc/>
    protected override EvalResult Evaluate(EvalInput input)
    {
        // A null list is an absence and an empty one an observation (T08). Both used to collapse to an empty list, so
        // a run with nothing recorded and no declared path read a "trivially perfect" 1.0 PASS, nothing recorded
        // against a declared path read a measured 0, and recorded calls against no declared path read 0 too.
        // No declared path: nothing to compare against, whatever was recorded. Checked first, since recording the
        // calls would not help a case that declares no path.
        if (input.ExpectedActions is null)
            return NotApplicable("No expected action sequence was declared (EvalInput.ExpectedActions), so the path " +
                                 "edit distance has nothing to compare against; path quality is judged without it.");

        // A declared path and no tool-call data: nothing recorded what the agent did, so the distance was not measured.
        if (input.ToolCalls is null)
            return EvalResult.Skipped(this,
                "No tool-call data was captured (EvalInput.ToolCalls is null), so the path edit distance was not " +
                "measured. Attach the run's trace or record its tool calls.");

        // actual = tool call names, expected = action descriptions, both recorded (possibly empty).
        var actual = input.ToolCalls.Select(tc => tc.Name).ToList();
        var expected = input.ExpectedActions.Select(ea => ea.Description).ToList();

        if (actual.Count == 0 && expected.Count == 0)
        {
            // Both recorded and empty: nothing was expected and nothing was done, a real match.
            return Build(
                value: 1.0,
                passed: true,
                severity: "none",
                evidence: new[] { new EvalEvidence("deterministic", "edit_distance", "The recorded and the expected action sequences are both empty; they match (1.0).") });
        }

        // One side empty is an ordinary distance (every step missing or extra), with its dimensions recorded.
        var editDistance = LevenshteinDistance(actual, expected);
        var maxLen = Math.Max(actual.Count, expected.Count);   // > 0: both empty returned above
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
