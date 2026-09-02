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
            // EMPTY as of 0.33.0-beta, and the three entries it held are worth recording rather
            // than deleting silently.
            //
            // WorkingMemory's three short rungs were declared "trivially retrievable by design",
            // with the gradient across rungs given as the measurement. Both halves were wrong.
            // The generator built `distance + 1` sessions with gold pinned to session 0, so the
            // haystack GREW with the label and the two variables moved as one — and BM25 scores
            // documents independently of their position, so the reference retriever could only
            // ever see the second of them. Measured on the shipped corpus: moving one gold to
            // every index of its own haystack left top-5 membership identical at all of them.
            // The published gradient was a context-VOLUME effect wearing a distance label.
            //
            // H is now held at 60 non-gold sessions on every rung and gold position is the only
            // variable. The reference retriever went flat — V9 9/7/7/6/8 of 12, non-monotone,
            // well inside the sampling noise at n=12 — which is what a control that cannot see
            // the independent variable should do. All five rungs discriminate; three of them
            // could not rank anything at all before.
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

    /// <summary>
    /// Shapes that are one <b>arm of a paired design</b>, scored on the pair rather than alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>forgetting/still-valid</c> is the <b>over-forgetting control</b>: same fact, same question
    /// text, same statement as its <c>invalidated</c> twin, and nothing that cancels it. What it
    /// exists to catch is a system reporting a still-valid fact as superseded — and that is a
    /// property of the <b>pair</b>, not of either arm's retrieval headroom. Scoring the arm alone
    /// averages the capability away, exactly as ADR-028 §7.4 says of Bitemporal.
    /// </para>
    /// <para>
    /// <b>The exemption carries its own bar, and a harder one.</b> A shape listed here is skipped by
    /// the headroom floor only if its vertical's <c>paired_arms</c> block clears BOTH pair
    /// conditions. Measured on the shipped corpus: pair headroom <b>0.4667</b> against a scaled
    /// floor of 0.24, and <b>3.68 sd</b> of separation against a floor of 2.0 — while the arm alone
    /// reads 0.0667. This is not a way around a red gate; it is the gate pointed at the right
    /// quantity.
    /// </para>
    /// <para>
    /// None of it was published until 2026-09-02. <c>_pair_discrimination</c> runs per shape, which
    /// is correct for Bitemporal (both arms live in one shape) and returns <c>{}</c> for Forgetting,
    /// whose arms ARE its shapes. Thirty paired questions, no pair figure, nothing saying so.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<(TypedMemEvalVertical, string), string> ScoredOnPairedArms =
        new()
        {
            [(TypedMemEvalVertical.Forgetting, "still-valid")] =
                "over-forgetting control — the capability is the pair, not the arm",
        };

    /// <summary>
    /// Shapes whose answer the <b>harness itself</b> hands the reference reader, scored on the
    /// reader rather than on retrieval.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>episodic/participant-attribution</c> asks which participant said something, and
    /// <c>render()</c> emits every turn as <c>"{role}: {content}"</c>. So provenance is free for any
    /// reader of the transcript, and our reference stack cannot fail this shape for the reason it
    /// exists to test. It discriminates a consumer's memory layer that <i>flattens</i> conversations
    /// into facts and drops the speaker — which is a real and common design — and it cannot
    /// discriminate ours.
    /// </para>
    /// <para>
    /// <b>This was not visible until a leak was fixed.</b> The shape published headroom 0.20, and
    /// all of that difficulty came from the calibration echo scattering the quoted statement across
    /// both roles' filler — which is also what let a gold-ablated reader answer "both of us", 3
    /// draws of 3. Removing the leak removed the difficulty with it: V9 went 12/15 to 15/15.
    /// Distractor engineering cannot restore it, because the question names a topic AND quotes a
    /// statement, so gold is the only session carrying the whole query.
    /// </para>
    /// <para>
    /// <b>The bar is on the reader.</b> With the full labelled transcript, attribution must land
    /// well above the 1/3 chance floor the three-way question sets. Failing that would mean the
    /// reference model mis-attributes with the answer in front of it, which is a corpus defect
    /// (ambiguous gold), not a retrieval result.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<(TypedMemEvalVertical, string), string> ScoredOnTheReader =
        new()
        {
            [(TypedMemEvalVertical.Episodic, "participant-attribution")] =
                "the answer is a role and the transcript labels every turn with its role, so the "
                + "reference retriever cannot fail it; the bar is V8 against the chance floor",
        };

    /// <summary>Minimum <c>V8 − chance</c> for a shape scored on the reader.</summary>
    private const double ReaderMarginOverChance = 0.30;

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

            if (ScoredOnTheReader.ContainsKey((vertical, shape)))
            {
                // Not a skip — a different bar, on the arm this shape can actually fail.
                Assert.True(
                    record.TryGetProperty("v8_above_chance", out var v8Above),
                    $"{vertical} shape '{shape}' is scored on the reader but publishes no "
                    + "`v8_above_chance`. It needs a chance floor to be scored against one.");
                Assert.True(
                    v8Above.GetDouble() >= ReaderMarginOverChance,
                    $"{vertical} shape '{shape}' attributes at only {v8Above.GetDouble():F4} above "
                    + "chance WITH the full labelled transcript in context. That is a corpus "
                    + "defect — ambiguous gold — not a retrieval result.");
                continue;
            }

            if (ScoredOnPairedArms.ContainsKey((vertical, shape)))
            {
                // Not a skip — a harder bar, applied to the quantity this shape actually measures.
                var pair = PairedArms(vertical);
                Assert.True(
                    pair.HasValue,
                    $"{vertical} shape '{shape}' is declared as scored on paired arms, but the "
                    + "sidecar publishes no `paired_arms` block. Re-probe the vertical.");
                Assert.True(
                    pair!.Value.GetProperty("pair_discriminates").GetBoolean(),
                    $"{vertical} shape '{shape}' is exempt from the per-shape floor BECAUSE its "
                    + "capability is a pair property — and the pair does not discriminate. The "
                    + "exemption is void; this is a real regression.");
                continue;
            }

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
                ScoredOnAbstention.ContainsKey((vertical, shape))
                || ScoredOnTheReader.ContainsKey((vertical, shape)),
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

    /// <summary>
    /// A corpus that declares paired arms must have a sidecar that scores them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wired from the corpus, not from the sidecar, for the same reason the V6 redundancy check is:
    /// asking only whether the published block looks reasonable lets the runner decide what it is
    /// willing to measure. The corpus says which questions carry a <c>pair_id</c>; the sidecar must
    /// then report a pair figure over them.
    /// </para>
    /// <para>
    /// Forgetting shipped 30 paired questions and no pair figure for the life of the vertical.
    /// <c>_pair_discrimination</c> is called per shape — right for Bitemporal, whose arms are two
    /// questions in one shape, and empty for Forgetting, whose arms ARE its shapes. An instrument
    /// that returns nothing is indistinguishable from a vertical that has nothing to report, and
    /// this release found three of those in one arc.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void VerticalsWithPairedArms_PublishAPairFigure(TypedMemEvalVertical vertical)
    {
        var paired = 0;
        foreach (var question in JsonDocument.Parse(TypedMemEvalCorpus.ReadJson(vertical))
                     .RootElement.EnumerateArray())
        {
            if (question.TryGetProperty("typedmemeval", out var extension)
                && extension.TryGetProperty("pair_id", out var pairId)
                && !string.IsNullOrWhiteSpace(pairId.GetString()))
            {
                paired++;
            }
        }

        if (paired == 0)
        {
            Assert.Null(PairedArms(vertical));
            return;
        }

        var block = PairedArms(vertical);
        Assert.True(
            block.HasValue,
            $"{vertical} has {paired} questions carrying a pair_id and publishes no `paired_arms` "
            + "block. The pairing is the vertical's acceptance argument; an unreported pair figure "
            + "reads identically to a vertical that has no pairs.");

        // Publishing the block is not the same as measuring it. A block that scored nothing must
        // say so, and must not be read as a clean result.
        Assert.True(
            block!.Value.GetProperty("pairs").GetInt32() > 0,
            $"{vertical} publishes a pair block over zero scored pairs. See its "
            + "`pairs_incomplete` list: the arms never came together.");
        Assert.False(
            block.Value.TryGetProperty("unmeasured", out _),
            $"{vertical}'s pairs are complete but were never measured on both arms. Re-probe "
            + "before shipping this corpus.");
    }

    private static JsonElement? PairedArms(TypedMemEvalVertical vertical)
    {
        var metadata = JsonDocument.Parse(TypedMemEvalCorpus.ReadMetadataJson(vertical)).RootElement;
        if (metadata.TryGetProperty("probes", out var probes)
            && probes.TryGetProperty("paired_arms", out var paired))
        {
            return paired.Clone();
        }

        return null;
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
