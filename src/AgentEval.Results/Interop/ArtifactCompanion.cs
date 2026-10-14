// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;

namespace AgentEval.Results.Interop;

/// <summary>
/// An immutable producer assertion binding one artifact digest to one AEF seal.
/// Parsing does not verify evidence, signatures, or whether the artifact was executed.
/// </summary>
public sealed class ArtifactCompanion
{
    /// <summary>The in-toto Statement v1 identifier.</summary>
    public const string StatementType = "https://in-toto.io/Statement/v1";
    /// <summary>The artifact-evidence predicate identifier (no external registration implied).</summary>
    public const string PredicateType = "https://agenteval.dev/aef/1/artifact-evidence";
    /// <summary>Maximum document size, checked before parsing or copying.</summary>
    public const int MaxDocumentBytes = 64 * 1024;
    /// <summary>Maximum optional DSSE envelope size.</summary>
    public const int MaxEnvelopeBytes = 128 * 1024;
    /// <summary>Maximum UTF-8 byte length of the display label.</summary>
    public const int MaxNameBytes = 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] _bytes;

    private ArtifactCompanion(byte[] bytes, string name, string artifact, string seal, string runHash)
    {
        _bytes = bytes;
        Name = name;
        ArtifactSha256 = artifact;
        SealSha256 = seal;
        RunHash = runHash;
    }

    /// <summary>A display label, never a path to resolve or a URI to fetch.</summary>
    public string Name { get; }
    /// <summary>The producer-supplied artifact SHA-256, in lowercase hexadecimal.</summary>
    public string ArtifactSha256 { get; }
    /// <summary>SHA-256 of the exact selected seal bytes.</summary>
    public string SealSha256 { get; }
    /// <summary>The declared AEF run hash; evidence verification must check it.</summary>
    public string RunHash { get; }
    /// <summary>A detached copy of the exact input bytes, including unknown optional fields.</summary>
    public byte[] ToBytes() => (byte[])_bytes.Clone();

    /// <summary>Reads strict, bounded JSON without reserializing the signed payload.</summary>
    public static ArtifactCompanion Parse(ReadOnlySpan<byte> utf8)
    {
        var root = AefJsonReader.ParseDocument(utf8, MaxDocumentBytes);
        Require(Text(root, "_type") == StatementType, "Statement v1");
        Require(Text(root, "predicateType") == PredicateType, "artifact-evidence predicate");
        Require(root["subject"] is JsonArray { Count: 1 }, "exactly one subject");
        var subject = (root["subject"] as JsonArray)![0] as JsonObject;
        Require(subject is not null, "subject object");
        var name = Text(subject!, "name");
        Require(name is { Length: > 0 } && StrictUtf8.GetByteCount(name) <= MaxNameBytes, "nonempty name of at most 1024 UTF-8 bytes");
        var artifact = Digest(subject!["digest"]);
        var predicate = root["predicate"] as JsonObject;
        Require(predicate is not null, "predicate object");
        var version = Text(predicate!, "schemaVersion");
        Require(version == "1.0" || AefVersion.DeclaresLaterMinor(version), "schemaVersion with major 1");
        Require(Text(predicate!, "binding") == "producer-asserted", "producer-asserted binding");
        var evidence = predicate!["evidence"] as JsonObject;
        Require(evidence is not null, "evidence object");
        var seal = Digest(evidence!["seal"]);
        var runHash = Text(evidence, "runHash");
        Require(IsDigest(runHash), "64 lowercase hexadecimal runHash characters");
        return new ArtifactCompanion(utf8.ToArray(), name!, artifact, seal, runHash!);
    }

    /// <summary>
    /// Creates a producer assertion from explicit digests. This does not verify the
    /// digests or attest that the artifact was executed.
    /// </summary>
    public static ArtifactCompanion Create(string name, string artifactSha256, string sealSha256, string runHash)
    {
        ArgumentNullException.ThrowIfNull(name);
        try
        {
            Require(name.Length > 0 && name.Length <= MaxNameBytes && StrictUtf8.GetByteCount(name) <= MaxNameBytes,
                "nonempty name of at most 1024 valid UTF-8 bytes");
        }
        catch (EncoderFallbackException)
        {
            throw Invalid("valid Unicode name");
        }
        Require(IsDigest(artifactSha256) && IsDigest(sealSha256) && IsDigest(runHash), "64 lowercase hexadecimal digest characters");
        var root = new JsonObject
        {
            ["_type"] = StatementType,
            ["subject"] = new JsonArray(new JsonObject
            {
                ["name"] = name,
                ["digest"] = new JsonObject { ["sha256"] = artifactSha256 },
            }),
            ["predicateType"] = PredicateType,
            ["predicate"] = new JsonObject
            {
                ["schemaVersion"] = "1.0", ["binding"] = "producer-asserted",
                ["evidence"] = new JsonObject
                {
                    ["seal"] = new JsonObject { ["sha256"] = sealSha256 },
                    ["runHash"] = runHash,
                },
            },
        };
        return Parse(StrictUtf8.GetBytes(root.ToJsonString()));
    }

    private static string? Text(JsonObject value, string key) =>
        value[key] is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;

    private static string Digest(JsonNode? node)
    {
        Require(node is JsonObject { Count: 1 }, "one sha256 digest algorithm");
        var value = Text((JsonObject)node!, "sha256");
        Require(IsDigest(value), "64 lowercase hexadecimal sha256 characters");
        return value!;
    }

    private static bool IsDigest(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Require(bool condition, string expected)
    {
        if (!condition) throw Invalid(expected);
    }

    private static AefInputException Invalid(string expected) => new(
        $"Expected: {expected}. Actual: unsupported artifact companion field. " +
        "Suggestions: supply the artifact-evidence 1.0 profile. Because: ambiguous bindings must not be accepted.");
}
