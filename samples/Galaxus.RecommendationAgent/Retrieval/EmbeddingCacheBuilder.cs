// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Signals;

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>
/// The <c>--rebuild-embeddings</c> path (design §D.4): regenerates the two committed JSON assets
/// from a live embedding deployment, so the offline demo can one day run on real
/// <c>text-embedding-3-small</c> vectors rather than on the concept stand-in.
/// </summary>
/// <remarks>
/// <para>
/// <b>This spends money.</b> One embedding call per product plus one per entry of
/// <see cref="DefaultQuerySet"/> — <b>170 calls and 13 383 prompt tokens</b> on the shipped
/// catalogue (99 products + 17 canonical queries + 54 authored interest phrases), MEASURED
/// 2026-09-05 against <c>text-embedding-3-small</c>. It is a deliberate, explicit, occasional
/// action behind a CLI switch, never something the demo does on startup.
/// </para>
/// <para>
/// <b>It refuses to write a file that would misrepresent itself.</b> The <c>model</c> stamp is
/// taken from <see cref="IEmbeddingSource.ModelId"/>, so it cannot lie by construction — but
/// generating <c>catalogue.embeddings.json</c> from an OFFLINE source and committing it would still
/// invite a reader to believe the asset holds real embedding-model vectors. So an offline source is
/// refused unless the caller passes <c>allowOfflineSource: true</c>, and the console says loudly
/// what was written and by what.
/// </para>
/// <para>
/// <b>The two <c>EmbeddedResource</c> entries are now IN the csproj</b>, restored in the same
/// commit that added the assets (B-6, 2026-09-05) — an <c>EmbeddedResource</c> pointing at a file
/// that does not exist is a hard build error (MSB3030), which is why they were absent until then.
/// The reminder below still prints, because it is right for the next person who regenerates into
/// an empty <c>Data/</c> folder.
/// </para>
/// <para>
/// <b>What generating the assets did NOT do.</b> Every demo and eval path still builds its
/// <c>HybridRetriever</c> with <see cref="ConceptEmbeddingSource"/>. Committing real vectors does
/// not silently move the demo onto them, and this class must not be read as if it had: the assets
/// are the real-vector path, and moving the default onto them is a separate, declared change that
/// would move every measured number in the suite.
/// </para>
/// </remarks>
public static class EmbeddingCacheBuilder
{
    /// <summary>File name of the product-vector asset (keyed by product id).</summary>
    public const string CatalogueAssetFileName = "catalogue.embeddings.json";

    /// <summary>File name of the query-vector asset (keyed by SHA-256 of the normalised query).</summary>
    public const string QueriesAssetFileName = "queries.embeddings.json";

    /// <summary>The folder the assets live in, relative to the project root.</summary>
    public const string DataFolderName = "Data";

    /// <summary>UTF-8 with no byte-order mark — the encoding both committed assets are written in.</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The canonical demo and eval queries, from <see cref="GalaxusDemoPrompts"/> — the only query
    /// texts that can be anticipated ahead of a run.
    /// </summary>
    /// <remarks>
    /// <b>Coverage is bounded and it is worth saying so.</b> The needs the agent actually sends to
    /// <c>SearchProductsByMeaning</c> are composed by the model at run time from an interest-map
    /// label, so a novel need is a cache miss by construction. Precomputed query vectors make the
    /// scripted demo path free and deterministic; they do not make the system offline-capable. That
    /// is <see cref="ConceptEmbeddingSource"/>'s job.
    /// </remarks>
    public static IReadOnlyList<string> CanonicalQueries { get; } =
    [
        GalaxusDemoPrompts.NadiaLatentInterest,
        GalaxusDemoPrompts.MarcoGiftTrap,
        GalaxusDemoPrompts.MarcoStatedGamingInterest,
        GalaxusDemoPrompts.SofiaReplenishmentAndGap,
        GalaxusDemoPrompts.LucaThinSignal,
        GalaxusDemoPrompts.PhantomSkuProbe,
        GalaxusDemoPrompts.OutOfStockProbe,
        GalaxusDemoPrompts.NearMissBrandProbe,
        GalaxusDemoPrompts.SensitiveInferenceProbe,
        GalaxusDemoPrompts.SensitiveStatedNeed,
        GalaxusDemoPrompts.StatedNeedIdenticalUtterance,
        GalaxusDemoPrompts.CommitPressureNoConfirm,
        GalaxusDemoPrompts.CommitConfirmed,
        GalaxusDemoPrompts.EvidenceFabricationTemptation,
        GalaxusDemoPrompts.EvidenceSupportedClaim,
        GalaxusDemoPrompts.LanguageInvarianceDe,
        GalaxusDemoPrompts.LanguageInvarianceFr,
    ];

    /// <summary>
    /// The authored interest phrases — every distinct value of
    /// <see cref="InterestMapBuilder.ContextPhrases"/>, in ordinal key order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These are queries, not decoration.</b> <c>InterestMapBuilder.ComposeConjunctionLabel</c>
    /// turns a context-tag suffix into one of these strings and that string IS what a searching arm
    /// asks the retriever for. They belong in the query asset for exactly the reason
    /// <see cref="CanonicalQueries"/> does: they are known ahead of any run, so their vectors can be
    /// computed once instead of on every run.
    /// </para>
    /// <para>
    /// <b>Why this list was added at B-6.</b> Without it the committed query asset carried
    /// <b>0 of 54</b> of them, so there was no real-vector path for any authored interest at all,
    /// and B-6's acceptance — <c>AuthoredQueryPhraseRetrievability</c> reporting zero dead phrases
    /// on the real-vector path — was not merely unmet but unmeasurable. Measured before the change.
    /// </para>
    /// <para>
    /// <b>What it does NOT cover, said plainly.</b> A label is a JOIN of up to
    /// <c>InterestMapBuilder.MaximumLabelPhrases</c> = 3 of these phrases, and the joined string is
    /// a different text with a different hash. Caching the parts does not cache the whole. This list
    /// makes each authored interest INDIVIDUALLY askable on the committed-asset path; it does not
    /// make the demo's composed queries cache hits.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> AuthoredInterestPhrases { get; } =
        InterestMapBuilder.ContextPhrases
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// What <c>--rebuild-embeddings</c> embeds into the query asset when no explicit set is given:
    /// <see cref="CanonicalQueries"/> followed by <see cref="AuthoredInterestPhrases"/>, de-duplicated.
    /// </summary>
    public static IReadOnlyList<string> DefaultQuerySet { get; } =
        CanonicalQueries.Concat(AuthoredInterestPhrases).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Regenerates both assets and writes them, printing a TravelDemo-style progress panel.
    /// </summary>
    /// <param name="products">The catalogue to embed.</param>
    /// <param name="source">The embedding source. Normally <see cref="AzureEmbeddingSource"/>.</param>
    /// <param name="outputDirectory">Where to write. Null resolves the project's <c>Data/</c> folder.</param>
    /// <param name="queries">Query texts to embed. Null uses <see cref="DefaultQuerySet"/>.</param>
    /// <param name="allowOfflineSource">Permits generating assets from an offline source. Off by default, on purpose.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was written, or null when the run refused to write.</returns>
    public static async Task<EmbeddingCacheBuildReport?> RunAsync(
        IReadOnlyList<Product> products,
        IEmbeddingSource source,
        string? outputDirectory = null,
        IEnumerable<string>? queries = null,
        bool allowOfflineSource = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(source);

        PrintHeader();

        if (products.Count == 0)
        {
            PrintRefusal("The catalogue is empty — there is nothing to embed.");
            return null;
        }

        if (source.IsOffline && !allowOfflineSource)
        {
            PrintRefusal(
                $"Embedding source '{source.Name}' ({source.ModelId}) is OFFLINE.\n" +
                "     Writing it into a committed asset would invite a reader to believe the file holds\n" +
                "     real embedding-model vectors. Set AZURE_OPENAI_ENDPOINT / AZURE_OPENAI_API_KEY and\n" +
                "     AZURE_OPENAI_EMBEDDING_DEPLOYMENT, or pass allowOfflineSource: true deliberately.");
            return null;
        }

        var directory = outputDirectory ?? ResolveOutputDirectory();
        Directory.CreateDirectory(directory);

        var queryTexts = (queries ?? DefaultQuerySet).Where(q => !string.IsNullOrWhiteSpace(q)).ToArray();
        var stopwatch  = Stopwatch.StartNew();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  Source     : {source.Name} ({source.ModelId}, {source.Dimensions} dims)");
        Console.WriteLine($"  Template   : {EmbeddingDocument.TemplateVersion}");
        Console.WriteLine($"  Output     : {directory}");
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────────");
        Console.ResetColor();

        Console.WriteLine($"  ⏳ Embedding {products.Count} product documents...");
        var catalogueFile = await BuildCatalogueCacheAsync(products, source, ReportProgress, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"  ⏳ Embedding {queryTexts.Length} canonical queries...");
        var queriesFile = await BuildQueryCacheAsync(queryTexts, source, ReportProgress, cancellationToken).ConfigureAwait(false);

        var cataloguePath = Path.Combine(directory, CatalogueAssetFileName);
        var queriesPath   = Path.Combine(directory, QueriesAssetFileName);

        // UTF-8 with NO byte-order mark. `Encoding.UTF8` emits one, and while System.Text.Json's
        // stream reader tolerates it, these files are committed and diffed: two invisible leading
        // bytes are exactly the kind of thing that later reads as "binary file" in a grep and gets
        // chased for an hour. Both consumers read the same bytes either way.
        await File.WriteAllTextAsync(cataloguePath, Serialize(catalogueFile), Utf8NoBom, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(queriesPath, Serialize(queriesFile), Utf8NoBom, cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();

        // Spend is read from the source that issued the calls, never estimated here. A source that
        // cannot report it yields (0, 0), which prints as "not reported" rather than as free.
        var (calls, promptTokens, callsWithoutUsage) = source is AzureEmbeddingSource azure
            ? (azure.CallCount, azure.PromptTokens, azure.CallsWithoutUsage)
            : (0, 0L, 0);

        var report = new EmbeddingCacheBuildReport(
            cataloguePath,
            catalogueFile.Vectors.Count,
            queriesPath,
            queriesFile.Vectors.Count,
            source.ModelId,
            catalogueFile.Dimensions,
            EmbeddingDocument.TemplateVersion,
            new FileInfo(cataloguePath).Length + new FileInfo(queriesPath).Length,
            stopwatch.Elapsed,
            calls,
            promptTokens,
            callsWithoutUsage);

        PrintReport(report, products.Count, queryTexts.Length);
        return report;

        static void ReportProgress(int index, int total, string label)
        {
            if (total <= 0) return;
            if (index % 10 == 0 || index == total - 1)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"     {index + 1,3}/{total}  {Shorten(label, 58)}");
                Console.ResetColor();
            }
        }
    }

    /// <summary>
    /// Embeds every product's <see cref="EmbeddingDocument"/> and returns the product-id-keyed asset.
    /// </summary>
    /// <param name="products">The catalogue.</param>
    /// <param name="source">Embedding source.</param>
    /// <param name="onProgress">Optional progress callback: (index, total, productId).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="InvalidOperationException">The source produced no usable vector for a product.</exception>
    public static async Task<EmbeddingCacheFile> BuildCatalogueCacheAsync(
        IReadOnlyList<Product> products,
        IEmbeddingSource source,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(source);

        var vectors    = new Dictionary<string, string>(products.Count, StringComparer.Ordinal);
        int dimensions = source.Dimensions;

        for (int i = 0; i < products.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var product = products[i];
            if (product is null) continue;

            onProgress?.Invoke(i, products.Count, product.Id);

            var vector = await source
                .EmbedAsync(EmbeddingDocument.ForProduct(product), cancellationToken)
                .ConfigureAwait(false);

            if (vector.IsUnavailable())
            {
                throw new InvalidOperationException(
                    $"Embedding source '{source.Name}' returned no vector for product '{product.Id}'. " +
                    "A partial asset is worse than no asset — it silently makes some products " +
                    "invisible to the dense leg. Aborting rather than writing one.");
            }

            if (dimensions <= 0) dimensions = vector.Length;

            if (vector.Length != dimensions)
            {
                throw new InvalidOperationException(
                    $"Product '{product.Id}' embedded to {vector.Length} dimensions but {dimensions} was expected.");
            }

            vectors[product.Id] = EmbeddingCacheFile.EncodeVector(EmbeddingVectors.Normalized(vector.Span));
        }

        return new EmbeddingCacheFile
        {
            Model                   = source.ModelId,
            Dimensions              = dimensions,
            DocumentTemplateVersion = EmbeddingDocument.TemplateVersion,
            GeneratedUtc            = DateTimeOffset.UtcNow.ToString("O"),
            Keying                  = EmbeddingCacheFile.KeyingProductId,
            Vectors                 = vectors,
        };
    }

    /// <summary>
    /// Embeds each query and returns the SHA-256-keyed asset. Keys are content-derived
    /// (<see cref="EmbeddingDocument.HashQuery"/>), never hand-written — a stable hand-written key
    /// is exactly how a cache starts replaying a vector for text that has since changed.
    /// </summary>
    /// <param name="queries">Query texts.</param>
    /// <param name="source">Embedding source.</param>
    /// <param name="onProgress">Optional progress callback: (index, total, query).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<EmbeddingCacheFile> BuildQueryCacheAsync(
        IReadOnlyList<string> queries,
        IEmbeddingSource source,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(source);

        var vectors    = new Dictionary<string, string>(queries.Count, StringComparer.Ordinal);
        int dimensions = source.Dimensions;

        for (int i = 0; i < queries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var query = queries[i];
            if (string.IsNullOrWhiteSpace(query)) continue;

            onProgress?.Invoke(i, queries.Count, query);

            var vector = await source.EmbedAsync(query, cancellationToken).ConfigureAwait(false);
            if (vector.IsUnavailable())
            {
                throw new InvalidOperationException(
                    $"Embedding source '{source.Name}' returned no vector for a canonical query. Aborting.");
            }

            if (dimensions <= 0) dimensions = vector.Length;

            if (vector.Length != dimensions)
            {
                throw new InvalidOperationException(
                    $"A canonical query embedded to {vector.Length} dimensions but {dimensions} was expected.");
            }

            vectors[EmbeddingDocument.HashQuery(query)] = EmbeddingCacheFile.EncodeVector(EmbeddingVectors.Normalized(vector.Span));
        }

        return new EmbeddingCacheFile
        {
            Model                   = source.ModelId,
            Dimensions              = dimensions,
            DocumentTemplateVersion = EmbeddingDocument.TemplateVersion,
            GeneratedUtc            = DateTimeOffset.UtcNow.ToString("O"),
            Keying                  = EmbeddingCacheFile.KeyingQuerySha256,
            Vectors                 = vectors,
        };
    }

    /// <summary>Serialises an asset with the same options the loader reads it back with.</summary>
    /// <param name="file">The asset.</param>
    public static string Serialize(EmbeddingCacheFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return JsonSerializer.Serialize(file, EmbeddingCacheFile.JsonOptions);
    }

    /// <summary>
    /// Finds the project's <c>Data/</c> folder by walking up from the running binary looking for
    /// <c>Galaxus.RecommendationAgent.csproj</c>. Falls back to <c>Data/</c> under the working
    /// directory, so the switch still does something sensible from a published binary.
    /// </summary>
    public static string ResolveOutputDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (int depth = 0; depth < 8 && directory is not null; depth++)
        {
            var projectFile = Path.Combine(directory.FullName, "Galaxus.RecommendationAgent.csproj");
            if (File.Exists(projectFile)) return Path.Combine(directory.FullName, DataFolderName);
            directory = directory.Parent;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), DataFolderName);
    }

    // ── Console UI (TravelDemo conventions) ──────────────────────────────────────────────────

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("""

╔══════════════════════════════════════════════════════════════════════════════╗
║  REBUILD EMBEDDINGS — regenerate the committed vector assets                  ║
╚══════════════════════════════════════════════════════════════════════════════╝
""");
        Console.ResetColor();
    }

    private static void PrintRefusal(string reason)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  ⛔ Refusing to write embedding assets.\n     {reason}\n");
        Console.ResetColor();
    }

    private static void PrintReport(EmbeddingCacheBuildReport report, int productCount, int queryCount)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────────");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  ✅ Embedding assets written.");
        Console.ResetColor();

        Console.WriteLine($"     catalogue : {report.CatalogueVectors}/{productCount} vectors → {report.CataloguePath}");
        Console.WriteLine($"     queries   : {report.QueryVectors}/{queryCount} vectors → {report.QueriesPath}");
        Console.WriteLine($"     model     : {report.Model} · {report.Dimensions} dims · template {report.DocumentTemplateVersion}");
        Console.WriteLine($"     size      : {report.TotalBytes / 1024.0:F1} KB · took {report.Elapsed.TotalSeconds:F1} s");
        Console.WriteLine(report.EmbeddingCalls == 0
            ? "     spend     : not reported by this source (0 calls counted) — NOT the same as free"
            : $"     spend     : {report.EmbeddingCalls} calls · {report.PromptTokens} prompt tokens"
              + (report.CallsWithoutUsage == 0
                    ? " (every response carried usage)"
                    : $" — LOWER BOUND: {report.CallsWithoutUsage} response(s) carried no usage block"));

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine();
        Console.WriteLine("  ⚠️  In the SAME commit that adds these files, restore the two lines the csproj");
        Console.WriteLine("      currently omits (they are a hard build error while the files are missing):");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("        <EmbeddedResource Include=\"Data\\catalogue.embeddings.json\" />");
        Console.WriteLine("        <EmbeddedResource Include=\"Data\\queries.embeddings.json\" />");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static string Shorten(string text, int budget)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= budget ? single : single[..Math.Max(0, budget - 1)] + "…";
    }
}

/// <summary>What a <c>--rebuild-embeddings</c> run actually wrote. Facts, for the console and for a caller.</summary>
/// <param name="CataloguePath">Absolute path of the product-vector asset.</param>
/// <param name="CatalogueVectors">How many product vectors it holds.</param>
/// <param name="QueriesPath">Absolute path of the query-vector asset.</param>
/// <param name="QueryVectors">How many query vectors it holds.</param>
/// <param name="Model">The embedding model stamped into both files.</param>
/// <param name="Dimensions">Vector length.</param>
/// <param name="DocumentTemplateVersion">The <see cref="EmbeddingDocument.TemplateVersion"/> in force.</param>
/// <param name="TotalBytes">Combined size on disk.</param>
/// <param name="Elapsed">Wall-clock duration, including every embedding call.</param>
/// <param name="EmbeddingCalls">Embedding calls issued. Zero when the source does not report it.</param>
/// <param name="PromptTokens">
/// Prompt tokens billed, summed from the responses' own usage blocks — never estimated. Zero when
/// the source does not report it, which is NOT the same as free.
/// </param>
/// <param name="CallsWithoutUsage">
/// Calls whose response carried no usage block. Non-zero makes <paramref name="PromptTokens"/> a
/// lower bound rather than a total.
/// </param>
public sealed record EmbeddingCacheBuildReport(
    string CataloguePath,
    int CatalogueVectors,
    string QueriesPath,
    int QueryVectors,
    string Model,
    int Dimensions,
    string DocumentTemplateVersion,
    long TotalBytes,
    TimeSpan Elapsed,
    int EmbeddingCalls = 0,
    long PromptTokens = 0,
    int CallsWithoutUsage = 0);
