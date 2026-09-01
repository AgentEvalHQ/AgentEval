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
            // EMPTY, and the two entries it held are worth recording rather than deleting silently.
            //
            // temporal/occurrence-order sat at 0.05 because it asked about two ADJACENT events on
            // the relation chain, so the single link between them stated the answer outright while
            // the question handed BM25 both rare names -- a lexical lookup in the vertical whose
            // premise is that narration order must be FOLLOWED. It now asks the two ENDS of the
            // chain, so every link is necessary: headroom 0.05 -> 0.75, and it is the strongest
            // shape in its vertical.
            //
            // bitemporal/belief-at-instant sat at 0.11 because naming the asked subject retrieved
            // everything -- only two sessions in the haystack mentioned that person. Same-subject,
            // other-month distractors now compete (median 2 -> 5, which is K_REF exactly):
            // headroom 0.11 -> 0.3056, and pair headroom 0.167 -> 0.5556 against a scaled floor of
            // 0.254, which it previously MISSED.
            //
            // Both were fixed rather than re-baselined around, which is the point of keeping a
            // ratchet: a list that only ever grows is a list nobody reads.
        };

    /// <summary>
    /// Shapes scored on <b>abstention</b> rather than retrieval headroom, with the reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A shape whose questions carry <b>no gold session</b> cannot be scored on <c>V1 − V9</c>:
    /// every arm above is defined in terms of reaching a gold fact, so all three are undefined and
    /// the difference cannot be formed. That is correct arithmetic and it used to be the end of it
    /// — the shape published no <c>headroom_perfect_selector</c>, both assertions above hit their
    /// <c>continue</c>, and 15 of Forgetting's 50 questions shipped <b>certified by nothing</b>
    /// while the suite stayed green. Absence read as a pass, which is the shape this family gates
    /// against everywhere else and had here in its own gate.
    /// </para>
    /// <para>
    /// So the skip is now a <b>declaration</b>. A shape may go unscored on headroom only if it is
    /// listed here and publishes the abstention axis instead; anything else unscored fails.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<(TypedMemEvalVertical, string), string> ScoredOnAbstention =
        new()
        {
            [(TypedMemEvalVertical.Forgetting, "never-known")] =
                "every question has zero gold sessions — the thing asked about was never "
                + "mentioned — so V1, V8 and V9 are undefined. Scored on V10/V11 abstention.",
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

    /// <summary>
    /// No shape may be skipped by the two gates above without saying so in the sidecar.
    /// </summary>
    /// <remarks>
    /// The bug this exists for shipped in the gate itself, not in the corpus: both assertions above
    /// open with <c>if (!record.TryGetProperty("headroom_perfect_selector", …)) continue;</c>, so a
    /// shape that publishes no headroom is waved through in silence. That is exactly the
    /// element-missing form of pass-by-absence — the artifact under test decides whether it gets
    /// tested — and it hid Forgetting's <c>never-known</c> for the whole life of the vertical.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void NoShape_GoesUnscoredInSilence(TypedMemEvalVertical vertical)
    {
        foreach (var (shape, record) in ByShape(vertical))
        {
            if (record.TryGetProperty("headroom_perfect_selector", out _))
                continue;   // scored on headroom; the two gates above own it

            Assert.True(
                ScoredOnAbstention.ContainsKey((vertical, shape)),
                $"{vertical} shape '{shape}' publishes no headroom and is not declared as scored "
                + "on another axis, so both discrimination gates skip it without a word. Either "
                + "give it gold sessions, or add it to ScoredOnAbstention with the reason.");

            Assert.True(
                record.TryGetProperty("discrimination_basis", out var basis),
                $"{vertical} shape '{shape}' is declared exempt but names no basis.");
            Assert.Equal("abstention", basis.GetString());

            Assert.True(
                record.TryGetProperty("discrimination_exempt_reason", out var reason)
                && !string.IsNullOrWhiteSpace(reason.GetString()),
                $"{vertical} shape '{shape}' declares an exemption with no reason attached.");

            // The exemption buys a different axis, not a free pass: the abstention arms must
            // actually be published, and `unmeasured` must be absent — a shape whose every draw
            // was silent is NOT MEASURED, and shipping that as an accepted exemption is the same
            // absence-reads-as-a-pass move one level down.
            Assert.True(
                record.TryGetProperty("abstention_full_haystack", out var full)
                && record.TryGetProperty("abstention_reference_retrieval", out _),
                $"{vertical} shape '{shape}' is scored on abstention but publishes neither arm.");
            Assert.False(
                record.TryGetProperty("unmeasured", out _),
                $"{vertical} shape '{shape}' publishes an abstention exemption whose arms were "
                + "never measured. Re-run the probes before shipping this corpus.");
            Assert.True(
                full.GetProperty("questions").GetInt32() > 0,
                $"{vertical} shape '{shape}' publishes an abstention arm over zero questions.");
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
