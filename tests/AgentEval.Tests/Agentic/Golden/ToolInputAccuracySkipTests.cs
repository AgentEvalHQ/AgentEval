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

        // Nothing captured the definitions (null): the schema check did not run — skipped, NOT inapplicable (B3).
        var schema = SchemaLeaf(result);
        Assert.Equal("skipped", schema.Score.Label);
        Assert.False(schema.Score.Passed);
        Assert.NotEqual(1.0, schema.Score.Value);
        Assert.Equal(AgentEval.Evals.Meta.MeasurementState.NotMeasured, schema.Score.CensusBucket());
        Assert.Contains(schema.Details.Recommendations!, r => r.Contains("tool definitions", StringComparison.OrdinalIgnoreCase));

        // The score is the judge's 0.40, below the 0.70 threshold: a measured fail stays a fail.
        Assert.Equal(0.40, result.Score.Value, precision: 10);
        Assert.False(result.Score.Passed);
    }

    // Every input shape, the schema leaf's state (B3): the case first (null = not captured, empty = declares none), then
    // the answer (no calls), then a real measurement.
    [Theory]
    [InlineData("null", false, "skipped")]
    [InlineData("null", true, "skipped")]
    [InlineData("empty", false, "inapplicable")]
    [InlineData("empty", true, "fail")]            // B6c-12: a call when the case declares no tools is to an undeclared tool
    [InlineData("declared", false, "skipped")]
    [InlineData("declared", true, "pass")]
    public async Task EveryInputShape_GivesTheStatedSchemaState(string definitions, bool withCalls, string expectedLabel)
    {
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(100));
        IReadOnlyList<ToolDefinition>? defs = definitions switch
        {
            "null" => null,
            "empty" => Array.Empty<ToolDefinition>(),
            _ => new[] { new ToolDefinition("search_flights", "Search", new Dictionary<string, object> { ["required"] = new object[] { "origin" } }) },
        };
        var input = new EvalInput(Query: "q", Response: "r", ToolCalls: withCalls ? OneCall() : null, ToolDefinitions: defs);

        var result = await eval.EvaluateAsync(input);

        Assert.Equal(expectedLabel, SchemaLeaf(result).Score.Label);
    }

    [Fact]
    public async Task NoToolDefinitionsCaptured_AGoodJudgeScore_CannotPassOnTheJudgeAlone()
    {
        // B3 (#203 review, round 2 H-B): no shipped pipeline fills ToolDefinitions, so "inapplicable" here let the tool
        // presets pass without the schema check everywhere. Not captured is not measured, and the leaf is required.
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "Find flights", Response: "Called search_flights.", ToolCalls: OneCall());

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("warn", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(AgentEval.Evals.Meta.MeasurementState.NotMeasured, result.Score.Measurement);
    }

    [Fact]
    public async Task ACaseThatDeclaresNoTools_AGoodJudgeScore_PassesOnTheJudgeAlone()
    {
        // An EMPTY list is the case declaring no tools: with no call made it cannot test schema validity, so the leaf is
        // inapplicable and the composite is the judge alone — callers are neither penalised nor flattered.
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "What is the capital of France?", Response: "Paris.", ToolCalls: [],
            ToolDefinitions: Array.Empty<ToolDefinition>());

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("inapplicable", SchemaLeaf(result).Score.Label);
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
    public async Task DefinitionsButNoToolCalls_InsideTheShippedPreset_TheWarnReachesThePreset()
    {
        // Found reviewing #203: ToolInputAccuracyEval's warn vanished inside the ToolCallAccuracy preset, which nests
        // it, so the preset passed on the judge alone. The preset must not pass either.
        var preset = AgentEval.Benchmarks.AgenticBenchmark.ToolCallAccuracy(new FixedScoreEvaluator(100));
        var definitions = new[] { new ToolDefinition("search_flights", "Search", new Dictionary<string, object>()) };
        var input = new EvalInput(Query: "Find flights", Response: "I did not call any tool.", ToolDefinitions: definitions);

        var result = await preset.EvaluateAsync(input);

        // Not merely "not a pass" (round 2 L-3): a warn that names the check whose pass was withheld.
        Assert.Equal("warn", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Contains("tool_call_accuracy", result.Details.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyToolDefinitions_IsAbsentInput_NotPerfect()
    {
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "q", Response: "r", ToolCalls: OneCall(), ToolDefinitions: Array.Empty<ToolDefinition>());

        var result = await eval.EvaluateAsync(input);

        // B6c-12: the case declares no tools, yet the agent called one — a call to an undeclared tool, measured as a
        // failure (it read inapplicable, and the judge alone could pass the evaluator).
        Assert.Equal("fail", SchemaLeaf(result).Score.Label);
        Assert.False(result.Score.Passed);
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

    // What each version of the schema check decides on a fixed set of inputs (round 2 L-3: the version test pinned a
    // constant, so it could not fail when the verdicts changed). Change a verdict and this fails until the version is
    // bumped and its row recorded; bump the version without a row and it fails too.
    private static readonly IReadOnlyDictionary<string, string> s_verdictsByVersion = new Dictionary<string, string>
    {
        ["2.5.0"] = "not-captured=skipped; none-declared-no-calls=inapplicable; none-declared-with-calls=fail; " +
                    "schema-ok=pass; required-missing=fail; undeclared-tool=fail; not-a-schema=skipped; declared-no-calls=skipped",
        // 2.6.0 (B10j): a pass on a minority of the calls warns.
        ["2.6.0"] = "not-captured=skipped; none-declared-no-calls=inapplicable; none-declared-with-calls=fail; " +
                    "schema-ok=pass; required-missing=fail; undeclared-tool=fail; not-a-schema=skipped; declared-no-calls=skipped; " +
                    "minority-checked=warn; half-checked=pass; minority-checked-failing=fail",
    };

    private static IEnumerable<(string Name, EvalInput Input)> VersionProbes()
    {
        var schema = new Dictionary<string, object> { ["type"] = "object", ["required"] = new List<object> { "q" } };
        var good = new ToolCall("search", new Dictionary<string, object> { ["q"] = "x" }, null);
        var missing = new ToolCall("search", new Dictionary<string, object>(), null);
        yield return ("not-captured", new EvalInput("q", "r", ToolCalls: [good], ToolDefinitions: null));
        yield return ("none-declared-no-calls", new EvalInput("q", "r", ToolCalls: [], ToolDefinitions: []));
        yield return ("none-declared-with-calls", new EvalInput("q", "r", ToolCalls: [good], ToolDefinitions: []));
        yield return ("schema-ok", new EvalInput("q", "r", ToolCalls: [good], ToolDefinitions: [Def("search", schema)]));
        yield return ("required-missing", new EvalInput("q", "r", ToolCalls: [missing], ToolDefinitions: [Def("search", schema)]));
        yield return ("undeclared-tool", new EvalInput("q", "r", ToolCalls: [good, new ToolCall("delete", null, null)], ToolDefinitions: [Def("search", schema)]));
        yield return ("not-a-schema", new EvalInput("q", "r", ToolCalls: [good], ToolDefinitions: [Def("search", new Dictionary<string, object> { ["q"] = "string" })]));
        yield return ("declared-no-calls", new EvalInput("q", "r", ToolCalls: [], ToolDefinitions: [Def("search", schema)]));
        var noSchema = Def("lookup", new Dictionary<string, object> { ["q"] = "string" });
        var lookup = new ToolCall("lookup", null, null);
        yield return ("minority-checked", new EvalInput("q", "r", ToolCalls: [good, lookup, lookup], ToolDefinitions: [Def("search", schema), noSchema]));
        yield return ("half-checked", new EvalInput("q", "r", ToolCalls: [good, lookup], ToolDefinitions: [Def("search", schema), noSchema]));
        yield return ("minority-checked-failing", new EvalInput("q", "r", ToolCalls: [missing, lookup, lookup], ToolDefinitions: [Def("search", schema), noSchema]));
    }

    [Fact]
    public async Task TheVersion_NamesTheVerdictsItGives()
    {
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(100));
        var verdicts = new List<string>();
        foreach (var (name, input) in VersionProbes())
            verdicts.Add($"{name}={SchemaLeaf(await eval.EvaluateAsync(input)).Score.Label}");
        var observed = string.Join("; ", verdicts);

        Assert.True(s_verdictsByVersion.TryGetValue(eval.Version, out var recorded),
            $"version {eval.Version} has no recorded verdicts; add them: \"{observed}\"");
        Assert.Equal(recorded, observed);
    }

    [Fact]
    public async Task APassOnAMinorityOfTheCalls_WarnsTheCase_AndSaysWhy()
    {
        // Review round 3 (B10j): one checkable call of ten passed the schema leaf, and a perfect judge passed the case.
        var schema = new Dictionary<string, object> { ["type"] = "object", ["required"] = new List<object> { "q" } };
        var calls = new List<ToolCall> { new("search", new Dictionary<string, object> { ["q"] = "x" }, null) };
        calls.AddRange(Enumerable.Repeat(new ToolCall("lookup", null, null), 9));
        var input = new EvalInput("q", "r", ToolCalls: calls,
            ToolDefinitions: [Def("search", schema), Def("lookup", new Dictionary<string, object> { ["q"] = "string" })]);

        var result = await new ToolInputAccuracyEval(new FixedScoreEvaluator(100)).EvaluateAsync(input);

        var leaf = SchemaLeaf(result);
        Assert.Equal("warn", leaf.Score.Label);
        Assert.Contains("Only 1 of 10", leaf.Details.Summary!, StringComparison.Ordinal);
        Assert.Equal("warn", result.Score.Label);
        Assert.False(result.Score.Passed);
    }

    // ── B5a (#203 review): a call is checked only against a schema the check can read ──────────────────────────
    // Before, a definition with no parameter schema — or a "required" in a shape the check could not read, such as
    // the JsonElement System.Text.Json gives a Dictionary<string, object> value — made every call to that tool PASS.

    private static ToolDefinition Def(string name, IReadOnlyDictionary<string, object>? parameters) => new(name, name, parameters);

    private static IReadOnlyDictionary<string, object> JsonSchema(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;

    [Fact]
    public async Task EveryCallToAToolWithoutASchema_IsNotChecked_SoTheLeafIsSkipped_NotPassed()
    {
        // A 0.40 judge: had the schema leaf passed, (1.0 + 0.4) / 2 = 0.70 would pass the composite on a check that
        // looked at nothing.
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(40));
        var input = new EvalInput(Query: "Find flights", Response: "Called search_flights.", ToolCalls: OneCall(),
            ToolDefinitions: [Def("search_flights", null)]);

        var result = await eval.EvaluateAsync(input);

        var leaf = SchemaLeaf(result);
        Assert.Equal("skipped", leaf.Score.Label);
        Assert.Contains("search_flights", leaf.Details.Summary!, StringComparison.Ordinal);
        Assert.False(result.Score.Passed);
    }

    [Fact]
    public async Task MixedCalls_AreScoredOverTheCheckableOnes_AndTheOthersAreNamed()
    {
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "q", Response: "r",
            ToolCalls:
            [
                new ToolCall("search_flights", new Dictionary<string, object> { ["origin"] = "NYC" }, null),
                new ToolCall("book", new Dictionary<string, object>(), null),        // misses its required "flight_id"
                new ToolCall("notes", new Dictionary<string, object>(), null),       // no schema: not checked
            ],
            ToolDefinitions:
            [
                Def("search_flights", new Dictionary<string, object> { ["required"] = new object[] { "origin" } }),
                Def("book", new Dictionary<string, object> { ["required"] = new object[] { "flight_id" } }),
                Def("notes", null),
            ]);

        var leaf = SchemaLeaf(await eval.EvaluateAsync(input));

        Assert.Equal(0.5, leaf.Score.Value, 3);                 // 1 of 2 checkable calls, not 2 of 3
        Assert.Equal(1, leaf.Details.Dimensions!["calls_unverifiable"]);
        Assert.Contains(leaf.Details.Evidence!, e => e.Source == "tool_definition" && e.Reference == "notes");
    }

    [Fact]
    public async Task AJsonDeserializedSchema_IsRead_NotTakenAsRequiringNothing()
    {
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "q", Response: "r",
            ToolCalls: [new ToolCall("book", new Dictionary<string, object>(), null)],
            ToolDefinitions: [Def("book", JsonSchema("""{"type":"object","required":["flight_id"]}"""))]);

        var leaf = SchemaLeaf(await eval.EvaluateAsync(input));

        // A measured fail — read, and the required key is missing — not a skip (a skip also scores 0 and does not pass).
        Assert.Equal("fail", leaf.Score.Label);
        Assert.Equal(1, leaf.Details.Dimensions!["calls_checked"]);
        Assert.Contains(leaf.Details.Evidence!, e => e.Message.Contains("flight_id", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"type":"object"}""", true)]                       // no "required": requires nothing — a checked pass
    [InlineData("""{"type":"object","required":"flight_id"}""", false)] // a string, not a list: unreadable
    [InlineData("""{"type":"object","required":[1]}""", false)]        // not names: unreadable
    public async Task ASchemaWithoutRequired_IsACheckedPass_AnUnreadableRequired_IsNotChecked(string schema, bool checkedPass)
    {
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "q", Response: "r",
            ToolCalls: [new ToolCall("book", new Dictionary<string, object>(), null)],
            ToolDefinitions: [Def("book", JsonSchema(schema))]);

        var leaf = SchemaLeaf(await eval.EvaluateAsync(input));

        Assert.Equal(checkedPass ? "pass" : "skipped", leaf.Score.Label);
    }

    [Theory]
    [InlineData(true)]    // a name→type map: not a JSON Schema
    [InlineData(false)]   // an empty object
    public async Task ParametersThatAreNotASchema_LeaveTheCallUnchecked_NotAPass(bool nameToTypeMap)
    {
        // B6c-12 (mid-branch review): with no "required" key these read as "requires nothing" — a checked pass.
        var parameters = nameToTypeMap
            ? new Dictionary<string, object> { ["flight_id"] = "string" }
            : new Dictionary<string, object>();
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "q", Response: "r",
            ToolCalls: [new ToolCall("book", new Dictionary<string, object>(), null)],
            ToolDefinitions: [Def("book", parameters)]);

        var leaf = SchemaLeaf(await eval.EvaluateAsync(input));

        Assert.Equal("skipped", leaf.Score.Label);
    }

    [Fact]
    public async Task DefinitionsThatDifferOnlyInCase_AreOneTool_AndDoNotThrow()
    {
        var eval = new ToolInputAccuracyEval(new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "q", Response: "r",
            ToolCalls: [new ToolCall("Book", new Dictionary<string, object>(), null)],
            ToolDefinitions:
            [
                Def("book", null),
                Def("BOOK", new Dictionary<string, object> { ["required"] = new object[] { "flight_id" } }),
            ]);

        var leaf = SchemaLeaf(await eval.EvaluateAsync(input));

        // Checked against the definition that has a schema: a measured fail, not the skip the schema-less one would give.
        Assert.Equal("fail", leaf.Score.Label);
        Assert.Equal(1, leaf.Details.Dimensions!["calls_checked"]);
    }
}
