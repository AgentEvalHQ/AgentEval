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
    public void SchemaV1_StillRejects_TheInapplicableLabel()
    {
        // The label enum is closed: {pass, fail, warn, skipped, error}. Slice 1.4 adds "inapplicable".
        Assert.False(Validates(Result("""{ "value": 0.0, "label": "inapplicable", "passed": false, "severity": "none" }""")));
        Assert.True(Validates(Result("""{ "value": 0.0, "label": "skipped", "passed": false, "severity": "none" }""")));
    }

    [Fact]
    public void SchemaV1_StillRejects_AMeasurementField()
    {
        // additionalProperties: false on `score`. Slice 1.4 adds `score.measurement`.
        Assert.False(Validates(Result(
            """{ "value": 0.0, "label": "skipped", "passed": false, "severity": "none", "measurement": "notApplicable" }""")));
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
    public void AnInapplicableScore_IsConstructableInMemory_ButItsDocumentDoesNotValidateYet()
    {
        // The honest statement of what Slice 1.1 ships without Slice 1.4, and the reason it is safe:
        // NOTHING in src/ produces an inapplicable score. It exists only when an author calls the
        // factory, and that author is opting in to a document schema v1 will not accept. Every result
        // the library produces on its own is unaffected — which is why 9,354 existing tests pass
        // unchanged and every historical content hash is untouched.
        var na = EvalScore.NotApplicable();
        var json = JsonSerializer.Serialize(na, s_persistenceLike);

        // WhenWritingDefault withholds `measurement` only while it IS the default. An actually
        // inapplicable score emits BOTH halves — the field and the out-of-enum label — and neither
        // can be suppressed without lying about the score. So the document is out of schema until
        // 1.4. Recorded, not hidden: the guarantee this slice makes is about the scores the library
        // PRODUCES, not about every score it can REPRESENT.
        Assert.Contains("\"measurement\":\"notApplicable\"", json, StringComparison.Ordinal);
        Assert.Contains("\"label\":\"inapplicable\"", json, StringComparison.Ordinal);
        Assert.Equal(MeasurementState.NotApplicable, na.Measurement);
        Assert.False(Validates(Result(json)));
    }

    [Fact]
    public void TheNonBreakingGuarantee_IsAboutWhatTheLibraryProduces()
    {
        // Stated as a pair so the boundary is not mistaken for a wider claim than it is:
        //   a Measured score  -> no `measurement` field, in-enum label, validates. Every existing
        //                        producer is in this class, which is why nothing broke.
        //   an n/a score      -> both halves present, does not validate. Reachable ONLY by calling
        //                        EvalScore.NotApplicable() by hand, i.e. by opting in.
        var measured = new EvalScore(0.9, null, "pass", true, null, "none", null);
        var measuredJson = JsonSerializer.Serialize(measured, s_persistenceLike);

        Assert.DoesNotContain("measurement", measuredJson, StringComparison.OrdinalIgnoreCase);
        Assert.True(Validates(Result(measuredJson)));

        Assert.False(Validates(Result(JsonSerializer.Serialize(EvalScore.NotApplicable(), s_persistenceLike))));
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
