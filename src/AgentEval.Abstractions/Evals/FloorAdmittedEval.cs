// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

using System.Globalization;
using AgentEval.Evals.Meta;
using AgentEval.Output;

/// <summary>
/// An <see cref="IEval"/> that was ADMITTED with a chance floor, and that puts the floor it was
/// admitted under onto every result it produces. AE-04's second half.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule this exists to keep, not to waive.</b> The programme's loudest prohibition is
/// <i>"AE-04 before AE-06"</i>: wiring <see cref="IEval"/> implementations into the primary
/// agent-evaluation entry point <b>while none of them has a chance floor</b> takes a contained
/// problem and makes it the product's front door. This type does not bulk-wire those implementations
/// and does not exempt them. It makes the prohibited state <b>unreachable</b>: the only door is
/// <see cref="Admit(IEval, ChanceFloor)"/>, the door will not open without a floor, and an eval that
/// never goes through it is exactly as unwired as it was before.
/// </para>
/// <para>
/// <b>The measurement, with its derivation, because a count without one goes stale unnoticed.</b>
/// Counting files under <c>src/</c> whose type declarations name <see cref="IEval"/> as a base
/// (<c>grep -rlE "^\s*(public|internal|private|protected|sealed|abstract|partial|static)[^=]*\b(class|record|struct)\b[^=]*[:,]\s*IEval\b" --include=*.cs src/</c>)
/// against files mentioning <c>ChanceFloor</c> (<c>grep -rl "\bChanceFloor\b" --include=*.cs src/</c>):
/// <b>79</b> and <b>7</b>, and the intersection is <b>exactly one — this file</b>. Before this type
/// existed the intersection was <b>zero</b>: not one eval in the library carried a floor. That "one"
/// is the door itself and not an eval that has been floored, so the count of ADMITTED evals is still
/// zero until a caller admits one, which is the whole point. The absolute figures move with the
/// library; the derivation is what makes them checkable, and the intersection is the fact that
/// matters.
/// </para>
/// <para>
/// <b>Where the floor lands, and why not on the score.</b> ADR-030 §3.2 CUT <c>EvalScore.ChanceFloor</c>
/// and ruled that floors live in <c>Details.Dimensions["chance_floor"]</c> plus one
/// <c>EvalEvidence("chance-floor", kind, derivation)</c>. This type writes exactly that convention,
/// through <see cref="ComparabilityFacts.ChanceFloorDimension"/> and
/// <see cref="ComparabilityFacts.ChanceFloorEvidenceSource"/>, so the library's own reader
/// (<c>EvalResultPersistence.ComparabilityOf</c>) reads the floor back off the persisted run with no
/// new vocabulary and no schema change. It is written onto the ROOT result — §3.2's first objection to
/// a score-level field was that a composite's root would carry <c>chanceFloor: null</c> forever, which
/// is the one node consumers actually read.
/// </para>
/// <para>
/// ⚠ <b>A not-derivable floor writes NO dimension.</b> An absent floor is not a zero floor — that is
/// how a metric gets condemned at p = 0.70. The evidence entry still goes on, carrying the reason, so
/// "somebody asked and could not answer" stays distinguishable from "nobody asked" (which is the
/// no-evidence, no-dimension state, and the only one that yields a null <c>ChanceFloor</c> downstream).
/// </para>
/// <para>
/// 🔴 <b>The floor may not come from the eval's own output.</b> It is supplied at admission, before
/// anything runs, and is never read back off the result. If the wrapped eval emits its own
/// <c>chance_floor</c> dimension or <c>chance-floor</c> evidence, <see cref="EvaluateAsync"/>
/// <b>throws</b> rather than merging or overwriting: the artifact under test would be supplying the
/// bar it is judged against, which is this repository's most-repeated defect (seven confirmed
/// instances, every one flattering).
/// </para>
/// <para>
/// This type is <b>not</b> meta-evaluation and deliberately does not live in
/// <c>AgentEval.Evals.Meta</c>. ADR-030 §3.2's load-bearing rule — <i>meta-evaluation never
/// implements <see cref="IEval"/></i> — is enforced by namespace (<c>MetaLaneArchitectureTests</c>),
/// and it is about a floor or a comparison returning an <see cref="EvalResult"/> as its OWN verdict.
/// This returns the WRAPPED eval's verdict, unaltered in score, with the floor annotated beside it.
/// </para>
/// </remarks>
public sealed class FloorAdmittedEval : IEval
{
    private readonly IEval _inner;

    private FloorAdmittedEval(IEval inner, ChanceFloor floor)
    {
        _inner = inner;
        Floor = floor;
    }

    /// <summary>The floor this eval was admitted under. Supplied at admission, never derived from a result.</summary>
    public ChanceFloor Floor { get; }

    /// <summary>The eval that was admitted.</summary>
    public IEval Inner => _inner;

    /// <inheritdoc/>
    public string Key => _inner.Key;

    /// <inheritdoc/>
    public string Name => _inner.Name;

    /// <inheritdoc/>
    public string Category => _inner.Category;

    /// <inheritdoc/>
    public string Version => _inner.Version;

    /// <summary>
    /// The door. Admits <paramref name="eval"/> if and only if it arrives with a usable floor.
    /// </summary>
    /// <param name="eval">The eval to admit.</param>
    /// <param name="floor">
    /// What an arm that understands nothing would score. A <see cref="FloorState.NotDerivable"/> floor
    /// is accepted — that is the "declares it needs none" case — but only WITH A STATED REASON in
    /// <see cref="ChanceFloor.Derivation"/>.
    /// </param>
    /// <returns>The admitted eval, which annotates every result with <paramref name="floor"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="eval"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// No floor was offered; or the floor carries no derivation; or it claims to be derived and its
    /// comparison bar is not a probability. Every message names the eval.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>A blank derivation is refused whatever the state, not only when not-derivable.</b> ADR-030
    /// §3.2: "the number without its derivation is unusable" — <c>0.44</c> means nothing without k,
    /// the pool and the sentence. The library already agrees: <c>ComparabilityOf</c> refuses to
    /// promote a bar that arrives with no <c>chance-floor</c> evidence beside it and records
    /// <see cref="FloorState.NotDerivable"/> instead. Admitting such a floor here would produce a
    /// result that silently downgrades itself at the persistence boundary.
    /// </para>
    /// <para>
    /// <b>A non-probability bar is refused</b> because the persisted reader requires
    /// <c>double.IsFinite(bar)</c> and would silently reclassify a NaN floor as not-derivable, and
    /// because a bar above 1.0 is a bar nothing can ever clear.
    /// </para>
    /// </remarks>
    public static FloorAdmittedEval Admit(IEval eval, ChanceFloor floor)
    {
        ArgumentNullException.ThrowIfNull(eval);

        if (floor is null)
        {
            throw new ArgumentException(
                $"Eval '{Describe(eval)}' was offered with NO chance floor, so it was refused. Every eval "
                + "admitted here must arrive with what an arm that understands nothing would score, or with a "
                + $"{nameof(ChanceFloor)}.{nameof(ChanceFloor.NotDerivable)}(reason) saying why no such number "
                + "exists. An eval with no floor cannot be shown to have measured anything.",
                nameof(floor));
        }

        if (string.IsNullOrWhiteSpace(floor.Derivation))
        {
            throw new ArgumentException(
                $"Eval '{Describe(eval)}' was offered a '{floor.State}' chance floor with NO derivation, so it "
                + "was refused. A floor's number without its derivation is unusable and a floor's ABSENCE without "
                + "its reason is worse: 'nobody could derive one' and 'nobody tried' are different facts, and "
                + "only the stated reason separates them.",
                nameof(floor));
        }

        if (floor.State is FloorState.Derived)
        {
            double bar = floor.ComparisonBar;
            if (!double.IsFinite(bar) || bar < 0.0 || bar > 1.0)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture,
                        $"Eval '{Describe(eval)}' was offered a DERIVED chance floor whose comparison bar is {bar}, ")
                    + "which is not a probability, so it was refused. A non-finite bar is silently reclassified as "
                    + "not-derivable by the persistence reader, and a bar outside [0,1] is one nothing can clear.",
                    nameof(floor));
            }
        }

        return new FloorAdmittedEval(eval, floor);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// The wrapped eval emitted its own chance-floor dimension or evidence. The bar an eval is judged
    /// against may not be supplied by the eval.
    /// </exception>
    public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        var result = await _inner.EvaluateAsync(input, ct).ConfigureAwait(false);
        return Annotate(result);
    }

    /// <summary>
    /// Writes <see cref="Floor"/> onto a result using ADR-030 §3.2's convention.
    /// </summary>
    /// <param name="result">The wrapped eval's result.</param>
    /// <returns>The same verdict, with the floor recorded beside it.</returns>
    /// <exception cref="InvalidOperationException">The result already carries a floor of its own.</exception>
    internal EvalResult Annotate(EvalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        bool selfReportedDimension =
            result.Details.Dimensions?.ContainsKey(ComparabilityFacts.ChanceFloorDimension) == true;
        bool selfReportedEvidence = result.Details.Evidence?.Any(e => string.Equals(
            e.Source, ComparabilityFacts.ChanceFloorEvidenceSource, StringComparison.Ordinal)) == true;

        if (selfReportedDimension || selfReportedEvidence)
        {
            throw new InvalidOperationException(
                $"Eval '{Key}' produced a result that already carries its own "
                + $"'{ComparabilityFacts.ChanceFloorDimension}' "
                + (selfReportedDimension && selfReportedEvidence ? "dimension and evidence"
                    : selfReportedDimension ? "dimension" : "evidence")
                + ". The floor an eval is judged against may not be supplied by the eval — that is the "
                + "gate-self-examination failure, and it fails in the flattering direction. Remove the "
                + "self-reported floor, or admit the eval with the floor it actually wants.");
        }

        var dimensions = Floor.State is FloorState.Derived
            ? WithDimension(result.Details.Dimensions, ComparabilityFacts.ChanceFloorDimension, Floor.ComparisonBar)
            // ⚠ NOT a zero. A not-derivable floor writes no number at all; only the reason.
            : result.Details.Dimensions;

        var evidence = Append(
            result.Details.Evidence,
            new EvalEvidence(
                ComparabilityFacts.ChanceFloorEvidenceSource,
                Floor.Kind,
                Floor.Derivation));

        return result with
        {
            Details = result.Details with { Dimensions = dimensions, Evidence = evidence },
        };
    }

    private static IReadOnlyDictionary<string, double> WithDimension(
        IReadOnlyDictionary<string, double>? existing, string key, double value)
    {
        var merged = existing is null
            ? new Dictionary<string, double>(1)
            : new Dictionary<string, double>(existing);
        merged[key] = value;
        return merged;
    }

    private static IReadOnlyList<EvalEvidence> Append(IReadOnlyList<EvalEvidence>? existing, EvalEvidence item)
    {
        var merged = existing is null ? new List<EvalEvidence>(1) : new List<EvalEvidence>(existing);
        merged.Add(item);
        return merged;
    }

    private static string Describe(IEval eval)
    {
        try { return string.IsNullOrWhiteSpace(eval.Key) ? eval.GetType().Name : eval.Key; }
        catch (Exception) { return eval.GetType().Name; }
    }
}
