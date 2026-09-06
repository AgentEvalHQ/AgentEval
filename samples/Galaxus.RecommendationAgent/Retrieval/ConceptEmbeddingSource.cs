// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Text;

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>
/// The OFFLINE default embedding source: authored concept vectors over a fixed, ordered list of
/// <see cref="ConceptCount"/> named concept dimensions, projected from text through an authored
/// keyword-to-concept lexicon.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a demo stand-in for a real embedding model.</b> It is not
/// <c>text-embedding-3-small</c> and it does not pretend to be. It exists so this sample is
/// deterministic, hermetic and runs with no API key and no network — which matters for an
/// interview demo and matters more for an evaluation, where a retrieval layer that changes
/// between runs makes every number unfalsifiable. The live path is
/// <see cref="AzureEmbeddingSource"/>; the committed-asset path is
/// <see cref="PrecomputedEmbeddingSource"/>. Both are written for real.
/// </para>
/// <para>
/// <b>Why not a hash embedding.</b> A signed-FNV bag-of-words vector demonstrates retrieval
/// PLUMBING and nothing else: semantically unrelated products with shared vocabulary score high,
/// and the genuine cross-category link — hiking pack to travel tripod, which shares almost no
/// vocabulary — scores near zero. Shipping that as "semantic search" would print a false tick on
/// the one claim this demo exists to make. So the offline default is not a hash. It is a small,
/// hand-authored semantic space.
/// </para>
/// <para>
/// <b>How it retrieves by meaning.</b> Every dimension is a named CONCEPT, not a hashed token
/// bucket, and many different surface words project onto the same dimension. A query about
/// <i>"shooting waterfalls on hikes"</i> lands on
/// <see cref="LandscapePhotography"/>, <see cref="LongExposureLightControl"/>,
/// <see cref="HikingTrekking"/> and <see cref="CarriedWeight"/>; a 10-stop ND filter's embedding
/// document lands on the same first two through completely different words ("neutral density",
/// "slow shutter", "context:golden-hour"). The two vectors are neighbours while sharing no
/// keyword — which is exactly the behaviour the lexical leg cannot produce, and exactly why the
/// hybrid needs both.
/// </para>
/// <para>
/// <b>Honest limits, stated rather than hidden.</b> (1) Coverage is bounded by the authored
/// lexicon: vocabulary outside it contributes nothing, and <see cref="UnmappedTokens"/> exists so
/// that is inspectable rather than invisible. (2) 24 dimensions cannot express the fine
/// distinctions a 1536-dimensional model can — it separates "landscape photography at dawn" from
/// "espresso brewing", not two wide-angle zooms from each other; the lexical leg does the
/// fine-grained work. (3) The lexicon is authored against the vocabulary the design specifies for
/// this catalogue, so it is calibrated to THIS demo and would need extending for another.
/// </para>
/// </remarks>
public sealed class ConceptEmbeddingSource : IEmbeddingSource
{
    // ── The 24 concept dimensions. Order is the vector layout and is FROZEN: changing it
    //    invalidates every cached vector, which is what EmbeddingDocument.TemplateVersion and
    //    ModelIdentifier's version suffix are for. ────────────────────────────────────────────

    /// <summary>Cameras, bodies, sensors, the act of photographing.</summary>
    public const string PhotographyCapture = "photography-capture";

    /// <summary>Landscape, scenery, mountains, water, sunrise — the subject matter, not the gear.</summary>
    public const string LandscapePhotography = "landscape-photography";

    /// <summary>Long exposure, neutral density, shutter control, tripod-dependent technique.</summary>
    public const string LongExposureLightControl = "long-exposure-light-control";

    /// <summary>Lenses, filter threads, mounts, heads, plates — the physical interface between things.</summary>
    public const string OpticsAndMounting = "optics-and-mounting";

    /// <summary>Grams matter. Ultralight, packable, "every 100 g counts".</summary>
    public const string CarriedWeight = "carried-weight";

    /// <summary>Travel, packing, cabin size, folds down, taken along.</summary>
    public const string TravelPortability = "travel-portability";

    /// <summary>Hiking, trekking, multi-day, hut to hut, on foot.</summary>
    public const string HikingTrekking = "hiking-trekking";

    /// <summary>Weather sealing, water resistance, dust, ruggedness, durability.</summary>
    public const string WeatherAndDurability = "weather-and-durability";

    /// <summary>Dawn, dusk, night, headlamps, lumens, early starts.</summary>
    public const string LowLightAndDawn = "low-light-and-dawn";

    /// <summary>Power banks, batteries, charging, USB-C, watts.</summary>
    public const string PowerAndCharging = "power-and-charging";

    /// <summary>Espresso machines, portafilters, tampers, shots, milk.</summary>
    public const string EspressoBrewing = "espresso-brewing";

    /// <summary>Grinders, burrs, grind size — the missing-companion axis for Sofia's gap.</summary>
    public const string CoffeeGrinding = "coffee-grinding";

    /// <summary>Beans, roast, descaler, canisters — the consumable cadence axis.</summary>
    public const string CoffeeConsumables = "coffee-consumables";

    /// <summary>Blenders, kettles, food processors, general kitchen durables.</summary>
    public const string KitchenAppliance = "kitchen-appliance";

    /// <summary>Water filtration, cartridges, limescale, carafes.</summary>
    public const string WaterFiltration = "water-filtration";

    /// <summary>Scales, precision, calibration, timing, 0.1 g.</summary>
    public const string MeasurementPrecision = "measurement-precision";

    /// <summary>Consoles, controllers, games — the gift-trap decoy category.</summary>
    public const string GamingConsole = "gaming-console";

    /// <summary>DACs, amplifiers, headphones, speakers — and the "filter" lexical trap.</summary>
    public const string HomeAudio = "home-audio";

    /// <summary>Bikes, saddles, handlebars, bar bags, bikepacking.</summary>
    public const string Cycling = "cycling";

    /// <summary>Merino, base layers, shells, insulation.</summary>
    public const string ApparelLayering = "apparel-layering";

    /// <summary>Packs, pouches, cases, litres, dry bags, clips — how things are carried.</summary>
    public const string StorageAndCarry = "storage-and-carry";

    /// <summary>Cleaning, descaling, spare parts, servicing.</summary>
    public const string MaintenanceAndCleaning = "maintenance-and-cleaning";

    /// <summary>Recycled materials, repairability, certifications, second-hand.</summary>
    public const string SustainabilityRepairability = "sustainability-repairability";

    /// <summary>Beginner, entry-level, budget, starter — the experience-level axis.</summary>
    public const string ValueAndEntryLevel = "value-and-entry-level";

    /// <summary>Number of concept dimensions. The vector length this source produces.</summary>
    public const int ConceptCount = 24;

    /// <summary>
    /// Model identifier stamped into any asset generated from this source. The version suffix is
    /// part of it on purpose: a cached vector from <c>galaxus-concept-v1</c> must never be loaded
    /// into an index built by a later, differently-authored lexicon.
    /// </summary>
    /// <remarks>
    /// Bumped to <c>v2</c> by the B-8 fix, which added the <c>"on bike"</c> phrase. The dimension
    /// list and its order are unchanged — only the query-side lexicon grew — but a vector cached
    /// under <c>v1</c> was computed by a lexicon that projected <c>mode:on-bike</c> onto nothing,
    /// so it is not interchangeable with one computed now.
    /// <para>
    /// Bumped to <c>v3</c> by D-v's lexicon closure (plan item 8.11), for the same reason and by
    /// the same rule: the dimension list and its order are untouched, and the lexicon grew by the
    /// closable half of the authored phrases that embedded to zero. ⚠ <b>No cached concept vector
    /// exists in this repository to invalidate</b> — the committed
    /// <c>catalogue.embeddings.json</c> is keyed on <c>text-embedding-3-small</c>, not on this
    /// source — so the bump costs nothing today and is made anyway, because the version suffix is
    /// only worth having if it moves when the lexicon does.
    /// </para>
    /// </remarks>
    public const string ModelIdentifier = "galaxus-concept-v3";

    /// <summary>
    /// Dense cosine floor suggested for this concept space.
    /// <b>TO-CALIBRATE — do not present this as measured.</b> It deliberately matches
    /// <see cref="AzureEmbeddingSource.UncalibratedDenseScoreFloor"/> so the two paths are
    /// comparable out of the box, and that equality is itself an unverified assumption: a floor is
    /// a property of an embedding space, and a value calibrated in one does not transfer to the other.
    /// </summary>
    public const float UncalibratedDenseScoreFloor = 0.28f;

    /// <summary>Longest lexicon phrase, in tokens.</summary>
    public const int MaxPhraseTokens = 3;

    private static readonly string[] ConceptNames =
    [
        PhotographyCapture,
        LandscapePhotography,
        LongExposureLightControl,
        OpticsAndMounting,
        CarriedWeight,
        TravelPortability,
        HikingTrekking,
        WeatherAndDurability,
        LowLightAndDawn,
        PowerAndCharging,
        EspressoBrewing,
        CoffeeGrinding,
        CoffeeConsumables,
        KitchenAppliance,
        WaterFiltration,
        MeasurementPrecision,
        GamingConsole,
        HomeAudio,
        Cycling,
        ApparelLayering,
        StorageAndCarry,
        MaintenanceAndCleaning,
        SustainabilityRepairability,
        ValueAndEntryLevel,
    ];

    private static readonly Dictionary<string, int> ConceptIndexByName = BuildConceptIndex();
    private static readonly Dictionary<string, ConceptWeight[]> Lexicon = BuildLexicon();

    /// <summary>The concept dimensions, in vector order.</summary>
    public static IReadOnlyList<string> Concepts => ConceptNames;

    /// <summary>How many phrases the lexicon holds. Printed by the console diagnostics; it is the coverage story.</summary>
    public static int LexiconSize => Lexicon.Count;

    /// <summary>The shared instance. The source is stateless and thread-safe, so one is enough.</summary>
    public static ConceptEmbeddingSource Instance { get; } = new();

    /// <inheritdoc />
    public string Name => "concept";

    /// <inheritdoc />
    public string ModelId => ModelIdentifier;

    /// <inheritdoc />
    public int Dimensions => ConceptCount;

    /// <inheritdoc />
    public bool IsOffline => true;

    /// <inheritdoc />
    /// <remarks>
    /// ✅ <b>DERIVED for THIS space.</b> No longer <see cref="UncalibratedDenseScoreFloor"/> — that
    /// constant's own summary called its equality with the Azure source "an unverified assumption",
    /// and the assumption is now discharged rather than repeated:
    /// <see cref="CalibratedThresholds.Concept"/> and <see cref="CalibratedThresholds.RealVectors"/>
    /// are derived separately, on the same fit slice, by the same rule.
    /// </remarks>
    public float SuggestedDenseScoreFloor => CalibratedThresholds.Concept.DenseScoreFloor;

    /// <inheritdoc />
    /// <remarks>
    /// Never returns the unavailability sentinel: this source is always available. Text it
    /// recognises nothing in yields an ALL-ZERO vector of length <see cref="ConceptCount"/>,
    /// which is "no concept signal", not "no embedder" — the two are different states and the
    /// retriever reports them differently.
    /// </remarks>
    public ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ReadOnlyMemory<float>>(Embed(text));
    }

    /// <summary>
    /// The synchronous, deterministic projection. Same input, same bytes out, on every machine —
    /// there is no floating-point ordering hazard because accumulation order follows token order.
    /// </summary>
    /// <param name="text">Any text — an <see cref="EmbeddingDocument"/> or a query.</param>
    /// <returns>A unit-length vector of length <see cref="ConceptCount"/>, or all zeros when nothing matched.</returns>
    public float[] Embed(string? text)
    {
        var accumulated = Accumulate(text, unmapped: null);
        var vector = new float[ConceptCount];

        for (int i = 0; i < ConceptCount; i++)
        {
            // Square root damps repetition: a description that says "lightweight" four times must
            // not out-weigh one that says it once and also says "packs to 41 cm".
            vector[i] = MathF.Sqrt(accumulated[i]);
        }

        EmbeddingVectors.NormalizeInPlace(vector);
        return vector;
    }

    /// <summary>
    /// The concepts this text projects onto, strongest first, as unit-vector components. This is
    /// what makes "it retrieves by meaning" inspectable instead of asserted — print it next to a
    /// hit and the audience can see WHY a tripod came back for a query about waterfalls.
    /// </summary>
    /// <param name="text">Any text.</param>
    /// <returns>Non-zero concepts with their normalised weights, descending.</returns>
    public IReadOnlyList<(string Concept, float Weight)> Explain(string? text)
    {
        var vector = Embed(text);
        var explained = new List<(string Concept, float Weight)>(ConceptCount);

        for (int i = 0; i < ConceptCount; i++)
        {
            if (vector[i] > 0f) explained.Add((ConceptNames[i], vector[i]));
        }

        explained.Sort(static (left, right) =>
        {
            var byWeight = right.Weight.CompareTo(left.Weight);
            return byWeight != 0 ? byWeight : string.CompareOrdinal(left.Concept, right.Concept);
        });

        return explained;
    }

    /// <summary>
    /// The tokens this text contains that the lexicon does not know — the coverage gap, made
    /// visible. An authoring aid, and the honest answer to "what does your offline embedder miss?".
    /// </summary>
    /// <param name="text">Any text.</param>
    /// <returns>Distinct unmapped tokens, in order of first appearance.</returns>
    public static IReadOnlyList<string> UnmappedTokens(string? text)
    {
        var unmapped = new List<string>();
        Accumulate(text, unmapped);

        var seen   = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<string>(unmapped.Count);
        foreach (var token in unmapped)
        {
            if (seen.Add(token)) unique.Add(token);
        }

        return unique;
    }

    /// <summary>Vector position of a named concept.</summary>
    /// <param name="concept">One of the concept constants on this class.</param>
    /// <exception cref="ArgumentException">The name is not a concept dimension.</exception>
    public static int IndexOf(string concept)
    {
        if (ConceptIndexByName.TryGetValue(concept, out var index)) return index;
        throw new ArgumentException($"'{concept}' is not one of the {ConceptCount} concept dimensions.", nameof(concept));
    }

    /// <summary>True when the lexicon knows a phrase (after normalisation and stemming).</summary>
    /// <param name="phrase">A word or short phrase.</param>
    public static bool Knows(string? phrase) =>
        !string.IsNullOrWhiteSpace(phrase) && TryLookup(PhraseKey(LexicalIndex.Tokenize(phrase)), out _);

    // ── Projection ───────────────────────────────────────────────────────────────────────────

    private static float[] Accumulate(string? text, List<string>? unmapped)
    {
        var accumulated = new float[ConceptCount];
        if (string.IsNullOrWhiteSpace(text)) return accumulated;

        var tokens = LexicalIndex.Tokenize(text);
        if (tokens.Count == 0) return accumulated;

        var keys = new string[tokens.Count];
        for (int i = 0; i < tokens.Count; i++) keys[i] = LookupKey(tokens[i]);

        int position = 0;
        while (position < tokens.Count)
        {
            bool matched = false;
            int maxSpan = Math.Min(MaxPhraseTokens, tokens.Count - position);

            // Longest phrase wins and CONSUMES its tokens, so "water filter" cannot also fire
            // "water" — which is what keeps a query about waterfalls out of the water-filtration
            // dimension.
            for (int span = maxSpan; span >= 1; span--)
            {
                var phrase = JoinKeys(keys, position, span);
                if (phrase.Length == 0) continue;

                if (TryLookup(phrase, out var weights))
                {
                    foreach (var weight in weights) accumulated[weight.Index] += weight.Weight;
                    position += span;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                unmapped?.Add(tokens[position]);
                position++;
            }
        }

        return accumulated;
    }

    /// <summary>
    /// The canonical lookup form of one token: the part after the last <c>:</c> (so
    /// <c>"context:golden-hour"</c> keys as <c>"golden hour"</c>), with <c>-</c> and <c>.</c>
    /// turned into spaces. Every lexicon key is stored in this same form, which is what lets a
    /// hyphenated tag and a two-word query phrase reach the same entry.
    /// </summary>
    private static string LookupKey(string token)
    {
        if (token.Length == 0) return string.Empty;

        var colon = token.LastIndexOf(':');
        var core  = colon >= 0 && colon < token.Length - 1 ? token[(colon + 1)..] : token;

        var builder = new StringBuilder(core.Length);
        bool lastWasSpace = true;

        foreach (var ch in core)
        {
            var c = ch is '-' or '.' or '+' or ':' ? ' ' : ch;
            if (c == ' ')
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
                builder.Append(' ');
                continue;
            }

            lastWasSpace = false;
            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    private static string JoinKeys(string[] keys, int start, int span)
    {
        var builder = new StringBuilder();
        for (int i = start; i < start + span; i++)
        {
            if (keys[i].Length == 0) continue;
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(keys[i]);
        }
        return builder.ToString();
    }

    private static string PhraseKey(IReadOnlyList<string> tokens)
    {
        var keys = new string[tokens.Count];
        for (int i = 0; i < tokens.Count; i++) keys[i] = LookupKey(tokens[i]);
        return JoinKeys(keys, 0, keys.Length);
    }

    /// <summary>
    /// Lexicon lookup with a deliberately crude, documented stemmer applied to the LAST word only:
    /// plural <c>-s</c>/<c>-es</c>, gerund <c>-ing</c> (with and without a restored <c>e</c>), and
    /// past <c>-ed</c>. Crude on purpose — a real stemmer would be a dependency and a source of
    /// surprises, and the important vocabulary is authored in both forms anyway.
    /// </summary>
    private static bool TryLookup(string phrase, out ConceptWeight[] weights)
    {
        if (phrase.Length > 0 && Lexicon.TryGetValue(phrase, out var direct))
        {
            weights = direct;
            return true;
        }

        var space = phrase.LastIndexOf(' ');
        var head  = space >= 0 ? phrase[..(space + 1)] : string.Empty;
        var last  = space >= 0 ? phrase[(space + 1)..] : phrase;

        foreach (var stem in Stems(last))
        {
            if (Lexicon.TryGetValue(head + stem, out var stemmed))
            {
                weights = stemmed;
                return true;
            }
        }

        weights = [];
        return false;
    }

    private static IEnumerable<string> Stems(string word)
    {
        if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal))
            yield return word[..^1];

        if (word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal))
            yield return word[..^2];

        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal))
        {
            yield return word[..^3];
            yield return word[..^3] + "e";
        }

        if (word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal))
        {
            yield return word[..^2];
            yield return word[..^1];
        }
    }

    private static Dictionary<string, int> BuildConceptIndex()
    {
        var index = new Dictionary<string, int>(ConceptCount, StringComparer.Ordinal);
        for (int i = 0; i < ConceptNames.Length; i++) index[ConceptNames[i]] = i;
        return index;
    }

    // ── The authored keyword → concept lexicon ────────────────────────────────────────────────
    //
    // Read this as the honest core of the offline path. Every entry is a hand-made claim that a
    // surface word carries a meaning. Words that are genuinely AMBIGUOUS in this catalogue are
    // authored as ambiguous — "filter" splits across long-exposure and water-filtration, "shot"
    // splits across photography and espresso — and the longer phrase disambiguates, because the
    // longest phrase wins and consumes its tokens.

    private static Dictionary<string, ConceptWeight[]> BuildLexicon()
    {
        var lexicon = new Dictionary<string, ConceptWeight[]>(512, StringComparer.Ordinal);

        void Add(string phrase, string c1, float w1,
                 string? c2 = null, float w2 = 0f,
                 string? c3 = null, float w3 = 0f,
                 string? c4 = null, float w4 = 0f)
        {
            var weights = new List<ConceptWeight>(4) { new(IndexOf(c1), w1) };
            if (c2 is not null) weights.Add(new ConceptWeight(IndexOf(c2), w2));
            if (c3 is not null) weights.Add(new ConceptWeight(IndexOf(c3), w3));
            if (c4 is not null) weights.Add(new ConceptWeight(IndexOf(c4), w4));

            lexicon[PhraseKey(LexicalIndex.Tokenize(phrase))] = weights.ToArray();
        }

        // ── Photography: the act, the gear, the subject ──────────────────────────────────────
        Add("photography", PhotographyCapture, 1.0f);
        Add("photograph", PhotographyCapture, 0.9f);
        Add("photo", PhotographyCapture, 0.8f);
        Add("photographer", PhotographyCapture, 0.9f);
        Add("camera", PhotographyCapture, 1.0f, OpticsAndMounting, 0.3f);
        Add("mirrorless", PhotographyCapture, 1.0f, OpticsAndMounting, 0.3f);
        Add("dslr", PhotographyCapture, 1.0f);
        Add("shoot", PhotographyCapture, 0.7f);
        Add("shooting", PhotographyCapture, 0.7f);
        Add("image", PhotographyCapture, 0.5f);
        Add("picture", PhotographyCapture, 0.5f);
        Add("sensor", PhotographyCapture, 0.7f);
        Add("full frame", PhotographyCapture, 0.8f, OpticsAndMounting, 0.3f);
        Add("megapixel", PhotographyCapture, 0.6f);
        Add("viewfinder", PhotographyCapture, 0.6f);
        Add("autofocus", PhotographyCapture, 0.5f, OpticsAndMounting, 0.3f);
        Add("exposure", PhotographyCapture, 0.4f, LongExposureLightControl, 0.6f);
        Add("iso", PhotographyCapture, 0.5f, LowLightAndDawn, 0.4f);

        // Optics and the physical interfaces between things.
        Add("lens", OpticsAndMounting, 1.0f, PhotographyCapture, 0.5f);
        Add("zoom", OpticsAndMounting, 0.8f, PhotographyCapture, 0.3f);
        Add("prime lens", OpticsAndMounting, 0.9f, PhotographyCapture, 0.3f);
        Add("wide angle", OpticsAndMounting, 0.8f, LandscapePhotography, 0.6f);
        Add("telephoto", OpticsAndMounting, 0.8f);
        Add("focal length", OpticsAndMounting, 0.8f);
        Add("aperture", OpticsAndMounting, 0.7f, LowLightAndDawn, 0.3f);
        Add("mount", OpticsAndMounting, 0.9f);
        Add("e mount", OpticsAndMounting, 1.0f);
        Add("filter thread", OpticsAndMounting, 1.0f, LongExposureLightControl, 0.4f);
        Add("adapter", OpticsAndMounting, 0.6f);
        Add("tripod head", OpticsAndMounting, 0.8f, LongExposureLightControl, 0.5f);
        Add("ball head", OpticsAndMounting, 0.8f, LongExposureLightControl, 0.4f);
        Add("arca swiss", OpticsAndMounting, 0.9f);
        Add("quick release", OpticsAndMounting, 0.7f, StorageAndCarry, 0.3f);
        Add("16 35", OpticsAndMounting, 0.8f, LandscapePhotography, 0.6f);
        Add("24 70", OpticsAndMounting, 0.8f);
        Add("82 mm", OpticsAndMounting, 0.7f, LongExposureLightControl, 0.3f);
        Add("77 mm", OpticsAndMounting, 0.7f, LongExposureLightControl, 0.3f);

        // Subject matter — the words a customer uses when they describe what they photograph.
        Add("landscape", LandscapePhotography, 1.0f, PhotographyCapture, 0.4f);
        Add("scenery", LandscapePhotography, 0.9f);
        Add("vista", LandscapePhotography, 0.8f);
        Add("panorama", LandscapePhotography, 0.8f, PhotographyCapture, 0.3f);
        Add("mountain", LandscapePhotography, 0.7f, HikingTrekking, 0.6f);
        Add("alpine", LandscapePhotography, 0.5f, HikingTrekking, 0.7f, WeatherAndDurability, 0.4f);
        Add("summit", LandscapePhotography, 0.4f, HikingTrekking, 0.8f);
        Add("waterfall", LandscapePhotography, 1.0f, LongExposureLightControl, 0.9f, WeatherAndDurability, 0.3f);
        Add("river", LandscapePhotography, 0.6f, LongExposureLightControl, 0.5f);
        Add("stream", LandscapePhotography, 0.5f, LongExposureLightControl, 0.4f);
        Add("seascape", LandscapePhotography, 0.9f, LongExposureLightControl, 0.6f);
        Add("horizon", LandscapePhotography, 0.5f);
        Add("moving water", LandscapePhotography, 0.7f, LongExposureLightControl, 1.0f);
        Add("golden hour", LandscapePhotography, 0.8f, LowLightAndDawn, 0.9f, PhotographyCapture, 0.4f);
        Add("blue hour", LandscapePhotography, 0.7f, LowLightAndDawn, 0.9f);

        // Light control — the technique, and the trap word.
        Add("long exposure", LongExposureLightControl, 1.0f, LandscapePhotography, 0.5f);
        Add("neutral density", LongExposureLightControl, 1.0f, OpticsAndMounting, 0.4f);
        Add("nd filter", LongExposureLightControl, 1.0f, OpticsAndMounting, 0.5f);
        Add("nd", LongExposureLightControl, 0.7f, OpticsAndMounting, 0.3f);
        Add("10 stop", LongExposureLightControl, 1.0f);
        Add("6 stop", LongExposureLightControl, 0.9f);
        Add("stop", LongExposureLightControl, 0.4f);
        Add("shutter", LongExposureLightControl, 0.8f, PhotographyCapture, 0.4f);
        Add("slow shutter", LongExposureLightControl, 1.0f);
        Add("motion blur", LongExposureLightControl, 0.8f);
        Add("tripod", LongExposureLightControl, 0.7f, OpticsAndMounting, 0.5f, CarriedWeight, 0.5f, TravelPortability, 0.4f);
        Add("remote release", LongExposureLightControl, 0.7f, PhotographyCapture, 0.3f);
        Add("polariser", LongExposureLightControl, 0.7f, OpticsAndMounting, 0.5f);
        Add("polarizer", LongExposureLightControl, 0.7f, OpticsAndMounting, 0.5f);
        Add("circular polariser", LongExposureLightControl, 0.8f, OpticsAndMounting, 0.5f);

        // "filter" alone is genuinely ambiguous in THIS catalogue — an ND filter, a water filter,
        // and a DAC's reconstruction filter. It is authored ambiguous rather than guessed, and it
        // is kept WEAK, because the disambiguating phrases ("nd filter", "water filter",
        // "digital filter", "filter thread") are longer and therefore win and consume their tokens.
        //
        // This was not free. A first cut weighted bare "filter" at 0.35 and left the DAC's
        // "7 digital filters / reconstruction filter presets" un-phrased, so three bare hits
        // out-scored the actual water filter on a query about hard tap water — the exact
        // "filter appears in DAC specs AND in water-filter specs" trap the design names in §B.1.
        // The fix is the phrase, not a thumb on the scale.
        Add("filter", LongExposureLightControl, 0.25f, WaterFiltration, 0.25f, HomeAudio, 0.15f);
        Add("filters", LongExposureLightControl, 0.25f, WaterFiltration, 0.25f, HomeAudio, 0.15f);
        Add("digital filter", HomeAudio, 0.9f);
        Add("reconstruction filter", HomeAudio, 0.9f);
        Add("filter preset", HomeAudio, 0.8f);

        // ── Carried weight: the binding constraint that makes the cross-category link work ────
        Add("lightweight", CarriedWeight, 1.0f, TravelPortability, 0.5f);
        Add("ultralight", CarriedWeight, 1.0f, TravelPortability, 0.5f, HikingTrekking, 0.4f);
        Add("light", CarriedWeight, 0.35f, LowLightAndDawn, 0.25f);
        Add("weight", CarriedWeight, 0.8f);
        Add("gram", CarriedWeight, 0.8f);
        Add("grams", CarriedWeight, 0.8f);
        Add("kg", CarriedWeight, 0.6f);
        Add("every gram", CarriedWeight, 1.0f, HikingTrekking, 0.5f);
        Add("carried weight", CarriedWeight, 1.0f, HikingTrekking, 0.6f);
        Add("carrying everything", CarriedWeight, 0.9f, HikingTrekking, 0.9f, StorageAndCarry, 0.5f);
        Add("packable", CarriedWeight, 0.8f, TravelPortability, 0.9f, StorageAndCarry, 0.4f);
        Add("packed size", CarriedWeight, 0.6f, TravelPortability, 0.9f);
        Add("compact", CarriedWeight, 0.6f, TravelPortability, 0.7f);
        Add("folds", CarriedWeight, 0.5f, TravelPortability, 0.9f);
        Add("folded", CarriedWeight, 0.5f, TravelPortability, 0.9f);
        Add("collapsible", CarriedWeight, 0.5f, TravelPortability, 0.9f);

        // ── Travel and portability ───────────────────────────────────────────────────────────
        Add("travel", TravelPortability, 1.0f);
        Add("trip", TravelPortability, 0.9f, HikingTrekking, 0.3f);
        Add("journey", TravelPortability, 0.7f);
        Add("cabin", TravelPortability, 0.8f, StorageAndCarry, 0.4f);
        Add("carry on", TravelPortability, 0.9f, StorageAndCarry, 0.5f);
        Add("hand luggage", TravelPortability, 0.9f, StorageAndCarry, 0.5f);
        Add("packing", TravelPortability, 0.8f, StorageAndCarry, 0.5f);
        Add("portable", TravelPortability, 0.9f, CarriedWeight, 0.5f);
        Add("portability", TravelPortability, 0.9f, CarriedWeight, 0.5f);
        Add("on the move", TravelPortability, 0.8f);
        Add("abroad", TravelPortability, 0.7f);

        // ── Hiking and multi-day movement on foot ────────────────────────────────────────────
        Add("hike", HikingTrekking, 1.0f, CarriedWeight, 0.4f);
        Add("hiking", HikingTrekking, 1.0f, CarriedWeight, 0.4f);
        Add("trail", HikingTrekking, 0.9f);
        Add("trek", HikingTrekking, 1.0f, CarriedWeight, 0.4f);
        Add("trekking", HikingTrekking, 1.0f, CarriedWeight, 0.4f);
        Add("backpacking", HikingTrekking, 1.0f, StorageAndCarry, 0.7f, CarriedWeight, 0.6f);
        Add("multi day", HikingTrekking, 1.0f, CarriedWeight, 0.6f, TravelPortability, 0.5f);
        Add("hut to hut", HikingTrekking, 1.0f, CarriedWeight, 0.7f, TravelPortability, 0.4f);
        Add("hut", HikingTrekking, 0.7f);
        Add("on foot", HikingTrekking, 0.9f, CarriedWeight, 0.6f);
        Add("walking", HikingTrekking, 0.6f);
        Add("mountaineering", HikingTrekking, 0.9f, WeatherAndDurability, 0.5f);
        Add("outdoor", HikingTrekking, 0.7f, WeatherAndDurability, 0.4f);
        Add("outdoors", HikingTrekking, 0.7f, WeatherAndDurability, 0.4f);
        Add("wild camping", HikingTrekking, 0.9f, CarriedWeight, 0.5f);
        Add("overnight", HikingTrekking, 0.6f, LowLightAndDawn, 0.4f);

        // ── Weather, water and durability ────────────────────────────────────────────────────
        Add("weather sealed", WeatherAndDurability, 1.0f);
        Add("weather sealing", WeatherAndDurability, 1.0f);
        Add("weatherproof", WeatherAndDurability, 1.0f);
        Add("water resistant", WeatherAndDurability, 0.9f);
        Add("waterproof", WeatherAndDurability, 1.0f);
        Add("rain", WeatherAndDurability, 0.8f);
        Add("wet", WeatherAndDurability, 0.6f);
        Add("dust", WeatherAndDurability, 0.7f);
        Add("rugged", WeatherAndDurability, 0.9f);
        Add("durable", WeatherAndDurability, 0.8f, SustainabilityRepairability, 0.3f);
        Add("splash", WeatherAndDurability, 0.7f);
        Add("dry bag", WeatherAndDurability, 0.9f, StorageAndCarry, 0.9f, TravelPortability, 0.4f);
        Add("ip67", WeatherAndDurability, 0.9f);
        Add("ip68", WeatherAndDurability, 0.9f);

        // ── Low light, dawn, and the reason a headlamp belongs to a photography story ────────
        Add("dawn", LowLightAndDawn, 1.0f, LandscapePhotography, 0.5f);
        Add("sunrise", LowLightAndDawn, 1.0f, LandscapePhotography, 0.7f);
        Add("before sunrise", LowLightAndDawn, 1.0f, HikingTrekking, 0.5f, LandscapePhotography, 0.5f);
        Add("sunset", LowLightAndDawn, 0.9f, LandscapePhotography, 0.7f);
        Add("dusk", LowLightAndDawn, 0.9f);
        Add("night", LowLightAndDawn, 0.8f);
        Add("dark", LowLightAndDawn, 0.7f);
        Add("low light", LowLightAndDawn, 1.0f, PhotographyCapture, 0.4f);
        Add("early start", LowLightAndDawn, 0.9f, HikingTrekking, 0.6f);
        Add("alpine start", LowLightAndDawn, 1.0f, HikingTrekking, 0.8f);
        Add("headlamp", LowLightAndDawn, 1.0f, HikingTrekking, 0.6f, CarriedWeight, 0.3f);
        Add("head torch", LowLightAndDawn, 1.0f, HikingTrekking, 0.6f);
        Add("torch", LowLightAndDawn, 0.8f);
        Add("lumen", LowLightAndDawn, 0.9f);
        Add("lumens", LowLightAndDawn, 0.9f);
        Add("beam", LowLightAndDawn, 0.5f);

        // ── Power and charging ───────────────────────────────────────────────────────────────
        Add("power bank", PowerAndCharging, 1.0f, TravelPortability, 0.6f);
        Add("powerbank", PowerAndCharging, 1.0f, TravelPortability, 0.6f);
        Add("battery", PowerAndCharging, 0.9f);
        Add("spare battery", PowerAndCharging, 1.0f, PhotographyCapture, 0.4f);
        Add("charge", PowerAndCharging, 0.8f);
        Add("charging", PowerAndCharging, 0.9f);
        Add("charger", PowerAndCharging, 0.9f);
        Add("usb c", PowerAndCharging, 0.9f);
        Add("usb", PowerAndCharging, 0.7f);
        Add("watt", PowerAndCharging, 0.8f);
        Add("watts", PowerAndCharging, 0.8f);
        Add("mah", PowerAndCharging, 0.9f);
        Add("cable", PowerAndCharging, 0.7f);
        Add("power delivery", PowerAndCharging, 0.9f);
        Add("recharge", PowerAndCharging, 0.9f);
        Add("np fz100", PowerAndCharging, 0.9f, PhotographyCapture, 0.6f);

        // ── Espresso ─────────────────────────────────────────────────────────────────────────
        Add("espresso", EspressoBrewing, 1.0f);
        Add("coffee", EspressoBrewing, 0.6f, CoffeeConsumables, 0.5f);
        Add("barista", EspressoBrewing, 0.9f);
        Add("portafilter", EspressoBrewing, 1.0f);
        Add("bottomless portafilter", EspressoBrewing, 1.0f);
        Add("tamper", EspressoBrewing, 1.0f, MeasurementPrecision, 0.3f);
        Add("tamping", EspressoBrewing, 0.9f);
        Add("wdt", EspressoBrewing, 1.0f);
        Add("distribution tool", EspressoBrewing, 0.9f);
        Add("puck", EspressoBrewing, 0.9f);
        Add("group head", EspressoBrewing, 1.0f);
        Add("brew", EspressoBrewing, 0.7f);
        Add("brewing", EspressoBrewing, 0.7f);
        Add("shot", EspressoBrewing, 0.45f, PhotographyCapture, 0.35f);
        Add("espresso shot", EspressoBrewing, 1.0f);
        Add("milk", EspressoBrewing, 0.7f);
        Add("pitcher", EspressoBrewing, 0.8f);
        Add("steam wand", EspressoBrewing, 0.9f);
        Add("latte", EspressoBrewing, 0.8f);
        Add("cappuccino", EspressoBrewing, 0.8f);
        Add("crema", EspressoBrewing, 0.8f);
        Add("9 bar", EspressoBrewing, 0.8f);

        // ── Grinding — the missing-companion axis behind Sofia's capability gap ──────────────
        Add("grinder", CoffeeGrinding, 1.0f, EspressoBrewing, 0.4f);
        Add("grind", CoffeeGrinding, 0.9f);
        Add("grinding", CoffeeGrinding, 0.9f);
        Add("burr", CoffeeGrinding, 1.0f);
        Add("burrs", CoffeeGrinding, 1.0f);
        Add("conical burr", CoffeeGrinding, 1.0f);
        Add("hand grinder", CoffeeGrinding, 1.0f, TravelPortability, 0.3f);
        Add("grind size", CoffeeGrinding, 1.0f, MeasurementPrecision, 0.3f);
        Add("stepless", CoffeeGrinding, 0.7f, MeasurementPrecision, 0.3f);

        // ── Consumables and cadence ──────────────────────────────────────────────────────────
        Add("beans", CoffeeConsumables, 1.0f);
        Add("whole beans", CoffeeConsumables, 1.0f, CoffeeGrinding, 0.5f);
        Add("single origin", CoffeeConsumables, 1.0f);
        Add("roast", CoffeeConsumables, 0.9f);
        Add("roasted", CoffeeConsumables, 0.9f);
        Add("canister", CoffeeConsumables, 0.8f, StorageAndCarry, 0.4f);
        Add("descaler", CoffeeConsumables, 0.8f, MaintenanceAndCleaning, 0.9f);
        Add("descaling", CoffeeConsumables, 0.7f, MaintenanceAndCleaning, 0.9f);
        Add("cleaning tablet", CoffeeConsumables, 0.6f, MaintenanceAndCleaning, 0.9f);
        Add("cartridge", WaterFiltration, 0.9f, CoffeeConsumables, 0.3f);
        Add("cartridges", WaterFiltration, 0.9f, CoffeeConsumables, 0.3f);
        Add("refill", CoffeeConsumables, 0.7f, WaterFiltration, 0.4f);
        Add("running out", CoffeeConsumables, 0.6f, WaterFiltration, 0.4f, MaintenanceAndCleaning, 0.3f);
        Add("run out", CoffeeConsumables, 0.6f, WaterFiltration, 0.4f);
        Add("consumable", CoffeeConsumables, 0.6f, WaterFiltration, 0.4f);
        Add("subscription", CoffeeConsumables, 0.5f);

        // ── Kitchen durables ─────────────────────────────────────────────────────────────────
        Add("kitchen", KitchenAppliance, 1.0f);
        Add("blender", KitchenAppliance, 1.0f);
        Add("smoothie", KitchenAppliance, 0.8f);
        Add("food processor", KitchenAppliance, 1.0f);
        Add("kettle", KitchenAppliance, 0.9f);
        Add("toaster", KitchenAppliance, 0.9f);
        Add("appliance", KitchenAppliance, 0.8f);
        Add("appliances", KitchenAppliance, 0.8f);
        Add("cooking", KitchenAppliance, 0.7f);
        Add("worktop", KitchenAppliance, 0.6f);

        // ── Water filtration ─────────────────────────────────────────────────────────────────
        Add("water filter", WaterFiltration, 1.0f);
        Add("water filtration", WaterFiltration, 1.0f);
        // Bare "water" leans to filtration because that is what water means in THIS catalogue's
        // structured fields ("Kitchen > Water > Cartridges"). The photographic senses are longer
        // phrases and win: "moving water", "water resistant", "water bottle".
        Add("water", WaterFiltration, 0.45f);
        Add("water bottle", StorageAndCarry, 0.6f, TravelPortability, 0.3f);
        Add("limescale", WaterFiltration, 1.0f, MaintenanceAndCleaning, 0.5f);
        Add("hard water", WaterFiltration, 0.9f);
        Add("carafe", WaterFiltration, 0.9f);
        Add("jug", WaterFiltration, 0.7f);
        Add("tap water", WaterFiltration, 0.9f);
        Add("purifier", WaterFiltration, 0.9f, HikingTrekking, 0.3f);
        Add("purify", WaterFiltration, 0.9f, HikingTrekking, 0.3f);
        Add("filter cartridge", WaterFiltration, 1.0f);

        // ── Measurement ──────────────────────────────────────────────────────────────────────
        Add("scale", MeasurementPrecision, 0.9f);
        Add("scales", MeasurementPrecision, 0.9f);
        Add("brewing scale", MeasurementPrecision, 1.0f, EspressoBrewing, 0.6f);
        Add("precision", MeasurementPrecision, 0.9f);
        Add("calibration", MeasurementPrecision, 0.9f);
        Add("accurate", MeasurementPrecision, 0.7f);
        Add("accuracy", MeasurementPrecision, 0.7f);
        Add("0 1 g", MeasurementPrecision, 1.0f);
        Add("measure", MeasurementPrecision, 0.7f);
        Add("measurement", MeasurementPrecision, 0.8f);
        Add("gauge", MeasurementPrecision, 0.7f);
        Add("timer", MeasurementPrecision, 0.6f);

        // ── Gaming — the decoy that must stay reachable when the customer ASKS for it ────────
        Add("gaming", GamingConsole, 1.0f);
        Add("gamer", GamingConsole, 0.9f);
        Add("console", GamingConsole, 1.0f);
        Add("nintendo switch", GamingConsole, 1.0f);
        Add("playstation", GamingConsole, 1.0f);
        Add("xbox", GamingConsole, 1.0f);
        Add("controller", GamingConsole, 0.9f);
        Add("joy con", GamingConsole, 1.0f);
        Add("pro controller", GamingConsole, 1.0f);
        Add("gaming headset", GamingConsole, 0.9f, HomeAudio, 0.5f);
        Add("game", GamingConsole, 0.8f);
        Add("games", GamingConsole, 0.8f);
        Add("dock", GamingConsole, 0.5f, PowerAndCharging, 0.4f);

        // ── Home audio — including the "filter" overlap trap the design calls out ────────────
        Add("audio", HomeAudio, 1.0f);
        Add("hi fi", HomeAudio, 1.0f);
        Add("hifi", HomeAudio, 1.0f);
        Add("headphones", HomeAudio, 1.0f);
        Add("headphone", HomeAudio, 1.0f);
        Add("earbuds", HomeAudio, 0.9f);
        Add("speaker", HomeAudio, 0.9f);
        Add("speakers", HomeAudio, 0.9f);
        Add("amplifier", HomeAudio, 1.0f);
        Add("dac", HomeAudio, 1.0f);
        Add("sound", HomeAudio, 0.7f);
        Add("listening", HomeAudio, 0.7f);
        Add("music", HomeAudio, 0.7f);
        Add("noise cancelling", HomeAudio, 0.9f);
        Add("soundstage", HomeAudio, 0.9f);
        Add("vinyl", HomeAudio, 0.8f);

        // ── Cycling ──────────────────────────────────────────────────────────────────────────
        Add("bike", Cycling, 1.0f);
        Add("bicycle", Cycling, 1.0f);
        Add("cycling", Cycling, 1.0f);
        Add("cyclist", Cycling, 0.9f);
        Add("riding", Cycling, 0.6f);
        Add("saddle", Cycling, 0.9f);
        Add("handlebar", Cycling, 0.9f);
        Add("bar bag", Cycling, 0.8f, StorageAndCarry, 0.9f, CarriedWeight, 0.4f);
        Add("pannier", Cycling, 0.9f, StorageAndCarry, 0.7f);
        Add("frame bag", Cycling, 0.9f, StorageAndCarry, 0.7f);
        Add("bikepacking", Cycling, 1.0f, StorageAndCarry, 0.6f, CarriedWeight, 0.5f, TravelPortability, 0.4f);
        Add("helmet", Cycling, 0.8f);
        Add("gravel", Cycling, 0.7f);
        // The B-8 counterpart to "on foot" above, and the half that was silently dead: the seed's
        // mode:on-bike tag keys as "on bike" (LookupKey strips the prefix), and with no entry here
        // it projected onto NOTHING — the tag would have been authored, printed on the Use: line,
        // and read by no dimension. Deliberately asymmetric with "on foot": walking means the
        // person carries the load, so "on foot" also fires CarriedWeight; on a bicycle the bicycle
        // carries it, so this one does not.
        Add("on bike", Cycling, 0.9f);

        // ── Apparel and layering ─────────────────────────────────────────────────────────────
        Add("merino", ApparelLayering, 1.0f, HikingTrekking, 0.4f);
        Add("base layer", ApparelLayering, 1.0f, HikingTrekking, 0.4f);
        Add("wool", ApparelLayering, 0.8f);
        Add("jacket", ApparelLayering, 0.9f, WeatherAndDurability, 0.4f);
        Add("shell", ApparelLayering, 0.8f, WeatherAndDurability, 0.6f);
        Add("fleece", ApparelLayering, 0.9f);
        Add("socks", ApparelLayering, 0.8f);
        Add("gloves", ApparelLayering, 0.8f, LowLightAndDawn, 0.2f);
        Add("clothing", ApparelLayering, 0.8f);
        Add("layering", ApparelLayering, 0.9f);
        Add("insulation", ApparelLayering, 0.8f);
        Add("thermal", ApparelLayering, 0.7f);

        // ── Storage and carry ────────────────────────────────────────────────────────────────
        Add("backpack", StorageAndCarry, 1.0f, HikingTrekking, 0.6f, CarriedWeight, 0.4f);
        Add("rucksack", StorageAndCarry, 1.0f, HikingTrekking, 0.6f);
        Add("pack", StorageAndCarry, 0.8f, HikingTrekking, 0.4f);
        Add("bag", StorageAndCarry, 0.8f);
        Add("pouch", StorageAndCarry, 0.8f);
        Add("case", StorageAndCarry, 0.7f);
        Add("holster", StorageAndCarry, 0.8f, PhotographyCapture, 0.3f);
        Add("sleeve", StorageAndCarry, 0.7f);
        Add("strap", StorageAndCarry, 0.7f);
        Add("capture clip", StorageAndCarry, 0.9f, PhotographyCapture, 0.7f, HikingTrekking, 0.4f);
        Add("clip", StorageAndCarry, 0.5f);
        Add("litre", StorageAndCarry, 0.8f);
        Add("litres", StorageAndCarry, 0.8f);
        Add("liter", StorageAndCarry, 0.8f);
        Add("organiser", StorageAndCarry, 0.7f);
        Add("compartment", StorageAndCarry, 0.7f);

        // ── Maintenance ──────────────────────────────────────────────────────────────────────
        Add("cleaning", MaintenanceAndCleaning, 0.9f);
        Add("clean", MaintenanceAndCleaning, 0.7f);
        Add("maintenance", MaintenanceAndCleaning, 1.0f);
        Add("maintain", MaintenanceAndCleaning, 0.8f);
        Add("brush", MaintenanceAndCleaning, 0.7f);
        Add("spare parts", MaintenanceAndCleaning, 0.8f, SustainabilityRepairability, 0.8f);
        Add("service", MaintenanceAndCleaning, 0.6f);
        Add("lubricant", MaintenanceAndCleaning, 0.8f, Cycling, 0.3f);

        // ── Sustainability ───────────────────────────────────────────────────────────────────
        Add("recycled", SustainabilityRepairability, 1.0f);
        Add("repairable", SustainabilityRepairability, 1.0f);
        Add("repairability", SustainabilityRepairability, 1.0f);
        Add("sustainable", SustainabilityRepairability, 1.0f);
        Add("sustainability", SustainabilityRepairability, 1.0f);
        Add("bluesign", SustainabilityRepairability, 1.0f);
        Add("fsc", SustainabilityRepairability, 0.9f);
        Add("fairtrade", SustainabilityRepairability, 0.9f);
        Add("second hand", SustainabilityRepairability, 0.9f, ValueAndEntryLevel, 0.6f);
        Add("refurbished", SustainabilityRepairability, 0.8f, ValueAndEntryLevel, 0.6f);
        Add("longevity", SustainabilityRepairability, 0.8f);

        // ── Experience level and price sensitivity ───────────────────────────────────────────
        Add("beginner", ValueAndEntryLevel, 1.0f);
        Add("entry level", ValueAndEntryLevel, 1.0f);
        Add("budget", ValueAndEntryLevel, 0.9f);
        Add("affordable", ValueAndEntryLevel, 0.9f);
        Add("starter", ValueAndEntryLevel, 0.9f);
        Add("getting started", ValueAndEntryLevel, 0.9f);
        Add("value for money", ValueAndEntryLevel, 0.9f);
        Add("upgrade", ValueAndEntryLevel, 0.4f);
        Add("professional", ValueAndEntryLevel, 0.2f);

        // ── Multi-word category names, so the "Category:" line of the embedding document
        //    contributes. Single-word roots ("Photography", "Gaming", "Cycling", "Kitchen") are
        //    already covered by their entries above. ───────────────────────────────────────────
        Add("home espresso", EspressoBrewing, 1.0f);
        Add("power travel tech", PowerAndCharging, 0.9f, TravelPortability, 0.7f);
        Add("home audio", HomeAudio, 1.0f);
        Add("small appliances", KitchenAppliance, 0.9f);

        // ── D-v (plan item 8.11): the CLOSABLE half of the dead authored phrases ─────────────
        //
        // `DeadPhrasesAreDiagnosedNotJustCounted` splits the authored context phrases that embed
        // to ZERO into three buckets. The `closable` bucket is the one where the products the gold
        // rewards for that interest DO embed, so a query-side entry can reach them; the
        // `no-products` bucket is a CORPUS gap that no lexicon entry closes. These entries are the
        // closable half, and every one of them is grounded in the tags the gold is derived from —
        // not in what the phrase sounds like.
        //
        // ⚠ THREE AUTHORING RULES THIS BLOCK KEEPS, because breaking any of them would make the
        //   diagnosis move for a reason that is not the fix:
        //
        //   1. ONLY CONTENT TOKENS. "the", "a", "an", "with", "on", "and", "out", "every" and the
        //      possessive "s" appear in these phrases and are deliberately NOT added. A stop word
        //      in this lexicon would give a non-zero vector to every query containing it, which
        //      would close D-v on paper by making the measurement meaningless.
        //
        //   2. NOTHING HERE MAY TOUCH THE `no-products` BUCKET. Those six phrases turn on the
        //      tokens `weather`, `gear`, `lasts`, `morning`, `routine`, `summer`, `conditions` and
        //      `winter`. Adding any of them would stop those phrases embedding to zero and shrink
        //      the dead count WITHOUT closing anything — the corpus still carries no product for
        //      the token. That is why "training through the winter" is closed on `training` alone.
        //
        //   3. THE STEMMER IS DOING WORK AND THE SINGULAR IS THE ENTRY. `ride` covers "ride",
        //      "rides" and "riding"; `ascent` covers "ascents"; `card` covers "cards"; `session`
        //      covers "sessions"; `weigh` covers "weighing". Authoring both forms would hide a
        //      stemmer regression behind a duplicate.
        //
        // ⚠ NO EXPECTED OUTCOME IS RECORDED HERE, ON PURPOSE. An earlier item in this plan filed a
        //   figure that turned out to be three times too big, and had it been written beside the
        //   change it would have been a pre-registered result rather than a measurement.

        // all-day-riding — "all-day rides". Gold: on-bike training and bikepacking kit.
        Add("ride", Cycling, 1.0f);
        Add("all-day", Cycling, 0.4f, HikingTrekking, 0.3f);

        // card-to-edit — "getting the day's cards onto a laptop". Gold: bodies and SD-slot kit.
        Add("card", PhotographyCapture, 0.7f, StorageAndCarry, 0.3f);
        Add("laptop", PhotographyCapture, 0.4f, TravelPortability, 0.3f);

        // commute — "the daily commute". Gold: lights and travel audio, on-bike and on-foot.
        Add("commute", TravelPortability, 0.6f, Cycling, 0.4f, LowLightAndDawn, 0.3f);

        // couch-co-op — "four people on one sofa". Gold: console kit tagged living-room.
        Add("sofa", GamingConsole, 0.5f, HomeAudio, 0.4f);

        // enthusiast — "an enthusiast's standards". The experience-level axis, entered at the far
        // end from `beginner` and at the same low weight the shipped `professional` entry uses:
        // this dimension records THAT the customer stated a level, not which one.
        Add("enthusiast", ValueAndEntryLevel, 0.2f);

        // late-night-session — "late sessions without waking the flat". Gold: console kit and
        // near-field audio, both tagged living-room / desk-listening.
        Add("session", GamingConsole, 0.5f, HomeAudio, 0.5f);

        // off-grid-power — "days with no socket". Gold: USB-C PD power banks.
        Add("socket", PowerAndCharging, 1.0f);

        // self-supported — "going out self-supported". Gold: hut-to-hut and bikepacking kit.
        Add("self-supported", HikingTrekking, 0.8f, CarriedWeight, 0.4f, TravelPortability, 0.4f);

        // steep-ascents — "steep ascents". Gold: multi-day trekking kit.
        Add("ascent", HikingTrekking, 0.9f, CarriedWeight, 0.3f);
        Add("steep", HikingTrekking, 0.7f);

        // two-channel-room — "a two-channel room". Gold: amplifiers and speakers.
        Add("two-channel", HomeAudio, 1.0f);

        // weigh-every-shot — "weighing every dose and yield". Gold: espresso scales and grinders.
        Add("weigh", MeasurementPrecision, 0.9f, EspressoBrewing, 0.4f);
        Add("dose", EspressoBrewing, 0.8f, MeasurementPrecision, 0.6f, CoffeeGrinding, 0.4f);
        Add("yield", EspressoBrewing, 0.7f, MeasurementPrecision, 0.5f);

        // winter-base-miles — "training through the winter". Gold: turbo trainers and bike
        // computers tagged context:training. ⚠ `winter` is deliberately NOT added — see rule 2.
        Add("training", Cycling, 0.8f);

        return lexicon;
    }

    /// <summary>One authored (concept, weight) contribution.</summary>
    /// <param name="Index">Vector position, from <see cref="IndexOf"/>.</param>
    /// <param name="Weight">Contribution before saturation and normalisation.</param>
    private readonly record struct ConceptWeight(int Index, float Weight);
}
