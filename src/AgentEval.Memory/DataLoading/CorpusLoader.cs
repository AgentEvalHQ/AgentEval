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
            throw new FileNotFoundException($"Corpus '{corpusName}' not found in embedded resources. Available: {string.Join(", ", names.Where(n => n.Contains("corpus")))}");

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
    /// Lists available corpus names from embedded resources.
    /// </summary>
    public static IReadOnlyList<string> ListAvailable()
    {
        var assembly = typeof(CorpusLoader).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.Contains("corpus") && n.EndsWith(".json"))
            .Select(n => Path.GetFileNameWithoutExtension(n))
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
