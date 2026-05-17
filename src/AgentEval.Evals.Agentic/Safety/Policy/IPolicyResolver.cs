// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Agentic.Safety.Policy;

/// <summary>
/// Resolves the <see cref="ProhibitedActionPolicy"/> applicable to a given subject
/// (e.g., an agent name, a deployment identifier, or an environment tag).
/// <para>
/// Implement this interface when different agents or environments require different
/// policy definitions. For the common case where one policy covers all subjects,
/// use the built-in <see cref="StaticPolicyResolver"/>.
/// </para>
/// <para>
/// Source: plan-05 §8.5 (ProhibitedActionsEvaluator implementation card).
/// </para>
/// </summary>
public interface IPolicyResolver
{
    /// <summary>
    /// Returns the policy applicable to <paramref name="subjectId"/> (e.g., agent name
    /// or deployment identifier). Must not return <c>null</c>.
    /// </summary>
    /// <param name="subjectId">
    /// An opaque identifier for the subject being evaluated. Typically the agent name
    /// passed to <c>ProhibitedActionsEval</c>.
    /// </param>
    ProhibitedActionPolicy GetPolicyFor(string subjectId);
}

/// <summary>
/// Default resolver that returns the same <see cref="ProhibitedActionPolicy"/> for
/// every subject. Use this when a single global policy covers all agents.
/// </summary>
public sealed class StaticPolicyResolver : IPolicyResolver
{
    private readonly ProhibitedActionPolicy _policy;

    /// <summary>
    /// Initialises a <see cref="StaticPolicyResolver"/> with the given policy.
    /// </summary>
    /// <param name="policy">The policy returned for all subjects.</param>
    public StaticPolicyResolver(ProhibitedActionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    /// <inheritdoc/>
    public ProhibitedActionPolicy GetPolicyFor(string subjectId) => _policy;
}
