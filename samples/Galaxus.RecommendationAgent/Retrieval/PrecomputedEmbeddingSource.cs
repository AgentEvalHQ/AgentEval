// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>
/// The committed-asset embedding path (design §D.4): loads real vectors generated once by
/// <c>--rebuild-embeddings</c>, validates the stamp on them, and falls through to a live source
/// on a cache miss.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolution order</b>, exactly as §D.4 states it: precomputed vector → live embedding (when
/// one was supplied and credentials exist) → unavailable, which the retriever turns into
/// LOUD degraded mode. There is no fourth step, and specifically no hash-embedder fallback: a
/// signed-FNV bag-of-words vector would demonstrate plumbing while quietly breaking the one claim
/// the demo exists to make.
/// </para>
/// <para>
/// <b>The stamp is checked, not trusted.</b> Model, dimensions and
/// <see cref="EmbeddingDocument.TemplateVersion"/> must all match. If
/// <see cref="EmbeddingDocument.ForProduct"/> ever changes, the committed vectors describe text
/// that no longer exists — and retrieving against them would silently return plausible, wrong
/// neighbours. <see cref="Load"/> throws on a mismatch. <see cref="TryLoad"/> converts the throw
/// into a warning the caller must print, and returns an EMPTY source so the run degrades loudly
/// instead of retrieving against stale vectors.
/// </para>
/// <para>
/// <b>A known limit, said plainly.</b> The query cache can only hold queries someone anticipated.
/// The needs the agent actually searches with are model-generated at run time, so a novel need is
/// a cache miss by construction. With credentials it falls through to the live source; without
/// them it degrades. That is why <see cref="ConceptEmbeddingSource"/>, not this class, is the
/// offline default: it can embed anything, deterministically, with no key.
/// </para>
/// </remarks>
public sealed class PrecomputedEmbeddingSource : IEmbeddingSource
{
    private readonly Dictionary<string, float[]> _vectorsByTextHash;
    private readonly IEmbeddingSource? _fallback;
    private int _cacheHits;
    private int _cacheMisses;
    private int _fallbackCalls;

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

    /// <summary>How many vectors were loaded from the committed assets.</summary>
    public int CachedVectorCount => _vectorsByTextHash.Count;

    /// <summary>True when no vector was loaded — the assets are missing, empty or were rejected.</summary>
    public bool IsEmpty => _vectorsByTextHash.Count == 0;

    /// <summary>True when a live source is available behind the cache.</summary>
    public bool HasLiveFallback => _fallback is not null;

    /// <summary>Cache hits so far.</summary>
    public int CacheHits => Volatile.Read(ref _cacheHits);

    /// <summary>Cache misses so far. A high number against a committed asset means the asset is stale in practice.</summary>
    public int CacheMisses => Volatile.Read(ref _cacheMisses);

    /// <summary>Calls that fell through to the live source. This is spend, and it is counted.</summary>
    public int FallbackCalls => Volatile.Read(ref _fallbackCalls);

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
    /// <param name="liveFallback">Optional live source used on a cache miss. Null means cache-or-degrade.</param>
    /// <param name="assetPaths">Asset files to load. Null loads the two canonical assets from <c>Data/</c>.</param>
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
    /// <param name="liveFallback">Optional live source used on a cache miss.</param>
    /// <param name="assetPaths">Asset files to load. Null loads the two canonical assets from <c>Data/</c>.</param>
    public static PrecomputedEmbeddingSource TryLoad(
        IReadOnlyList<Product> products,
        IEmbeddingSource? liveFallback = null,
        IEnumerable<string>? assetPaths = null)
        => LoadCore(products, liveFallback, assetPaths, throwOnMismatch: false);

    /// <inheritdoc />
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

        Interlocked.Increment(ref _fallbackCalls);
        var vector = await _fallback.EmbedAsync(text, cancellationToken).ConfigureAwait(false);

        if (!vector.IsUnavailable() && Dimensions > 0 && vector.Length != Dimensions)
        {
            throw new InvalidOperationException(
                $"Live embedding source '{_fallback.Name}' returned {vector.Length} dimensions but the " +
                $"precomputed cache holds {Dimensions}. Mixing two embedding spaces in one index " +
                "produces confident nonsense — regenerate the assets against the live deployment.");
        }

        return vector;
    }

    /// <summary>
    /// Finds an asset by file name: embedded resource first, then <c>Data/</c> beside the binary,
    /// then <c>Data/</c> in each parent directory up to the repository root, then the working
    /// directory. Returns null when the asset does not exist anywhere — which is the expected
    /// state in this build, since real vectors cannot be generated offline.
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
    /// <param name="fileName">e.g. <c>"queries.embeddings.json"</c>.</param>
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

        var names = assetPaths?.ToArray()
                    ?? [EmbeddingCacheBuilder.CatalogueAssetFileName, EmbeddingCacheBuilder.QueriesAssetFileName];

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

                var isProductKeyed = string.Equals(file.Keying, EmbeddingCacheFile.KeyingProductId, StringComparison.Ordinal);
                var productsById   = isProductKeyed ? IndexProducts(products) : null;

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

                    if (productsById is not null)
                    {
                        // Product-keyed asset: re-render the document HERE and key by its hash, so a
                        // template change shows up as a cache miss rather than as a wrong vector.
                        if (!productsById.TryGetValue(key, out var product))
                        {
                            warnings.Add($"Embedding asset '{described}' carries a vector for unknown product '{key}'. Skipped.");
                            continue;
                        }

                        vectors[EmbeddingDocument.HashQuery(EmbeddingDocument.ForProduct(product))] = vector;
                    }
                    else
                    {
                        // Query-keyed asset: the key IS the SHA-256 of the normalised query text.
                        vectors[key] = vector;
                    }
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
                $"The committed vectors are '{modelId}' but the live fallback is '{liveFallback.ModelId}'. " +
                "A cache hit and a cache miss would then be answered from two different embedding spaces, " +
                "which is worse than no cache at all.";

            if (throwOnMismatch) throw new InvalidOperationException(message);
            warnings.Add(message);
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
