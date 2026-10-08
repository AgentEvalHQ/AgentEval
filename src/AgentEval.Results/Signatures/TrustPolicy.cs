// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;

namespace AgentEval.Results.Signatures;

/// <summary>
/// A trust policy (contracts/aef/1/spec/04-integrity.md, [SIG-4]): which public keys a verifier trusts, the identity
/// each speaks for, and what that identity may do beyond signing (<c>"may": ["redact"]</c> authorizes redactions,
/// [OVL-10]). Always an input from the verifier's caller, never read from a run, an overlay or a runner manifest.
/// </summary>
/// <remarks>
/// The file format is spec 09 §9.2.1's: <c>{"keys": [{"identity": …, "publicKey": &lt;SPKI PEM&gt;, "may": ["redact"]?}]}</c>.
/// A key's id is computed from its public key ([SIG-3]), never read from the policy, and no two keys of a policy have
/// one key id. One identity may hold several keys (a rotation); what it may do is the union of their <c>may</c>
/// ([SIG-4]). Members the format does not define are ignored. The order of <see cref="Keys"/> is the policy order
/// [SIG-5] tries keys in and lists identities in.
/// </remarks>
public sealed class TrustPolicy
{
    /// <summary>The action a policy grants with <c>"may": ["redact"]</c>: authorizing redactions ([OVL-10]).</summary>
    public const string Redact = "redact";

    /// <summary>A policy that trusts no key.</summary>
    public static TrustPolicy Empty { get; } = new([]);

    /// <summary>A policy of these keys, in this order.</summary>
    /// <exception cref="ArgumentException">Two keys have one key id ([SIG-3]).</exception>
    public TrustPolicy(IEnumerable<TrustedKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Keys = [.. keys];
        if (DuplicateKeyId(Keys) is { } keyId)
        {
            throw new ArgumentException($"The key {keyId} is listed twice: a trust policy lists each key once ([SIG-3]).", nameof(keys));
        }
    }

    /// <summary>The trusted keys, in policy order.</summary>
    public IReadOnlyList<TrustedKey> Keys { get; }

    /// <summary>
    /// The key with this key id ([SIG-5]: a signature with a <c>keyid</c> is checked only against the trusted key with
    /// that id), or null when the policy lists none.
    /// </summary>
    public TrustedKey? Find(string keyId) => Keys.FirstOrDefault(k => string.Equals(k.KeyId, keyId, StringComparison.Ordinal));

    /// <summary>
    /// Whether the policy lets <paramref name="identity"/> do <paramref name="action"/> beyond signing: the union of the
    /// <c>may</c> of the identity's keys holds it ([SIG-4]).
    /// </summary>
    public bool Allows(string identity, string action) =>
        Keys.Any(k => string.Equals(k.Identity, identity, StringComparison.Ordinal) && k.May.Contains(action, StringComparer.Ordinal));

    /// <summary>Reads a trust policy file.</summary>
    /// <exception cref="TrustPolicyException">The file is not a trust policy.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static TrustPolicy Load(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>
    /// Reads a trust policy from its JSON bytes: an I-JSON object whose <c>keys</c> is an array of objects, each with a
    /// string <c>identity</c>, a string <c>publicKey</c> holding a PEM SubjectPublicKeyInfo
    /// (<see cref="PublicKeyInfo.FromPem"/>), and optionally <c>may</c>, an array of strings. A key of an algorithm AEF
    /// does not verify with is kept, as <see cref="AefKeyAlgorithm.Unsupported"/> ([SIG-2]). The policy is refused as a
    /// whole ([SIG-3]: a verifier does not verify against part of a policy) when a key is not a strict PEM of a DER
    /// SubjectPublicKeyInfo, when a P-256 or Ed25519 key is not usable, or when two keys have one key id.
    /// </summary>
    /// <exception cref="TrustPolicyException">The bytes are not a trust policy, or [SIG-3] refuses it.</exception>
    public static TrustPolicy Parse(ReadOnlySpan<byte> utf8Json)
    {
        JsonObject root;
        try
        {
            root = AefJsonReader.ParseDocument(utf8Json);
        }
        catch (AefReadException e)
        {
            throw new TrustPolicyException($"A trust policy is a JSON document ({e.Code}): {e.Message}");
        }
        if (root["keys"] is not JsonArray list)
        {
            throw new TrustPolicyException("A trust policy is {\"keys\": [{\"identity\": …, \"publicKey\": <SPKI PEM>, \"may\": [...]?}]}.");
        }

        var keys = new List<TrustedKey>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not JsonObject entry || !TryString(entry["identity"], out var identity) || !TryString(entry["publicKey"], out var pem))
            {
                throw new TrustPolicyException($"keys[{i}] is not an object with a string identity and a string publicKey.");
            }

            var may = new List<string>();
            if (entry.TryGetPropertyValue("may", out var mayNode))
            {
                if (mayNode is not JsonArray actions)
                {
                    throw new TrustPolicyException($"keys[{i}].may is not an array of strings.");
                }
                foreach (var action in actions)
                {
                    if (!TryString(action, out var name))
                    {
                        throw new TrustPolicyException($"keys[{i}].may is not an array of strings.");
                    }
                    may.Add(name);
                }
            }

            PublicKeyInfo key;
            try
            {
                key = PublicKeyInfo.FromPem(pem);
            }
            catch (FormatException e)
            {
                throw new TrustPolicyException($"keys[{i}].publicKey (for {identity}): {e.Message}");
            }
            keys.Add(new TrustedKey(identity, key, may));
        }

        if (DuplicateKeyId(keys) is { } duplicate)
        {
            throw new TrustPolicyException($"The key {duplicate} is listed twice: a trust policy lists each key once ([SIG-3]).");
        }
        return new TrustPolicy(keys);
    }

    /// <summary>The first key id that two keys share, or null.</summary>
    private static string? DuplicateKeyId(IEnumerable<TrustedKey> keys)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return keys.FirstOrDefault(k => !seen.Add(k.KeyId))?.KeyId;
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

/// <summary>A key of a trust policy: the identity it speaks for, the public key, and what the identity may do beyond signing.</summary>
public sealed class TrustedKey
{
    /// <summary>A trusted key.</summary>
    public TrustedKey(string identity, PublicKeyInfo publicKey, IEnumerable<string>? may = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(publicKey);
        Identity = identity;
        PublicKey = publicKey;
        May = [.. may ?? []];
    }

    /// <summary>The identity the key speaks for, for example <c>git:alice@example.com</c> or <c>spiffe://…</c>.</summary>
    public string Identity { get; }

    /// <summary>The public key.</summary>
    public PublicKeyInfo PublicKey { get; }

    /// <summary>The key id ([SIG-3]), computed from the public key.</summary>
    public string KeyId => PublicKey.KeyId;

    /// <summary>What the identity may do beyond signing, as listed (<c>redact</c> is the one AEF 1.0 defines).</summary>
    public IReadOnlyList<string> May { get; }
}

/// <summary>A trust policy that cannot be read: not the format of spec 09 §9.2.1, or a key that is not a SubjectPublicKeyInfo.</summary>
public sealed class TrustPolicyException(string message) : FormatException(message);
