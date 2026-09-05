// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>
/// Which embedding SPACE a run retrieves in. Requested on the command line, resolved once by
/// <see cref="EmbeddingSpace"/>, and printed in every banner that shows a retrieved number.
/// </summary>
public enum EmbeddingSpaceChoice
{
    /// <summary>Let <see cref="EmbeddingSpace"/> choose. See <see cref="EmbeddingSpace.AutoPrefers"/>.</summary>
    Auto = 0,

    /// <summary>Force the committed <c>text-embedding-3-small</c> assets (<see cref="PrecomputedEmbeddingSource"/>).</summary>
    RealVectors = 1,

    /// <summary>Force the authored 24-dimension concept space (<see cref="ConceptEmbeddingSource"/>).</summary>
    ConceptVectors = 2,
}

/// <summary>
/// The ONE place that decides which <see cref="IEmbeddingSource"/> a run retrieves with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> Before it, four construction sites — Demo 01's retriever, Demo 01's
/// confidence and attribution arithmetic, <c>DiscoveryWorkflow</c> and the eval suite's composition
/// root — each named <see cref="ConceptEmbeddingSource"/> literally. Committing real vectors (B-6)
/// therefore changed nothing that runs: there was no seam to move. This is that seam, and it is a
/// SELECTOR rather than a rewrite — every one of those sites now asks the same question and gets
/// the same answer, so a run cannot be half in one embedding space and half in another.
/// </para>
/// <para>
/// <b>Two vectors from two spaces must never meet.</b> The concept space is 24 authored dimensions;
/// <c>text-embedding-3-small</c> is 1536. A cosine between them is not a weak signal, it is a
/// category error, and <see cref="EmbeddingVectors.DotOfUnitVectors"/> returns 0 for mismatched
/// lengths precisely so it cannot be computed by accident. Resolution is therefore per PROCESS,
/// memoised, and <see cref="Requested"/> refuses to change once anything has resolved.
/// </para>
/// <para>
/// <b>No live fallback is ever attached, and that is a decision, not an omission.</b>
/// <see cref="PrecomputedEmbeddingSource"/> accepts one, and attaching it here would look like
/// generosity. It would in fact do three bad things at once: (1) it would spend money on a demo
/// documented as needing no key, silently, once per uncached query; (2) the fallback's model id is
/// whatever <c>AZURE_OPENAI_EMBEDDING_DEPLOYMENT</c> happens to name — on the machine this was
/// written on, <c>text-embedding-ada-002</c> — and <see cref="PrecomputedEmbeddingSource"/> answers
/// a model mismatch by CLEARING the cache, so the committed real vectors would be discarded in
/// favour of a live space nobody asked for; (3) it would make <see cref="EmbedOffline"/> a blocking
/// network call. The offline promise is kept structurally: both resolvable sources satisfy
/// <see cref="IEmbeddingSource.IsOffline"/>, and <see cref="Resolve"/> asserts it.
/// </para>
/// <para>
/// <b>A fallback is never silent.</b> When the real-vector path is asked for and the assets cannot
/// be validated, <see cref="Resolve"/> returns the concept source WITH the reason, and every
/// banner prints it. A silent fallback is the failure this whole file is guarding: it would let a
/// stale or absent asset masquerade as real-vector retrieval, and every number downstream would be
/// attributed to the wrong space.
/// </para>
/// </remarks>
public static class EmbeddingSpace
{
    /// <summary>
    /// What <see cref="EmbeddingSpaceChoice.Auto"/> resolves to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>CONCEPT, and the reason is measured rather than preferred.</b> The committed assets hold
    /// 99 product vectors and <b>71</b> query vectors — 17 canonical prompts and the 54 authored
    /// interest phrases. The query side of a cache can only answer text somebody anticipated, and
    /// the text the arms actually search with is composed at run time: a conjunction label is a
    /// JOIN of up to three phrases, a leaf-category signal is a category name, and a live agent
    /// writes its own need. Those hash differently, miss the cache, and — with no live fallback,
    /// by design — come back <c>Unavailable</c>, which turns that search LEXICAL-ONLY.
    /// </para>
    /// <para>
    /// MEASURED on this catalogue, 2026-09-05, all of it with <c>--real-vectors</c> against the
    /// committed assets, no key and nothing spent:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>38 of the 50</b> distinct queries the scored personas' interest maps actually issue
    ///     come back <c>Unavailable</c> (ARM D of <c>AuthoredQueryPhraseRetrievability</c>). In the
    ///     concept space the same figure is 8 of 50.
    ///   </item>
    ///   <item>
    ///     Demo 01's offline arm goes from <b>6 recommendations to 0</b> for every persona that has
    ///     any — Nadia, Marco and Sofia alike. Nadia's three searches return 0 candidates each,
    ///     because <see cref="LexicalIndex"/> indexes name, brand and specs only: her six products
    ///     were the DENSE leg's, entirely. Marco's and Sofia's survive retrieval and are then
    ///     dropped as <c>low_confidence</c>, because <c>Demo01.Confidence</c> takes a cosine
    ///     against the composed label, which is not in the cache either.
    ///   </item>
    ///   <item>
    ///     Eval 04 (D-3 injection containment) <b>FAILS</b>: the poisoned listing stops reaching
    ///     the candidate set — k falls from 32-40 to 1-7 — so every arm reads INAPPLICABLE and the
    ///     eval correctly refuses to bank a clean sheet it never earned. <c>--ci --dry-run</c>
    ///     exits 1 with it.
    ///   </item>
    ///   <item>
    ///     Demo 02 survives (its planner splits a conjunction label into its component phrases,
    ///     and several of those ARE cached) but the narrative moves: Nadia's loop stops on
    ///     GapsUnresolvable instead of CoverageSufficient, which is the opposite of what the
    ///     <c>--help</c> text promises about her.
    ///   </item>
    /// </list>
    /// <para>
    /// So making real vectors the default would not "retrieve differently" — it would stop the
    /// dense leg running at all on most searches, and take the cross-category match the sample
    /// exists to demonstrate with it. The concept space embeds ANY text, deterministically, with no
    /// key; that is why it is the default and why <see cref="ConceptEmbeddingSource"/> says so
    /// about itself. None of the above is a reason to hide the real-vector path — it is the reason
    /// to make it a printed, one-flag choice rather than a silent default.
    /// </para>
    /// <para>
    /// <b>This is one line to flip.</b> When the query asset covers the composed labels — which
    /// costs a <c>--rebuild-embeddings</c> run and is a declared, measured change, not a default —
    /// this constant is the whole edit.
    /// </para>
    /// </remarks>
    public const EmbeddingSpaceChoice AutoPrefers = EmbeddingSpaceChoice.ConceptVectors;

    private static readonly Lock Gate = new();
    private static EmbeddingSpaceChoice _requested = EmbeddingSpaceChoice.Auto;
    private static EmbeddingSourceResolution? _resolution;
    private static IReadOnlyList<Product>? _resolvedFor;

    /// <summary>
    /// The space this process was ASKED for. Set once from the command line, before anything
    /// retrieves.
    /// </summary>
    /// <remarks>
    /// Setting it after <see cref="Resolve"/> has run THROWS. A process that changed space
    /// half-way would produce one report whose numbers came from two incomparable spaces, and
    /// nothing downstream could tell which line came from which.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Something has already resolved a source.</exception>
    public static EmbeddingSpaceChoice Requested
    {
        get { lock (Gate) return _requested; }
        set
        {
            lock (Gate)
            {
                if (_requested == value) return;

                if (_resolution is not null)
                {
                    throw new InvalidOperationException(
                        $"The embedding space is already resolved to '{_resolution.Chosen}'. Changing it now would " +
                        "produce one run whose numbers came from two incomparable vector spaces. Set " +
                        $"{nameof(EmbeddingSpace)}.{nameof(Requested)} from the argument parser, before any retriever is built.");
                }

                _requested = value;
            }
        }
    }

    /// <summary>The resolution in force, or null when nothing has resolved yet.</summary>
    public static EmbeddingSourceResolution? Current
    {
        get { lock (Gate) return _resolution; }
    }

    /// <summary>
    /// Resolves the embedding source for this process, loading and validating the committed assets
    /// when the real-vector path is in play. Memoised: the assets are parsed at most once.
    /// </summary>
    /// <param name="products">
    /// The catalogue. Needed because the catalogue asset is keyed by product id and each document
    /// is re-rendered at load, which is what makes a template change a cache MISS rather than a
    /// wrong vector.
    /// </param>
    /// <exception cref="InvalidOperationException">A resolved source claims not to be offline.</exception>
    public static EmbeddingSourceResolution Resolve(IReadOnlyList<Product> products)
    {
        ArgumentNullException.ThrowIfNull(products);

        lock (Gate)
        {
            if (_resolution is not null && ReferenceEquals(_resolvedFor, products)) return _resolution;

            var resolution = ResolveCore(products, _requested);

            // Structural, not aspirational: EmbedOffline() below is called synchronously from the
            // confidence arithmetic, and a source that reached the network there would block a
            // console render on a rate-limited endpoint.
            if (!resolution.Source.IsOffline)
            {
                throw new InvalidOperationException(
                    $"Embedding source '{resolution.Source.Name}' is not offline. This selector never attaches a live " +
                    "path — see the remarks on EmbeddingSpace — so reaching here means one was wired in elsewhere.");
            }

            _resolution  = resolution;
            _resolvedFor = products;
            return resolution;
        }
    }

    /// <summary>
    /// Embeds text in the resolved space, synchronously.
    /// </summary>
    /// <remarks>
    /// Safe because both resolvable sources are offline and complete synchronously — the concept
    /// source is arithmetic, and the precomputed source with no live fallback is a dictionary
    /// lookup. Returns the UNAVAILABLE sentinel (an empty memory) for text the precomputed source
    /// does not hold; callers that turn that into a cosine get 0 from
    /// <see cref="EmbeddingVectors.DotOfUnitVectors"/>, which is the correct reading — no evidence,
    /// not evidence of nothing.
    /// </remarks>
    /// <param name="products">The catalogue, for <see cref="Resolve"/>.</param>
    /// <param name="text">Text to embed.</param>
    public static ReadOnlyMemory<float> EmbedOffline(IReadOnlyList<Product> products, string text)
    {
        var resolution = Resolve(products);
        var pending    = resolution.Source.EmbedAsync(text);
        return pending.IsCompleted ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
    }

    private static EmbeddingSourceResolution ResolveCore(IReadOnlyList<Product> products, EmbeddingSpaceChoice requested)
    {
        var effective = requested == EmbeddingSpaceChoice.Auto ? AutoPrefers : requested;

        if (effective == EmbeddingSpaceChoice.ConceptVectors)
        {
            var reason = requested == EmbeddingSpaceChoice.Auto
                ? "default: the concept space embeds ANY text with no key, so a run-time-composed query still "
                + "reaches the dense leg. Pass --real-vectors for the committed text-embedding-3-small assets."
                : "requested on the command line (--concept-vectors).";

            return new EmbeddingSourceResolution(
                ConceptEmbeddingSource.Instance,
                requested,
                EmbeddingSpaceChoice.ConceptVectors,
                reason,
                FellBack: false,
                Warnings: [],
                CachedVectorCount: 0);
        }

        // TryLoad, never Load: an absent or stale asset is a condition to REPORT and degrade from,
        // not a crash. liveFallback stays null — see the remarks on this class.
        PrecomputedEmbeddingSource precomputed;
        try
        {
            precomputed = PrecomputedEmbeddingSource.TryLoad(products, liveFallback: null);
        }
        catch (Exception ex)
        {
            return FallBack(
                requested,
                $"the committed assets could not be read at all ({ex.GetType().Name}: {ex.Message})",
                []);
        }

        if (precomputed.IsEmpty)
        {
            return FallBack(
                requested,
                "the committed assets loaded NO vectors",
                precomputed.LoadWarnings);
        }

        return new EmbeddingSourceResolution(
            precomputed,
            requested,
            EmbeddingSpaceChoice.RealVectors,
            requested == EmbeddingSpaceChoice.Auto
                ? $"default: {precomputed.CachedVectorCount} committed '{precomputed.ModelId}' vectors validated."
                : $"requested on the command line (--real-vectors): {precomputed.CachedVectorCount} committed "
                + $"'{precomputed.ModelId}' vectors validated.",
            FellBack: false,
            precomputed.LoadWarnings,
            precomputed.CachedVectorCount);
    }

    private static EmbeddingSourceResolution FallBack(
        EmbeddingSpaceChoice requested,
        string why,
        IReadOnlyList<string> warnings)
        => new(
            ConceptEmbeddingSource.Instance,
            requested,
            EmbeddingSpaceChoice.ConceptVectors,
            $"the real-vector path was asked for but {why}. Falling back to the concept space — every number "
          + "below was produced by 24 authored dimensions, NOT by text-embedding-3-small.",
            FellBack: true,
            warnings,
            CachedVectorCount: 0);
}

/// <summary>
/// What <see cref="EmbeddingSpace.Resolve"/> decided, and why — everything a banner needs to say
/// which space produced the numbers on the screen.
/// </summary>
/// <param name="Source">The source every retriever and every cosine in this run must use.</param>
/// <param name="Requested">What the command line asked for.</param>
/// <param name="Chosen">What it actually got. Different from <paramref name="Requested"/> only via a fallback.</param>
/// <param name="Reason">Why, in words, never empty. Printed.</param>
/// <param name="FellBack">True when the real-vector path was wanted and could not be validated.</param>
/// <param name="Warnings">Loader warnings. Non-empty means the caller MUST print them.</param>
/// <param name="CachedVectorCount">Committed vectors loaded, or 0 on the concept path.</param>
public sealed record EmbeddingSourceResolution(
    IEmbeddingSource Source,
    EmbeddingSpaceChoice Requested,
    EmbeddingSpaceChoice Chosen,
    string Reason,
    bool FellBack,
    IReadOnlyList<string> Warnings,
    int CachedVectorCount)
{
    /// <summary>The flag that forces this space, for a banner that tells a reader how to change it.</summary>
    public string Flag => Chosen == EmbeddingSpaceChoice.RealVectors ? "--real-vectors" : "--concept-vectors";

    /// <summary>One line: which space, which model, how many vectors.</summary>
    public string SummaryLine =>
        $"Embedding space: {Source.Name} ({Source.ModelId}, {Source.Dimensions} dims)"
      + (CachedVectorCount > 0 ? $" · {CachedVectorCount} committed vectors" : string.Empty)
      + $" · {Flag}";

    /// <summary>
    /// Prints the space, its reason, and any loader warning. Yellow when a fallback happened,
    /// because a reader who asked for real vectors and got authored ones must not have to notice
    /// a grey line to find that out.
    /// </summary>
    /// <param name="indent">Leading spaces, so it lines up with the caller's own banner.</param>
    public void PrintBanner(string indent = "  ")
    {
        Console.ForegroundColor = FellBack ? ConsoleColor.Yellow : ConsoleColor.DarkGray;
        Console.WriteLine($"{indent}{SummaryLine}");
        Console.WriteLine($"{indent}  {(FellBack ? "⚠️  " : string.Empty)}{Reason}");

        foreach (var warning in Warnings)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"{indent}  ⚠️  {warning}");
        }

        Console.ResetColor();
    }
}
