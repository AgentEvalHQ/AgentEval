// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Core;
using AgentEval.Memory.Engine;
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
        "single-session-user" or "single-session-assistant" or "single-session-preference"
            => BenchmarkScenarioType.BasicRetention,
        "multi-session" => BenchmarkScenarioType.CrossSession,
        "temporal-reasoning" => BenchmarkScenarioType.TemporalReasoning,
        "knowledge-update" => BenchmarkScenarioType.FactUpdateHandling,
        "abstention" => BenchmarkScenarioType.Abstention,
        _ => BenchmarkScenarioType.BasicRetention
    };

    /// <summary>
    /// Runs a LongMemEval scenario efficiently by injecting the haystack as conversation history
    /// (via IHistoryInjectableAgent) and only making LLM calls for the query + judge.
    /// This reduces LLM calls from 300-600 per question to just 2 (query + judge).
    /// </summary>
    /// <param name="agent">Must implement IHistoryInjectableAgent for history injection.</param>
    /// <param name="scenario">A scenario produced by LoadFromFile or LoadSubset.</param>
    /// <param name="judge">The LLM judge for scoring the response.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Score 0-100 for this question, or null if agent doesn't support history injection.</returns>
    public static async Task<double?> RunEfficientAsync(
        IEvaluableAgent agent,
        MemoryTestScenario scenario,
        IMemoryJudge judge,
        CancellationToken ct = default)
    {
        if (agent is not IHistoryInjectableAgent injectable)
            return null;

        // Reset agent state
        if (agent is ISessionResettableAgent resettable)
            await resettable.ResetSessionAsync(ct);

        // Extract haystack turns directly from scenario steps (user/assistant pairs)
        var history = ExtractHaystackFromSteps(scenario.Steps);

        // Inject the entire haystack as conversation history — ZERO LLM calls
        injectable.InjectConversationHistory(history);

        // Now send only the query — 1 LLM call
        if (scenario.Queries.Count == 0) return 0;

        var query = scenario.Queries[0];
        var response = await agent.InvokeAsync(query.Question, ct);

        // Judge the response — 1 LLM call
        var judgment = await judge.JudgeAsync(response.Text, query, ct);

        return judgment.Score;
    }

    /// <summary>
    /// Extracts conversation history from scenario steps as injectable turns.
    /// Pairs user messages (Fact steps) with assistant messages (Noise steps).
    /// </summary>
    public static IReadOnlyList<(string UserMessage, string AssistantResponse)> ExtractHaystackFromSteps(
        IReadOnlyList<MemoryStep> steps)
    {
        var turns = new List<(string User, string Assistant)>();
        string? pendingUser = null;

        foreach (var step in steps)
        {
            if (step.Type == MemoryStepType.Fact && pendingUser == null)
            {
                pendingUser = step.Content;
            }
            else if (step.Type == MemoryStepType.Noise && pendingUser != null)
            {
                turns.Add((pendingUser, step.Content));
                pendingUser = null;
            }
            else if (step.Type == MemoryStepType.Fact && pendingUser != null)
            {
                turns.Add((pendingUser, "I understand."));
                pendingUser = step.Content;
            }
            else if (step.Type == MemoryStepType.Noise && pendingUser == null)
            {
                // Orphan assistant message — skip
            }
        }

        if (pendingUser != null)
            turns.Add((pendingUser, "I understand."));

        return turns;
    }

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
        public JsonElement AnswerRaw { get; set; }

        /// <summary>Answer as string, handling int/bool/array values in the dataset.</summary>
        [JsonIgnore]
        public string Answer => AnswerRaw.ValueKind switch
        {
            JsonValueKind.String => AnswerRaw.GetString() ?? "",
            JsonValueKind.Number => AnswerRaw.GetRawText(),
            _ => AnswerRaw.GetRawText()
        };

        [JsonPropertyName("question_date")]
        public string? QuestionDate { get; set; }

        [JsonPropertyName("haystack_sessions")]
        public List<List<LongMemEvalTurn>>? HaystackSessions { get; set; }

        [JsonPropertyName("haystack_session_ids")]
        public List<string>? HaystackSessionIds { get; set; }

        [JsonPropertyName("answer_session_ids")]
        public List<string>? AnswerSessionIds { get; set; }
    }

    internal class LongMemEvalTurn
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";
    }
}
