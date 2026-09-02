using System.Collections.Generic;
using System.Text.Json;
using AgentEval.Memory.External.TypedMemEval;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Every shape's realised coverage must sit inside the declared band, and every multi-shape vertical
/// must publish enough per-shape detail to check that.
/// </summary>
/// <remarks>
/// <para>
/// ADR-026 declares a BM25 coverage band of [0.50, 0.90] and ADR-028 §3d keeps it as the calibration
/// TARGET. Nothing enforced it per shape. The vertical MEAN is what gets checked at generation time,
/// and a mean is satisfiable by averaging: Arithmetic once calibrated to 0.700 — dead on target —
/// while its shapes sat at 0.857 / 0.947 / 0.083 / 0.894. Per-shape calibration was built to stop
/// that, and then no gate confirmed the result.
/// </para>
/// <para>
/// Three shapes are outside the band right now and nothing reports it, which is the whole argument
/// for this test. It is a RATCHET rather than a wall, for the reason the discrimination ratchet
/// beside it gives: a gate that is red for the life of a known-weak shape stops being read, and a
/// regression introduced while fixing something else then lands invisibly. Listed shapes carry their
/// measured value and may move toward the band but not away from it; anything unlisted must be in
/// band outright.
/// </para>
/// </remarks>
public class TypedMemEvalCoverageBandTests
{
    /// <summary>ADR-026's declared BM25 coverage band. Constants here, never read from the artifact.</summary>
    private const double BandLow = 0.50;
    private const double BandHigh = 0.90;

    /// <summary>
    /// Shapes outside the band today, with the coverage measured on the shipped corpus.
    /// </summary>
    /// <remarks>
    /// Each is a real finding, not a tolerance:
    /// <list type="bullet">
    /// <item><c>conjunction/alias-then-count</c> — legitimately hard rather than broken. V1 is 15/15,
    /// so the corpus is answerable; BM25 simply cannot find the evidence, which is the point of a
    /// retrieval benchmark. Declared rather than tuned, because tuning it toward 0.70 would trade
    /// away the widest headroom in the family (0.93).</item>
    /// <item><c>prospective/due-window</c> — below the floor AND reasoning-limited (V8 4/18), so it is
    /// hard in two different ways at once. Scoped for redesign.</item>
    /// <item><c>prospective/not-yet-true</c> — SATURATED at 1.0. BM25 returns gold for every question,
    /// and its headroom of 0.1667 is one question out of six. This is the one entry here that is a
    /// defect rather than a declared property.</item>
    /// </list>
    /// </remarks>
    private static readonly Dictionary<(TypedMemEvalVertical, string), double> OutOfBand =
        new()
        {
            // 0.3356 -> 0.4344 at 0.33.0-beta, moving toward the band. The ratchet records the
            // BETTER value so it cannot drift back: the decoy designation's event count now
            // differs from gold's, which changed how many sessions the shape carries.
            [(TypedMemEvalVertical.Conjunction, "alias-then-count")] = 0.4344,
            [(TypedMemEvalVertical.Prospective, "due-window")] = 0.4352,
            [(TypedMemEvalVertical.Prospective, "not-yet-true")] = 1.0000,
        };

    /// <summary>
    /// Multi-shape verticals that publish no per-shape coverage, so their shapes cannot be checked.
    /// </summary>
    /// <remarks>
    /// These calibrate on a single knob, so the sidecar carries only a vertical mean. That is exactly
    /// the condition under which a collapsed shape hides — and skipping them silently would make this
    /// gate decoration for a third of the family. Listing them makes the gap a declared, testable
    /// fact: each is fixed by opting into per-shape calibration, which changes the corpus and so owes
    /// a re-probe.
    /// </remarks>
    /// <summary>
    /// Shapes whose questions carry <b>no gold session</b>, so realised coverage is not defined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Coverage is the share of a question's gold sessions a K-budget BM25 retriever surfaces. With
    /// no gold the ratio is 0/0, which the generator reports as 1.0 — and 1.0 in this gate means
    /// SATURATED, the one failure the band exists to catch. Reading it that way would be exactly
    /// backwards: nothing is saturated, there is simply nothing to retrieve.
    /// </para>
    /// <para>
    /// So it is exempt, and exempt by DECLARATION rather than by a value-based guess like "skip
    /// shapes at exactly 1.0" — that rule would also skip a genuinely saturated shape, which is how
    /// <c>prospective/not-yet-true</c> would have escaped. These shapes are scored on abstention
    /// instead (V10/V11, ADR-028 §13); the exemption is from the coverage band only.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<(TypedMemEvalVertical, string), string> NoGoldSoNoCoverage =
        new()
        {
            [(TypedMemEvalVertical.Forgetting, "never-known")] =
                "zero gold sessions by construction — the thing asked about was never mentioned",
        };

    private static readonly HashSet<TypedMemEvalVertical> NoPerShapeCoverage =
        new()
        {
            TypedMemEvalVertical.Temporal,
            TypedMemEvalVertical.WorkingMemory,
            // Forgetting left this list on 2026-09-02 by opting into per-shape calibration. Its
            // shapes had been taking the knob in different directions under one value: invalidated
            // and still-valid now land at 0.65 and 0.70, and never-known cannot be calibrated at
            // all (see NoGoldSoNoCoverage).
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
    public void EveryShape_SitsInsideTheCoverageBand(TypedMemEvalVertical vertical)
    {
        var perShape = PerShapeCoverage(vertical);
        if (perShape.Count == 0)
        {
            return;     // covered by MultiShapeVerticals_PublishPerShapeCoverage
        }

        foreach (var (shape, coverage) in perShape)
        {
            if (NoGoldSoNoCoverage.ContainsKey((vertical, shape)))
            {
                // Not a pass — a different question. Assert the reading is the one the exemption
                // claims, so a shape that quietly GAINS gold stops being exempt.
                Assert.True(
                    coverage >= 1.0 - 1e-9,
                    $"{vertical} shape '{shape}' is declared as having no gold, so its coverage "
                    + $"should be the 0/0 reading of 1.0 — it is {coverage:F4}. If this shape now "
                    + "has gold sessions, remove it from NoGoldSoNoCoverage and calibrate it.");
                continue;
            }

            if (OutOfBand.TryGetValue((vertical, shape), out var recorded))
            {
                // May move TOWARD the band, never further from it. Distance is measured from
                // whichever edge it sits outside, so a saturated shape and a collapsed one are both
                // held by the same rule.
                var recordedDistance = DistanceOutside(recorded);
                var currentDistance = DistanceOutside(coverage);
                Assert.True(
                    currentDistance <= recordedDistance + 1e-4,
                    $"{vertical} shape '{shape}' moved FURTHER outside the band: coverage "
                    + $"{coverage:F4} against a recorded {recorded:F4}. Declared out-of-band shapes "
                    + "may improve, but they may not get worse.");
                continue;
            }

            Assert.True(
                coverage >= BandLow && coverage <= BandHigh,
                $"{vertical} shape '{shape}' has realised coverage {coverage:F4}, outside the "
                + $"[{BandLow}, {BandHigh}] band. A vertical MEAN in range hides this — which is the "
                + "averaging defect per-shape calibration exists to refuse. Either bring it into "
                + "band, or add it to OutOfBand with its measured value and the reason it belongs "
                + "there.");
        }
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void MultiShapeVerticals_PublishPerShapeCoverage(TypedMemEvalVertical vertical)
    {
        // A vertical with one shape has nothing to decompose, so the mean IS the per-shape figure.
        if (ShapeCount(vertical) < 2)
        {
            return;
        }

        var published = PerShapeCoverage(vertical).Count > 0;

        if (NoPerShapeCoverage.Contains(vertical))
        {
            // A ratchet in the other direction: once a vertical starts publishing per-shape coverage
            // it must not stop, and the declared list must shrink rather than be quietly kept.
            if (published)
            {
                Assert.Fail(
                    $"{vertical} now publishes per-shape coverage — remove it from "
                    + "NoPerShapeCoverage so the band check applies to it.");
            }

            return;
        }

        Assert.True(
            published,
            $"{vertical} has {ShapeCount(vertical)} shapes and publishes no per-shape coverage, so "
            + "no gate can tell whether any of them left the band. Opt the generator into per-shape "
            + "calibration, or declare the gap in NoPerShapeCoverage.");
    }

    /// <summary>How far outside [BandLow, BandHigh] a value sits; 0 when inside.</summary>
    private static double DistanceOutside(double coverage) =>
        coverage < BandLow ? BandLow - coverage
        : coverage > BandHigh ? coverage - BandHigh
        : 0.0;

    private static int ShapeCount(TypedMemEvalVertical vertical)
    {
        var metadata = JsonDocument.Parse(TypedMemEvalCorpus.ReadMetadataJson(vertical)).RootElement;
        if (!metadata.TryGetProperty("probes", out var probes)
            || !probes.TryGetProperty("by_shape", out var byShape))
        {
            return 0;
        }

        var count = 0;
        foreach (var _ in byShape.EnumerateObject())
        {
            count++;
        }

        return count;
    }

    private static Dictionary<string, double> PerShapeCoverage(TypedMemEvalVertical vertical)
    {
        var metadata = JsonDocument.Parse(TypedMemEvalCorpus.ReadMetadataJson(vertical)).RootElement;
        var result = new Dictionary<string, double>();
        if (!metadata.TryGetProperty("coverage", out var coverage)
            || !coverage.TryGetProperty("per_shape_realised", out var perShape))
        {
            return result;
        }

        foreach (var shape in perShape.EnumerateObject())
        {
            result[shape.Name] = shape.Value.GetDouble();
        }

        return result;
    }
}
