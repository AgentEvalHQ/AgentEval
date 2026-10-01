// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Decisions;

/// <summary>
/// The preview diagnostic for AgentEval's decision-model abstraction (<see cref="IDecisionClient"/> and its request,
/// question and answer types, and the System-One transport).
/// </summary>
/// <remarks>
/// Microsoft.Extensions.AI is defining a decision-model abstraction of its own. When it ships, AgentEval will adapt to
/// it rather than keep a third public one, so these types can change shape or be replaced. Suppress the diagnostic to
/// acknowledge that.
/// </remarks>
public static class DecisionsPreview
{
    /// <summary>The diagnostic id reported on use of a preview decision type.</summary>
    public const string DiagnosticId = "AGENTEVAL_DECISIONS_PREVIEW001";
}
