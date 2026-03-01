// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.DependencyInjection;

/// <summary>
/// Configuration options for AgentEval service registration.
/// </summary>
public class AgentEvalServiceOptions
{
    /// <summary>
    /// Gets or sets the service lifetime for registered services.
    /// Default is <see cref="ServiceLifetime.Scoped"/>.
    /// </summary>
    public ServiceLifetime ServiceLifetime { get; set; } = ServiceLifetime.Scoped;

    /// <summary>
    /// Gets or sets a factory for creating evaluation harness instances.
    /// If null, no evaluation harness is registered.
    /// </summary>
    public Func<IServiceProvider, IEvaluationHarness>? EvaluationHarnessFactory { get; set; }

    /// <summary>
    /// Gets or sets a factory for creating logger instances.
    /// If null, no logger is registered.
    /// </summary>
    public Func<IServiceProvider, IAgentEvalLogger>? LoggerFactory { get; set; }

    /// <summary>
    /// Gets or sets whether to enable in-memory caching for embedding generation.
    /// When enabled, repeated calls with the same text return cached results,
    /// which is beneficial for stochastic evaluation where reference texts are constant.
    /// Default is <c>false</c> (opt-in).
    /// </summary>
    public bool EnableEmbeddingCaching { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of embeddings to cache.
    /// Only used when <see cref="EnableEmbeddingCaching"/> is <c>true</c>.
    /// Default is 1000.
    /// </summary>
    public int EmbeddingCacheMaxSize { get; set; } = 1000;
}
