// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Loads and samples LongMemEval dataset entries.
/// Supports stratified sampling to ensure proportional representation of all 6 question types,
/// fixing the bias caused by the dataset being sorted by type.
/// </summary>
/// <remarks>
/// <para>
/// v0.10.1-beta removed the previously-bundled <c>longmemeval-subset.json</c> embedded
/// resource. That file was a hand-authored "inspired by LongMemEval" approximation
/// (10 entries, partial schema), not the real paper dataset — running against it
/// produced scores that were misleading at best. From v0.10.1 every preset
/// (Subset / Full) reads the real <c>longmemeval_s_cleaned.json</c> from disk.
/// </para>
/// <para>
/// Dataset resolution order (highest precedence first):
/// <list type="number">
///   <item><description>Explicit <c>datasetPath</c> passed to <see cref="LoadFromFile(string, ExternalBenchmarkOptions)"/>.</description></item>
///   <item><description><c>LONGMEMEVAL_DATASET_PATH</c> environment variable (resolved via <see cref="ResolveDatasetPath"/>).</description></item>
///   <item><description>Canonical local default: <c>&lt;workspace-root&gt;/src/AgentEval.Memory/Data/longmemeval/longmemeval_s_cleaned.json</c>
///   where the workspace root is the nearest ancestor with <c>*.sln</c>, <c>*.slnx</c>, or <c>.git/</c>
///   (mirrors the <c>WorkspaceRootDiscovery</c> convention).</description></item>
///   <item><description>If none of the above resolves to an existing file → <see cref="LongMemEvalDatasetNotFoundException"/>.</description></item>
/// </list>
/// </para>
/// </remarks>
public static class LongMemEvalDataLoader
{
    /// <summary>The canonical file name (no path) for the LongMemEval S-mode cleaned dataset.</summary>
    public const string CanonicalDatasetFileName = "longmemeval_s_cleaned.json";

    /// <summary>The <c>longmemeval-cleaned</c> Hugging Face dataset URL — surfaced in the not-found exception message.</summary>
    public const string DatasetDownloadUrl =
        "https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned/tree/main";

    /// <summary>The env var consumers can set to point at a non-canonical dataset location.</summary>
    public const string DatasetPathEnvVar = "LONGMEMEVAL_DATASET_PATH";

    /// <summary>
    /// Loads entries from a LongMemEval JSON file with optional stratified sampling.
    /// </summary>
    /// <param name="path">Path to the dataset (oracle, S, or M format).</param>
    /// <param name="options">Benchmark options controlling sampling.</param>
    /// <returns>List of entries, sampled proportionally by question type if options specify.</returns>
    /// <exception cref="LongMemEvalDatasetNotFoundException">When the file does not exist on disk.</exception>
    public static IReadOnlyList<LongMemEvalEntry> LoadFromFile(string path, ExternalBenchmarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            throw LongMemEvalDatasetNotFoundException.ForPath(path);

        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<LongMemEvalEntry>>(json)
            ?? throw new InvalidOperationException("Failed to deserialize LongMemEval dataset.");

        return Sample(entries, options);
    }

    /// <summary>
    /// Resolves the LongMemEval dataset path using the documented order:
    /// explicit <paramref name="explicitPath"/> → <c>LONGMEMEVAL_DATASET_PATH</c> env var →
    /// canonical local default under the workspace root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Caller-supplied paths are <b>authoritative</b>. A non-whitespace
    /// <paramref name="explicitPath"/> or <c>LONGMEMEVAL_DATASET_PATH</c> env var
    /// is treated as "the caller asked for this specific dataset" — if the file
    /// does not exist at the supplied location, the method throws
    /// <see cref="LongMemEvalDatasetNotFoundException"/> instead of silently
    /// falling back to a different dataset. This prevents typos (in <c>DatasetPath</c>
    /// or in the <c>Full()</c> env-var path) from producing misleading benchmark
    /// results against the canonical local default. Falling back to the canonical
    /// local path only happens when neither explicit path nor env var is supplied.
    /// </para>
    /// <para>
    /// Returns the resolved existing path, or <c>null</c> if no path was supplied
    /// (explicit and env var both empty) and the canonical local path is missing —
    /// the caller decides how to phrase the "nothing reachable" message.
    /// </para>
    /// </remarks>
    /// <exception cref="LongMemEvalDatasetNotFoundException">
    /// When a non-whitespace <paramref name="explicitPath"/> or the
    /// <c>LONGMEMEVAL_DATASET_PATH</c> env var is set but the corresponding file
    /// does not exist on disk.
    /// </exception>
    public static string? ResolveDatasetPath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (File.Exists(explicitPath))
                return explicitPath;
            throw LongMemEvalDatasetNotFoundException.ForPath(explicitPath);
        }

        var envPath = Environment.GetEnvironmentVariable(DatasetPathEnvVar);
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            if (File.Exists(envPath))
                return envPath;
            throw LongMemEvalDatasetNotFoundException.ForPath(envPath);
        }

        var canonical = TryFindCanonicalLocalPath();
        if (canonical != null && File.Exists(canonical))
            return canonical;

        return null;
    }

    /// <summary>
    /// Loads entries via <see cref="ResolveDatasetPath"/>; throws
    /// <see cref="LongMemEvalDatasetNotFoundException"/> when no candidate resolves to an
    /// existing file. This is the entry point preset factories should use — it bakes the
    /// "real data only, no fake fallback" contract into a single helper.
    /// </summary>
    /// <param name="options">Benchmark options controlling sampling.</param>
    /// <returns>The sampled entries.</returns>
    /// <exception cref="LongMemEvalDatasetNotFoundException">When the dataset cannot be located.</exception>
    public static IReadOnlyList<LongMemEvalEntry> LoadResolved(ExternalBenchmarkOptions options)
    {
        var resolved = ResolveDatasetPath(options.DatasetPath);
        if (resolved == null)
            throw LongMemEvalDatasetNotFoundException.ForResolutionFailure();

        return LoadFromFile(resolved, options);
    }

    /// <summary>
    /// Returns the distribution of question types in the given entries.
    /// </summary>
    public static Dictionary<string, int> GetTypeDistribution(IEnumerable<LongMemEvalEntry> entries)
        => entries.GroupBy(e => e.QuestionType).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> looking for an ancestor that
    /// looks like a repo / solution root (contains a <c>*.sln</c>, <c>*.slnx</c>, or
    /// <c>.git/</c>). Returns the canonical dataset path under that root, or <c>null</c>
    /// when no such ancestor exists (e.g. consumer running from a published binary
    /// outside any repo).
    /// </summary>
    /// <remarks>
    /// Mirrors the convention used by <c>AgentEval.DataLoaders.WorkspaceRootDiscovery</c>
    /// and the Samples helper's <c>SampleWorkspaceRoot</c>. Kept inline here to avoid a
    /// hard dependency on that internal type from <c>AgentEval.Memory</c>.
    /// </remarks>
    internal static string? TryFindCanonicalLocalPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("*.sln").Length > 0 ||
                dir.GetFiles("*.slnx").Length > 0 ||
                dir.GetDirectories(".git").Length > 0)
            {
                return Path.Combine(
                    dir.FullName,
                    "src", "AgentEval.Memory", "Data", "longmemeval",
                    CanonicalDatasetFileName);
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static IReadOnlyList<LongMemEvalEntry> Sample(
        List<LongMemEvalEntry> entries, ExternalBenchmarkOptions options)
    {
        if (!options.MaxQuestions.HasValue)
            return entries;

        var max = options.MaxQuestions.Value;
        if (max <= 0)
            return [];
        if (max >= entries.Count)
            return entries;

        if (options.StratifiedSampling)
            return StratifiedSample(entries, max, options.RandomSeed);

        // Shuffle then take (avoids the sorted-by-type bias)
        return ShuffledSample(entries, max, options.RandomSeed);
    }

    /// <summary>
    /// Stratified sampling: take proportional samples from each question type.
    /// When budget allows, ensures at least 1 per type. When budget is smaller than
    /// the number of types, randomly selects which types to include.
    /// </summary>
    private static IReadOnlyList<LongMemEvalEntry> StratifiedSample(
        List<LongMemEvalEntry> entries, int maxQuestions, int? seed)
    {
        var rng = seed.HasValue ? new Random(seed.Value) : Random.Shared;
        var groups = entries.GroupBy(e => e.QuestionType).ToList();
        var totalEntries = entries.Count;

        // First pass: compute proportional allocations
        var perType = new Dictionary<string, int>();
        foreach (var group in groups)
        {
            var proportion = (double)group.Count() / totalEntries;
            var take = Math.Max(1, (int)Math.Round(maxQuestions * proportion));
            perType[group.Key] = Math.Min(take, group.Count());
        }

        // Second pass: reduce to not exceed maxQuestions
        var total = perType.Values.Sum();
        while (total > maxQuestions)
        {
            // First try reducing allocations > 1
            var largest = perType.OrderByDescending(kv => kv.Value).First();
            if (largest.Value > 1)
            {
                perType[largest.Key]--;
                total--;
            }
            else
            {
                // All allocations are 1 but still over budget — drop random types
                var typeToRemove = perType.Keys.OrderBy(_ => rng.Next()).First();
                perType.Remove(typeToRemove);
                total--;
            }
        }

        // Sample from each remaining group using Fisher–Yates partial shuffle (O(take), not O(n log n))
        var result = new List<LongMemEvalEntry>();
        var usedIds = new HashSet<string>();
        foreach (var group in groups)
        {
            if (!perType.TryGetValue(group.Key, out var take) || take <= 0)
                continue;
            var pool = group.ToList();
            FisherYatesShuffle(pool, take, rng);
            foreach (var e in pool.Take(take))
            {
                result.Add(e);
                usedIds.Add(e.QuestionId);
            }
        }

        // Top-up: if result is still under budget (due to capped groups),
        // fill with random entries from the remaining pool (no duplicates).
        if (result.Count < maxQuestions)
        {
            var remaining = entries
                .Where(e => !usedIds.Contains(e.QuestionId))
                .ToList();
            var needed = maxQuestions - result.Count;
            FisherYatesShuffle(remaining, needed, rng);
            result.AddRange(remaining.Take(needed));
        }

        return result;
    }

    private static IReadOnlyList<LongMemEvalEntry> ShuffledSample(
        List<LongMemEvalEntry> entries, int maxQuestions, int? seed)
    {
        var rng = seed.HasValue ? new Random(seed.Value) : Random.Shared;
        var pool = entries.ToList();
        FisherYatesShuffle(pool, maxQuestions, rng);
        return pool.Take(maxQuestions).ToList();
    }

    /// <summary>
    /// Performs a partial Fisher–Yates shuffle in-place, placing <paramref name="count"/>
    /// randomly-selected elements at the front of <paramref name="list"/>.
    /// O(count) time, O(1) extra space — avoids the O(n log n) sort-based shuffle anti-pattern.
    /// </summary>
    private static void FisherYatesShuffle(List<LongMemEvalEntry> list, int count, Random rng)
    {
        var n = list.Count;
        var limit = Math.Min(count, n);
        for (var i = 0; i < limit; i++)
        {
            var j = rng.Next(i, n);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

/// <summary>
/// Thrown when the LongMemEval dataset cannot be located. Carries the canonical local
/// path, the env-var name consumers can set, and the Hugging Face download URL so the
/// message tells the user exactly how to recover.
/// </summary>
/// <remarks>
/// <para>
/// Subclasses <see cref="FileNotFoundException"/> so existing <c>catch (FileNotFoundException)</c>
/// blocks still trigger (the type was previously thrown in this position) — code that needs to
/// distinguish "dataset not found" from "some other file not found" can pattern-match on this
/// concrete type. The sample at <c>samples/AgentEval.Samples/Benchmarks/08_LongMemEvalBenchmark.cs</c>
/// catches this type to render the user-facing "download instructions" box.
/// </para>
/// </remarks>
public sealed class LongMemEvalDatasetNotFoundException : FileNotFoundException
{
    /// <summary>The canonical local path the loader checks (under the workspace root).</summary>
    public string? CanonicalLocalPath { get; }

    /// <summary>The env var consumers can set to point at a non-canonical location.</summary>
    public string EnvVarName { get; }

    /// <summary>The dataset download URL (Hugging Face).</summary>
    public string DownloadUrl { get; }

    private LongMemEvalDatasetNotFoundException(string message, string? canonicalLocalPath)
        : base(message)
    {
        CanonicalLocalPath = canonicalLocalPath;
        EnvVarName = LongMemEvalDataLoader.DatasetPathEnvVar;
        DownloadUrl = LongMemEvalDataLoader.DatasetDownloadUrl;
    }

    internal static LongMemEvalDatasetNotFoundException ForPath(string explicitPath)
    {
        var msg =
            $"LongMemEval dataset not found at '{explicitPath}'. " +
            $"Download {LongMemEvalDataLoader.CanonicalDatasetFileName} from {LongMemEvalDataLoader.DatasetDownloadUrl} " +
            $"and either pass the explicit path or set the {LongMemEvalDataLoader.DatasetPathEnvVar} environment variable.";
        return new LongMemEvalDatasetNotFoundException(msg, canonicalLocalPath: null);
    }

    internal static LongMemEvalDatasetNotFoundException ForResolutionFailure()
    {
        var canonical = LongMemEvalDataLoader.TryFindCanonicalLocalPath();
        var msg =
            $"LongMemEval dataset not found. v0.10.1+ requires the real {LongMemEvalDataLoader.CanonicalDatasetFileName} " +
            $"on disk — no fake fallback is bundled. " +
            $"Download from {LongMemEvalDataLoader.DatasetDownloadUrl} and place it at " +
            $"'{canonical ?? "<workspace-root>/src/AgentEval.Memory/Data/longmemeval/" + LongMemEvalDataLoader.CanonicalDatasetFileName}', " +
            $"or set the {LongMemEvalDataLoader.DatasetPathEnvVar} environment variable to its absolute path.";
        return new LongMemEvalDatasetNotFoundException(msg, canonical);
    }
}
