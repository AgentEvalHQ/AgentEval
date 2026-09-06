// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;
using Json.Schema;
using Xunit;
using SchemaEvalOpts = Json.Schema.EvaluationOptions;

namespace AgentEval.Tests.Evals;

/// <summary>
/// The boundary between ADR-030 Slice 1.1 (shipped here) and Slice 1.4 (deferred: §9 Q4 is still
/// open and §0.0 lists it as gating this very item), pinned rather than asserted.
/// <para>
/// §6.2 item 3 states the constraint: schema v1 has <c>additionalProperties: false</c> on
/// <c>score</c> and a <b>closed</b> <c>label</c> enum. So the two halves of applicability —
/// the <c>measurement</c> field and the <c>"inapplicable"</c> label — are both schema changes, and
/// shipping either one silently would invalidate every document the library writes and change every
/// historical <c>ScenarioResult</c> content hash.
/// </para>
/// <para>
/// <b>These tests are the checkable form of the deferral.</b> They record what schema v1 does today,
/// so nobody has to take "1.4 is deferred" on trust — and so the day the schema bumps, they are the
/// tests that have to change on purpose rather than the ones that break by surprise.
/// </para>
/// <para>
/// ⚠ <b>THAT DAY WAS 2026-09-06, and this file changed on purpose — the sentence above is the
/// authorisation, not an exception to it.</b> Q4's answer splits 1.4 in two: <b>(i)</b> widen the
/// schema now — the READ side — and <b>(ii)</b> defer writing the field and the <c>$id</c> bump to
/// the next major. Part (i) is here. The two facts that were <c>StillRejects</c> are now
/// <c>NowAccepts</c>, each carrying the negative direction that makes it a WIDENING rather than an
/// OPENING; <see cref="SchemaV1_Id_IsStillV1"/> and
/// <see cref="EveryResultTheLibraryProducesOnItsOwn_StillValidates"/> are untouched and still green,
/// which is what says part (ii) has not been smuggled in with part (i).
/// </para>
/// </summary>
public class InapplicableSchemaBoundaryTests
{
    private static readonly JsonSerializerOptions s_persistenceLike = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static JsonSchema LoadSchema()
    {
        var asm = typeof(FileSystemOutputStore).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".eval-result.schema.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private static bool Validates(string json) =>
        LoadSchema().Evaluate(JsonNode.Parse(json), new SchemaEvalOpts { OutputFormat = OutputFormat.List }).IsValid;

    private static string Result(string scoreJson) => $$"""
        {
          "metric": { "key": "k", "name": "n", "category": "c", "version": "1.0.0" },
          "score": {{scoreJson}},
          "details": { "dimensions": null, "evidence": null, "recommendations": null, "subResults": null, "aggregationStrategy": null },
          "provenance": { "type": "atomic-code", "estimatedCost": 0.0, "cacheHit": false },
          "evaluatedAt": "2026-09-05T10:00:00Z"
        }
        """;

    [Fact]
    public void SchemaV1_NowAccepts_TheInapplicableLabel_AndStillRejectsAnythingElse()
    {
        // ⚠ WIDENED 2026-09-06 — Slice 1.4 part (i), Q4's answer: the READ side of applicability
        //   lands now, the WRITE side and the `$id` bump wait for the next major. The label enum is
        //   {pass, fail, warn, skipped, error, inapplicable}.
        Assert.True(Validates(Result("""{ "value": 0.0, "label": "inapplicable", "passed": false, "severity": "none" }""")));
        Assert.True(Validates(Result("""{ "value": 0.0, "label": "skipped", "passed": false, "severity": "none" }""")));

        // …and it is still an ENUM. Widening a closed set to a larger closed set is the change; a
        // schema that accepts any label would accept a typo as a verdict.
        Assert.False(Validates(Result("""{ "value": 0.0, "label": "inapplicible", "passed": false, "severity": "none" }""")));
    }

    [Fact]
    public void SchemaV1_NowAccepts_AMeasurementField_ByNamingIt_NotByOpeningTheObject()
    {
        Assert.True(Validates(Result(
            """{ "value": 0.0, "label": "skipped", "passed": false, "severity": "none", "measurement": "notApplicable" }""")));

        // ⚠ THE TWO DIRECTIONS THAT MAKE THAT A WIDENING RATHER THAN AN OPENING.
        //   (a) `measurement` is an enum of the three MeasurementState members, so an unknown state
        //       is refused rather than round-tripped as a string nobody defined.
        Assert.False(Validates(Result(
            """{ "value": 0.0, "label": "skipped", "passed": false, "severity": "none", "measurement": "probably" }""")));

        //   (b) `additionalProperties: false` on `score` is UNTOUCHED. If this passed, the field
        //       would have been admitted by opening the object, and every future typo with it.
        Assert.False(Validates(Result(
            """{ "value": 0.0, "label": "skipped", "passed": false, "severity": "none", "measurment": "notApplicable" }""")));
    }

    [Fact]
    public void SchemaV1_Id_IsStillV1()
    {
        // $id bumps with the schema. While it says v1, neither half of applicability is persisted.
        var asm = typeof(FileSystemOutputStore).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".eval-result.schema.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();

        Assert.Contains("https://agenteval.dev/schemas/v1/eval-result.schema.json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInapplicableScore_NowRoundTripsThroughADocumentTheSchemaAccepts()
    {
        // ⚠ THIS TEST IS THE ONE THAT MOVED, AND IT MOVED ON PURPOSE. Its previous name ended
        //   "…ButItsDocumentDoesNotValidateYet", and it recorded the deferral: an inapplicable score
        //   was constructable in memory and its document was out of schema. Part (i) closes that
        //   half. What has NOT changed is that nothing in src/ PRODUCES an inapplicable score — it
        //   still exists only when an author calls the factory.
        var na = EvalScore.NotApplicable();
        var json = JsonSerializer.Serialize(na, s_persistenceLike);

        // WhenWritingDefault withholds `measurement` only while it IS the default. An actually
        // inapplicable score emits BOTH halves — the field and the label — and neither can be
        // suppressed without lying about the score. Both are now in schema.
        Assert.Contains("\"measurement\":\"notApplicable\"", json, StringComparison.Ordinal);
        Assert.Contains("\"label\":\"inapplicable\"", json, StringComparison.Ordinal);
        Assert.Equal(MeasurementState.NotApplicable, na.Measurement);
        Assert.True(Validates(Result(json)));
    }

    [Fact]
    public void TheNonBreakingGuarantee_IsThatNoPRODUCEDDOCUMENTCHANGEDABYTE()
    {
        // Stated as a pair, because widening a schema is only free if the write path did not move:
        //   a Measured score  -> STILL no `measurement` field, in-enum label, validates. Every
        //                        existing producer is in this class, so no document the library
        //                        writes changed and no historical content hash moved. That is the
        //                        byte-level prediction part (i) makes, checked here rather than
        //                        promised in a release note.
        //   an n/a score      -> both halves present, and NOW validates. Reachable only by calling
        //                        EvalScore.NotApplicable() by hand, i.e. by opting in.
        var measured = new EvalScore(0.9, null, "pass", true, null, "none", null);
        var measuredJson = JsonSerializer.Serialize(measured, s_persistenceLike);

        Assert.DoesNotContain("measurement", measuredJson, StringComparison.OrdinalIgnoreCase);
        Assert.True(Validates(Result(measuredJson)));

        Assert.True(Validates(Result(JsonSerializer.Serialize(EvalScore.NotApplicable(), s_persistenceLike))));
    }

    [Fact]
    public void EveryResultTheLibraryProducesOnItsOwn_StillValidates()
    {
        // The non-breaking claim, checked at the schema rather than argued: a skipped result — the one
        // shape Slice 1.5 changed — still validates, summary and all.
        var skipped = EvalResult.Skipped(new StubEval(), "no tool definitions were supplied");
        var json = JsonSerializer.Serialize(skipped, s_persistenceLike);

        Assert.True(Validates(json), $"A skipped result must still validate against schema v1.\n{json}");
    }

    private sealed class StubEval : IEval
    {
        public string Key => "k";
        public string Name => "n";
        public string Category => "c";
        public string Version => "1.0.0";
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
