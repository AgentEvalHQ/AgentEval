// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Process;
using Xunit;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// ADR-030 Slice 0.3 (defect D-c). <c>ToolInputAccuracyEval</c>'s deterministic schema leaf returned
/// three <b>perfect</b> scores on absent input — no tool calls, no tool definitions, zero calls
/// checked — and line 129 shipped evidence reading "schema validation skipped" beside a 1.0. Supply
/// no <c>ToolDefinitions</c> and the leaf reported perfect, forever, lifting the composite by 0.5
/// of its weight. Absent input is <c>label:"skipped"</c>, and version 1.0.0 → 2.0.0.
/// <para>
/// 2.1.0 (#203) checks the case before the answer: no tool definitions is <c>"inapplicable"</c> (the case
/// cannot test schema validity; the composite is the judge alone), definitions without tool calls stays
/// <c>"skipped"</c> — and because the schema leaf is required, that composite can no longer pass.
/// </para>
/// </summary>
public class ToolInputAccuracySkipTests
{
    private const string SchemaLeafKey = "tool_input_accuracy_schema";

    private static EvalResult SchemaLeaf(EvalResult composite) =>
        Assert.Single(composite.Details.SubResults!, s => s.Metric.Key == SchemaLeafKey);

    private static IReadOnlyList<ToolCall> OneCall() => new[]
    {
        new ToolCall("search_flights", new Dictionary<string, object> { ["origin"] = "NYC" }, null),
    };

    [Fact]
    public async Task NoToolDefinitions_DoesNotScorePerfect()
    {
        // The §8 acceptance test. Pre-fix: schema leaf = 1.0 / "pass" with evidence saying
        // "schema validation skipped"; the composite with a 0.40 judge reads (1.0 + 0.4) / 2 = 0.70
        // and PASSES the 0.70 threshold on the strength of a check that did not run.
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(40));
        var input = new EvalInput(Query: "Find flights", Response: "Called search_flights.", ToolCalls: OneCall());

        var result = await eval.EvaluateAsync(input);

        var schema = SchemaLeaf(result);
        Assert.Equal("inapplicable", schema.Score.Label);
        Assert.False(schema.Score.Passed);
        Assert.NotEqual(1.0, schema.Score.Value);
        Assert.Equal(AgentEval.Evals.Meta.MeasurementState.NotApplicable, schema.Score.Measurement);
        Assert.Contains(schema.Details.Recommendations!, r => r.Contains("tool definitions", StringComparison.OrdinalIgnoreCase));

        // The composite is now the judge alone: 0.40, below the 0.70 threshold.
        Assert.Equal(0.40, result.Score.Value, precision: 10);
        Assert.False(result.Score.Passed);
    }

    [Fact]
    public async Task NoToolDefinitions_AGoodJudgeScore_PassesOnTheJudgeAlone()
    {
        // The documented intent for callers that only supply ToolCalls: neither penalised nor flattered. An
        // inapplicable leaf never blocks the composite, so a good judge score is a pass.
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "Find flights", Response: "Called search_flights.", ToolCalls: OneCall());

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
    }

    [Fact]
    public async Task DefinitionsButNoToolCalls_SchemaLeafIsSkipped_AndTheCompositeCannotPass()
    {
        // The case could test schema validity (it declares the tool) and the answer called nothing: the
        // required schema leaf did not run, so the judge's 100 alone is not the composite's pass (#203).
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(100));
        var definitions = new[] { new ToolDefinition("search_flights", "Search", new Dictionary<string, object>()) };
        var input = new EvalInput(Query: "Find flights", Response: "I did not call any tool.", ToolDefinitions: definitions);

        var result = await eval.EvaluateAsync(input);

        var schema = SchemaLeaf(result);
        Assert.Equal("skipped", schema.Score.Label);
        Assert.False(schema.Score.Passed);
        Assert.Contains(schema.Details.Recommendations!, r => r.Contains("no tool calls", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("warn", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Contains(SchemaLeafKey, result.Details.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyToolDefinitions_IsAbsentInput_NotPerfect()
    {
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "q", Response: "r", ToolCalls: OneCall(), ToolDefinitions: Array.Empty<ToolDefinition>());

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("inapplicable", SchemaLeaf(result).Score.Label);
    }

    [Fact]
    public async Task ToolDefinitionsPresent_SchemaLeafStillMeasures()
    {
        // Guard: the fix must not widen. With definitions the leaf still validates and can fail.
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(100));
        var definitions = new[]
        {
            new ToolDefinition("search_flights", "Search", new Dictionary<string, object>
            {
                ["required"] = new object[] { "origin", "destination" },
            }),
        };
        var input = new EvalInput(Query: "q", Response: "r", ToolCalls: OneCall(), ToolDefinitions: definitions);

        var result = await eval.EvaluateAsync(input);

        var schema = SchemaLeaf(result);
        Assert.Equal("fail", schema.Score.Label);
        Assert.Equal(0.0, schema.Score.Value, precision: 10);
        Assert.Contains(schema.Details.Evidence!, e => e.Message.Contains("destination", StringComparison.Ordinal));
        Assert.Equal("atomic-code", schema.Provenance.Type);
    }

    [Fact]
    public async Task ToolDefinitionsPresent_AllRequiredSupplied_SchemaLeafPasses()
    {
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(100));
        var definitions = new[]
        {
            new ToolDefinition("search_flights", "Search", new Dictionary<string, object>
            {
                ["required"] = new object[] { "origin" },
            }),
        };
        var input = new EvalInput(Query: "q", Response: "r", ToolCalls: OneCall(), ToolDefinitions: definitions);

        var result = await eval.EvaluateAsync(input);

        var schema = SchemaLeaf(result);
        Assert.Equal("pass", schema.Score.Label);
        Assert.Equal(1.0, schema.Score.Value, precision: 10);
        Assert.True(result.Score.Passed);
    }

    [Fact]
    public void Version_IsBumped_BecauseTheVerdictChanged()
    {
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(100));

        Assert.Equal("2.1.0", eval.Version);
    }
}
