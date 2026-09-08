// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>
/// The LIVE embedding path (design §D.4): real <c>text-embedding-3-small</c> vectors from the
/// configured Azure OpenAI deployment.
/// </summary>
/// <remarks>
/// <para>
/// This is the source <c>--rebuild-embeddings</c> runs against to produce the committed assets,
/// and the fallback <see cref="PrecomputedEmbeddingSource"/> reaches for on a cache miss when
/// credentials are present. It is NOT the default demo path — the default must run with no key
/// and no network, which is what <see cref="ConceptEmbeddingSource"/> is for.
/// </para>
/// <para>
/// <b>Dimension is asserted, not assumed.</b> <see cref="Dimensions"/> starts at the declared
/// value for the configured deployment and the FIRST response is checked against it. A
/// deployment quietly pointed at <c>text-embedding-3-large</c> would otherwise produce 3072-dim
/// vectors that fail far away from here, inside a cosine loop, as an empty result set rather
/// than an error.
/// </para>
/// </remarks>
public sealed class AzureEmbeddingSource : IEmbeddingSource, IDisposable
{
    /// <summary>Vector length of <c>text-embedding-3-small</c>.</summary>
    public const int TextEmbedding3SmallDimensions = 1536;

    /// <summary>
    /// Dense cosine floor suggested for OpenAI text-embedding cosines.
    /// <b>TO-CALIBRATE — do not present this as measured.</b> The defensible answer is the
    /// calibration METHOD, not this number: build a gold set from the four personas (5–8 known-good
    /// product ids each), sweep the floor, pick the value maximising recall@24 subject to
    /// precision@8, then report both plus the abstention rate it induces — and remember that a
    /// sweep only LOCATES; a held-out check resolves.
    /// </summary>
    public const float UncalibratedDenseScoreFloor = 0.28f;

    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly bool _ownsGenerator;
    private int _dimensions;
    private int _callCount;
    private long _promptTokens;
    private int _callsWithoutUsage;

    /// <summary>
    /// Wraps an existing MEAI embedding generator — the seam the eval project uses to inject a
    /// recorded or stubbed generator without touching this class.
    /// </summary>
    /// <param name="generator">The generator to call.</param>
    /// <param name="modelId">Model identifier stamped into generated assets, e.g. <c>"text-embedding-3-small"</c>.</param>
    /// <param name="declaredDimensions">Expected vector length; asserted against the first response.</param>
    /// <param name="ownsGenerator">When true, <see cref="Dispose"/> disposes the generator.</param>
    public AzureEmbeddingSource(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        string modelId,
        int declaredDimensions = TextEmbedding3SmallDimensions,
        bool ownsGenerator = false)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(declaredDimensions);

        _generator     = generator;
        _ownsGenerator = ownsGenerator;
        _dimensions    = declaredDimensions;
        ModelId        = modelId;
    }

    /// <inheritdoc />
    public string Name => "azure";

    /// <inheritdoc />
    public string ModelId { get; }

    /// <inheritdoc />
    public int Dimensions => _dimensions;

    /// <inheritdoc />
    public bool IsOffline => false;

    /// <inheritdoc />
    /// <remarks>
    /// ✅ <b>DERIVED for the real space</b> — see <see cref="CalibratedThresholds.RealVectors"/>.
    /// This source embeds the QUERIES that search the committed <c>text-embedding-3-small</c> index,
    /// so its floor is that index's floor and not a second opinion about it.
    /// </remarks>
    public float SuggestedDenseScoreFloor => CalibratedThresholds.RealVectors.DenseScoreFloor;

    /// <summary>How many embedding calls this source has issued. Printed by <c>--rebuild-embeddings</c>; it is spend.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>
    /// Prompt tokens billed so far, summed from each response's own usage block. This is the
    /// number an invoice is computed from, so it is READ FROM THE RESPONSE rather than estimated
    /// from character counts — a four-characters-per-token rule of thumb forecast 13 278 tokens for
    /// the B-6 rebuild against a billed 13 383 (0.8 % low over 170 calls, 7 % low on the single
    /// document it was checked against), and an estimate presented as a cost is a fabricated
    /// measurement.
    /// </summary>
    public long PromptTokens => Interlocked.Read(ref _promptTokens);

    /// <summary>
    /// Calls whose response carried NO usage block. Non-zero means <see cref="PromptTokens"/> is a
    /// LOWER BOUND, not a total — which the caller must be able to say out loud rather than quietly
    /// under-report the spend.
    /// </summary>
    public int CallsWithoutUsage => Volatile.Read(ref _callsWithoutUsage);

    /// <summary>True once a response has been seen and <see cref="Dimensions"/> is a fact rather than a declaration.</summary>
    public bool DimensionsConfirmed { get; private set; }

    /// <summary>
    /// Creates a source against the configured Azure OpenAI deployment.
    /// </summary>
    /// <param name="deployment">Embedding deployment name; null uses <see cref="Config.EmbeddingDeployment"/>.</param>
    /// <exception cref="InvalidOperationException">Azure OpenAI is not configured.</exception>
    public static AzureEmbeddingSource Create(string? deployment = null)
    {
        if (!Config.IsConfigured)
        {
            throw new InvalidOperationException(
                "Azure OpenAI is not configured (AZURE_OPENAI_ENDPOINT / AZURE_OPENAI_API_KEY). " +
                "The offline retrieval path needs no key — use ConceptEmbeddingSource instead.");
        }

        var model       = string.IsNullOrWhiteSpace(deployment) ? Config.EmbeddingDeployment : deployment.Trim();
        var azureClient = new AzureOpenAIClient(Config.Endpoint, Config.KeyCredential);

        IEmbeddingGenerator<string, Embedding<float>> generator =
            azureClient.GetEmbeddingClient(model).AsIEmbeddingGenerator();

        return new AzureEmbeddingSource(generator, model, DeclaredDimensionsFor(model), ownsGenerator: true);
    }

    /// <summary>
    /// Non-throwing <see cref="Create"/>. Used on the demo path, where "no credentials" is an
    /// ordinary, expected state that must degrade loudly rather than crash.
    /// </summary>
    /// <param name="source">The created source, or null.</param>
    /// <param name="reason">Why creation failed, or null on success.</param>
    /// <param name="deployment">Embedding deployment name; null uses <see cref="Config.EmbeddingDeployment"/>.</param>
    public static bool TryCreate(out AzureEmbeddingSource? source, out string? reason, string? deployment = null)
    {
        if (!Config.IsConfigured)
        {
            source = null;
            reason = "AZURE_OPENAI_ENDPOINT / AZURE_OPENAI_API_KEY are not set.";
            return false;
        }

        try
        {
            source = Create(deployment);
            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or UriFormatException)
        {
            source = null;
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The published vector length for a known embedding deployment. Unknown deployments fall back
    /// to the small model's length and are corrected by the first response.
    /// </summary>
    /// <param name="deployment">Deployment or model name.</param>
    public static int DeclaredDimensionsFor(string? deployment)
    {
        if (string.IsNullOrWhiteSpace(deployment)) return TextEmbedding3SmallDimensions;

        return deployment.Contains("3-large", StringComparison.OrdinalIgnoreCase) ? 3072
             : deployment.Contains("ada-002", StringComparison.OrdinalIgnoreCase) ? 1536
             : TextEmbedding3SmallDimensions;
    }

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return EmbeddingVectors.Unavailable;

        Interlocked.Increment(ref _callCount);

        // The batch overload deliberately, over the one-string extension that wraps it: the
        // extension returns only the embedding and DROPS the response's usage block, and this class
        // documents its call count as spend. A spend you can count but not price is half a fact.
        var generated = await _generator
            .GenerateAsync([text], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (generated.Count == 0)
        {
            throw new InvalidOperationException(
                $"Embedding deployment '{ModelId}' returned no embedding for a non-empty input. " +
                "An empty result from a LIVE source is a fault, not a degraded mode.");
        }

        if (generated.Usage?.InputTokenCount is { } inputTokens)
        {
            Interlocked.Add(ref _promptTokens, inputTokens);
        }
        else
        {
            Interlocked.Increment(ref _callsWithoutUsage);
        }

        var vector = generated[0].Vector;
        if (vector.Length == 0)
        {
            throw new InvalidOperationException(
                $"Embedding deployment '{ModelId}' returned an empty vector. " +
                "An empty vector from a LIVE source is a fault, not a degraded mode.");
        }

        if (!DimensionsConfirmed)
        {
            if (vector.Length != _dimensions)
            {
                // Correct the declaration and say so loudly: an index silently built at one
                // length and queried at another returns nothing and looks like "no matches".
                throw new InvalidOperationException(
                    $"Embedding deployment '{ModelId}' returned {vector.Length}-dimensional vectors, " +
                    $"but {_dimensions} was expected. Point AZURE_OPENAI_EMBEDDING_DEPLOYMENT at a " +
                    $"{_dimensions}-dimensional model, or rebuild the embedding assets against this one " +
                    "(a mixed-dimension index cannot be searched).");
            }

            _dimensions = vector.Length;
            DimensionsConfirmed = true;
        }

        // OpenAI embeddings arrive unit-normalised, but normalising is idempotent and cheap, and
        // it makes "cosine == dot product" an invariant this project owns rather than one it hopes for.
        return EmbeddingVectors.Normalized(vector.Span);
    }

    /// <summary>Disposes the wrapped generator when this instance created it.</summary>
    public void Dispose()
    {
        if (_ownsGenerator) _generator.Dispose();
    }
}
