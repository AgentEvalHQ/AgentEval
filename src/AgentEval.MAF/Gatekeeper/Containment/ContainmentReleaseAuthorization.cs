// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Signed, short-lived authority to release one exact containment-record version. Its TTL bounds the authority
/// to release; it never expires the containment record itself.
/// </summary>
public sealed record ContainmentReleaseAuthorization
{
    /// <summary>The maximum accepted authorization lifetime.</summary>
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Creates and validates a release-authorization envelope.</summary>
    public ContainmentReleaseAuthorization(
        ContainmentTarget target,
        long expectedVersion,
        string operatorId,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        string nonce,
        string algorithm,
        int algorithmVersion,
        string keyId,
        string signature)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        if (expectedVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedVersion),
                "Containment release expected version must be positive.");
        }

        ExpectedVersion = expectedVersion;
        OperatorId = ContainmentValidation.Identity(
            operatorId,
            nameof(operatorId),
            ContainmentValidation.MaxActorLength);
        IssuedAtUtc = ContainmentValidation.Utc(issuedAtUtc, nameof(issuedAtUtc));
        ExpiresAtUtc = ContainmentValidation.Utc(expiresAtUtc, nameof(expiresAtUtc));
        if (ExpiresAtUtc <= IssuedAtUtc)
        {
            throw new ArgumentException(
                "Containment release expiry must be after issue time.",
                nameof(expiresAtUtc));
        }

        if (ExpiresAtUtc - IssuedAtUtc > MaximumLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                $"Containment release authority cannot exceed {MaximumLifetime.TotalMinutes:0} minutes.");
        }

        Nonce = ContainmentValidation.Token(
            nonce,
            nameof(nonce),
            ContainmentValidation.MaxNonceLength,
            minLength: 16);
        Algorithm = ContainmentValidation.Token(
            algorithm,
            nameof(algorithm),
            ContainmentValidation.MaxAlgorithmLength);
        if (algorithmVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(algorithmVersion),
                "Containment release algorithm version must be positive.");
        }

        AlgorithmVersion = algorithmVersion;
        KeyId = ContainmentValidation.Token(
            keyId,
            nameof(keyId),
            ContainmentValidation.MaxKeyIdLength);
        Signature = ContainmentValidation.Token(
            signature,
            nameof(signature),
            ContainmentValidation.MaxSignatureLength,
            minLength: 16);
    }

    /// <summary>The exact tenant-scoped target to release.</summary>
    public ContainmentTarget Target { get; }

    /// <summary>The active record version the operator reviewed.</summary>
    public long ExpectedVersion { get; }

    /// <summary>The normalized operator identity.</summary>
    public string OperatorId { get; }

    /// <summary>When signing authority began, in UTC.</summary>
    public DateTimeOffset IssuedAtUtc { get; }

    /// <summary>When signing authority ends, in UTC.</summary>
    public DateTimeOffset ExpiresAtUtc { get; }

    /// <summary>A bounded unique replay-prevention token.</summary>
    public string Nonce { get; }

    /// <summary>The verifier-selected signature algorithm token.</summary>
    public string Algorithm { get; }

    /// <summary>The positive signature algorithm/payload version.</summary>
    public int AlgorithmVersion { get; }

    /// <summary>The verifier-selected signing key identifier.</summary>
    public string KeyId { get; }

    /// <summary>The opaque unpadded base64url/hex-compatible signature token.</summary>
    public string Signature { get; }
}
