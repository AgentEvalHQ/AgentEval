// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Agentic.Safety;

/// <summary>
/// Pluggable interface for delegating content-safety classification to an external
/// service (e.g., Azure AI Content Safety).
/// <para>
/// When this is registered in the DI container, content-safety evaluators
/// (<c>HateUnfairnessEval</c>, <c>SexualEval</c>, <c>ViolenceEval</c>,
/// <c>SelfHarmEval</c>) delegate to this client for their primary classification.
/// When not registered, those evaluators fall back to their LLM-judge implementations.
/// </para>
/// <para>
/// Provide a concrete implementation backed by the Azure AI Content Safety REST API
/// (<c>https://westus.api.cognitive.microsoft.com/contentsafety/text:analyze?api-version=2024-09-01</c>)
/// or any equivalent service. The <see cref="NullContentSafetyClient"/> ships as the
/// default no-op implementation for local and offline use.
/// </para>
/// <para>
/// Source: plan-05 A4.8 (Azure Content Safety integration interface).
/// </para>
/// </summary>
public interface IContentSafetyClient
{
    /// <summary>
    /// Classifies <paramref name="content"/> across the four standard content-safety
    /// dimensions.
    /// </summary>
    /// <param name="content">
    /// The text to classify. Typically the agent's response or a tool-call result.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A dictionary mapping category name to a normalised severity score in [0, 1]:
    /// <list type="bullet">
    ///   <item><term><c>"hate"</c></term><description>Hate speech or unfair treatment based on identity.</description></item>
    ///   <item><term><c>"sexual"</c></term><description>Sexual content.</description></item>
    ///   <item><term><c>"violence"</c></term><description>Violent content.</description></item>
    ///   <item><term><c>"self_harm"</c></term><description>Content that promotes or depicts self-harm.</description></item>
    /// </list>
    /// A score of <c>0.0</c> means no flagged content was detected; <c>1.0</c> means
    /// maximum severity for that category.
    /// </returns>
    Task<IReadOnlyDictionary<string, double>> ClassifyAsync(
        string content,
        CancellationToken ct = default);
}

/// <summary>
/// Default null implementation of <see cref="IContentSafetyClient"/>.
/// Returns <c>0.0</c> (no flagged content) for every category.
/// <para>
/// Use this when no real Content Safety service is configured; content-safety
/// evaluators that receive this client fall back to their LLM-judge paths.
/// </para>
/// </summary>
public sealed class NullContentSafetyClient : IContentSafetyClient
{
    /// <summary>
    /// The singleton instance. Use this to avoid allocating a new object on every
    /// evaluator construction when no real client is needed.
    /// </summary>
    public static readonly NullContentSafetyClient Instance = new();

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<string, double>> ClassifyAsync(
        string content,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, double>>(
            new Dictionary<string, double>
            {
                ["hate"]      = 0.0,
                ["sexual"]    = 0.0,
                ["violence"]  = 0.0,
                ["self_harm"] = 0.0,
            });
}
