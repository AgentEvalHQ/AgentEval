// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Memory.Models;

namespace AgentEval.Memory.DataLoading;

/// <summary>
/// Adapter that converts LongMemEval (ICLR 2025, MIT license) question format
/// to AgentEval's ScenarioDefinition/MemoryTestScenario format.
///
/// LongMemEval tests 5 memory abilities:
///   - Information Extraction → BasicRetention
///   - Multi-Session Reasoning → CrossSession
///   - Temporal Reasoning → TemporalReasoning
///   - Knowledge Updates → FactUpdateHandling
///   - Abstention → Abstention
///
/// Attribution: Data adapted from LongMemEval (ICLR 2025)
/// Authors: Di Wu, Hongwei Wang, Wenhao Yu, Yuwei Zhang, Kai-Wei Chang, Dong Yu
/// Repository: https://github.com/xiaowu0162/LongMemEval
/// License: MIT
/// </summary>
public static class LongMemEvalAdapter
{
    /// <summary>
    /// Loads LongMemEval questions from a JSON file and converts them to MemoryTestScenarios.
    /// </summary>
    /// <param name="jsonPath">Path to a LongMemEval JSON file (oracle, S, or M format).</param>
    /// <param name="maxQuestions">Maximum number of questions to load (null = all).</param>
    /// <returns>List of converted scenarios, grouped by question type.</returns>
    public static IReadOnlyList<MemoryTestScenario> LoadFromFile(string jsonPath, int? maxQuestions = null)
    {
        ArgumentNullException.ThrowIfNull(jsonPath);
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException($"LongMemEval data file not found: {jsonPath}");

        var json = File.ReadAllText(jsonPath);
        var entries = JsonSerializer.Deserialize<List<LongMemEvalEntry>>(json)
            ?? throw new InvalidOperationException("Failed to deserialize LongMemEval data");

        if (maxQuestions.HasValue)
            entries = entries.Take(maxQuestions.Value).ToList();

        return entries.Select(ConvertToScenario).ToList();
    }

    /// <summary>
    /// Loads the curated AgentEval subset of LongMemEval-style questions from embedded resources.
    /// This subset is MIT licensed and ships with AgentEval.
    /// </summary>
    public static IReadOnlyList<MemoryTestScenario> LoadSubset()
    {
        var assembly = typeof(LongMemEvalAdapter).Assembly;
        var names = assembly.GetManifestResourceNames();
        var resourceName = names.FirstOrDefault(n => n.EndsWith("longmemeval-subset.json"));

        if (resourceName == null)
            throw new FileNotFoundException("LongMemEval subset not found in embedded resources.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        var entries = JsonSerializer.Deserialize<List<LongMemEvalEntry>>(json)
            ?? throw new InvalidOperationException("Failed to deserialize LongMemEval subset");

        return entries.Select(ConvertToScenario).ToList();
    }

    /// <summary>
    /// Maps LongMemEval question types to AgentEval benchmark categories.
    /// </summary>
    public static BenchmarkScenarioType MapQuestionType(string questionType) => questionType switch
    {
        "single-session-user" or "single-session-assistant" => BenchmarkScenarioType.BasicRetention,
        "multi-session" => BenchmarkScenarioType.CrossSession,
        "temporal-reasoning" => BenchmarkScenarioType.TemporalReasoning,
        "knowledge-update" => BenchmarkScenarioType.FactUpdateHandling,
        "abstention" => BenchmarkScenarioType.Abstention,
        _ => BenchmarkScenarioType.BasicRetention
    };

    private static MemoryTestScenario ConvertToScenario(LongMemEvalEntry entry)
    {
        var steps = new List<MemoryStep>();

        // Convert haystack sessions to conversation steps
        if (entry.HaystackSessions != null)
        {
            foreach (var session in entry.HaystackSessions)
            {
                foreach (var turn in session)
                {
                    if (turn.Role == "user")
                        steps.Add(MemoryStep.Fact(turn.Content));
                    else
                        steps.Add(MemoryStep.Noise(turn.Content));
                }
            }
        }

        // Create the query
        var queries = new List<MemoryQuery>();
        if (entry.QuestionType == "abstention")
        {
            queries.Add(MemoryQuery.CreateAbstention(entry.Question,
                MemoryFact.Create("any fabricated detail")));
        }
        else
        {
            queries.Add(MemoryQuery.Create(entry.Question,
                MemoryFact.Create(entry.Answer)));
        }

        return new MemoryTestScenario
        {
            Name = $"LongMemEval-{entry.QuestionId}",
            Description = $"[{entry.QuestionType}] {entry.Question}",
            Steps = steps,
            Queries = queries,
            Metadata = new Dictionary<string, object>
            {
                ["source"] = "LongMemEval",
                ["question_type"] = entry.QuestionType,
                ["question_id"] = entry.QuestionId
            }
        };
    }

    // --- LongMemEval JSON model ---

    internal class LongMemEvalEntry
    {
        [JsonPropertyName("question_id")]
        public string QuestionId { get; set; } = "";

        [JsonPropertyName("question_type")]
        public string QuestionType { get; set; } = "";

        [JsonPropertyName("question")]
        public string Question { get; set; } = "";

        [JsonPropertyName("answer")]
        public string Answer { get; set; } = "";

        [JsonPropertyName("question_date")]
        public string? QuestionDate { get; set; }

        [JsonPropertyName("haystack_sessions")]
        public List<List<LongMemEvalTurn>>? HaystackSessions { get; set; }

        [JsonPropertyName("haystack_session_ids")]
        public List<int>? HaystackSessionIds { get; set; }

        [JsonPropertyName("answer_session_ids")]
        public List<int>? AnswerSessionIds { get; set; }
    }

    internal class LongMemEvalTurn
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";
    }
}
