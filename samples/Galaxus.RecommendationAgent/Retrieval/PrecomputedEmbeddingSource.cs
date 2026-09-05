// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>
/// The committed-asset embedding path (design §D.4): loads the real PRODUCT vectors generated once
/// by <c>--rebuild-embeddings</c>, validates the stamp on them, and embeds everything else — every
/// QUERY — through a live source at search time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolution order:</b> committed product vector → live embedding (when one was supplied) →
/// unavailable, which the retriever turns into LOUD degraded mode. There is no fourth step, and
/// specifically no hash-embedder fallback: a signed-FNV bag-of-words vector would demonstrate
/// plumbing while quietly breaking the one claim the demo exists to make.
/// </para>
/// <para>
/// <b>This class is an index with a live query path, NOT a lookup table.</b> That distinction is
/// the whole of B-21, and it was learned the expensive way. Until 2026-09-05 the committed assets
/// were TWO files — 99 product vectors and 71 <i>pre-guessed query texts</i> — and this class was
/// a <c>Dictionary&lt;string, float[]&gt;</c> lookup over both, with no live path attached. A
/// query composed at run time is not among 71 guesses, so it resolved to <c>Unavailable</c>, the
/// dense leg ranked nothing, and <c>--real-vectors</c> produced <c>0 in → 0 out</c> for every
/// persona. The product vectors were never the problem: measured 2026-09-05, queries embedded
/// LIVE against those same committed vectors give <c>"camera"</c> → Sony α7 IV at 0.372,
/// <c>"a warm jacket for hiking"</c> → Arc'teryx shell at 0.458, and Nadia's own composed label
/// <c>"multi-day trips, starts before sunrise, carried"</c> → Osprey trekking pack 0.381, Peak
/// Design travel tripod 0.365, Katadyn water filter 0.327, Petzl headlamp 0.325 — with no shared
/// keyword anywhere. One architectural mistake, not a model problem and not a corpus problem.
/// </para>
/// <para>
/// The query table and its asset are DELETED. Caching a query is caching an ANSWER to a question
/// nobody has asked yet; caching a product is caching a description of a thing that exists. Only
/// the second is a legitimate build artifact, and only the second is committed.
/// </para>
/// <para>
/// <b>The stamp is checked, not trusted.</b> Model, dimensions and
/// <see cref="EmbeddingDocument.TemplateVersion"/> must all match. If
/// <see cref="EmbeddingDocument.ForProduct"/> ever changes, the committed vectors describe text
/// that no longer exists — and retrieving against them would silently return plausible, wrong
/// neighbours. <see cref="Load"/> throws on a mismatch. <see cref="TryLoad"/> converts the throw
/// into a warning the caller must print, and returns an EMPTY source so the run degrades loudly
/// instead of retrieving against stale vectors. A live source whose model id differs from the
/// asset's stamp is refused the same way: a cache hit and a cache miss answered from two different
/// embedding spaces is worse than no cache at all.
/// </para>
/// <para>
/// <b>Within one run, one text is embedded once.</b> The live path is memoised per instance
/// (<see cref="LiveMemoHits"/> against <see cref="FallbackCalls"/>), keyed on the exact text, and
/// the memo entry is the in-flight TASK rather than its result — so two concurrent searches for
/// the same need issue one call, not two. The memo is deliberately separate from the committed
/// dictionary, so <see cref="CachedVectorCount"/> keeps describing the ASSET and never quietly
/// grows to include things this process happened to look up.
/// </para>
/// <para>
/// <b>Still not the key-free default.</b> This path needs credentials, so
/// <see cref="ConceptEmbeddingSource"/> remains what <see cref="EmbeddingSpace"/> resolves to when
/// none are present — and, per <see cref="EmbeddingSpace.AutoPrefers"/>, what it prefers even when
/// they are. That is now a reproducibility argument rather than a retrieval one; the retrieval
/// argument is gone, because this path works.
/// </para>
/// </remarks>
public sealed class PrecomputedEmbeddingSource : IEmbeddingSource
{
    private readonly Dictionary<string, float[]> _vectorsByTextHash;
    private readonly IEmbeddingSource? _fallback;

    /// <summary>
    /// Per-run memo for the LIVE path, keyed on the exact query text. Holds the in-flight task,
    /// not the finished vector, so concurrent callers asking for the same text share one call
    /// instead of racing to issue two. Kept apart from <see cref="_vectorsByTextHash"/> on
    /// purpose: that dictionary is the committed ASSET and its count is reported as such.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<ReadOnlyMemory<float>>>> _liveByText = new(StringComparer.Ordinal);

    private int _cacheHits;
    private int _cacheMisses;
    private int _fallbackCalls;
    private int _liveMemoHits;

    private PrecomputedEmbeddingSource(
        Dictionary<string, float[]> vectorsByTextHash,
        IEmbeddingSource? fallback,
        string modelId,
        int dimensions,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> loadedAssetPaths)
    {
        _vectorsByTextHash = vectorsByTextHash;
        _fallback          = fallback;
        ModelId            = modelId;
        Dimensions         = dimensions;
        LoadWarnings       = warnings;
        LoadedAssetPaths   = loadedAssetPaths;
    }

    /// <inheritdoc />
    public string Name => _fallback is null ? "precomputed" : $"precomputed+{_fallback.Name}";

    /// <inheritdoc />
    public string ModelId { get; }

    /// <inheritdoc />
    public int Dimensions { get; }

    /// <inheritdoc />
    public bool IsOffline => _fallback is null || _fallback.IsOffline;

    /// <inheritdoc />
    public float SuggestedDenseScoreFloor =>
        _fallback?.SuggestedDenseScoreFloor ?? AzureEmbeddingSource.UncalibratedDenseScoreFloor;

    /// <summary>
    /// How many PRODUCT vectors were loaded from the committed asset. Never grows during a run:
    /// live query vectors go to a separate memo, so this number always describes the file.
    /// </summary>
    public int CachedVectorCount => _vectorsByTextHash.Count;

    /// <summary>True when no vector was loaded — the assets are missing, empty or were rejected.</summary>
    public bool IsEmpty => _vectorsByTextHash.Count == 0;

    /// <summary>True when a live source is available to embed queries. False makes this path index-only, and every query Unavailable.</summary>
    public bool HasLiveFallback => _fallback is not null;

    /// <summary>
    /// The live query embedder behind the index, or null. Exposed for
    /// <see cref="EmbeddingSpace"/>'s space-identity probe, which MUST go straight at the live
    /// source: probing through this class would answer from the committed vector and compare the
    /// asset with itself.
    /// </summary>
    public IEmbeddingSource? LiveSource => _fallback;

    /// <summary>Lookups answered straight from the committed product asset, at no cost.</summary>
    public int CacheHits => Volatile.Read(ref _cacheHits);

    /// <summary>
    /// Lookups the committed asset could not answer. Since B-21 a QUERY is expected to miss —
    /// the asset holds product documents only — so this counts the live path's workload rather
    /// than staleness. A miss on a PRODUCT document is the staleness signal, and it shows up as
    /// a template-version rejection at load instead.
    /// </summary>
    public int CacheMisses => Volatile.Read(ref _cacheMisses);

    /// <summary>
    /// Live embedding calls actually ISSUED. This is spend, it is counted, and it is counted
    /// AFTER the memo — so it is the number of distinct texts this run embedded, not the number
    /// of times it asked.
    /// </summary>
    public int FallbackCalls => Volatile.Read(ref _fallbackCalls);

    /// <summary>
    /// Requests the per-run memo answered without a call. <c>FallbackCalls + LiveMemoHits</c> is
    /// how many times the live path was asked; <see cref="FallbackCalls"/> alone is what it cost.
    /// </summary>
    public int LiveMemoHits => Volatile.Read(ref _liveMemoHits);

    /// <summary>
    /// Problems found while loading — a missing asset, a stamp mismatch, a bad vector. Non-empty
    /// means the caller MUST print them: a silently half-loaded cache is the failure this class
    /// exists to prevent.
    /// </summary>
    public IReadOnlyList<string> LoadWarnings { get; }

    /// <summary>Which asset files were actually read.</summary>
    public IReadOnlyList<string> LoadedAssetPaths { get; }

    /// <summary>
    /// Loads the committed assets, THROWING on any stamp mismatch.
    /// </summary>
    /// <param name="products">
    /// The catalogue. The catalogue asset is keyed by product id, so each product's document is
    /// re-rendered here and its hash mapped onto the stored vector. That re-rendering is what makes
    /// a template change detectable rather than silent.
    /// </param>
    /// <param name="liveFallback">Live source for every text the asset does not hold — i.e. every QUERY. Null means index-or-degrade.</param>
    /// <param name="assetPaths">Asset files to load. Null loads the canonical product asset from <c>Data/</c>.</param>
    /// <exception cref="InvalidOperationException">An asset's model, dimensions or template version does not match.</exception>
    public static PrecomputedEmbeddingSource Load(
        IReadOnlyList<Product> products,
        IEmbeddingSource? liveFallback = null,
        IEnumerable<string>? assetPaths = null)
    {
        var source = LoadCore(products, liveFallback, assetPaths, throwOnMismatch: true);
        return source;
    }

    /// <summary>
    /// Non-throwing <see cref="Load"/>: a stamp mismatch or a missing asset becomes a warning in
    /// <see cref="LoadWarnings"/> and an empty cache, so the demo path degrades loudly rather than
    /// crashing — and never retrieves against vectors it could not validate.
    /// </summary>
    /// <param name="products">The catalogue.</param>
    /// <param name="liveFallback">Live source for every text the asset does not hold — i.e. every QUERY. Null means index-or-degrade.</param>
    /// <param name="assetPaths">Asset files to load. Null loads the canonical product asset from <c>Data/</c>.</param>
    public static PrecomputedEmbeddingSource TryLoad(
        IReadOnlyList<Product> products,
        IEmbeddingSource? liveFallback = null,
        IEnumerable<string>? assetPaths = null)
        => LoadCore(products, liveFallback, assetPaths, throwOnMismatch: false);

    /// <inheritdoc />
    /// <remarks>
    /// A product document hits the committed asset and costs nothing. Anything else — every query
    /// — goes to the live source ONCE per distinct text per instance, and to the memo thereafter.
    /// With no live source attached, anything else is <c>Unavailable</c>, which
    /// <see cref="HybridRetriever"/> reports as degraded rather than as an empty result.
    /// </remarks>
    public async ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return EmbeddingVectors.Unavailable;

        var key = EmbeddingDocument.HashQuery(text);
        if (_vectorsByTextHash.TryGetValue(key, out var cached))
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }

        Interlocked.Increment(ref _cacheMisses);

        if (_fallback is null) return EmbeddingVectors.Unavailable;

        if (_liveByText.TryGetValue(text, out var memoised))
        {
            Interlocked.Increment(ref _liveMemoHits);
            return await memoised.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        // ExecutionAndPublication, and the value is the TASK: two threads that arrive together on
        // the same text run the factory once and await the same call. Storing the finished vector
        // instead would let both issue one, which is exactly the "embedded twice" this exists to
        // stop.
        var entry = _liveByText.GetOrAdd(
            text,
            t => new Lazy<Task<ReadOnlyMemory<float>>>(
                () => EmbedLiveAsync(t), LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            // WaitAsync, not a token passed into the call: the memo is shared, and one caller
            // cancelling must not cancel the vector every other caller is waiting for.
            return await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A faulted entry would make one transient 429 permanent for the rest of the run.
            // Cancellation of THIS caller leaves the entry alone — the call is still in flight.
            if (entry.Value.IsFaulted)
            {
                _liveByText.TryRemove(new KeyValuePair<string, Lazy<Task<ReadOnlyMemory<float>>>>(text, entry));
            }
            throw;
        }
    }

    /// <summary>
    /// One live embedding call, dimension-checked against the committed asset.
    /// </summary>
    /// <remarks>
    /// The dimension check is the guard that a query and the index it is searched against are the
    /// same length. It is NOT a guard that they are the same SPACE —
    /// <c>text-embedding-ada-002</c> and <c>text-embedding-3-small</c> are both 1536 — which is why
    /// <see cref="EmbeddingSpace"/> takes the live deployment's NAME from the asset's own model
    /// stamp and then proves the space with an identity probe before any of this runs.
    /// </remarks>
    private async Task<ReadOnlyMemory<float>> EmbedLiveAsync(string text)
    {
        Interlocked.Increment(ref _fallbackCalls);

        var vector = await _fallback!.EmbedAsync(text, CancellationToken.None).ConfigureAwait(false);

        if (!vector.IsUnavailable() && Dimensions > 0 && vector.Length != Dimensions)
        {
            throw new InvalidOperationException(
                $"Live embedding source '{_fallback.Name}' returned {vector.Length} dimensions but the " +
                $"committed product index holds {Dimensions}. Mixing two embedding spaces in one index " +
                "produces confident nonsense — regenerate the assets against the live deployment.");
        }

        return vector;
    }

    /// <summary>
    /// Reads a vector straight out of the committed asset, without the live path. The seam
    /// <see cref="EmbeddingSpace"/>'s identity probe uses to compare a freshly embedded product
    /// document against the committed vector for that same text.
    /// </summary>
    /// <param name="text">Exact text, normally an <see cref="EmbeddingDocument.ForProduct"/> render.</param>
    /// <param name="vector">The committed unit vector, when present.</param>
    public bool TryGetCommitted(string text, out ReadOnlyMemory<float> vector)
    {
        if (!string.IsNullOrWhiteSpace(text) &&
            _vectorsByTextHash.TryGetValue(EmbeddingDocument.HashQuery(text), out var found))
        {
            vector = found;
            return true;
        }

        vector = EmbeddingVectors.Unavailable;
        return false;
    }

    /// <summary>
    /// Finds an asset by file name: embedded resource first, then <c>Data/</c> beside the binary,
    /// then <c>Data/</c> in each parent directory up to the repository root, then the working
    /// directory. Returns null when the asset does not exist anywhere — which since B-6
    /// (2026-09-05) means the assets were deleted or the resource was not embedded, not that they
    /// were never generated.
    /// </summary>
    /// <param name="fileName">e.g. <c>"catalogue.embeddings.json"</c>.</param>
    public static string? ResolveAssetPath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Data", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "Data", fileName),
        };

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 6 && directory is not null; depth++)
        {
            candidates.Add(Path.Combine(directory.FullName, "Data", fileName));
            directory = directory.Parent;
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// Opens an asset as a stream, preferring an embedded resource so there is no output-path
    /// resolution to get wrong once the two <c>EmbeddedResource</c> entries are restored to the csproj.
    /// </summary>
    /// <param name="fileName">e.g. <c>"catalogue.embeddings.json"</c>.</param>
    /// <param name="stream">The opened stream; the caller disposes it.</param>
    /// <param name="describedAs">Where it came from, for warnings and diagnostics.</param>
    public static bool TryOpenAsset(string fileName, out Stream? stream, out string describedAs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)) continue;

            var resourceStream = assembly.GetManifestResourceStream(resource);
            if (resourceStream is null) continue;

            stream      = resourceStream;
            describedAs = $"embedded resource '{resource}'";
            return true;
        }

        var path = ResolveAssetPath(fileName);
        if (path is not null)
        {
            stream      = File.OpenRead(path);
            describedAs = path;
            return true;
        }

        stream      = null;
        describedAs = fileName;
        return false;
    }

    private static PrecomputedEmbeddingSource LoadCore(
        IReadOnlyList<Product> products,
        IEmbeddingSource? liveFallback,
        IEnumerable<string>? assetPaths,
        bool throwOnMismatch)
    {
        ArgumentNullException.ThrowIfNull(products);

        var vectors  = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var loaded   = new List<string>();

        string modelId    = liveFallback?.ModelId ?? "none";
        int    dimensions = liveFallback?.Dimensions ?? 0;
        bool   stampSeen  = false;

        // ONE asset: the product vectors. The query asset that used to sit beside it was deleted
        // at B-21 — see the remarks on this class for why a pre-guessed query table is a bug
        // rather than an asset.
        var names = assetPaths?.ToArray() ?? [EmbeddingCacheBuilder.CatalogueAssetFileName];

        foreach (var name in names)
        {
            Stream? stream = null;
            string described;

            try
            {
                if (File.Exists(name))
                {
                    stream    = File.OpenRead(name);
                    described = name;
                }
                else if (!TryOpenAsset(Path.GetFileName(name), out stream, out described))
                {
                    warnings.Add(
                        $"Embedding asset '{name}' was not found. This is expected in a build that has never " +
                        "run --rebuild-embeddings; the retriever will use its configured source instead.");
                    continue;
                }

                var file = ParseAsset(stream!, described);

                if (!string.Equals(file.DocumentTemplateVersion, EmbeddingDocument.TemplateVersion, StringComparison.Ordinal))
                {
                    var message =
                        $"Embedding asset '{described}' was generated from document template " +
                        $"'{file.DocumentTemplateVersion}' but this build renders '{EmbeddingDocument.TemplateVersion}'. " +
                        "The vectors describe text that no longer exists — regenerate with --rebuild-embeddings. " +
                        "REFUSING to load them (retrieving against stale vectors returns plausible, wrong neighbours).";

                    if (throwOnMismatch) throw new InvalidOperationException(message);
                    warnings.Add(message);
                    continue;
                }

                if (stampSeen && !string.Equals(file.Model, modelId, StringComparison.Ordinal))
                {
                    var message =
                        $"Embedding asset '{described}' holds '{file.Model}' vectors but '{modelId}' was already " +
                        "loaded. Two embedding models are two different spaces and must never share one index.";

                    if (throwOnMismatch) throw new InvalidOperationException(message);
                    warnings.Add(message);
                    continue;
                }

                if (dimensions > 0 && file.Dimensions != dimensions)
                {
                    var message =
                        $"Embedding asset '{described}' holds {file.Dimensions}-dimensional vectors but " +
                        $"{dimensions} was expected.";

                    if (throwOnMismatch) throw new InvalidOperationException(message);
                    warnings.Add(message);
                    continue;
                }

                modelId    = file.Model;
                dimensions = file.Dimensions;
                stampSeen  = true;

                // Since B-21 the ONLY legitimate keying is by product id. A query-keyed asset is
                // the deleted pre-guessed query table, and loading one would silently re-create
                // the bug B-21 removed: run-time-composed queries would still miss it, but the
                // handful that happened to hit would be answered from a stale snapshot instead of
                // from the live embedder. Refused, loudly, rather than partially honoured.
                if (!string.Equals(file.Keying, EmbeddingCacheFile.KeyingProductId, StringComparison.Ordinal))
                {
                    var message =
                        $"Embedding asset '{described}' is keyed '{file.Keying}', not " +
                        $"'{EmbeddingCacheFile.KeyingProductId}'. This loader accepts PRODUCT vectors only — a " +
                        "query-vector asset is the pre-guessed query table deleted at B-21, and queries are now " +
                        "embedded live at search time. REFUSING to load it.";

                    if (throwOnMismatch) throw new InvalidOperationException(message);
                    warnings.Add(message);
                    continue;
                }

                var productsById = IndexProducts(products);

                foreach (var (key, encoded) in file.Vectors)
                {
                    float[] vector;
                    try
                    {
                        vector = EmbeddingCacheFile.DecodeVector(encoded);
                    }
                    catch (FormatException ex)
                    {
                        warnings.Add($"Embedding asset '{described}' has an undecodable vector for key '{key}': {ex.Message}");
                        continue;
                    }

                    if (vector.Length != file.Dimensions)
                    {
                        warnings.Add(
                            $"Embedding asset '{described}' key '{key}' decoded to {vector.Length} floats, " +
                            $"not {file.Dimensions}. Skipped.");
                        continue;
                    }

                    EmbeddingVectors.NormalizeInPlace(vector);

                    // The document is re-rendered HERE and keyed by its hash, so a template change
                    // shows up as a cache miss rather than as a wrong vector.
                    if (!productsById.TryGetValue(key, out var product))
                    {
                        warnings.Add($"Embedding asset '{described}' carries a vector for unknown product '{key}'. Skipped.");
                        continue;
                    }

                    vectors[EmbeddingDocument.HashQuery(EmbeddingDocument.ForProduct(product))] = vector;
                }

                loaded.Add(described);
            }
            catch (JsonException ex)
            {
                var message = $"Embedding asset '{name}' is not valid JSON: {ex.Message}";
                if (throwOnMismatch) throw new InvalidOperationException(message, ex);
                warnings.Add(message);
            }
            catch (IOException ex)
            {
                var message = $"Embedding asset '{name}' could not be read: {ex.Message}";
                if (throwOnMismatch) throw new InvalidOperationException(message, ex);
                warnings.Add(message);
            }
            finally
            {
                stream?.Dispose();
            }
        }

        if (liveFallback is not null &&
            stampSeen &&
            !string.Equals(modelId, liveFallback.ModelId, StringComparison.Ordinal))
        {
            var message =
                $"The committed product vectors are '{modelId}' but the live query embedder is " +
                $"'{liveFallback.ModelId}'. The index and the queries searched against it would then be in two " +
                "different embedding spaces, which produces confident nonsense rather than a weak signal. " +
                "REFUSING to load them — the caller must fall back and say so.";

            if (throwOnMismatch) throw new InvalidOperationException(message);
            warnings.Add(message);

            // Cleared, so IsEmpty is true and the caller degrades LOUDLY. Note what is NOT done
            // here: the good product vectors are not kept and quietly queried with the wrong
            // embedder. Two spaces never meet, even at the cost of the whole path.
            vectors.Clear();
            modelId    = liveFallback.ModelId;
            dimensions = liveFallback.Dimensions;
        }

        return new PrecomputedEmbeddingSource(vectors, liveFallback, modelId, dimensions, warnings, loaded);
    }

    private static EmbeddingCacheFile ParseAsset(Stream stream, string described)
    {
        var file = JsonSerializer.Deserialize<EmbeddingCacheFile>(stream, EmbeddingCacheFile.JsonOptions);
        if (file is null)
        {
            throw new InvalidOperationException($"Embedding asset '{described}' deserialised to null.");
        }
        return file;
    }

    private static Dictionary<string, Product> IndexProducts(IReadOnlyList<Product> products)
    {
        var byId = new Dictionary<string, Product>(products.Count, StringComparer.Ordinal);
        foreach (var product in products)
        {
            if (product is not null) byId[product.Id] = product;
        }
        return byId;
    }
}

/// <summary>
/// The on-disk shape of a committed embedding asset (design §D.4). Base64 float32 little-endian,
/// which is exact — there is no quantisation and therefore no "did the cache change recall?" question.
/// </summary>
/// <remarks>
/// The three stamp fields exist so a stale asset fails LOUDLY: <see cref="Model"/> pins the
/// embedding space, <see cref="Dimensions"/> pins the vector length, and
/// <see cref="DocumentTemplateVersion"/> pins the text the vectors were computed from.
/// </remarks>
public sealed record EmbeddingCacheFile
{
    /// <summary><see cref="Keying"/> value for the catalogue asset: keys are product ids.</summary>
    public const string KeyingProductId = "product-id";

    /// <summary><see cref="Keying"/> value for the query asset: keys are SHA-256 hashes of normalised query text.</summary>
    public const string KeyingQuerySha256 = "query-sha256";

    /// <summary>Serializer options shared by the loader and the builder, so one file round-trips exactly.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The embedding model these vectors came from, e.g. <c>"text-embedding-3-small"</c>.</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>Vector length.</summary>
    [JsonPropertyName("dimensions")]
    public required int Dimensions { get; init; }

    /// <summary>The <see cref="EmbeddingDocument.TemplateVersion"/> the source text was rendered with.</summary>
    [JsonPropertyName("documentTemplateVersion")]
    public required string DocumentTemplateVersion { get; init; }

    /// <summary>ISO-8601 UTC timestamp of generation. Provenance, not logic.</summary>
    [JsonPropertyName("generatedUtc")]
    public required string GeneratedUtc { get; init; }

    /// <summary>How <see cref="Vectors"/> is keyed: <see cref="KeyingProductId"/> or <see cref="KeyingQuerySha256"/>.</summary>
    [JsonPropertyName("keying")]
    public string Keying { get; init; } = KeyingProductId;

    /// <summary>Key to base64 float32 little-endian vector.</summary>
    [JsonPropertyName("vectors")]
    public required IReadOnlyDictionary<string, string> Vectors { get; init; }

    /// <summary>Encodes a vector as base64 float32 little-endian — exact, and about 5.7 bytes per dimension.</summary>
    /// <param name="vector">The vector.</param>
    public static string EncodeVector(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (int i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Decodes a base64 float32 little-endian vector.</summary>
    /// <param name="encoded">Base64 text.</param>
    /// <exception cref="FormatException">The text is not base64, or its length is not a multiple of 4.</exception>
    public static float[] DecodeVector(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);

        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new FormatException(
                $"Vector payload is {bytes.Length} bytes, which is not a whole number of float32 values.");
        }

        var vector = new float[bytes.Length / sizeof(float)];
        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        }
        return vector;
    }
}
