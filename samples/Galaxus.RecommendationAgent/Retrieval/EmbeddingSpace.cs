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

    /// <summary>
    /// Force the real <c>text-embedding-3-small</c> space: the committed PRODUCT vectors
    /// (<see cref="PrecomputedEmbeddingSource"/>) searched with queries embedded LIVE at search
    /// time. Needs credentials; without them <see cref="EmbeddingSpace.Resolve"/> falls back to
    /// the concept space and says so.
    /// </summary>
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
/// <b>The real-vector path embeds its QUERIES LIVE, and that is the B-21 fix.</b> Until 2026-09-05
/// this selector attached no live source and the committed assets carried a second file of 71
/// pre-guessed query texts. A query composed at run time is not one of 71 guesses, so it came back
/// <c>Unavailable</c>, the dense leg ranked nothing, and <c>--real-vectors</c> produced
/// <c>0 in → 0 out</c> for every persona. Production retrieval systems do not work that way and
/// never did: the INDEX is precomputed, the QUERY is embedded when it is asked. So the query table
/// is deleted and <see cref="AzureEmbeddingSource"/> is attached here, memoised per run, once per
/// distinct text.
/// </para>
/// <para>
/// <b>The live deployment's NAME comes from the asset's own model stamp, not from configuration.</b>
/// This is the rule that makes the path work rather than a convenience. <c>Config.EmbeddingDeployment</c>
/// resolves from <c>AZURE_OPENAI_EMBEDDING_DEPLOYMENT</c>, which on the machine this was written on
/// names <c>text-embedding-ada-002</c> — a DIFFERENT space from the committed
/// <c>text-embedding-3-small</c> vectors, and, fatally, the same 1536 dimensions, so no shape check
/// can catch it. The committed index names the only embedder that can answer questions about it, and
/// so the index picks the deployment. When the configured deployment differs, that is PRINTED rather
/// than silently overridden.
/// </para>
/// <para>
/// <b>And the space is PROVEN, not assumed.</b> <see cref="Resolve"/> embeds one product's exact
/// embedding document through the live source and takes the cosine against the committed vector for
/// that same text. In the right space the expected value is 1.0 by construction — the asset holds
/// the vector for exactly that string — so <see cref="SpaceIdentityProbeFloor"/> is a tolerance for
/// float32 round-trip and provider nondeterminism, NOT a tuned threshold; in the wrong space it is
/// near zero. The measured cosine is carried on the resolution and printed in the banner, so the
/// number is never an unexamined constant. It costs one embedding call of roughly 120 tokens.
/// </para>
/// <para>
/// <b>No credentials ⇒ the concept space, loudly.</b> Real embeddings need a key. Without one the
/// real-vector path CANNOT embed a query at all, and the honest answer is not a zero vector and not
/// an index that returns nothing — it is the concept space, with the reason printed. Every banner
/// says which space produced the numbers on screen.
/// </para>
/// <para>
/// <b>A fallback is never silent.</b> When the real-vector path is asked for and cannot be stood up
/// — absent assets, a stale stamp, no key, a failed probe — <see cref="Resolve"/> returns the
/// concept source WITH the reason, and every banner prints it. A silent fallback is the failure this
/// whole file is guarding: it would let a stale or absent asset masquerade as real-vector retrieval,
/// and every number downstream would be attributed to the wrong space.
/// </para>
/// <para>
/// ⚠ <b>The real-vector path is no longer offline, and nothing pretends otherwise.</b>
/// <see cref="EmbedAsync"/> is async precisely because it may reach the network: the confidence and
/// attribution arithmetic in <c>Demo01</c> goes through this selector, so it is in the SAME space
/// that did the retrieving — which is the only arrangement under which the banner's claim about
/// "the numbers on screen" is true of all of them. The alternative, keeping those two call sites on
/// the concept space, was rejected: it would put one run's retrieval and one run's confidence in two
/// incomparable spaces, which is exactly the half-and-half state the memoisation and the
/// <see cref="Requested"/> guard below exist to prevent.
/// </para>
/// </remarks>
public static class EmbeddingSpace
{
    /// <summary>
    /// What <see cref="EmbeddingSpaceChoice.Auto"/> resolves to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>CONCEPT — and at B-21 the REASON changed completely while the answer did not.</b> That
    /// distinction is the whole of this comment, because a default kept for a reason that has been
    /// refuted is not a default, it is inertia.
    /// </para>
    /// <para>
    /// <b>The old reason is DEAD.</b> Until 2026-09-05 this constant said: real vectors would stop
    /// the dense leg running at all, because 38 of the 50 issued queries missed a 71-entry
    /// pre-guessed query table, Demo 01 fell from 6 recommendations to 0, and Eval 04 exited 1.
    /// Every one of those numbers was real and every one of them was a property of the query table,
    /// not of the vectors. The table is deleted, queries are embedded live, and the same path now
    /// retrieves. Re-measured 2026-09-05 after the fix, <c>--real-vectors</c> with credentials:
    /// ARM D of <c>AuthoredQueryPhraseRetrievability</c> reads <b>0 of 50</b> unanswerable, against
    /// 38 of 50 before and 8 of 50 in the concept space.
    /// </para>
    /// <para>
    /// <b>The new reason is REPRODUCIBILITY, and it is a different argument.</b> An <c>Auto</c> that
    /// preferred real vectors would resolve differently depending on whether a key happens to be
    /// present in the environment — so two runs of the same eval, on the same commit, would score in
    /// two incomparable spaces and neither would say which one you were reading unless you looked at
    /// the banner. That is the silent-downgrade failure this file exists to prevent, running in
    /// reverse: a silent UPGRADE is just as unattributable. The concept space is deterministic,
    /// needs no key, spends nothing, and is the same on every machine; it is the right thing for a
    /// default that a scored suite runs under.
    /// </para>
    /// <para>
    /// <b>Which of the two RETRIEVES better is a separate question, and the answer is real
    /// vectors.</b> Nothing here claims otherwise, and the flag is one keystroke. What a default may
    /// not do is decide that question differently on two machines.
    /// </para>
    /// <para>
    /// <b>This is one line to flip</b> if the project ever decides reproducibility is worth less
    /// than fidelity. It is a declared, measured change, not a quiet one.
    /// </para>
    /// </remarks>
    public const EmbeddingSpaceChoice AutoPrefers = EmbeddingSpaceChoice.ConceptVectors;

    /// <summary>
    /// Minimum cosine the space-identity probe must reach before the real-vector path is accepted.
    /// </summary>
    /// <remarks>
    /// <b>Not a tuned threshold.</b> The probe embeds one product's exact
    /// <see cref="EmbeddingDocument.ForProduct"/> text through the live source and compares it with
    /// the committed vector for that same text, so the expected value in the right space is 1.0 by
    /// construction. This number is the tolerance for a float32 round-trip through base64 and for
    /// provider nondeterminism — nothing else. In a WRONG space (an <c>ada-002</c> deployment
    /// against a <c>3-small</c> index, which no dimension check can catch because both are 1536)
    /// the cosine is near zero, so the test separates the two cases by roughly the whole range.
    /// The measured value is carried on <see cref="EmbeddingSourceResolution.SpaceIdentityCosine"/>
    /// and printed, so it is never taken on trust.
    /// </remarks>
    public const float SpaceIdentityProbeFloor = 0.98f;

    private static readonly Lock Gate = new();
    private static EmbeddingSpaceChoice _requested = EmbeddingSpaceChoice.Auto;
    private static EmbeddingSourceResolution? _resolution;
    private static IReadOnlyList<Product>? _resolvedFor;

    /// <summary>Latch for <see cref="PrintLiveSpend"/>: the figure is printed at most once per process.</summary>
    private static bool _liveSpendPrinted;

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
    /// <remarks>
    /// On the real-vector path this issues ONE live embedding call — the space-identity probe. It
    /// happens under the resolution lock, once per process, before anything retrieves; a startup
    /// probe that proves the index and the query embedder are the same space is worth strictly more
    /// than a mid-run crash or, worse, a run of confident nonsense.
    /// </remarks>
    public static EmbeddingSourceResolution Resolve(IReadOnlyList<Product> products)
    {
        ArgumentNullException.ThrowIfNull(products);

        lock (Gate)
        {
            if (_resolution is not null)
            {
                if (ReferenceEquals(_resolvedFor, products)) return _resolution;

                // A DIFFERENT catalogue after something has already resolved. Before B-21 re-running
                // ResolveCore here was merely wasteful; now it would issue a second live probe and,
                // worse, swap the process's embedding source out from under everything that had
                // already retrieved with the first one — one report, two spaces, exactly what the
                // Requested setter throws to prevent. Every call site passes Catalogue.Default.All,
                // whose backing reference is stable, so reaching this is a wiring mistake.
                throw new InvalidOperationException(
                    $"The embedding space is already resolved to '{_resolution.Chosen}' over a catalogue of "
                  + $"{_resolvedFor?.Count ?? 0} products, and a different catalogue of {products.Count} was passed. "
                  + "Resolution is per PROCESS: re-resolving would put one run's numbers in two vector spaces.");
            }

            var resolution = ResolveCore(products, _requested);

            _resolution  = resolution;
            _resolvedFor = products;
            return resolution;
        }
    }

    /// <summary>
    /// Embeds text in the resolved space.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Async because the resolved space may be LIVE.</b> This replaced a synchronous
    /// <c>EmbedOffline</c> at B-21. That method was safe only while this selector refused to attach
    /// a live path, and it was the reason the confidence arithmetic could not join the real-vector
    /// path — so the choice was between two spaces in one run and an async call chain, and the
    /// async call chain is the one that keeps the banner honest.
    /// </para>
    /// <para>
    /// Returns the UNAVAILABLE sentinel (an empty memory) when the resolved source cannot answer;
    /// callers that turn that into a cosine get 0 from
    /// <see cref="EmbeddingVectors.DotOfUnitVectors"/>, which is the correct reading — no evidence,
    /// not evidence of nothing.
    /// </para>
    /// </remarks>
    /// <param name="products">The catalogue, for <see cref="Resolve"/>.</param>
    /// <param name="text">Text to embed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static ValueTask<ReadOnlyMemory<float>> EmbedAsync(
        IReadOnlyList<Product> products,
        string text,
        CancellationToken cancellationToken = default)
        => Resolve(products).Source.EmbedAsync(text, cancellationToken);

    /// <summary>
    /// Prints what the live query path actually cost this run, and prints NOTHING on the concept
    /// path.
    /// </summary>
    /// <remarks>
    /// The banner warns before the run that <c>--real-vectors</c> spends; this closes the loop with
    /// the count afterwards. Distinct texts embedded, requests the memo absorbed, and prompt tokens
    /// read from the responses' own usage blocks — never estimated, because an estimate presented
    /// as a cost is a fabricated measurement.
    /// </remarks>
    /// <param name="indent">Leading spaces, so it lines up with the caller's own panel.</param>
    /// <remarks>
    /// ⚠ <b>PRINT-ONCE per process.</b> Both entry points call it in a <c>finally</c> so that no
    /// command can declare a cost and report none, and Demo 01 calls it inside its own panel where
    /// the figure reads best. Without this latch that demo would print the line twice, and a reader
    /// who added the two totals would double the bill. The second call is a no-op, not a second
    /// measurement.
    /// </remarks>
    public static void PrintLiveSpend(string indent = "  ")
    {
        if (Current is not { Source: PrecomputedEmbeddingSource index } || !index.HasLiveFallback) return;
        if (_liveSpendPrinted) return;
        _liveSpendPrinted = true;

        var azure = index.LiveSource as AzureEmbeddingSource;

        // azure.CallCount is every call the deployment was billed for and index.FallbackCalls is
        // the QUERY half of it; the difference is the startup space-identity probe, which is spend
        // too and is therefore named rather than folded into the query number.
        var probeCalls = azure is null ? 0 : Math.Max(0, azure.CallCount - index.FallbackCalls);

        var tokens = azure is not null
            ? $"{azure.PromptTokens} prompt token(s)"
              + (azure.CallsWithoutUsage == 0 ? string.Empty : $" — LOWER BOUND: {azure.CallsWithoutUsage} response(s) carried no usage block")
            : "token usage not reported by this source, which is NOT the same as free";

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(
            $"{indent}💸 Live embedding: {index.FallbackCalls} query call(s) for {index.FallbackCalls} distinct text(s)"
          + (probeCalls > 0 ? $" + {probeCalls} space-identity probe" : string.Empty)
          + $" · {index.LiveMemoHits} request(s) served from the per-run memo and {index.CacheHits} from the "
          + $"committed index, at no cost · {tokens} in total.");
        Console.ResetColor();
    }

    private static EmbeddingSourceResolution ResolveCore(IReadOnlyList<Product> products, EmbeddingSpaceChoice requested)
    {
        var effective = requested == EmbeddingSpaceChoice.Auto ? AutoPrefers : requested;

        if (effective == EmbeddingSpaceChoice.ConceptVectors)
        {
            var reason = requested == EmbeddingSpaceChoice.Auto
                ? "default: deterministic, no key, identical on every machine — so two runs of the same suite "
                + "cannot silently score in two spaces. Pass --real-vectors for the committed "
                + "text-embedding-3-small index with LIVE query embedding (needs credentials)."
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

        // ── Step 1: read the committed index, and its STAMP. TryLoad, never Load: an absent or
        //    stale asset is a condition to REPORT and degrade from, not a crash. No live source
        //    yet — the stamp is what decides which live source is even admissible.
        PrecomputedEmbeddingSource index;
        try
        {
            index = PrecomputedEmbeddingSource.TryLoad(products, liveFallback: null);
        }
        catch (Exception ex)
        {
            return FallBack(requested, $"the committed index could not be read at all ({ex.GetType().Name}: {ex.Message})", []);
        }

        if (index.IsEmpty)
        {
            return FallBack(requested, "the committed index loaded NO vectors", index.LoadWarnings);
        }

        // ── Step 2: credentials. A real embedding space needs a real embedder; there is no offline
        //    way to embed a query into text-embedding-3-small. Absent credentials are an ORDINARY
        //    state on this demo, so this degrades with a printed reason rather than failing.
        if (!Config.IsConfigured)
        {
            return FallBack(
                requested,
                $"the {index.CachedVectorCount} committed '{index.ModelId}' product vectors validated, but a QUERY "
              + "must be embedded at search time and AZURE_OPENAI_ENDPOINT / AZURE_OPENAI_API_KEY are not set. An "
              + "index with no way to embed a query returns nothing at all, which is worse than a different space",
                index.LoadWarnings);
        }

        // ── Step 3: the deployment NAME comes from the asset's stamp. See the class remarks: the
        //    configured deployment is 1536 dims too, so nothing downstream could catch the swap.
        var configured = Config.EmbeddingDeployment;
        var overridden = !string.Equals(configured, index.ModelId, StringComparison.Ordinal);

        if (!AzureEmbeddingSource.TryCreate(out var live, out var createReason, deployment: index.ModelId))
        {
            return FallBack(
                requested,
                $"the committed index needs a live '{index.ModelId}' query embedder and one could not be created "
              + $"({createReason})",
                index.LoadWarnings);
        }

        // ── Step 4: rebuild WITH the live source, so the loader's own model-mismatch guard runs
        //    against it rather than being trusted to be redundant.
        PrecomputedEmbeddingSource searchable;
        try
        {
            searchable = PrecomputedEmbeddingSource.TryLoad(products, liveFallback: live);
        }
        catch (Exception ex)
        {
            live!.Dispose();
            return FallBack(requested, $"the committed index could not be re-read with the live query embedder attached ({ex.GetType().Name}: {ex.Message})", []);
        }

        if (searchable.IsEmpty)
        {
            live!.Dispose();
            return FallBack(
                requested,
                "the committed index was REFUSED once the live query embedder was attached",
                searchable.LoadWarnings);
        }

        // ── Step 5: prove the space. See SpaceIdentityProbeFloor — the expected value is 1.0.
        var (probed, cosine, probeNote) = ProbeSpaceIdentity(products, searchable);
        if (!probed)
        {
            live!.Dispose();
            return FallBack(requested, probeNote, searchable.LoadWarnings);
        }

        var reasonText =
            (requested == EmbeddingSpaceChoice.Auto ? "default" : "requested on the command line (--real-vectors)")
          + $": {searchable.CachedVectorCount} committed '{searchable.ModelId}' product vectors validated, and "
          + $"QUERIES are embedded LIVE against deployment '{live!.ModelId}' at search time. "
          + $"Space identity probe: cosine {cosine:F4} against the committed vector for the same text "
          + $"(expected 1.0000, floor {SpaceIdentityProbeFloor:F2}). {probeNote}"
          + (overridden
                ? $" ⚠️  AZURE_OPENAI_EMBEDDING_DEPLOYMENT resolves to '{configured}', which was NOT used: the "
                + "committed index names the only embedder that can answer questions about it, and two "
                + "embedding models are two spaces. Rebuild the index if you want a different one."
                : string.Empty);

        return new EmbeddingSourceResolution(
            searchable,
            requested,
            EmbeddingSpaceChoice.RealVectors,
            reasonText,
            FellBack: false,
            searchable.LoadWarnings,
            searchable.CachedVectorCount)
        {
            SpaceIdentityCosine = cosine,
            LiveQueryDeployment = live.ModelId,
        };
    }

    /// <summary>
    /// Embeds one product's exact embedding document through the live path and compares it with the
    /// committed vector for that same text. In the right space this is 1.0 by construction.
    /// </summary>
    /// <remarks>
    /// This is the check a dimension assertion cannot make. <c>text-embedding-ada-002</c> and
    /// <c>text-embedding-3-small</c> both return 1536 floats, so a deployment pointed at the wrong
    /// one produces vectors of exactly the right SHAPE in exactly the wrong SPACE — and every
    /// cosine downstream is then noise wearing a plausible number. Cost: one call.
    /// </remarks>
    private static (bool Passed, double Cosine, string Note) ProbeSpaceIdentity(
        IReadOnlyList<Product> products,
        PrecomputedEmbeddingSource searchable)
    {
        var probeProduct = products.FirstOrDefault(p => p is not null);
        if (probeProduct is null) return (false, 0.0, "the catalogue is empty, so the space could not be probed");

        var document = EmbeddingDocument.ForProduct(probeProduct);

        if (!searchable.TryGetCommitted(document, out var committed))
        {
            return (false, 0.0,
                $"the committed index holds no vector for '{probeProduct.Id}', so the space could not be probed. "
              + "That means the asset and this build's document template disagree in a way the stamp did not catch");
        }

        ReadOnlyMemory<float> fresh;
        try
        {
            // Straight at the live source, never through `searchable` — that would hit the
            // committed vector and the probe would compare the asset with itself, which is the
            // artifact-supplies-its-own-input failure this project keeps a rule about.
            var pending = searchable.LiveSource!.EmbedAsync(document);
            fresh = pending.IsCompleted ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return (false, 0.0, $"the live query embedder could not be reached ({ex.GetType().Name}: {ex.Message})");
        }

        if (fresh.IsUnavailable())
        {
            return (false, 0.0, "the live query embedder returned NO vector for a product document");
        }

        var cosine = EmbeddingVectors.DotOfUnitVectors(
            EmbeddingVectors.Normalized(fresh.Span), committed.Span);

        if (cosine < SpaceIdentityProbeFloor)
        {
            return (false, cosine,
                $"the live embedder and the committed index are NOT the same space: re-embedding "
              + $"'{probeProduct.Id}'s own document scored {cosine:F4} against its committed vector, and the "
              + $"expected value in one space is 1.0000. Two spaces produce confident nonsense, not a weak signal");
        }

        return (true, cosine, $"Probed on '{probeProduct.Id}'.");
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

    /// <summary>
    /// The deployment queries are embedded against, or null on the concept path. Taken from the
    /// committed index's model stamp, never from configuration — see <see cref="EmbeddingSpace"/>.
    /// </summary>
    public string? LiveQueryDeployment { get; init; }

    /// <summary>
    /// The space-identity probe's measured cosine, or null when no probe ran. Printed rather than
    /// merely compared, so nobody has to trust <see cref="EmbeddingSpace.SpaceIdentityProbeFloor"/>
    /// to know the index and the query embedder agree.
    /// </summary>
    public double? SpaceIdentityCosine { get; init; }

    /// <summary>True when this run's queries reach a network. The concept path never does.</summary>
    public bool QueriesAreLive => LiveQueryDeployment is { Length: > 0 };

    /// <summary>One line: which space, which model, how many vectors, and whether queries go live.</summary>
    public string SummaryLine =>
        $"Embedding space: {Source.Name} ({Source.ModelId}, {Source.Dimensions} dims)"
      + (CachedVectorCount > 0 ? $" · {CachedVectorCount} committed product vectors" : string.Empty)
      + (QueriesAreLive
            ? $" · queries embedded LIVE against '{LiveQueryDeployment}'"
              + (SpaceIdentityCosine is { } cosine ? $" · space probe {cosine:F4}" : string.Empty)
            : " · queries embedded offline")
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

        if (QueriesAreLive)
        {
            // Not buried in grey with the rest: this run spends money, and a reader who believed
            // the sample's "no key needed" promise must be told the promise does not hold here.
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"{indent}  💸 This run EMBEDS QUERIES LIVE. It needs credentials and it spends — a "
                            + "fraction of a cent, but not zero. --concept-vectors is the key-free path.");
        }

        foreach (var warning in Warnings)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"{indent}  ⚠️  {warning}");
        }

        Console.ResetColor();
    }
}
