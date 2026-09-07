// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Meta;

namespace AgentEval.Benchmarks;

/// <summary>
/// One check: an <b>atomic</b> <see cref="IEval"/> and the chance floor it is admitted with.
/// </summary>
/// <remarks>
/// <para>
/// The pairing is the point. A benchmark definition that held bare <see cref="IEval"/>s would be a
/// list of evals nobody had asked "what would an arm that understood nothing score?" — the state
/// <see cref="FloorAdmittedEval"/> exists to make unreachable. Here the floor travels WITH the eval
/// from the moment the definition is written, and <see cref="Admit"/> is the only way to turn the
/// pair into something runnable.
/// </para>
/// <para>
/// 🔴 <b>This type does not check the floor and does not check atomicity.</b> Both are the door's
/// job, and duplicating either here would create a second, drifting copy of a rule that already has
/// one owner: <see cref="FloorAdmittedEval.Admit(IEval, ChanceFloor)"/> refuses a missing floor and
/// a floor with no derivation, and its <c>Annotate</c> refuses a result carrying sub-results — which
/// is how "atomic only" is enforced, structurally, on the result rather than on a type name. A
/// composite smuggled in here is caught the first time it runs, with a message that names it.
/// </para>
/// </remarks>
/// <param name="Eval">The atomic eval this check runs.</param>
/// <param name="Floor">
/// What an arm that understood nothing would score, or <see cref="ChanceFloor.NotDerivable(string)"/>
/// with the reason no such number exists.
/// </param>
public sealed record AdmittedCheck(IEval Eval, ChanceFloor Floor)
{
    /// <summary>Puts this check through the door.</summary>
    /// <returns>The admitted eval, which annotates every result with <see cref="Floor"/>.</returns>
    /// <exception cref="ArgumentNullException"><see cref="Eval"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The door refused the pair: no floor, a floor with no derivation, a derived floor whose bar is
    /// not a probability, or an eval that is already admitted.
    /// </exception>
    public FloorAdmittedEval Admit() => FloorAdmittedEval.Admit(Eval, Floor);
}
