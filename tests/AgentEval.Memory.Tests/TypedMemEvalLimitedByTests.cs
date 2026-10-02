// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Memory.External.TypedMemEval;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Pins what the TypedMemEval guide says about the per-shape <c>limited_by</c> field: the rule it
/// states, and that on the shipped records the field never reads <c>reasoning</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>tools/run_typedmemeval_probes.py</c> writes <c>reasoning</c> when
/// <c>(V1 − V8) &gt; (V1 − V9) / 2</c> — the whole haystack recovers less than half of the
/// V9-to-V1 gap — and <c>retrieval</c> otherwise.
/// </para>
/// <para>
/// The presence check in <see cref="TypedMemEvalDiscriminationTests"/> accepts either value, so a
/// field that never varies passes it. These tests read the values instead: one derives each value
/// from the shape's own V1/V8/V9 counts, the other pins the guide's statement that all 35 shapes
/// carrying the field read <c>retrieval</c>.
/// </para>
/// </remarks>
public class TypedMemEvalLimitedByTests
{
    public static TheoryData<TypedMemEvalVertical> AllVerticals()
    {
        var data = new TheoryData<TypedMemEvalVertical>();
        foreach (var vertical in Enum.GetValues<TypedMemEvalVertical>())
        {
            data.Add(vertical);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void LimitedBy_MatchesTheDocumentedMidpointRule(TypedMemEvalVertical vertical)
    {
        foreach (var (shape, record) in ShapesWithLimitedBy(vertical))
        {
            var v1 = Rate(record, "v1");
            var v8 = Rate(record, "v8");
            var v9 = Rate(record, "v9");
            var expected = (v1 - v8) > (v1 - v9) / 2 ? "reasoning" : "retrieval";
            var actual = record.GetProperty("limited_by").GetString();

            Assert.True(
                expected == actual,
                $"{vertical}/{shape}: limited_by is '{actual}', but V1 {v1:F4}, V8 {v8:F4}, V9 {v9:F4} "
                + $"give '{expected}' under the rule the TypedMemEval guide states.");
        }
    }

    [Fact]
    public void LimitedBy_ReadsRetrievalOnEveryShippedShape()
    {
        var values = new List<string>();
        foreach (var vertical in Enum.GetValues<TypedMemEvalVertical>())
        {
            foreach (var (shape, record) in ShapesWithLimitedBy(vertical))
                values.Add($"{vertical}/{shape}={record.GetProperty("limited_by").GetString()}");
        }

        // The guide says 35 shapes carry the field and every one reads "retrieval", so it does not
        // discriminate on the shipped records. If a shape ever reads "reasoning", that statement is
        // false: update the guide's limited_by note and its table together with this test.
        Assert.Equal(35, values.Count);
        Assert.All(values, value => Assert.EndsWith("=retrieval", value));
    }

    private static double Rate(JsonElement record, string arm) =>
        (double)record.GetProperty($"{arm}_passed").GetInt32()
        / record.GetProperty($"{arm}_applicable").GetInt32();

    private static List<(string Shape, JsonElement Record)> ShapesWithLimitedBy(TypedMemEvalVertical vertical)
    {
        var result = new List<(string Shape, JsonElement Record)>();
        using var document = JsonDocument.Parse(TypedMemEvalCorpus.ReadMetadataJson(vertical));
        if (!document.RootElement.TryGetProperty("probes", out var probes)
            || !probes.TryGetProperty("by_shape", out var byShape))
        {
            return result;
        }

        foreach (var shape in byShape.EnumerateObject())
        {
            if (shape.Value.TryGetProperty("limited_by", out _))
                result.Add((shape.Name, shape.Value.Clone()));
        }

        return result;
    }
}
