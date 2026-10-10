// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;

namespace AgentEval.Results.Signatures;

/// <summary>
/// DSSE v1 as AEF uses it (contracts/aef/1/spec/04-integrity.md, [SIG-1]): the payload types of the files AEF signs,
/// and the pre-authentication encoding a signature covers.
/// </summary>
public static class Dsse
{
    /// <summary>
    /// The payload type of <c>seal.json</c> (signed in <c>attestation.dsse.json</c>) and of an overlay batch seal
    /// <c>overlays/seal-&lt;nnnn&gt;.json</c> (signed in <c>overlays/seal-&lt;nnnn&gt;.dsse.json</c>).
    /// </summary>
    public const string InTotoPayloadType = "application/vnd.in-toto+json";

    /// <summary>The payload type of a checkpoint manifest, signed in <c>&lt;checkpoint&gt;.dsse.json</c> beside it.</summary>
    public const string CheckpointPayloadType = "application/vnd.agenteval.aef.checkpoint+json";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The DSSE pre-authentication encoding, the bytes a signature covers:
    /// <c>"DSSEv1" SP LEN(payloadType) SP payloadType SP LEN(payload) SP payload</c>, with the payload type in UTF-8 and
    /// each length the byte count in ASCII decimal without leading zeros.
    /// </summary>
    /// <exception cref="ArgumentException">The payload type holds an unpaired surrogate, which has no UTF-8 encoding.</exception>
    public static byte[] Pae(string payloadType, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(payloadType);
        byte[] type;
        try
        {
            type = StrictUtf8.GetBytes(payloadType);
        }
        catch (EncoderFallbackException e)
        {
            throw new ArgumentException("The payload type is not valid Unicode text.", nameof(payloadType), e);
        }

        var head = Encoding.ASCII.GetBytes("DSSEv1 " + type.Length.ToString(CultureInfo.InvariantCulture) + " ");
        var middle = Encoding.ASCII.GetBytes(" " + payload.Length.ToString(CultureInfo.InvariantCulture) + " ");
        var message = new byte[head.Length + type.Length + middle.Length + payload.Length];
        head.CopyTo(message, 0);
        type.CopyTo(message, head.Length);
        middle.CopyTo(message, head.Length + type.Length);
        payload.CopyTo(message.AsSpan(head.Length + type.Length + middle.Length));
        return message;
    }
}

/// <summary>One signature of a DSSE envelope: the key id it names (empty when it names none) and the signature bytes.</summary>
public sealed class DsseSignature
{
    private readonly byte[] _sig;

    /// <summary>A signature: <paramref name="keyId"/> is the empty string for one that names no key.</summary>
    public DsseSignature(string keyId, ReadOnlySpan<byte> sig)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        KeyId = keyId;
        _sig = sig.ToArray();
    }

    /// <summary>
    /// The key id the signature names, or the empty string when its <c>keyid</c> is absent, empty or <c>null</c> (DSSE
    /// treats them alike). A hint for choosing the key, never a reason to trust it ([SIG-4]).
    /// </summary>
    public string KeyId { get; }

    /// <summary>The signature bytes (for ECDSA P-256 a DER <c>SEQUENCE { r, s }</c>; for Ed25519 64 bytes, [SIG-2]).</summary>
    public ReadOnlySpan<byte> Sig => _sig;
}

/// <summary>
/// A DSSE v1 JSON envelope ([SIG-1]): <c>payloadType</c>, <c>payload</c> (the signed bytes, base64) and at least one
/// signature. <see cref="TryParse"/> reads one as a verifier must; <see cref="Create"/> and <see cref="ToJson"/> write
/// one.
/// </summary>
public sealed class DsseEnvelope
{
    private readonly byte[] _payload;

    /// <summary>An envelope over <paramref name="payload"/>, with its signatures in order.</summary>
    /// <exception cref="ArgumentException">No signature: an envelope with none is malformed ([SIG-1]).</exception>
    public DsseEnvelope(string payloadType, ReadOnlySpan<byte> payload, IReadOnlyList<DsseSignature> signatures)
    {
        ArgumentNullException.ThrowIfNull(payloadType);
        ArgumentNullException.ThrowIfNull(signatures);
        if (signatures.Count == 0)
        {
            throw new ArgumentException("A DSSE envelope has at least one signature.", nameof(signatures));
        }
        PayloadType = payloadType;
        _payload = payload.ToArray();
        Signatures = [.. signatures];
    }

    /// <summary>What the payload is ([SIG-1]'s table gives the type of each file AEF signs).</summary>
    public string PayloadType { get; }

    /// <summary>The signed bytes, decoded from base64: for AEF, the exact bytes of the file the envelope signs.</summary>
    public ReadOnlySpan<byte> Payload => _payload;

    /// <summary>The signatures, in envelope order.</summary>
    public IReadOnlyList<DsseSignature> Signatures { get; }

    /// <summary>The bytes each signature covers: <see cref="Dsse.Pae"/> of this envelope's type and payload.</summary>
    public byte[] PreAuthenticationEncoding() => Dsse.Pae(PayloadType, _payload);

    /// <summary>
    /// Signs <paramref name="payload"/> with each signer in turn: one signature per signer, naming its key id
    /// ([SIG-3]).
    /// </summary>
    public static DsseEnvelope Create(string payloadType, ReadOnlySpan<byte> payload, params IAefSigner[] signers)
    {
        ArgumentNullException.ThrowIfNull(signers);
        var message = Dsse.Pae(payloadType, payload);
        return new DsseEnvelope(payloadType, payload, [.. signers.Select(s => new DsseSignature(s.KeyId, s.Sign(message)))]);
    }

    /// <summary>
    /// Reads an envelope from its JSON bytes. False, with the reason, for an envelope [SIG-5] calls <c>malformed</c>:
    /// not a JSON document as <see cref="AefJsonReader.ParseDocument"/> reads one (an I-JSON object, [ENC-1]–[ENC-3],
    /// within the limits of [ENC-17]: 56 MiB for an envelope, <see cref="AefLimits.MaxEnvelopeBytes"/>); <c>payloadType</c>
    /// absent or not a string; <c>payload</c>
    /// absent, not a string or not base64 ([SIG-1]); <c>signatures</c> absent, not an array, or empty; a signature
    /// that is not an object, whose <c>sig</c> is absent, not a string or not base64, or whose <c>keyid</c> is
    /// present but neither a string nor <c>null</c> (<c>null</c> reads as absent). Members DSSE does not define are
    /// ignored, as DSSE requires.
    /// </summary>
    public static bool TryParse(
        ReadOnlySpan<byte> utf8Json,
        [NotNullWhen(true)] out DsseEnvelope? envelope,
        [NotNullWhen(false)] out string? malformed)
    {
        envelope = null;
        JsonObject root;
        try
        {
            root = AefJsonReader.ParseDocument(utf8Json, AefLimits.MaxEnvelopeBytes);
        }
        catch (AefReadException e)
        {
            malformed = $"not an AEF JSON document ({e.Code}): {e.Message}";
            return false;
        }

        if (!TryString(root, "payloadType", out var payloadType))
        {
            malformed = "payloadType is absent or not a string";
            return false;
        }
        if (!TryString(root, "payload", out var payloadText) || !Base64Strict.TryDecode(payloadText, out var payload))
        {
            malformed = "payload is absent, not a string, or not base64";
            return false;
        }
        if (root["signatures"] is not JsonArray { Count: > 0 } list)
        {
            malformed = "signatures is absent, not an array, or empty";
            return false;
        }

        var signatures = new List<DsseSignature>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not JsonObject item)
            {
                malformed = $"signatures[{i}] is not an object";
                return false;
            }
            if (!TryString(item, "sig", out var sigText) || !Base64Strict.TryDecode(sigText, out var sig))
            {
                malformed = $"signatures[{i}].sig is absent, not a string, or not base64";
                return false;
            }
            // An absent keyid, an empty one and null (DSSE's protobuf JSON mapping) all name no key.
            var keyId = "";
            if (item.TryGetPropertyValue("keyid", out var keyNode) && keyNode is not null)
            {
                if (!TryString(keyNode, out var named))
                {
                    malformed = $"signatures[{i}].keyid is neither a string nor null";
                    return false;
                }
                keyId = named;
            }
            signatures.Add(new DsseSignature(keyId, sig));
        }

        envelope = new DsseEnvelope(payloadType, payload, signatures);
        malformed = null;
        return true;
    }

    /// <summary>
    /// The envelope as an AEF JSON document (<see cref="AefJsonWriter.Document"/>: indented by two spaces, ending in LF):
    /// <c>payloadType</c>, <c>payload</c> and <c>signatures</c> (each <c>keyid</c> and <c>sig</c>), base64 in the
    /// standard alphabet with padding ([SIG-1]). A signature without a key id is written with an empty <c>keyid</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The envelope would exceed the 56 MiB [ENC-17] allows a DSSE envelope (<see cref="AefLimits.MaxEnvelopeBytes"/>).
    /// </exception>
    public byte[] ToJson()
    {
        var signatures = new JsonArray();
        foreach (var signature in Signatures)
        {
            signatures.Add(new JsonObject { ["keyid"] = signature.KeyId, ["sig"] = Base64Strict.Encode(signature.Sig) });
        }
        return AefJsonWriter.Document(new JsonObject
        {
            ["payloadType"] = PayloadType,
            ["payload"] = Base64Strict.Encode(_payload),
            ["signatures"] = signatures,
        }, AefLimits.MaxEnvelopeBytes);
    }

    private static bool TryString(JsonObject obj, string name, [NotNullWhen(true)] out string? value)
    {
        value = null;
        return obj.TryGetPropertyValue(name, out var node) && TryString(node, out value);
    }

    private static bool TryString(JsonNode? node, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            value = v.GetValue<string>();
            return true;
        }
        return false;
    }
}
