// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Formats.Asn1;
using System.Security.Cryptography;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Writing;

/// <summary>
/// Loads the private key a writer signs seals, batch seals and checkpoints with ([SIG-1], [SIG-2]): an unencrypted
/// PKCS#8 <c>PRIVATE KEY</c> PEM block (RFC 5208, RFC 5958). AgentEval signs with ECDSA P-256 only: [SIG-2] lets a
/// signer use either of its two algorithms, and the base library has no Ed25519 signer (its verification here is
/// managed code, which is fine for public inputs but not for a private scalar). An Ed25519 key is refused as an
/// algorithm not supported for signing.
/// </summary>
public static class AefSigningKey
{
    private const string IdEcPublicKey = "1.2.840.10045.2.1";
    private const string IdEd25519 = "1.3.101.112";

    /// <summary>Reads a signer from a PKCS#8 PEM file. The caller disposes it.</summary>
    /// <exception cref="ArgumentException">Not an unencrypted PKCS#8 PEM block, or an EC key that is not on P-256.</exception>
    /// <exception cref="NotSupportedException">A key of an algorithm AgentEval does not sign with (Ed25519, RSA, …).</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static EcdsaP256Signer Load(string path) => FromPkcs8Pem(File.ReadAllText(path));

    /// <summary>Reads a signer from the text of a PKCS#8 PEM block. The caller disposes it.</summary>
    /// <exception cref="ArgumentException">Not an unencrypted PKCS#8 PEM block, or an EC key that is not on P-256.</exception>
    /// <exception cref="NotSupportedException">A key of an algorithm AgentEval does not sign with (Ed25519, RSA, …).</exception>
    public static EcdsaP256Signer FromPkcs8Pem(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        if (!PemEncoding.TryFind(pem, out var fields) || !pem.AsSpan()[fields.Label].SequenceEqual("PRIVATE KEY"))
        {
            throw new ArgumentException("Not an unencrypted PKCS#8 PEM block (-----BEGIN PRIVATE KEY-----).", nameof(pem)); // DevSkim: ignore DS173238 — the PEM label in an error message, no key
        }

        var der = new byte[fields.DecodedDataLength];
        if (!Convert.TryFromBase64Chars(pem.AsSpan()[fields.Base64Data], der, out var length) || length != der.Length)
        {
            throw new ArgumentException("The PEM block's body is not base64.", nameof(pem));
        }

        string algorithm;
        try
        {
            var reader = new AsnReader(der, AsnEncodingRules.DER);
            var info = reader.ReadSequence();   // PrivateKeyInfo
            reader.ThrowIfNotEmpty();
            info.ReadInteger();                 // version
            algorithm = info.ReadSequence().ReadObjectIdentifier();
        }
        catch (AsnContentException e)
        {
            throw new ArgumentException("Not a DER PKCS#8 PrivateKeyInfo.", nameof(pem), e);
        }

        if (algorithm == IdEd25519)
        {
            throw new NotSupportedException("Ed25519: algorithm not supported for signing (AgentEval signs with ECDSA P-256, one of [SIG-2]'s two).");
        }

        if (algorithm != IdEcPublicKey)
        {
            throw new NotSupportedException($"{algorithm}: algorithm not supported for signing (AgentEval signs with ECDSA P-256, [SIG-2]).");
        }

        using var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(der, out var read);
            if (read != der.Length)
            {
                throw new ArgumentException("Bytes after the PrivateKeyInfo.", nameof(pem));
            }
        }
        catch (CryptographicException e)
        {
            throw new ArgumentException("Not an EC private key in PKCS#8.", nameof(pem), e);
        }

        return new EcdsaP256Signer(key);   // refuses a key on another curve
    }
}
