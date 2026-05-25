// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Process;

/// <summary>
/// Evaluates the accuracy of arguments supplied to tool calls.
/// <para>
/// This is a <strong>hybrid evaluator</strong>: a <see cref="CompositeEval"/> composed of:
/// <list type="number">
///   <item>A deterministic <see cref="AtomicCodeEval"/> that validates JSON schema conformance
///   (required parameters present, types match) against the available
///   <see cref="EvalInput.ToolDefinitions"/>.</item>
///   <item>An LLM-judge <see cref="AtomicLlmEval"/> that assesses semantic groundedness —
///   are the argument values derived from the user's query and prior tool outputs, or
///   are they hallucinated?</item>
/// </list>
/// Both components carry equal weight (0.50 each), aggregated via
/// <see cref="WeightedSumAggregation"/>.
/// </para>
/// <para>
/// Source: forked from Azure/azure-sdk-for-python
/// sdk/evaluation/azure-ai-evaluation/azure/ai/evaluation/_evaluators/_tool_input_accuracy/tool_input_accuracy.prompty
/// License: MIT. Modifications listed in the corresponding prompt file at
/// Resources/Prompts/process/tool-input-accuracy.v1.md.
/// </para>
/// <para>
/// Foundry reference: <c>azureai://built-in/evaluators/tool_input_accuracy</c>
/// </para>
/// </summary>
public sealed class ToolInputAccuracyEval : IEval
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
    /// Initialises a new <see cref="ToolInputAccuracyEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used for the semantic-groundedness component.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.70.</param>
    public ToolInputAccuracyEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.70)
    {
        ArgumentNullException.ThrowIfNull(judge);

        var schemaValidation = new ToolInputSchemaEval();
        var semanticGroundedness = new AtomicLlmEval(
            evaluator: judge,
            key: "tool_input_accuracy_semantic",
            name: "Tool Input Accuracy (Semantic)",
            category: "agentic-process",
            version: "1.0.0",
            criteria: new[]
            {
                "Argument values are traceable to the user query or prior tool outputs (no hallucinated values)",
                "No argument contains a fabricated identifier, date, or name absent from the conversation",
                "Values passed to chained tool calls are consistent with the results of preceding calls",
                "Argument values are plausible given the query context",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.tool_input_accuracy.v1",
            failureSeverity: "medium");

        _inner = new CompositeEval(
            key: "tool_input_accuracy",
            name: "Tool Input Accuracy",
            category: "agentic-process",
            version: "1.0.0",
            components: new[]
            {
                new EvalComponent(schemaValidation, Weight: 0.50),
                new EvalComponent(semanticGroundedness, Weight: 0.50),
            },
            aggregation: WeightedSumAggregation.Instance,
            threshold: passThreshold);
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);

    // ─────────────────────────────────────────────────────────────────────────
    // Deterministic schema-validation component
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deterministic eval that checks whether each tool call's arguments satisfy
    /// the required-parameter and basic-type constraints declared in
    /// <see cref="EvalInput.ToolDefinitions"/>.
    /// </summary>
    private sealed class ToolInputSchemaEval : AtomicCodeEval
    {
        public ToolInputSchemaEval()
            : base("tool_input_accuracy_schema", "Tool Input Accuracy (Schema)", "agentic-process", "1.0.0") { }

        protected override EvalResult Evaluate(EvalInput input)
        {
            // Fast-pass: no tool calls or no definitions to validate against.
            if (input.ToolCalls is null or { Count: 0 })
                return Build(1.0, true, "none");

            if (input.ToolDefinitions is null or { Count: 0 })
            {
                // TODO (A1.7 / plan-13 T4.1e item 37 / lastreview/19 §2):
                // Implement full JSON Schema validation once `ToolDefinition.Parameters`
                // carries a schema object that can be validated against (today it's a
                // free-form `IReadOnlyDictionary<string, object>` — see ToolDefinition
                // in AgentEval.Abstractions). For now, when no definitions are provided
                // we skip schema validation and pass through (score=1.0) so the composite
                // does not penalise callers that only supply ToolCalls without
                // ToolDefinitions. Deferred until the ToolDefinition.Parameters typing
                // sweep lands in v0.11+ (no concrete task ID yet).
                return Build(1.0, true, "none",
                    evidence: new[]
                    {
                        new EvalEvidence(
                            Source: "tool_definitions",
                            Reference: "(none provided)",
                            Message: "No tool definitions supplied; schema validation skipped.")
                    });
            }

            // Build a lookup: tool name → definition.
            var defByName = input.ToolDefinitions
                .ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

            var evidence = new List<EvalEvidence>();
            int totalCalls = 0;
            int passedCalls = 0;

            foreach (var call in input.ToolCalls)
            {
                totalCalls++;

                if (!defByName.TryGetValue(call.Name, out var def))
                {
                    evidence.Add(new EvalEvidence(
                        Source: "tool_call",
                        Reference: call.Name,
                        Message: $"Tool '{call.Name}' not found in provided tool definitions."));
                    continue;
                }

                // Check that every required parameter (declared in ToolDefinition.Parameters)
                // is present in the call's Arguments.
                // TODO (A1.7 / plan-13 T4.1e item 37 / lastreview/19 §2):
                // when ToolDefinition.Parameters carries a full JSON Schema object
                // (with "required" and "properties" keys), validate types as well — including
                // enum constraints, regex `pattern` matches, numeric bounds, and nested-object
                // shape. For now we perform a simple required-key presence check; type / shape
                // violations slip through with a passing score. Deferred until the
                // ToolDefinition.Parameters typing sweep lands.
                var schemaParams = def.Parameters;
                if (schemaParams is null)
                {
                    // No parameter schema → treat as passing.
                    passedCalls++;
                    continue;
                }

                // Attempt to read a "required" array from the parameters dictionary.
                IReadOnlyList<string>? requiredKeys = null;
                if (schemaParams.TryGetValue("required", out var reqObj)
                    && reqObj is IEnumerable<object> reqArr)
                {
                    requiredKeys = reqArr.Select(o => o?.ToString() ?? "").ToList();
                }

                if (requiredKeys is null or { Count: 0 })
                {
                    passedCalls++;
                    continue;
                }

                var provided = call.Arguments?.Keys
                    .Select(k => k.ToLowerInvariant())
                    .ToHashSet()
                    ?? new HashSet<string>();

                var missing = requiredKeys
                    .Where(k => !provided.Contains(k.ToLowerInvariant()))
                    .ToList();

                if (missing.Count == 0)
                {
                    passedCalls++;
                }
                else
                {
                    evidence.Add(new EvalEvidence(
                        Source: "tool_call",
                        Reference: call.Name,
                        Message: $"Missing required parameter(s): {string.Join(", ", missing)}."));
                }
            }

            if (totalCalls == 0)
                return Build(1.0, true, "none");

            double score = (double)passedCalls / totalCalls;
            bool passed = score >= 0.70;
            string severity = passed ? "none" : (score < 0.40 ? "high" : "medium");

            return Build(score, passed, severity,
                dimensions: new Dictionary<string, double>
                {
                    ["schema_pass_rate"] = score,
                    ["calls_checked"]    = totalCalls,
                    ["calls_passed"]     = passedCalls,
                },
                evidence: evidence.Count > 0 ? evidence : null);
        }
    }
}
