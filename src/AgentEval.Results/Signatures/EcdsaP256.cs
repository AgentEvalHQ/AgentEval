// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Formats.Asn1;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace AgentEval.Results.Signatures;

/// <summary>
/// ECDSA over NIST P-256 with SHA-256 as AEF signs with it (contracts/aef/1/spec/04-integrity.md, [SIG-2]; FIPS 186-5):
/// the signature is the DER encoding of <c>SEQUENCE { r INTEGER, s INTEGER }</c>, read strictly, and a public key is an
/// uncompressed point on the curve.
/// </summary>
/// <remarks>
/// The DER signature and the point are checked here, in managed code, and only then handed to the platform
/// (<see cref="ECDsa.VerifyHash(ReadOnlySpan{byte}, ReadOnlySpan{byte}, DSASignatureFormat)"/> with r‖s in IEEE P1363
/// form), so that Windows (CNG) and Linux (OpenSSL), which differ in what DER they tolerate, give the same answer.
/// There is no low-S rule: s and n − s are both valid ([SIG-2]).
/// </remarks>
public static class EcdsaP256
{
    /// <summary>The size of a coordinate, of r and of s, in bytes.</summary>
    public const int ScalarSize = 32;

    /// <summary>The size of an uncompressed point (0x04, X, Y), in bytes.</summary>
    public const int PointSize = 1 + 2 * ScalarSize;

    // P-256 (FIPS 186-5 / SP 800-186 §3.2.1.3): y^2 = x^3 - 3x + b over GF(p); the base point has prime order n.
    private static readonly BigInteger P = Hex("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff"); // DevSkim: ignore DS173237 — public curve constant
    private static readonly BigInteger B = Hex("5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b"); // DevSkim: ignore DS173237 — public curve constant
    private static readonly BigInteger N = Hex("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551"); // DevSkim: ignore DS173237 — public curve constant

    /// <summary>
    /// Whether <paramref name="message"/> is signed by <paramref name="signature"/> (strict DER) under the public key
    /// <paramref name="point"/> (an uncompressed point on P-256): SHA-256 of the message, then ECDSA verification.
    /// False for a point that is not one, a signature <see cref="TryDecodeSignature"/> refuses, or one that does not
    /// verify.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> point, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        Span<byte> rs = stackalloc byte[2 * ScalarSize];
        if (!IsValidPublicKey(point) || !TryDecodeSignature(signature, rs))
        {
            return false;
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(message, hash);
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256, // DevSkim: ignore DS440100 — P-256 is the curve [SIG-2] specifies
            Q = new ECPoint { X = point[1..(1 + ScalarSize)].ToArray(), Y = point[(1 + ScalarSize)..].ToArray() },
        });
        return key.VerifyHash(hash, rs, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Whether <paramref name="point"/> is 0x04 followed by X and Y, each below p, with (X, Y) on P-256.</summary>
    public static bool IsValidPublicKey(ReadOnlySpan<byte> point) =>
        point.Length == PointSize && point[0] == 0x04 && IsOnCurve(point[1..(1 + ScalarSize)], point[(1 + ScalarSize)..]);

    /// <summary>Whether big-endian coordinates <paramref name="x"/>, <paramref name="y"/> (32 bytes each, below p) are a point of P-256.</summary>
    public static bool IsOnCurve(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        if (x.Length != ScalarSize || y.Length != ScalarSize)
        {
            return false;
        }
        var bx = new BigInteger(x, isUnsigned: true, isBigEndian: true);
        var by = new BigInteger(y, isUnsigned: true, isBigEndian: true);
        return bx < P && by < P && Mod(by * by) == Mod(bx * bx * bx - 3 * bx + B);
    }

    /// <summary>
    /// Reads a DER signature into r‖s (IEEE P1363: 32 bytes each, big-endian) in <paramref name="rs"/>. Strict: one
    /// DER <c>SEQUENCE</c> with nothing after it, holding exactly two <c>INTEGER</c>s, each minimally encoded, positive,
    /// and in [1, n − 1]; definite, minimal lengths. A raw r‖s, BER, a negative or padded integer, or trailing bytes are
    /// refused.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="rs"/> is not 64 bytes.</exception>
    public static bool TryDecodeSignature(ReadOnlySpan<byte> der, Span<byte> rs)
    {
        if (rs.Length != 2 * ScalarSize)
        {
            throw new ArgumentException("r‖s is 64 bytes.", nameof(rs));
        }

        if (der.IsEmpty)
        {
            return false;
        }

        try
        {
            AsnDecoder.ReadSequence(der, AsnEncodingRules.DER, out var offset, out var length, out var consumed);
            if (consumed != der.Length)
            {
                return false;   // bytes after the SEQUENCE
            }

            var body = der.Slice(offset, length);
            var r = AsnDecoder.ReadIntegerBytes(body, AsnEncodingRules.DER, out var used);
            body = body[used..];
            var s = AsnDecoder.ReadIntegerBytes(body, AsnEncodingRules.DER, out used);
            body = body[used..];
            return body.IsEmpty && TryScalar(r, rs[..ScalarSize]) && TryScalar(s, rs[ScalarSize..]);
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    /// <summary>The DER <c>SEQUENCE { r, s }</c> of an r‖s signature (IEEE P1363, 64 bytes), minimally encoded.</summary>
    /// <exception cref="ArgumentException"><paramref name="rs"/> is not 64 bytes.</exception>
    public static byte[] EncodeSignature(ReadOnlySpan<byte> rs)
    {
        if (rs.Length != 2 * ScalarSize)
        {
            throw new ArgumentException("r‖s is 64 bytes.", nameof(rs));
        }
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(new BigInteger(rs[..ScalarSize], isUnsigned: true, isBigEndian: true));
            writer.WriteInteger(new BigInteger(rs[ScalarSize..], isUnsigned: true, isBigEndian: true));
        }
        return writer.Encode();
    }

    /// <summary>An INTEGER's contents as a scalar in [1, n − 1], right-aligned in 32 bytes; false otherwise.</summary>
    private static bool TryScalar(ReadOnlySpan<byte> contents, Span<byte> scalar)
    {
        // X.690 §8.3.2: the first nine bits are never all zero or all one. Checked here as well as by the decoder.
        if (contents.IsEmpty
            || (contents.Length > 1 && contents[0] == 0x00 && contents[1] < 0x80)
            || (contents.Length > 1 && contents[0] == 0xFF && contents[1] >= 0x80)
            || contents[0] >= 0x80)
        {
            return false;   // empty, padded, or negative
        }
        if (contents[0] == 0x00 && contents.Length > 1)
        {
            contents = contents[1..];   // the sign byte before a high bit
        }
        if (contents.Length > ScalarSize)
        {
            return false;
        }

        var value = new BigInteger(contents, isUnsigned: true, isBigEndian: true);
        if (value.IsZero || value >= N)
        {
            return false;
        }
        scalar.Clear();
        contents.CopyTo(scalar[(ScalarSize - contents.Length)..]);
        return true;
    }

    private static BigInteger Mod(BigInteger value)
    {
        var r = value % P;
        return r.Sign < 0 ? r + P : r;
    }

    private static BigInteger Hex(string hex) =>
        BigInteger.Parse("0" + hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
}
