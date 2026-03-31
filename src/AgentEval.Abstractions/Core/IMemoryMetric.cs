// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>
/// Marker interface for memory evaluation metrics.
/// Memory metrics assess an agent's ability to retain, recall, and reason
/// about information across conversational context.
/// </summary>
/// <remarks>
/// Memory metrics typically extract a <c>MemoryEvaluationResult</c>
/// from the <see cref="EvaluationContext"/> property bag rather than reading
/// <see cref="EvaluationContext.Input"/> and <see cref="EvaluationContext.Output"/> directly.
/// Use <c>MemoryEvaluationContextExtensions.ToEvaluationContext()</c> to bridge
/// a memory evaluation result into the standard context pipeline.
/// </remarks>
public interface IMemoryMetric : IMetric { }
