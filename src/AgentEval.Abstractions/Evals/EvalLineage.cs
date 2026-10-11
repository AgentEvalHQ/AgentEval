// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// Which of a composite's verdict rules decided its label, and the components that decided it: what an AEF composite
/// line records as <c>aggregation.rulePath</c> and <c>decisive</c> (AEF 1.0 [RES-5], [RES-6]). Set by
/// <c>CompositeEval</c>; it describes the verdict, it never changes it.
/// </summary>
/// <param name="RulePath">
/// <c>required-error</c>, <c>nothing-measured</c>, <c>threshold</c>, <c>severity</c>, <c>under-covered</c>,
/// <c>required-not-measured</c> or <c>failure-effect</c>, as AEF names them.
/// </param>
/// <param name="Decisive">The <see cref="EvalMetadata.Key"/>s of the components that decided it; empty when the rule
/// names none (a threshold, nothing measured).</param>
public sealed record EvalLineage(string RulePath, IReadOnlyList<string> Decisive);
