// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Integrity;

/// <summary>A run's verification outcome (§4.5).</summary>
public enum AefOutcome
{
    /// <summary>No <c>seal.json</c>, and no problem: neither verified nor failed. It rules out nothing.</summary>
    Unsealed,

    /// <summary>
    /// The seal verifies (§4.1) and the run keeps the rules across files (§3.9): internally consistent, nothing more
    /// ([SIG-7]: anyone can edit a file and re-seal; a reader says "intact (unsigned)" or shows who signed it).
    /// </summary>
    Intact,

    /// <summary>A problem of §3.9, or of §4.1 other than <c>withheld</c>.</summary>
    Invalid,
}

/// <summary>What a caller gives a run verifier beyond the run (§4.5): a trust policy ([SIG-4]) and trusted run hashes.</summary>
public sealed class AefVerifyOptions
{
    /// <summary>
    /// The caller's trust policy, or null: with one, the verification says whom <c>attestation.dsse.json</c> is signed by
    /// (<see cref="AefRunVerification.SignedBy"/>), and redactions its identities may authorize withhold blobs ([OVL-10]).
    /// </summary>
    public TrustPolicy? Policy { get; init; }

    /// <summary>
    /// Run hashes the caller trusts (from verified checkpoints, a transparency log, its own records), or null: with them,
    /// the verification says whether the run is anchored ([SIG-8]).
    /// </summary>
    public IReadOnlyCollection<string>? Anchors { get; init; }
}

/// <summary>A run verifier's report (§4.5): the outcome, every problem, and the stronger levels when the caller gave their inputs.</summary>
public sealed class AefRunVerification
{
    internal AefRunVerification(AefOutcome outcome, IReadOnlyList<AefProblem> problems, IReadOnlyList<string>? signedBy, bool? anchored, int withheld, string? runId, AefRunHash? runHash)
    {
        Outcome = outcome;
        Problems = problems;
        SignedBy = signedBy;
        Anchored = anchored;
        Withheld = withheld;
        RunId = runId;
        RunHash = runHash;
    }

    /// <summary>The outcome.</summary>
    public AefOutcome Outcome { get; }

    /// <summary>The problems of §3.9 and §4.1 together, ordered as §3.9 orders them ([CONF-2]). Overlay problems are not among them.</summary>
    public IReadOnlyList<AefProblem> Problems { get; }

    /// <summary>
    /// With a trust policy: the identities <c>attestation.dsse.json</c> verifies for (§4.4), in policy order, for an
    /// intact run (empty for any other, or without a signature that verifies). Null without a policy.
    /// </summary>
    public IReadOnlyList<string>? SignedBy { get; }

    /// <summary>
    /// With trusted run hashes: whether the run is intact and its run hash ([SEAL-4]) is one of them. Null without them.
    /// </summary>
    public bool? Anchored { get; }

    /// <summary>The number of sealed blobs authorized redactions withhold ([OVL-10]): "intact, <i>n</i> withheld".</summary>
    public int Withheld { get; }

    /// <summary>run.json's <c>runId</c>, when run.json reads and holds one.</summary>
    public string? RunId { get; }

    /// <summary>The run's run hash ([SEAL-4]); null for a folder beyond the file limit, which is not read.</summary>
    public AefRunHash? RunHash { get; }

    /// <summary>The outcome's wire name: <c>unsealed</c>, <c>intact</c> or <c>invalid</c>.</summary>
    public static string Name(AefOutcome outcome) => outcome switch
    {
        AefOutcome.Unsealed => "unsealed",
        AefOutcome.Intact => "intact",
        AefOutcome.Invalid => "invalid",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}

/// <summary>
/// The run verifier (contracts/aef/1/spec/04-integrity.md, §4.5; spec 09 §9.1): reads a run (spec 02, §3.9's reading
/// rules), checks its paths ([RUN-3]), its seal (§4.1) and the rules across files (§3.9), and reports the outcome, every
/// problem, and, given their inputs, whom the run is signed by and whether it is anchored. The overlay chain (§4.2) is
/// read only to tell a withheld blob from a missing one ([OVL-10]); its own problems are an overlay verifier's
/// (<see cref="OverlayChain"/>) and never change the outcome.
/// </summary>
public static class AefRunVerifier
{
    /// <summary>Verifies the run in <paramref name="directory"/>.</summary>
    /// <exception cref="DirectoryNotFoundException">No such folder.</exception>
    /// <exception cref="IOException">The folder or a file cannot be read.</exception>
    public static AefRunVerification Verify(string directory, AefVerifyOptions? options = null) =>
        Verify(AefRunFolder.Open(directory), options);

    /// <summary>Verifies a run folder already listed.</summary>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public static AefRunVerification Verify(AefRunFolder folder, AefVerifyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        options ??= new AefVerifyOptions();
        var signedByNone = options.Policy is null ? null : (IReadOnlyList<string>)[];
        var anchoredNot = options.Anchors is null ? (bool?)null : false;
        if (folder.OverFileLimit)
        {
            // [ENC-18]: a part of the folder is never read as the whole.
            return new AefRunVerification(AefOutcome.Invalid, folder.PathProblems, signedByNone, anchoredNot, 0, null, null);
        }

        var problems = new HashSet<AefProblem>(folder.PathProblems);
        var documents = AefRunDocuments.Read(folder);
        problems.UnionWith(documents.Problems);

        // [OVL-10]: which blobs authorized redactions withhold, from the verified batches of the chain.
        var runHash = SealVerifier.RunHashOf(folder);
        var chain = OverlayChain.Verify(folder, documents.RunId, runHash.Value, documents.ResultIds);
        var withheld = EffectiveView.AuthorizedRedactions(folder, chain, options.Policy).ToHashSet(StringComparer.Ordinal);

        var seal = SealVerifier.Verify(folder, documents.Run, withheld);
        problems.UnionWith(seal.Problems);
        if (documents.AllRead)
        {
            // §3.9 blob: a missing blob is exempt only when the run is sealed and an authorized redaction withholds it;
            // in an unsealed run "withheld" has no seal to mean anything against.
            var sealedPaths = seal.Seal.SubjectNames.ToHashSet(StringComparer.Ordinal);
            var exempt = seal.Seal.Present ? withheld : (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);
            problems.UnionWith(CrossFileRules.Check(folder, documents, sealedPaths, exempt));
        }

        var outcome = problems.Any(p => p.Code != "withheld") ? AefOutcome.Invalid
            : !seal.Seal.Present ? AefOutcome.Unsealed
            : AefOutcome.Intact;

        IReadOnlyList<string>? signedBy = signedByNone;
        if (options.Policy is { } policy && outcome == AefOutcome.Intact)
        {
            signedBy = SignedBy(folder, policy);
        }

        bool? anchored = anchoredNot;
        if (options.Anchors is { } anchors && outcome == AefOutcome.Intact)
        {
            anchored = anchors.Contains(runHash.Value, StringComparer.Ordinal);
        }

        // §3.9: two entries whose names are not Unicode strings and share a spelling are each reported.
        return new AefRunVerification(outcome, AefFolder.PerEntry(AefProblemOrder.Sort(problems), folder.IllFormed), signedBy, anchored, seal.Withheld, documents.RunId, runHash);
    }

    /// <summary>
    /// The identities <c>attestation.dsse.json</c> verifies for over <c>seal.json</c>'s exact bytes under
    /// <paramref name="policy"/> (§4.4), in policy order; none when the run has no signature, or when it is above the
    /// 56 MiB [ENC-17] allows an envelope (refused: the signature verifies for nobody, as a malformed one).
    /// </summary>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public static IReadOnlyList<string> SignedBy(AefRunFolder folder, TrustPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(policy);
        if (!folder.Has(SealVerifier.AttestationPath) || !folder.Has(SealVerifier.SealPath)
            || folder.Size(SealVerifier.AttestationPath) > AefLimits.MaxEnvelopeBytes || folder.Size(SealVerifier.SealPath) > AefLimits.MaxSealBytes)
        {
            return [];
        }

        return DsseVerifier.Verify(
            folder.Read(SealVerifier.AttestationPath, AefLimits.MaxEnvelopeBytes),
            folder.Read(SealVerifier.SealPath, AefLimits.MaxSealBytes),
            Dsse.InTotoPayloadType,
            policy).VerifiesFor;
    }
}
