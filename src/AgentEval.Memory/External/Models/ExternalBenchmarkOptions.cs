// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.External.Models;

/// <summary>
/// Configuration options for running an external benchmark.
/// </summary>
public class ExternalBenchmarkOptions
{
    /// <summary>Maximum number of questions to run (null = all, runs every question in the dataset). Default: null.</summary>
    public int? MaxQuestions { get; init; } = null;

    /// <summary>
    /// Use stratified sampling to ensure proportional representation of each question type.
    /// When false, questions are shuffled then truncated. Default: true.
    /// </summary>
    public bool StratifiedSampling { get; init; } = true;

    /// <summary>
    /// Preserve session boundaries when formatting history for injection.
    /// Default: true.
    /// </summary>
    public bool PreserveSessionBoundaries { get; init; } = true;

    /// <summary>
    /// Include timestamps in injected history (from dataset metadata).
    /// Default: true.
    /// </summary>
    public bool IncludeTimestamps { get; init; } = true;

    /// <summary>
    /// Random seed for reproducible sampling. Null = non-deterministic.
    /// </summary>
    public int? RandomSeed { get; init; }

    /// <summary>
    /// Dataset mode identifier. Meaning is benchmark-specific
    /// (e.g., "Oracle", "S", "M" for LongMemEval). Default: null.
    /// </summary>
    public string? DatasetMode { get; init; }

    /// <summary>
    /// Optional path to the dataset file. Null = use embedded subset.
    /// </summary>
    public string? DatasetPath { get; init; }

    /// <summary>
    /// Controls how conversation history is injected into the agent.
    /// Default: <see cref="HistoryInjectionMode.TextBlob"/> — matches the original LongMemEval paper's
    /// prompt format and works with any agent.
    /// </summary>
    /// <remarks>
    /// TextBlob is the default because it matches the LongMemEval paper methodology and ensures
    /// history is visible to all middleware, context providers, and memory pipelines.
    /// Set to <see cref="HistoryInjectionMode.StructuredChatHistory"/> to force fast structured injection
    /// (requires IHistoryInjectableAgent). Set to <see cref="HistoryInjectionMode.Auto"/> to let the
    /// runner choose based on agent capabilities.
    /// </remarks>
    public HistoryInjectionMode HistoryInjectionMode { get; init; } = HistoryInjectionMode.TextBlob;
}
