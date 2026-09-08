// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Meta;

namespace AgentEval.Benchmarks;

/// <summary>
/// One check's answer on one case: the meta-lane tuple, and the full result it came from.
/// </summary>
/// <remarks>
/// <para>
/// Both, deliberately. <see cref="Observation"/> is the five-field tuple the whole meta layer is
/// defined over (ADR-030 §4.1) — floors, controls, exact tests and rep collapse read nothing else,
/// which is what keeps them portable to a consumer that has never heard of
/// <see cref="EvalResult"/>. <see cref="Result"/> is kept beside it because a p-value with no
/// evidence behind it is a number nobody can act on: the reader who asks "why did case 12 fail?"
/// needs the summary, the evidence and the floor's derivation, and those live on the result.
/// </para>
/// <para>
/// The direction is one-way and stays that way: a result becomes an observation, never the reverse
/// (§4.6). Nothing here reads <see cref="Result"/> to decide the observation's
/// <see cref="MeasurementState"/> after the fact.
/// </para>
/// </remarks>
/// <param name="CheckKey">The eval key this observation belongs to.</param>
/// <param name="Observation">The meta-lane tuple: case, arm, value, state.</param>
/// <param name="Result">The full result the observation was derived from.</param>
public sealed record CheckObservation(string CheckKey, Observation Observation, EvalResult Result);
