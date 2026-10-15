// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Integrity;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Interop;

/// <summary>Creates and verifies artifact bindings against an owned copy of selected evidence.</summary>
public interface IArtifactCompanionService
{
    /// <summary>Creates a producer assertion after verifying a sealed run. Does not write files.</summary>
    ArtifactCompanion Create(string directory, string name, string artifactSha256,
        TrustPolicy? evidencePolicy = null, CancellationToken cancellationToken = default);

    /// <summary>Checks independent artifact, evidence and signature outcomes. Does not decide evaluation success.</summary>
    ArtifactCompanionVerification Verify(ArtifactCompanion companion, string artifactSha256, string directory,
        byte[]? envelope = null, TrustPolicy? policy = null, CancellationToken cancellationToken = default);

    /// <summary>Signs exact companion bytes; caller retains ownership of the signer.</summary>
    byte[] Sign(ArtifactCompanion companion, IAefSigner signer);
}

/// <summary>Independent binding checks; no field represents an evaluation pass or execution proof.</summary>
public sealed class ArtifactCompanionVerification
{
    internal ArtifactCompanionVerification(bool artifact, bool seal, bool runHash,
        AefRunVerification evidence, DsseVerification? signature, bool policySupplied)
    {
        ArtifactMatches = artifact;
        SealMatches = seal;
        RunHashMatches = runHash;
        Evidence = evidence;
        Signature = signature;
        PolicySupplied = policySupplied;
    }

    /// <summary>Whether the explicitly selected artifact digest matches.</summary>
    public bool ArtifactMatches { get; }
    /// <summary>Whether exact seal bytes match; false when no seal is present.</summary>
    public bool SealMatches { get; }
    /// <summary>Whether the evidence verifier's run hash matches the declared identity.</summary>
    public bool RunHashMatches { get; }
    /// <summary>Base evidence outcome, including withholding and its separate signer identities.</summary>
    public AefRunVerification Evidence { get; }
    /// <summary>Null for absent envelope; otherwise exact-payload and signature checks.</summary>
    public DsseVerification? Signature { get; }
    /// <summary>False means the envelope was checked using an empty trust policy.</summary>
    public bool PolicySupplied { get; }
    /// <summary>Byte binding and base evidence integrity only, independent of companion signature trust.</summary>
    public bool IntactBinding => ArtifactMatches && SealMatches && RunHashMatches && Evidence.Outcome == AefOutcome.Intact;
}

/// <summary>BCL-only bounded companion service. Source files are copied before verification.</summary>
public sealed class ArtifactCompanionService : IArtifactCompanionService
{
    private readonly AefStagingOptions _limits;

    /// <summary>Uses finite capture limits; defaults to 1000 files and 64 MiB total.</summary>
    public ArtifactCompanionService(AefStagingOptions? limits = null)
    {
        _limits = limits ?? new();
        _limits.Validate();
    }

    /// <inheritdoc/>
    public ArtifactCompanion Create(string directory, string name, string artifactSha256,
        TrustPolicy? evidencePolicy = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Validate caller fields before touching evidence. These placeholder hashes are never returned.
        _ = ArtifactCompanion.Create(name, artifactSha256, new string('0', 64), new string('0', 64));
        using var snapshot = AefOwnedSnapshot.Capture(directory, _limits, cancellationToken);
        var folder = AefRunFolder.Open(snapshot.FullName);
        var verification = AefRunVerifier.Verify(folder, new() { Policy = evidencePolicy });
        cancellationToken.ThrowIfCancellationRequested();
        if (verification.Outcome != AefOutcome.Intact || verification.RunHash is not { Sealed: true } hash)
            throw new AefInputException("Expected: intact sealed evidence. Actual: unsealed or invalid run. " +
                "Suggestions: verify and seal the selected evidence. Because: a companion must bind a verified seal.");
        return ArtifactCompanion.Create(name, artifactSha256,
            Hash(folder.Read(SealVerifier.SealPath, AefLimits.MaxSealBytes)), hash.Value);
    }

    /// <inheritdoc/>
    public ArtifactCompanionVerification Verify(ArtifactCompanion companion, string artifactSha256, string directory,
        byte[]? envelope = null, TrustPolicy? policy = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(companion);
        _ = ArtifactCompanion.Create("selected", artifactSha256, companion.SealSha256, companion.RunHash);
        if (envelope is { Length: > ArtifactCompanion.MaxEnvelopeBytes })
            throw new AefLimitException("Expected: companion envelope at most 128 KiB. Actual: oversized envelope. " +
                "Suggestions: supply the bounded companion envelope. Because: verification has finite memory limits.");
        var signature = envelope is null ? null : DsseVerifier.Verify(envelope.ToArray(), companion.ToBytes(),
            Dsse.InTotoPayloadType, policy ?? TrustPolicy.Empty);
        using var snapshot = AefOwnedSnapshot.Capture(directory, _limits, cancellationToken);
        var folder = AefRunFolder.Open(snapshot.FullName);
        var verification = AefRunVerifier.Verify(folder, new() { Policy = policy });
        cancellationToken.ThrowIfCancellationRequested();
        var sealMatches = folder.Has(SealVerifier.SealPath) &&
            Hash(folder.Read(SealVerifier.SealPath, AefLimits.MaxSealBytes)) == companion.SealSha256;
        return new(artifactSha256 == companion.ArtifactSha256, sealMatches,
            verification.RunHash is { Sealed: true } hash && hash.Value == companion.RunHash,
            verification, signature, policy is not null);
    }

    /// <inheritdoc/>
    public byte[] Sign(ArtifactCompanion companion, IAefSigner signer)
    {
        ArgumentNullException.ThrowIfNull(companion);
        ArgumentNullException.ThrowIfNull(signer);
        var bytes = DsseEnvelope.Create(Dsse.InTotoPayloadType, companion.ToBytes(), signer).ToJson();
        if (bytes.Length > ArtifactCompanion.MaxEnvelopeBytes)
            throw new AefLimitException("Expected: companion envelope at most 128 KiB. Actual: oversized signer output. " +
                "Suggestions: use a supported AEF signer. Because: the envelope must remain bounded.");
        return bytes;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
