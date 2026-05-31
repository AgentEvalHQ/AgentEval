// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.Memory.DataLoading;

/// <summary>
/// Loads pre-built conversation corpora from embedded JSON resources.
/// Used by the benchmark runner to inject context pressure into agents
/// without making expensive LLM calls.
/// </summary>
public static class CorpusLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Loads a conversation corpus by name from embedded resources.
    /// </summary>
    /// <param name="corpusName">Corpus name (e.g., "context-small", "context-medium").</param>
    /// <returns>List of (user, assistant) conversation turn pairs.</returns>
    public static IReadOnlyList<(string UserMessage, string AssistantResponse)> Load(string corpusName)
    {
        ArgumentNullException.ThrowIfNull(corpusName);

        var assembly = typeof(CorpusLoader).Assembly;
        var names = assembly.GetManifestResourceNames();
        var resourceName = names.FirstOrDefault(n => n.EndsWith($"{corpusName}.json"));

        if (resourceName == null)
        {
            var availableCorpora = ListAvailable();
            throw new FileNotFoundException(
                $"Corpus '{corpusName}' not found in embedded resources. " +
                $"Available corpora: {string.Join(", ", availableCorpora)}. " +
                "Call CorpusLoader.ListAvailable() for the full list.");
        }
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        var corpus = JsonSerializer.Deserialize<CorpusFile>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize corpus '{corpusName}'");

        return corpus.Turns
            .Select(t => (t.User, t.Assistant))
            .ToList();
    }

    /// <summary>
    /// Loads a corpus and returns only the first N turns.
    /// </summary>
    public static IReadOnlyList<(string UserMessage, string AssistantResponse)> Load(string corpusName, int maxTurns)
    {
        return Load(corpusName).Take(maxTurns).ToList();
    }

    /// <summary>
    /// Upper bound on how many times the base corpus is repeated by
    /// <see cref="LoadToTargetTokens(string,int,int)"/> (PERF-07). Without a cap, a large
    /// <c>targetTokens</c> against a tiny corpus produces an unbounded repeat count and an
    /// out-of-memory <see cref="List{T}"/> allocation. The default bounds the pathological case
    /// while comfortably covering real overflow tests (largest context windows ≈ a few million
    /// tokens, reached in far fewer repeats for any non-trivial corpus).
    /// </summary>
    public const int DefaultMaxRepeats = 100_000;

    /// <summary>
    /// Loads a corpus and repeats it until reaching the target token count.
    /// Tokens are estimated at 4 characters per token.
    /// Used for overflow testing where we need to exceed the model's context window.
    /// </summary>
    /// <param name="corpusName">The embedded corpus to load.</param>
    /// <param name="targetTokens">Approximate token count to reach by repetition (must be &gt;= 0).</param>
    /// <param name="maxRepeats">
    /// Hard cap on repeats (PERF-07). If the target would need more repeats than this, the result is
    /// capped and will contain fewer than <paramref name="targetTokens"/> tokens. Defaults to
    /// <see cref="DefaultMaxRepeats"/>.
    /// </param>
    public static IReadOnlyList<(string UserMessage, string AssistantResponse)> LoadToTargetTokens(
        string corpusName, int targetTokens, int maxRepeats = DefaultMaxRepeats)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRepeats);

        var baseTurns = Load(corpusName);
        var baseChars = baseTurns.Sum(t => t.UserMessage.Length + t.AssistantResponse.Length);
        var baseTokensEst = baseChars / 4;

        if (baseTokensEst <= 0) return baseTurns;

        // PERF-07: compute the raw repeat count in double, then clamp to [1, maxRepeats] BEFORE casting,
        // so a target large enough to overflow int (or to demand a runaway allocation) is bounded.
        var rawRepeats = Math.Ceiling((double)targetTokens / baseTokensEst);
        var repeats = (int)Math.Clamp(rawRepeats, 1, maxRepeats);

        var result = new List<(string, string)>(baseTurns.Count * repeats);
        for (int i = 0; i < repeats; i++)
            result.AddRange(baseTurns);

        return result;
    }

    /// <summary>
    /// Lists available corpus names from embedded resources.
    /// </summary>
    public static IReadOnlyList<string> ListAvailable()
    {
        var assembly = typeof(CorpusLoader).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.Contains("corpus") && n.EndsWith(".json"))
            .Select(n =>
            {
                var nameWithoutExtension = Path.GetFileNameWithoutExtension(n);
                var parts = nameWithoutExtension.Split('.');
                return parts[^1];
            })
            .ToList();
    }

    private class CorpusFile
    {
        [JsonPropertyName("turns")]
        public List<CorpusTurn> Turns { get; set; } = [];
    }

    private class CorpusTurn
    {
        [JsonPropertyName("user")]
        public string User { get; set; } = "";

        [JsonPropertyName("assistant")]
        public string Assistant { get; set; } = "";
    }
}
