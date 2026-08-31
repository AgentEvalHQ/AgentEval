using System.Collections.Generic;
using System.Text.Json;
using AgentEval.Memory.External.TypedMemEval;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Gates every shape on whether it can tell two systems apart. ADR-028.
/// </summary>
/// <remarks>
/// <para>
/// The family used to accept a shape on BM25 <b>coverage</b>, which is a proxy. What it cares about
/// is <b>discrimination</b>: a shape where a perfect selector and a plain lexical one score the same
/// cannot rank anything, whatever its coverage says. <c>conjunction/order-then-value</c> shipped at
/// V9 15/15 — headroom 0.00 — for its whole life with coverage sitting comfortably at 0.667.
/// </para>
/// <para>
/// Coverage remains the <i>calibration target</i>, because it is structural and free while headroom
/// costs a probe run. <b>Search on the cheap proxy; accept on the measured truth.</b>
/// </para>
/// <para>
/// A RATCHET, not a wall, for the reason the coverage ratchet beside it gives: a gate that is
/// uniformly red for the life of a known-weak shape stops being read, and a regression introduced
/// while fixing something else then lands invisibly. Listed shapes may improve and may not regress;
/// anything unlisted must clear the floor outright.
/// </para>
/// </remarks>
public class TypedMemEvalDiscriminationTests
{
    /// <summary>Minimum <c>V1 − V9</c> at which a shape can rank two systems. ADR-028 §3a.</summary>
    private const double DiscriminationFloor = 0.15;

    /// <summary>
    /// Shapes whose headroom is 0.00 <b>by design</b>, with the design that makes it so.
    /// </summary>
    /// <remarks>
    /// These are not debt and must never be "fixed" from a headroom table read without the design
    /// note. WorkingMemory is a DISTANCE LADDER: the independent variable is how far back the gold
    /// sits, and coverage is <i>meant</i> to fall across the rungs. Deleting the saturation at the
    /// short end deletes the low end of the gradient, which is the only thing the vertical measures.
    /// </remarks>
    private static readonly Dictionary<(TypedMemEvalVertical, string), string> SaturatedByDesign =
        new()
        {
            [(TypedMemEvalVertical.WorkingMemory, "distance-8")] =
                "declared ladder: the short rungs are meant to be trivially retrievable",
            [(TypedMemEvalVertical.WorkingMemory, "distance-15")] =
                "declared ladder: the gradient across rungs is the measurement",
            [(TypedMemEvalVertical.WorkingMemory, "distance-25")] =
                "declared ladder: still inside the easy half by construction",
        };

    /// <summary>
    /// Shapes that cannot yet rank and are awaiting a redesign. May improve; may not regress.
    /// </summary>
    /// <remarks>
    /// Both are near-closed-choice forms where the question names the entity it asks about, which
    /// hands a lexical retriever the session it needs. That is the same structural cap that limited
    /// <c>episodic/participant-attribution</c> to 0.20 after calibration — so these likely need new
    /// QUESTION FORMS rather than tuning, and ADR-028 §7.4 scopes them separately.
    /// </remarks>
    private static readonly Dictionary<(TypedMemEvalVertical, string), double> PendingRedesign =
        new()
        {
            [(TypedMemEvalVertical.Temporal, "occurrence-order")] = 0.05,
            [(TypedMemEvalVertical.Bitemporal, "belief-at-instant")] = 0.11,
        };

    public static TheoryData<TypedMemEvalVertical> AllVerticals()
    {
        var data = new TheoryData<TypedMemEvalVertical>();
        foreach (var vertical in System.Enum.GetValues<TypedMemEvalVertical>())
        {
            data.Add(vertical);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void EveryShape_CanRankTwoSystems(TypedMemEvalVertical vertical)
    {
        var byShape = ByShape(vertical);
        Assert.True(byShape.Count > 0, $"{vertical} publishes no per-shape probe record.");

        foreach (var (shape, record) in byShape)
        {
            if (!record.TryGetProperty("headroom_perfect_selector", out var headroomProperty))
                continue;   // arm not measured for this shape

            var headroom = headroomProperty.GetDouble();

            if (SaturatedByDesign.ContainsKey((vertical, shape)))
                continue;

            if (PendingRedesign.TryGetValue((vertical, shape), out var recorded))
            {
                // Tolerance matches the precision the ratchet is written at, so a repeating value
                // rounding down is not read as a regression.
                Assert.True(
                    headroom >= recorded - 1e-4,
                    $"{vertical} shape '{shape}' REGRESSED: headroom {headroom:F4} against a "
                    + $"recorded {recorded:F4}. This shape is awaiting redesign; it may improve, "
                    + "but it may not get worse.");
                continue;
            }

            Assert.True(
                headroom >= DiscriminationFloor,
                $"{vertical} shape '{shape}' has headroom {headroom:F3} (V1 − V9), below the "
                + $"{DiscriminationFloor} floor: a perfect selector and a plain lexical one score "
                + "close enough that this shape cannot rank two systems. Either redesign it, or "
                + "add it to PendingRedesign with its measured value and a reason.");
        }
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void ReachableHeadroom_IsPublishedWhereverHeadroomIs(TypedMemEvalVertical vertical)
    {
        // V1 − V9 is what a PERFECT selector buys. A real retriever returns gold plus whatever else
        // it ranks highly, so it cannot beat having everything — its ceiling is V8, not V1. Where
        // those diverge the published headroom is unreachable, and a consumer reading it would buy
        // retrieval work that cannot help: prospective/due-window reads 0.94 and can reach 0.17.
        foreach (var (shape, record) in ByShape(vertical))
        {
            if (!record.TryGetProperty("headroom_perfect_selector", out _))
                continue;

            Assert.True(
                record.TryGetProperty("headroom_reachable", out var reachable),
                $"{vertical} shape '{shape}' publishes headroom without the reachable figure. "
                + "The pair is the point — one number alone cannot say whether retrieval work helps.");
            Assert.True(
                record.TryGetProperty("limited_by", out var limitedBy),
                $"{vertical} shape '{shape}' publishes no retrieval/reasoning classification.");
            Assert.Contains(limitedBy.GetString(), new[] { "retrieval", "reasoning" });
            Assert.True(reachable.GetDouble() >= 0);
        }
    }

    private static Dictionary<string, JsonElement> ByShape(TypedMemEvalVertical vertical)
    {
        var metadata = JsonDocument.Parse(TypedMemEvalCorpus.ReadMetadataJson(vertical)).RootElement;
        var result = new Dictionary<string, JsonElement>();
        if (!metadata.TryGetProperty("probes", out var probes)
            || !probes.TryGetProperty("by_shape", out var byShape))
        {
            return result;
        }

        foreach (var shape in byShape.EnumerateObject())
            result[shape.Name] = shape.Value.Clone();

        return result;
    }
}
