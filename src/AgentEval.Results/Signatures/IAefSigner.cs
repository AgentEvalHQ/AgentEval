// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Results.Signatures;

/// <summary>
/// Signs what AEF signs (contracts/aef/1/spec/04-integrity.md, §4.4): a DSSE pre-authentication encoding
/// (<see cref="Dsse.Pae"/>) of a seal, an overlay batch seal or a checkpoint manifest. A signer uses one of the two
/// algorithms of [SIG-2]; <see cref="DsseEnvelope.Create"/> puts its signature in an envelope under its key id.
/// </summary>
public interface IAefSigner
{
    /// <summary>The public key a verifier's trust policy lists to trust this signer ([SIG-4]).</summary>
    PublicKeyInfo PublicKey { get; }

    /// <summary>The key id written in each signature's <c>keyid</c> ([SIG-3]): <see cref="PublicKey"/>'s.</summary>
    string KeyId { get; }

    /// <summary>The signature of <paramref name="message"/> (the PAE bytes), encoded as [SIG-2] requires.</summary>
    byte[] Sign(ReadOnlySpan<byte> message);
}
