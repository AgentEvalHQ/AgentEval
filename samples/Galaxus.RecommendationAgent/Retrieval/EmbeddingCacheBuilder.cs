// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Signals;

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>
/// The <c>--rebuild-embeddings</c> path (design §D.4): regenerates the committed product-vector
/// asset from a live embedding deployment, so the real-vector retrieval path has an index to search.
/// </summary>
/// <remarks>
/// <para>
/// <b>This spends money.</b> One embedding call per product — <b>99 calls</b> on the shipped
/// catalogue. The B-6 run that produced the committed asset cost 170 calls and 13 383 prompt tokens
/// (≈ USD 0.00027) because it also embedded 71 query texts; that half is gone, so a rebuild today is
/// roughly 58 % of it. It is a deliberate, explicit, occasional action behind a CLI switch, never
/// something the demo does on startup.
/// </para>
/// <para>
/// <b>The query asset is DELETED, and that is the B-21 fix rather than a simplification.</b> This
/// builder used to write a second file holding 71 pre-guessed query vectors — 17 canonical prompts
/// and 54 authored interest phrases — and <see cref="PrecomputedEmbeddingSource"/> served queries out
/// of it. A query composed at run time is not one of 71 guesses, so it missed, came back
/// <c>Unavailable</c>, and <c>--real-vectors</c> retrieved NOTHING. The <c>DefaultQuerySet</c>,
/// <c>CanonicalQueries</c> and <c>AuthoredInterestPhrases</c> lists existed only to feed that file
/// and went with it. Queries are now embedded LIVE at search time, which is what a production
/// retrieval system does: the INDEX is precomputed, the QUERY is not.
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
/// <b>The stamp is load-bearing twice over.</b> <see cref="EmbeddingSpace"/> reads the asset's
/// <c>model</c> field to decide which live deployment may embed queries against it — the committed
/// index names the only embedder that can answer questions about it — and then proves the space with
/// an identity probe. So what this builder stamps decides what a later run is allowed to search with.
/// </para>
/// </remarks>
public static class EmbeddingCacheBuilder
{
    /// <summary>File name of the product-vector asset (keyed by product id).</summary>
    public const string CatalogueAssetFileName = "catalogue.embeddings.json";

    /// <summary>The folder the assets live in, relative to the project root.</summary>
    public const string DataFolderName = "Data";

    /// <summary>UTF-8 with no byte-order mark — the encoding the committed asset is written in.</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Regenerates the product-vector asset and writes it, printing a TravelDemo-style progress panel.
    /// </summary>
    /// <param name="products">The catalogue to embed.</param>
    /// <param name="source">The embedding source. Normally <see cref="AzureEmbeddingSource"/>.</param>
    /// <param name="outputDirectory">Where to write. Null resolves the project's <c>Data/</c> folder.</param>
    /// <param name="allowOfflineSource">Permits generating assets from an offline source. Off by default, on purpose.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was written, or null when the run refused to write.</returns>
    public static async Task<EmbeddingCacheBuildReport?> RunAsync(
        IReadOnlyList<Product> products,
        IEmbeddingSource source,
        string? outputDirectory = null,
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

        var stopwatch = Stopwatch.StartNew();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  Source     : {source.Name} ({source.ModelId}, {source.Dimensions} dims)");
        Console.WriteLine($"  Template   : {EmbeddingDocument.TemplateVersion}");
        Console.WriteLine($"  Output     : {directory}");
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────────");
        Console.ResetColor();

        Console.WriteLine($"  ⏳ Embedding {products.Count} product documents...");
        var catalogueFile = await BuildCatalogueCacheAsync(products, source, ReportProgress, cancellationToken).ConfigureAwait(false);

        var cataloguePath = Path.Combine(directory, CatalogueAssetFileName);

        // UTF-8 with NO byte-order mark. `Encoding.UTF8` emits one, and while System.Text.Json's
        // stream reader tolerates it, this file is committed and diffed: two invisible leading
        // bytes are exactly the kind of thing that later reads as "binary file" in a grep and gets
        // chased for an hour. Both consumers read the same bytes either way.
        await File.WriteAllTextAsync(cataloguePath, Serialize(catalogueFile), Utf8NoBom, cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();

        // Spend is read from the source that issued the calls, never estimated here. A source that
        // cannot report it yields (0, 0), which prints as "not reported" rather than as free.
        var (calls, promptTokens, callsWithoutUsage) = source is AzureEmbeddingSource azure
            ? (azure.CallCount, azure.PromptTokens, azure.CallsWithoutUsage)
            : (0, 0L, 0);

        var report = new EmbeddingCacheBuildReport(
            cataloguePath,
            catalogueFile.Vectors.Count,
            source.ModelId,
            catalogueFile.Dimensions,
            EmbeddingDocument.TemplateVersion,
            new FileInfo(cataloguePath).Length,
            stopwatch.Elapsed,
            calls,
            promptTokens,
            callsWithoutUsage);

        PrintReport(report, products.Count);
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

    private static void PrintReport(EmbeddingCacheBuildReport report, int productCount)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────────");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  ✅ Embedding asset written.");
        Console.ResetColor();

        Console.WriteLine($"     catalogue : {report.CatalogueVectors}/{productCount} vectors → {report.CataloguePath}");
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
        Console.WriteLine("  ⚠️  If Data/ was empty before this run, restore the line the csproj omits in the");
        Console.WriteLine("      SAME commit that adds the file (an EmbeddedResource pointing at a missing file is");
        Console.WriteLine("      a hard build error):");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("        <EmbeddedResource Include=\"Data\\catalogue.embeddings.json\" />");
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
/// <param name="Model">The embedding model stamped into the file. EmbeddingSpace reads it back to decide which live deployment may embed queries against this index.</param>
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
    string Model,
    int Dimensions,
    string DocumentTemplateVersion,
    long TotalBytes,
    TimeSpan Elapsed,
    int EmbeddingCalls = 0,
    long PromptTokens = 0,
    int CallsWithoutUsage = 0);
