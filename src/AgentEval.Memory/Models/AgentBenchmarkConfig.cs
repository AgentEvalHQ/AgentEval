// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text;

namespace AgentEval.Memory.Models;

/// <summary>
/// Full agent configuration captured at benchmark time.
/// The user provides this when creating a baseline via <c>result.ToBaseline(name, config)</c>.
/// <see cref="ConfigurationId"/> is computed deterministically from memory-affecting properties,
/// enabling automatic timeline vs. comparison routing in reports.
/// </summary>
public class AgentBenchmarkConfig
{
    /// <summary>Agent name (e.g., "WeatherAssistant").</summary>
    public required string AgentName { get; init; }

    /// <summary>Agent type/adapter (e.g., "MEAI ChatClientAgentAdapter").</summary>
    public string? AgentType { get; init; }

    /// <summary>Model identifier (e.g., "gpt-4o").</summary>
    public string? ModelId { get; init; }

    /// <summary>Model version (e.g., "2025-01-01").</summary>
    public string? ModelVersion { get; init; }

    /// <summary>Temperature setting.</summary>
    public double? Temperature { get; init; }

    /// <summary>Max tokens setting.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Reducer strategy description (e.g., "SlidingWindowChatReducer(window:50)").</summary>
    public string? ReducerStrategy { get; init; }

    /// <summary>
    /// All context providers in the agent's pipeline.
    /// A real agent often stacks multiple: chat history + semantic search + user preferences.
    /// </summary>
    public IReadOnlyList<string> ContextProviders { get; init; } = [];

    /// <summary>Primary memory provider (e.g., "InMemoryChatHistoryProvider").</summary>
    public string? MemoryProvider { get; init; }

    /// <summary>Arbitrary key-value config (embedding model, vector store, session timeout, etc.).</summary>
    public IReadOnlyDictionary<string, string> CustomConfig { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Deterministic 12-char hex hash of memory-affecting properties.
    /// Same config produces the same ID every time.
    /// Reports use this to route: same ID = timeline (progression), different ID = radar (comparison).
    /// </summary>
    public string ConfigurationId => ComputeConfigurationId();

    private string ComputeConfigurationId()
    {
        var customConfigSegment = string.Join(";",
            CustomConfig.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        var key = string.Join("|",
            AgentName ?? "",
            ModelId ?? "",
            ModelVersion ?? "",
            ReducerStrategy ?? "",
            MemoryProvider ?? "",
            string.Join(",", ContextProviders.OrderBy(p => p)),
            customConfigSegment);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
    }
}
