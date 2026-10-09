// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Results.Signatures;

/// <summary>What is wrong with an envelope as a whole ([SIG-5]); a verification without either has none.</summary>
public enum DsseEnvelopeResult
{
    /// <summary>Not an envelope [SIG-1] accepts (<see cref="DsseEnvelope.TryParse"/>): no signature is checked.</summary>
    Malformed,

    /// <summary>
    /// The payload is not byte for byte the file the envelope signs, or its <c>payloadType</c> is not the one [SIG-1]
    /// gives that file. Each signature is still checked and reported; the envelope verifies for no identity.
    /// </summary>
    PayloadMismatch,
}

/// <summary>The result of one signature ([SIG-5]).</summary>
public enum DsseSignatureResult
{
    /// <summary>A trusted key, a valid signature: the key's identity is reported.</summary>
    Verified,

    /// <summary>The policy has no key with the signature's key id, or none of its keys verifies a signature without one.</summary>
    UntrustedKey,

    /// <summary>The trusted key with the signature's key id does not verify it (a raw r‖s included, [SIG-2]).</summary>
    Invalid,

    /// <summary>The trusted key with the signature's key id is of an algorithm AEF does not verify with ([SIG-2]): not verified.</summary>
    UnsupportedAlgorithm,
}

/// <summary>One signature's result: the key id it is reported under, the result, and the identity when verified.</summary>
/// <param name="KeyId">
/// The signature's <c>keyid</c>; for one without, the id of the trusted key that verified it, or the empty string
/// when none did ([SIG-5]).
/// </param>
/// <param name="Result">The result.</param>
/// <param name="Identity">The identity of the trusted key, when <see cref="DsseSignatureResult.Verified"/>; otherwise null.</param>
public sealed record DsseSignatureCheck(string KeyId, DsseSignatureResult Result, string? Identity);

/// <summary>
/// The verification of a DSSE envelope against the file it signs and a trust policy ([SIG-5]): the envelope's result,
/// each signature's in envelope order, and the identities the envelope verifies for.
/// </summary>
public sealed class DsseVerification
{
    internal DsseVerification(DsseEnvelopeResult? envelopeResult, string? why, IReadOnlyList<DsseSignatureCheck> signatures, IReadOnlyList<string> verifiesFor)
    {
        EnvelopeResult = envelopeResult;
        Why = why;
        Signatures = signatures;
        VerifiesFor = verifiesFor;
    }

    /// <summary><see cref="DsseEnvelopeResult.Malformed"/> or <see cref="DsseEnvelopeResult.PayloadMismatch"/>; null when the envelope is neither.</summary>
    public DsseEnvelopeResult? EnvelopeResult { get; }

    /// <summary>For people: why the envelope is malformed or mismatched; null otherwise.</summary>
    public string? Why { get; }

    /// <summary>One result per signature, in envelope order; empty when the envelope is malformed.</summary>
    public IReadOnlyList<DsseSignatureCheck> Signatures { get; }

    /// <summary>
    /// The identities the envelope verifies for, each once, in the order of the policy position of the first key that
    /// verified for each ([SIG-5]): those of the verified signatures, unless the envelope is malformed or its payload
    /// mismatched.
    /// </summary>
    public IReadOnlyList<string> VerifiesFor { get; }

    /// <summary>Whether the envelope verifies for <paramref name="identity"/> ([SIG-5]).</summary>
    public bool VerifiesForIdentity(string identity) => VerifiesFor.Contains(identity, StringComparer.Ordinal);
}

/// <summary>
/// Verifies DSSE envelopes as AEF requires (contracts/aef/1/spec/04-integrity.md, §4.4): the seal's
/// (<c>attestation.dsse.json</c>), an overlay batch's (<c>overlays/seal-&lt;nnnn&gt;.dsse.json</c>) and a checkpoint's
/// (<c>&lt;checkpoint&gt;.dsse.json</c>).
/// </summary>
public static class DsseVerifier
{
    /// <summary>
    /// Verifies an envelope given as its JSON bytes. <paramref name="file"/> is the exact bytes of the file it signs
    /// and <paramref name="payloadType"/> the type [SIG-1] gives that file (<see cref="Dsse.InTotoPayloadType"/> or
    /// <see cref="Dsse.CheckpointPayloadType"/>); <paramref name="policy"/> is the caller's ([SIG-4]). An envelope beyond
    /// the 56 MiB [ENC-17] allows is <c>malformed</c>, and verifies for no one ([SIG-1]); a caller that holds a file
    /// rather than its bytes uses <see cref="VerifyFile"/>, which does not read such a file.
    /// </summary>
    public static DsseVerification Verify(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> file, string payloadType, TrustPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(payloadType);
        ArgumentNullException.ThrowIfNull(policy);
        if (envelope.Length > AefLimits.MaxEnvelopeBytes)
        {
            return Oversized();
        }

        return DsseEnvelope.TryParse(envelope, out var parsed, out var malformed)
            ? Verify(parsed, file, payloadType, policy)
            : new DsseVerification(DsseEnvelopeResult.Malformed, malformed, [], []);
    }

    /// <summary>
    /// Verifies the envelope in the file at <paramref name="envelopePath"/> (see
    /// <see cref="Verify(ReadOnlySpan{byte}, ReadOnlySpan{byte}, string, TrustPolicy)"/>). A file beyond the 56 MiB
    /// [ENC-17] allows an envelope is <c>malformed</c> without being read, and verifies for no one ([SIG-1]).
    /// </summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static DsseVerification VerifyFile(string envelopePath, ReadOnlySpan<byte> file, string payloadType, TrustPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(envelopePath);
        ArgumentNullException.ThrowIfNull(payloadType);
        ArgumentNullException.ThrowIfNull(policy);
        return ReadEnvelopeFile(envelopePath) is { } envelope
            ? Verify(envelope, file, payloadType, policy)
            : Oversized();
    }

    /// <summary>
    /// The bytes of the envelope file at <paramref name="path"/>, or null when it is beyond the 56 MiB [ENC-17] allows an
    /// envelope: such a file is not read ([SIG-1]: <c>malformed</c> without being read, see <see cref="Oversized"/>).
    /// </summary>
    /// <exception cref="IOException">The file cannot be read, or grew beyond the limit while it was read.</exception>
    public static byte[]? ReadEnvelopeFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        if (stream.Length > AefLimits.MaxEnvelopeBytes)
        {
            return null;
        }

        // Never more than the limit plus one byte, whatever the file does while it is read.
        var buffer = new byte[(int)stream.Length];
        stream.ReadExactly(buffer);
        if (stream.ReadByte() >= 0)
        {
            throw new IOException($"{path}: the file grew while it was read.");
        }

        return buffer;
    }

    /// <summary>
    /// The verification of an envelope beyond the 56 MiB [ENC-17] allows one: <c>malformed</c>, not read, no
    /// per-signature results, and it verifies for no one ([SIG-1], [SIG-5]).
    /// </summary>
    public static DsseVerification Oversized() =>
        new(DsseEnvelopeResult.Malformed, $"beyond the {AefLimits.MaxEnvelopeBytes} bytes [ENC-17] allows a DSSE envelope: not read ([SIG-1])", [], []);

    /// <summary>
    /// Verifies a parsed envelope ([SIG-5]). Each signature is checked over the envelope's own pre-authentication
    /// encoding: one with a key id only against the trusted key with that id; one without against every trusted P-256
    /// or Ed25519 key in policy order (keys of another algorithm are skipped), reported under the id of the first that
    /// verifies it. The payload must be
    /// <paramref name="file"/>'s bytes with <paramref name="payloadType"/> (<c>payload-mismatch</c> otherwise, after
    /// which the signatures are still reported but the envelope verifies for no identity).
    /// </summary>
    public static DsseVerification Verify(DsseEnvelope envelope, ReadOnlySpan<byte> file, string payloadType, TrustPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(payloadType);
        ArgumentNullException.ThrowIfNull(policy);

        var message = envelope.PreAuthenticationEncoding();
        var results = new List<DsseSignatureCheck>(envelope.Signatures.Count);
        var verifiedBy = new HashSet<TrustedKey>(ReferenceEqualityComparer.Instance);
        foreach (var signature in envelope.Signatures)
        {
            var (check, key) = Check(signature, message, policy);
            results.Add(check);
            if (key is not null)
            {
                verifiedBy.Add(key);
            }
        }

        string? mismatch = !envelope.Payload.SequenceEqual(file) ? "the payload is not the file's bytes"
            : !string.Equals(envelope.PayloadType, payloadType, StringComparison.Ordinal) ? $"payloadType is not {payloadType}"
            : null;
        IReadOnlyList<string> verifiesFor = mismatch is not null
            ? []
            : [.. policy.Keys.Where(verifiedBy.Contains).Select(k => k.Identity).Distinct(StringComparer.Ordinal)];
        return new DsseVerification(mismatch is null ? null : DsseEnvelopeResult.PayloadMismatch, mismatch, results, verifiesFor);
    }

    /// <summary>The wire name of an envelope result: <c>malformed</c>, <c>payload-mismatch</c>.</summary>
    public static string Name(DsseEnvelopeResult result) => result switch
    {
        DsseEnvelopeResult.Malformed => "malformed",
        DsseEnvelopeResult.PayloadMismatch => "payload-mismatch",
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    /// <summary>The wire name of a signature result: <c>verified</c>, <c>untrusted-key</c>, <c>invalid</c>, <c>unsupported-algorithm</c>.</summary>
    public static string Name(DsseSignatureResult result) => result switch
    {
        DsseSignatureResult.Verified => "verified",
        DsseSignatureResult.UntrustedKey => "untrusted-key",
        DsseSignatureResult.Invalid => "invalid",
        DsseSignatureResult.UnsupportedAlgorithm => "unsupported-algorithm",
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    /// <summary>One signature's result, and the trusted key that verified it (null when none did).</summary>
    private static (DsseSignatureCheck Check, TrustedKey? Key) Check(DsseSignature signature, byte[] message, TrustPolicy policy)
    {
        if (signature.KeyId.Length > 0)
        {
            var key = policy.Find(signature.KeyId);
            if (key is null)
            {
                return (new(signature.KeyId, DsseSignatureResult.UntrustedKey, null), null);
            }
            if (key.PublicKey.Algorithm == AefKeyAlgorithm.Unsupported)
            {
                return (new(signature.KeyId, DsseSignatureResult.UnsupportedAlgorithm, null), null);
            }
            return key.PublicKey.Verify(message, signature.Sig)
                ? (new(signature.KeyId, DsseSignatureResult.Verified, key.Identity), key)
                : (new(signature.KeyId, DsseSignatureResult.Invalid, null), null);
        }

        // No key id: every trusted key AEF can verify with, in policy order; the first that verifies names the result.
        foreach (var key in policy.Keys)
        {
            if (key.PublicKey.Algorithm != AefKeyAlgorithm.Unsupported && key.PublicKey.Verify(message, signature.Sig))
            {
                return (new(key.KeyId, DsseSignatureResult.Verified, key.Identity), key);
            }
        }
        return (new("", DsseSignatureResult.UntrustedKey, null), null);
    }
}
