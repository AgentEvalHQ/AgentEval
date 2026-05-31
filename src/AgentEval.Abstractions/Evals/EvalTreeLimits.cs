// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// Canonical limits for walking the recursive <see cref="EvalResult"/> composite tree (ARC-03).
/// </summary>
/// <remarks>
/// The depth cap was previously copy-pasted as a private <c>const 32</c> in the producer guard
/// (<c>CompositeEval.MaxNestingDepth</c>), the GraphQL consumer (<c>Query.MaxTreeWalkDepth</c>), and every
/// PDF renderer (<c>MaxRenderWalkDepth</c>) — "mirrored by comment only", so a change had to be made in
/// many places. They now all reference <see cref="MaxTreeWalkDepth"/> so the producer and every consumer
/// share one source of truth.
/// </remarks>
public static class EvalTreeLimits
{
    /// <summary>
    /// Maximum recursion depth when producing or walking an <see cref="EvalResult"/> tree. The producer
    /// (CompositeEval) throws beyond this; consumers (GraphQL resolver, PDF/Markdown renderers) stop
    /// descending. Keeping producer and consumers on the same value guarantees any tree the producer
    /// admits is fully walkable by every consumer.
    /// </summary>
    public const int MaxTreeWalkDepth = 32;
}
