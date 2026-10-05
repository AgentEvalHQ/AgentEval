// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>Pairs an eval with its weight and required flag inside a composite.</summary>
public sealed record EvalComponent(IEval Eval, double Weight = 1.0, bool Required = true)
{
    /// <summary>
    /// What this component's own measured failure does to the composite's verdict (#203 review, B6b). The default,
    /// <see cref="ComponentFailureEffect.Averaged"/>, leaves it to the aggregate score — so one dimension can fail and
    /// the composite still read PASS. A preset says <see cref="ComponentFailureEffect.Fail"/> for a dimension whose
    /// failure means the answer cannot be trusted, and <see cref="ComponentFailureEffect.Warn"/> for one whose failure
    /// means the answer is usable but not optimal.
    /// </summary>
    public ComponentFailureEffect OnFailure { get; init; }
}

/// <summary>
/// What a component's measured failure does to its composite's verdict. Only a MEASURED failure (the component ran and
/// its own verdict is fail) counts; a skipped or errored component is governed by the composite's coverage rules.
/// The effect only ever escalates a verdict, never lifts one.
/// </summary>
public enum ComponentFailureEffect
{
    /// <summary>The failure only moves the aggregate score (the behaviour before 0.44).</summary>
    Averaged = 0,

    /// <summary>A pass becomes a warn, and the summary names the component: usable, but not optimal.</summary>
    Warn = 1,

    /// <summary>The composite fails: an accuracy dimension failed, so the answer cannot be trusted.</summary>
    Fail = 2,

    /// <summary>
    /// A security gate's check: the composite fails on anything short of a pass — a failure, or a needs-review
    /// <c>warn</c> (fail-closed). Under <see cref="Fail"/> a check that only warned makes the composite warn, "not
    /// confirmed"; a gate does not let a borderline result through (#203 review round 3, B10c).
    /// </summary>
    FailUnlessPass = 3,
}
