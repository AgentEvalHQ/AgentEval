// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.Evals.Agentic;

/// <summary>
/// DI extension methods for the agentic evaluator suite.
/// </summary>
public static class AgenticServiceCollectionExtensions
{
    /// <summary>
    /// Hook for registering the agentic evaluator suite. Today the agentic
    /// evaluators are constructed directly (consumers <c>new</c> them) and
    /// there are no per-evaluator services to wire — this extension is a
    /// stable hook so the umbrella <c>AgentEval</c> NuGet package can
    /// reference the agentic suite symmetrically with the other
    /// <c>AddAgentEval*</c> registration methods. When per-evaluator DI
    /// surface lands in a future release, the additional registrations
    /// will go here without changing this method's signature.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAgentEvalAgentic(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
