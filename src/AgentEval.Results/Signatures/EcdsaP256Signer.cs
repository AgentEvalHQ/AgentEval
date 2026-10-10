// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;

namespace AgentEval.Results.Signatures;

/// <summary>
/// Signs with an ECDSA P-256 private key over SHA-256 ([SIG-2]), writing the signature as a minimal DER
/// <c>SEQUENCE { r, s }</c> (<see cref="EcdsaP256.EncodeSignature"/>), the form an AEF verifier reads.
/// </summary>
public sealed class EcdsaP256Signer : IAefSigner, IDisposable
{
    private readonly ECDsa _key;

    /// <summary>
    /// A signer with a copy of <paramref name="key"/>'s private key, which must be on P-256. The caller keeps and
    /// disposes <paramref name="key"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The key is not a P-256 key, or has no private part.</exception>
    public EcdsaP256Signer(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ECParameters parameters;
        try
        {
            parameters = key.ExportParameters(includePrivateParameters: true);
        }
        catch (CryptographicException e)
        {
            throw new ArgumentException("The key's private part cannot be exported.", nameof(key), e);
        }

        // The point on P-256's equation, checked here rather than by curve name, which platforms report differently.
        if (parameters.D is null || parameters.Q.X is null || parameters.Q.Y is null || !EcdsaP256.IsOnCurve(parameters.Q.X, parameters.Q.Y))
        {
            throw new ArgumentException("Not an ECDSA P-256 private key.", nameof(key));
        }

        _key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256, // DevSkim: ignore DS440100 — P-256 is the curve [SIG-2] specifies
            Q = parameters.Q,
            D = parameters.D,
        });
        PublicKey = PublicKeyInfo.FromP256(parameters.Q.X, parameters.Q.Y);
        CryptographicOperations.ZeroMemory(parameters.D);
    }

    /// <summary>A signer with a new random P-256 key.</summary>
    public static EcdsaP256Signer Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); // DevSkim: ignore DS440100 — P-256 is the curve [SIG-2] specifies
        return new EcdsaP256Signer(key);
    }

    /// <summary>A signer from a PKCS #8 <c>PRIVATE KEY</c> PEM block holding a P-256 key.</summary>
    /// <exception cref="ArgumentException">Not such a key.</exception>
    public static EcdsaP256Signer FromPem(string pem)
    {
        using var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
        }
        catch (CryptographicException e)
        {
            throw new ArgumentException("Not an EC private key in PEM.", nameof(pem), e);
        }
        return new EcdsaP256Signer(key);
    }

    /// <inheritdoc/>
    public PublicKeyInfo PublicKey { get; }

    /// <inheritdoc/>
    public string KeyId => PublicKey.KeyId;

    /// <summary>The DER signature of SHA-256(<paramref name="message"/>), r and s minimally encoded.</summary>
    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        var hash = SHA256.HashData(message);
        return EcdsaP256.EncodeSignature(_key.SignHash(hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <inheritdoc/>
    public void Dispose() => _key.Dispose();
}
