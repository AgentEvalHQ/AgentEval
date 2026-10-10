// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Numerics;
using System.Security.Cryptography;

namespace AgentEval.Results.Signatures;

/// <summary>
/// Ed25519 signature verification (RFC 8032 §5.1.7), managed, for the Ed25519 keys a trust policy may list
/// (contracts/aef/1/spec/04-integrity.md, [SIG-2]). Verify only: AgentEval signs with ECDSA P-256.
/// </summary>
/// <remarks>
/// <para>
/// Strict where RFC 8032 is: a point encoding whose y is not below p, or whose x is zero with the sign bit set, does not
/// decode (§5.1.3), so every accepted encoding is canonical; S must be below L (§5.1.7 step 1). The check is the
/// cofactorless equation [S]B = R + [k]A', which §5.1.7 step 3 allows ("sufficient, but not required") and [SIG-2]
/// requires, with k reduced mod L; the cofactored [8][S]B = [8]R + [8][k]A' differs only for signatures crafted with
/// points of small order. <see cref="Verify"/> refuses no key that decodes, as RFC 8032; a trust policy refuses a
/// small-order key before any verification (<see cref="IsUsablePublicKey"/>, [SIG-2], [SIG-3]).
/// </para>
/// <para>
/// Everything verified is public (key, message, signature), so the arithmetic is plain <see cref="BigInteger"/> and
/// not constant time.
/// </para>
/// </remarks>
public static class Ed25519
{
    /// <summary>The size of a public key and of a point encoding, in bytes.</summary>
    public const int PublicKeySize = 32;

    /// <summary>The size of a signature (R, then S), in bytes.</summary>
    public const int SignatureSize = 64;

    // edwards25519 (RFC 8032 §5.1, Table 1): -x^2 + y^2 = 1 + d x^2 y^2 over GF(p).
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493", System.Globalization.CultureInfo.InvariantCulture); // DevSkim: ignore DS173237 — the public group order
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    private static readonly BigInteger SqrtMinusOne = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly Point Identity = new(0, 1, 1, 0);
    private static readonly Point Base = BasePoint();

    /// <summary>
    /// Whether <paramref name="signature"/> is a valid Ed25519 signature of <paramref name="message"/> under
    /// <paramref name="publicKey"/> (RFC 8032 §5.1.7, pure Ed25519: no context, no prehash). False for wrong sizes, a
    /// public key or R that does not decode to a point, an S not below L, or a signature that does not satisfy the
    /// group equation.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeySize || signature.Length != SignatureSize)
        {
            return false;
        }

        // Step 1: R and A' decode as points; S is an integer in [0, L).
        var s = new BigInteger(signature[32..], isUnsigned: true, isBigEndian: false);
        if (s >= L || !TryDecode(signature[..32], out var r) || !TryDecode(publicKey, out var a))
        {
            return false;
        }

        // Step 2: k = SHA-512(R || A || M) as a little-endian integer (dom2 is empty and PH the identity for Ed25519),
        // reduced mod L as [SIG-2] requires (and RFC 8032 §6's reference code, libsodium, OpenSSL and Go do). Step 2
        // itself does not reduce; for a key with a small-order component, [k]A' and [k mod L]A' can differ.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        hash.AppendData(signature[..32]);
        hash.AppendData(publicKey);
        hash.AppendData(message);
        var k = new BigInteger(hash.GetHashAndReset(), isUnsigned: true, isBigEndian: false) % L;

        // Step 3: [S]B = R + [k]A'.
        return Equal(Multiply(s, Base), Add(r, Multiply(k, a)));
    }

    /// <summary>
    /// Whether 32 bytes decode to a point of edwards25519 (RFC 8032 §5.1.3): y below p, x recoverable from the curve
    /// equation, and not x = 0 with the sign bit set.
    /// </summary>
    public static bool IsValidPublicKey(ReadOnlySpan<byte> publicKey) =>
        publicKey.Length == PublicKeySize && TryDecode(publicKey, out _);

    /// <summary>
    /// Whether a public key is usable in a trust policy ([SIG-2]): it decodes to a point (<see cref="IsValidPublicKey"/>)
    /// that is not of small order, that is eight times it is not the neutral point. A small-order key verifies forged
    /// signatures (the neutral key verifies R = neutral, S = 0 for every message), so a policy holding one is refused
    /// ([SIG-3]).
    /// </summary>
    public static bool IsUsablePublicKey(ReadOnlySpan<byte> publicKey) =>
        publicKey.Length == PublicKeySize && TryDecode(publicKey, out var a) && !Equal(Multiply(8, a), Identity);

    // ------------------------------------------------------------------ points (RFC 8032 §5.1.3, §5.1.4)

    /// <summary>A point in extended homogeneous coordinates: x = X/Z, y = Y/Z, x·y = T/Z.</summary>
    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    private static bool TryDecode(ReadOnlySpan<byte> encoded, out Point point)
    {
        point = Identity;
        if (encoded.Length != PublicKeySize)
        {
            return false;
        }

        // 1. Little-endian; bit 255 is the sign of x, the rest is y, which must be below p.
        Span<byte> bytes = stackalloc byte[PublicKeySize];
        encoded.CopyTo(bytes);
        var sign = bytes[31] >> 7;
        bytes[31] &= 0x7F;
        var y = new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
        if (y >= P)
        {
            return false;
        }

        if (RecoverX(y, sign) is not { } x)
        {
            return false;
        }
        point = new Point(x, y, 1, Mod(x * y));
        return true;
    }

    /// <summary>The x of the point with this y and sign bit, or null when there is none (§5.1.3 steps 2 to 4).</summary>
    private static BigInteger? RecoverX(BigInteger y, int sign)
    {
        // 2. x^2 = (y^2 - 1) / (d y^2 + 1); candidate x = u v^3 (u v^7)^((p-5)/8).
        var y2 = Mod(y * y);
        var u = Mod(y2 - 1);
        var v = Mod(D * y2 + 1);
        var v3 = Mod(v * v * v);
        var v7 = Mod(v3 * v3 * v);
        var x = Mod(u * v3 * BigInteger.ModPow(Mod(u * v7), (P - 5) / 8, P));

        // 3. v x^2 = u: x is a root; v x^2 = -u: x·sqrt(-1) is; otherwise no root exists.
        var vx2 = Mod(v * x * x);
        if (vx2 != u)
        {
            if (vx2 != Mod(-u))
            {
                return null;
            }
            x = Mod(x * SqrtMinusOne);
        }

        // 4. The sign bit picks the root; x = 0 has no negative.
        if (x.IsZero && sign == 1)
        {
            return null;
        }
        if ((int)(x % 2) != sign)
        {
            x = P - x;
        }
        return x;
    }

    /// <summary>Point addition with the complete formulas of §5.1.4 (they also double).</summary>
    private static Point Add(Point p, Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(p.T * 2 * D * q.T);
        var d = Mod(p.Z * 2 * q.Z);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    /// <summary>[k]Q, by double-and-add from the low bit.</summary>
    private static Point Multiply(BigInteger k, Point q)
    {
        var result = Identity;
        for (var addend = q; !k.IsZero; k >>= 1, addend = Add(addend, addend))
        {
            if (!k.IsEven)
            {
                result = Add(result, addend);
            }
        }
        return result;
    }

    /// <summary>Whether two points are the same: X1/Z1 = X2/Z2 and Y1/Z1 = Y2/Z2.</summary>
    private static bool Equal(Point p, Point q) =>
        Mod(p.X * q.Z - q.X * p.Z).IsZero && Mod(p.Y * q.Z - q.Y * p.Z).IsZero;

    /// <summary>B: y = 4/5, x even (Table 1).</summary>
    private static Point BasePoint()
    {
        var y = Mod(4 * Inverse(5));
        var x = RecoverX(y, 0) ?? throw new InvalidOperationException("edwards25519 has no point with y = 4/5.");
        return new Point(x, y, 1, Mod(x * y));
    }

    private static BigInteger Mod(BigInteger value)
    {
        var r = value % P;
        return r.Sign < 0 ? r + P : r;
    }

    private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);
}
