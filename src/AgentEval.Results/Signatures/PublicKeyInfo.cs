// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;

namespace AgentEval.Results.Signatures;

/// <summary>The signature algorithm of a public key, as [SIG-2] sorts them.</summary>
public enum AefKeyAlgorithm
{
    /// <summary>
    /// A key of another algorithm (RSA, ECDSA on another curve, an EC key without a named curve, an Ed25519 identifier
    /// with parameters; see <see cref="PublicKeyInfo.UnsupportedReason"/>). A signature such a key would check is
    /// <c>unsupported-algorithm</c>, which counts as not verified ([SIG-2]).
    /// </summary>
    Unsupported,

    /// <summary>ECDSA over NIST P-256 with SHA-256 (<c>id-ecPublicKey</c>, <c>prime256v1</c>; RFC 5480).</summary>
    EcdsaP256,

    /// <summary>Ed25519 (<c>id-Ed25519</c>; RFC 8410).</summary>
    Ed25519,
}

/// <summary>
/// A public key as a DER-encoded SubjectPublicKeyInfo (RFC 5280 §4.1.2.7), the form a trust policy lists it in and its
/// key id is computed from, over the bytes as given ([SIG-3]). Parsing is strict whatever the algorithm: one DER
/// <c>SEQUENCE { AlgorithmIdentifier, BIT STRING }</c> with nothing after it, minimal lengths, and a key BIT STRING
/// without unused bits. A P-256 key (<c>id-ecPublicKey</c> with the named curve <c>prime256v1</c>) is usable when it is
/// an uncompressed point on the curve (RFC 5480 §2.1.1, §2.2); an Ed25519 key (<c>id-Ed25519</c> without parameters)
/// when it is 32 bytes that decode to a point not of small order (RFC 8410 §3, RFC 8032 §5.1.3) ([SIG-2]).
/// </summary>
public sealed class PublicKeyInfo
{
    private const string IdEcPublicKey = "1.2.840.10045.2.1";
    private const string Prime256V1 = "1.2.840.10045.3.1.7";
    private const string IdEd25519 = "1.3.101.112";

    private readonly byte[] _der;
    private readonly byte[] _key;

    private PublicKeyInfo(byte[] der, AefKeyAlgorithm algorithm, byte[] key, string? unsupportedReason)
    {
        _der = der;
        _key = key;
        Algorithm = algorithm;
        UnsupportedReason = unsupportedReason;
        KeyId = Signatures.KeyId.Of(der);
    }

    /// <summary>The key's algorithm; <see cref="AefKeyAlgorithm.Unsupported"/> for one AEF cannot verify with.</summary>
    public AefKeyAlgorithm Algorithm { get; }

    /// <summary>Why the key is <see cref="AefKeyAlgorithm.Unsupported"/>; null for a key that verifies.</summary>
    public string? UnsupportedReason { get; }

    /// <summary>The key id ([SIG-3]): <c>sha256:</c> and the lower-case hex SHA-256 of the DER bytes.</summary>
    public string KeyId { get; }

    /// <summary>The DER-encoded SubjectPublicKeyInfo, exactly as given.</summary>
    public ReadOnlySpan<byte> Der => _der;

    /// <summary>
    /// Reads a SubjectPublicKeyInfo. One of another algorithm (any AlgorithmIdentifier but <c>id-ecPublicKey</c> with
    /// the named curve <c>prime256v1</c> and <c>id-Ed25519</c> without parameters) is read as
    /// <see cref="AefKeyAlgorithm.Unsupported"/> ([SIG-2]); a P-256 or Ed25519 key that is not usable is refused, so that
    /// a trust policy holding one is refused as a whole ([SIG-3]).
    /// </summary>
    /// <exception cref="FormatException">
    /// The bytes are not a DER SubjectPublicKeyInfo, whatever the algorithm (BER that is not DER, or a key BIT STRING
    /// with unused bits: the key id is over DER bytes, never re-encoded); or the key is a P-256 key that is not an
    /// uncompressed point on the curve, or an Ed25519 key that is not 32 bytes decoding to a point not of small order.
    /// </exception>
    public static PublicKeyInfo FromDer(ReadOnlySpan<byte> der)
    {
        var bytes = der.ToArray();
        if (!TryRead(bytes, out var algorithm, out var parameters, out var unusedBits, out var key))
        {
            throw new FormatException("Not a DER SubjectPublicKeyInfo (SEQUENCE { AlgorithmIdentifier, BIT STRING }).");
        }
        if (unusedBits != 0)
        {
            throw new FormatException("Not a DER SubjectPublicKeyInfo: the key BIT STRING has unused bits ([SIG-3]).");
        }

        switch (algorithm)
        {
            case IdEcPublicKey:
                if (parameters is null || !IsObjectIdentifier(parameters, out var curve))
                {
                    return Unsupported(bytes, "EC parameters other than a named curve (RFC 5480 §2.1.1)");
                }
                if (curve != Prime256V1)
                {
                    return Unsupported(bytes, $"an EC key on another curve ({curve})");
                }
                if (key.Length == 1 + EcdsaP256.ScalarSize && key[0] is 0x02 or 0x03)
                {
                    throw new FormatException("An unusable P-256 key: a compressed point ([SIG-2]).");
                }
                return EcdsaP256.IsValidPublicKey(key)
                    ? new PublicKeyInfo(bytes, AefKeyAlgorithm.EcdsaP256, key, null)
                    : throw new FormatException("An unusable P-256 key: not an uncompressed point on the curve ([SIG-2]).");

            case IdEd25519:
                if (parameters is not null)
                {
                    return Unsupported(bytes, "an Ed25519 identifier with parameters, which RFC 8410 §3 requires to be absent");
                }
                if (!Ed25519.IsValidPublicKey(key))
                {
                    throw new FormatException("An unusable Ed25519 key: not 32 bytes that decode to a point (RFC 8032 §5.1.3, [SIG-2]).");
                }
                return Ed25519.IsUsablePublicKey(key)
                    ? new PublicKeyInfo(bytes, AefKeyAlgorithm.Ed25519, key, null)
                    : throw new FormatException("An unusable Ed25519 key: a point of small order, which verifies forged signatures ([SIG-2]).");

            default:
                return Unsupported(bytes, $"algorithm {algorithm}");
        }
    }

    /// <summary>
    /// Reads a PEM <c>PUBLIC KEY</c> block in RFC 7468's strict form, as [SIG-3] requires of a trust policy's
    /// <c>publicKey</c>: the <c>-----BEGIN PUBLIC KEY-----</c> line, lines of base64 (standard alphabet, with padding)
    /// and nothing else, each of 64 characters but the last (1 to 64), the <c>-----END PUBLIC KEY-----</c> line; LF or
    /// CRLF line ends, and an optional final line end. No text before or after, no blank line, no whitespace inside a
    /// line.
    /// </summary>
    /// <exception cref="FormatException">Not such a block, or not a SubjectPublicKeyInfo (<see cref="FromDer"/>).</exception>
    public static PublicKeyInfo FromPem(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        const string Begin = "-----BEGIN PUBLIC KEY-----";
        const string End = "-----END PUBLIC KEY-----";

        var text = pem.EndsWith('\n') ? pem[..^1] : pem;
        var lines = text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToArray();
        if (lines.Length < 3 || lines[0] != Begin || lines[^1] != End)
        {
            throw new FormatException("Not a PEM PUBLIC KEY block.");
        }

        // RFC 7468 §3's strict form: every base64 line holds 64 characters, but the last, which holds 1 to 64.
        var bodyLines = lines[1..^1];
        if (bodyLines[..^1].Any(l => l.Length != 64) || bodyLines[^1].Length is < 1 or > 64)
        {
            throw new FormatException("The PEM body's lines are not of 64 characters, the last of 1 to 64 (RFC 7468, [SIG-3]).");
        }

        var body = string.Concat(bodyLines);
        if (bodyLines.Any(l => l.Length == 0) || body.IndexOfAny(['-', '_']) >= 0
            || !Base64Strict.TryDecode(body, out var der) || (body.Length % 4) != 0)
        {
            throw new FormatException("The PEM body is not base64 in the standard alphabet with padding.");
        }
        return FromDer(der);
    }

    /// <summary>The SubjectPublicKeyInfo of the P-256 point (<paramref name="x"/>, <paramref name="y"/>), each 32 bytes big-endian.</summary>
    /// <exception cref="ArgumentException">Not a point on P-256.</exception>
    public static PublicKeyInfo FromP256(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        if (!EcdsaP256.IsOnCurve(x, y))
        {
            throw new ArgumentException("Not a point on P-256.", nameof(x));
        }
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(IdEcPublicKey);
                writer.WriteObjectIdentifier(Prime256V1);
            }
            writer.WriteBitString([0x04, .. x, .. y]);
        }
        return FromDer(writer.Encode());
    }

    /// <summary>The SubjectPublicKeyInfo of a 32-byte Ed25519 public key.</summary>
    /// <exception cref="ArgumentException">Not a usable key: 32 bytes that decode to a point not of small order.</exception>
    public static PublicKeyInfo FromEd25519(ReadOnlySpan<byte> publicKey)
    {
        if (!Ed25519.IsUsablePublicKey(publicKey))
        {
            throw new ArgumentException("Not an Ed25519 public key.", nameof(publicKey));
        }
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(IdEd25519);
            }
            writer.WriteBitString(publicKey);
        }
        return FromDer(writer.Encode());
    }

    /// <summary>The key as a PEM <c>PUBLIC KEY</c> block: 64-character lines, LF line ends, a final LF.</summary>
    public string ToPem()
    {
        var base64 = Base64Strict.Encode(_der);
        var pem = new StringBuilder("-----BEGIN PUBLIC KEY-----\n");
        for (var i = 0; i < base64.Length; i += 64)
        {
            pem.Append(base64, i, Math.Min(64, base64.Length - i)).Append('\n');
        }
        return pem.Append("-----END PUBLIC KEY-----\n").ToString();
    }

    /// <summary>
    /// Whether <paramref name="signature"/> signs <paramref name="message"/> under this key: ECDSA P-256 with SHA-256
    /// over a strict DER signature (<see cref="EcdsaP256"/>), or Ed25519 (<see cref="Signatures.Ed25519"/>).
    /// </summary>
    /// <exception cref="NotSupportedException">The key is <see cref="AefKeyAlgorithm.Unsupported"/>.</exception>
    public bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature) => Algorithm switch
    {
        AefKeyAlgorithm.EcdsaP256 => EcdsaP256.Verify(_key, message, signature),
        AefKeyAlgorithm.Ed25519 => Ed25519.Verify(_key, message, signature),
        _ => throw new NotSupportedException($"An unsupported key ({UnsupportedReason}) verifies nothing."),
    };

    /// <inheritdoc/>
    public override string ToString() => $"{Algorithm} {KeyId}";

    private static PublicKeyInfo Unsupported(byte[] der, string reason) => new(der, AefKeyAlgorithm.Unsupported, [], reason);

    /// <summary>
    /// SubjectPublicKeyInfo ::= SEQUENCE { algorithm AlgorithmIdentifier, subjectPublicKey BIT STRING };
    /// AlgorithmIdentifier ::= SEQUENCE { algorithm OBJECT IDENTIFIER, parameters ANY OPTIONAL }; nothing after either.
    /// DER only: definite minimal lengths, a primitive BIT STRING whose unused bits are zero.
    /// </summary>
    private static bool TryRead(byte[] der, out string algorithm, out byte[]? parameters, out int unusedBits, out byte[] key)
    {
        algorithm = "";
        parameters = null;
        unusedBits = 0;
        key = [];
        if (der.Length == 0)
        {
            return false;
        }

        try
        {
            var outer = new AsnReader(der, AsnEncodingRules.DER);
            var spki = outer.ReadSequence();
            outer.ThrowIfNotEmpty();

            var identifier = spki.ReadSequence();
            algorithm = identifier.ReadObjectIdentifier();
            if (identifier.HasData)
            {
                parameters = identifier.ReadEncodedValue().ToArray();
            }
            identifier.ThrowIfNotEmpty();

            key = spki.ReadBitString(out unusedBits);
            spki.ThrowIfNotEmpty();
            return true;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static bool IsObjectIdentifier(byte[] encoded, out string oid)
    {
        oid = "";
        try
        {
            var reader = new AsnReader(encoded, AsnEncodingRules.DER);
            oid = reader.ReadObjectIdentifier();
            reader.ThrowIfNotEmpty();
            return true;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }
}

/// <summary>Key ids ([SIG-3]).</summary>
public static class KeyId
{
    /// <summary>
    /// The key id of a public key: <c>sha256:</c> and the lower-case hex SHA-256 of its DER-encoded
    /// SubjectPublicKeyInfo.
    /// </summary>
    public static string Of(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo)).ToLowerInvariant();
}
