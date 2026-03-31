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
public static class LongMemEvalDataLoader
{
    /// <summary>
    /// Loads entries from a LongMemEval JSON file with optional stratified sampling.
    /// </summary>
    /// <param name="path">Path to the dataset (oracle, S, or M format).</param>
    /// <param name="options">Benchmark options controlling sampling.</param>
    /// <returns>List of entries, sampled proportionally by question type if options specify.</returns>
    public static IReadOnlyList<LongMemEvalEntry> LoadFromFile(string path, ExternalBenchmarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"LongMemEval dataset not found: {path}");

        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<LongMemEvalEntry>>(json)
            ?? throw new InvalidOperationException("Failed to deserialize LongMemEval dataset.");

        return Sample(entries, options);
    }

    /// <summary>
    /// Loads entries from the embedded LongMemEval subset that ships with AgentEval.
    /// </summary>
    public static IReadOnlyList<LongMemEvalEntry> LoadEmbedded(ExternalBenchmarkOptions options)
    {
        var assembly = typeof(LongMemEvalDataLoader).Assembly;
        var names = assembly.GetManifestResourceNames();
        var resourceName = names.FirstOrDefault(n => n.EndsWith("longmemeval-subset.json"));

        if (resourceName == null)
            throw new FileNotFoundException("LongMemEval subset not found in embedded resources.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        var entries = JsonSerializer.Deserialize<List<LongMemEvalEntry>>(json)
            ?? throw new InvalidOperationException("Failed to deserialize LongMemEval embedded subset.");

        return Sample(entries, options);
    }

    /// <summary>
    /// Returns the distribution of question types in the given entries.
    /// </summary>
    public static Dictionary<string, int> GetTypeDistribution(IEnumerable<LongMemEvalEntry> entries)
        => entries.GroupBy(e => e.QuestionType).ToDictionary(g => g.Key, g => g.Count());

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
