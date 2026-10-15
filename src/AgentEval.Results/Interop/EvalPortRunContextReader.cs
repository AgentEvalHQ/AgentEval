// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Ingestion;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Interop;

/// <summary>Verified base-run context with the evidence identity that covered its declaration.</summary>
public sealed class VerifiedEvalPortRunContext
{
    internal VerifiedEvalPortRunContext(string runId, string runHash, EvalPortRunContext? context, AefRunVerification evidence)
    {
        RunId = runId; RunHash = runHash; Context = context; Evidence = evidence;
    }

    /// <summary>The verified run identifier.</summary>
    public string RunId { get; }
    /// <summary>The verified sealed manifest identity.</summary>
    public string RunHash { get; }
    /// <summary>Null when the sealed run has no context extension; no isolation or group is inferred.</summary>
    public EvalPortRunContext? Context { get; }
    /// <summary>Base evidence integrity and separate signer/withholding outcomes.</summary>
    public AefRunVerification Evidence { get; }
}

/// <summary>Reads EvalPort context only from intact sealed evidence.</summary>
public interface IEvalPortRunContextReader
{
    /// <summary>Captures bounded evidence, verifies it, and optionally requires an exact run hash.</summary>
    VerifiedEvalPortRunContext Read(string directory, string? expectedRunHash = null,
        TrustPolicy? policy = null, CancellationToken cancellationToken = default);
}

/// <summary>Uses owned evidence bytes for verification and extension parsing; never modifies the source run.</summary>
public sealed class EvalPortRunContextReader : IEvalPortRunContextReader
{
    private readonly AefStagingOptions _limits;

    /// <summary>Uses finite capture limits; defaults to 1000 files and 64 MiB total.</summary>
    public EvalPortRunContextReader(AefStagingOptions? limits = null)
    {
        _limits = limits ?? new(); _limits.Validate();
    }

    /// <inheritdoc/>
    public VerifiedEvalPortRunContext Read(string directory, string? expectedRunHash = null,
        TrustPolicy? policy = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedRunHash is not null && (expectedRunHash.Length != 64 ||
            expectedRunHash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw Refuse("invalid expected run hash");
        using var snapshot = AefOwnedSnapshot.Capture(directory, _limits, cancellationToken);
        var folder = AefRunFolder.Open(snapshot.FullName);
        var evidence = AefRunVerifier.Verify(folder, new() { Policy = policy });
        cancellationToken.ThrowIfCancellationRequested();
        if (evidence.Outcome != AefOutcome.Intact || evidence.RunHash is not { Sealed: true } hash || evidence.RunId is null)
            throw Refuse("unsealed or invalid evidence");
        if (expectedRunHash is not null && expectedRunHash != hash.Value) throw Refuse("selected run hash mismatch");
        var path = "ext/" + EvalPortRunContext.ExtensionFile;
        var context = folder.Has(path) ? EvalPortRunContext.Parse(folder.Read(path, EvalPortRunContext.MaxBytes)) : null;
        cancellationToken.ThrowIfCancellationRequested();
        return new(evidence.RunId, hash.Value, context, evidence);
    }

    private static AefInputException Refuse(string actual) => new(
        $"Expected: intact sealed evidence matching the selected run. Actual: {actual}. " +
        "Suggestions: verify the run and its selection hash. Because: context declarations need evidence provenance.");
}
