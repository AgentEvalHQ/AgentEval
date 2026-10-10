// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Schemas;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Writing;

/// <summary>Who seals a run ([SEAL-5] <c>sealedBy</c>).</summary>
public enum AefSealedBy
{
    /// <summary><c>producer</c>: the producer sealed its own run.</summary>
    [AefName("producer")] Producer,

    /// <summary><c>ingest</c>: a host sealed a run on taking custody of it ([SEAL-1]).</summary>
    [AefName("ingest")] Ingest,
}

/// <summary>How <see cref="AefSealer.Seal"/> seals a run.</summary>
public sealed class AefSealOptions
{
    /// <summary>Who seals: the producer (the default), or a host taking custody (<see cref="AefSealedBy.Ingest"/>).</summary>
    public AefSealedBy SealedBy { get; init; } = AefSealedBy.Producer;

    /// <summary>A signer for <c>attestation.dsse.json</c> ([SIG-1]), or null to seal without signing.</summary>
    public IAefSigner? Signer { get; init; }

    /// <summary>The clock <c>sealedAt</c> is read from when <see cref="SealedAt"/> is null; the system's when null.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// The seal's <c>sealedAt</c> as written: an RFC 3339 UTC time with up to nine fraction digits ([ENC-8]), kept at the
    /// precision given (a <see cref="DateTimeOffset"/> holds 100 ns). Null: the time of <see cref="TimeProvider"/>.
    /// </summary>
    public string? SealedAt { get; init; }
}

/// <summary>What <see cref="AefSealer.Seal"/> wrote.</summary>
/// <param name="RunHash">The run hash ([SEAL-4]): the SHA-256 of the manifest, 64 lower-case hex characters.</param>
/// <param name="Manifest">The manifest the seal lists ([SEAL-3]).</param>
/// <param name="SealedAt">The seal's <c>sealedAt</c>, as written.</param>
/// <param name="Signed">Whether <c>attestation.dsse.json</c> was written.</param>
/// <param name="Verification">The run verifier's report after sealing (with the signer's key in its trust policy when signed).</param>
public sealed record AefSealResult(string RunHash, AefManifest Manifest, string SealedAt, bool Signed, AefRunVerification Verification);

/// <summary>
/// Seals a closed run (contracts/aef/1/spec/04-integrity.md, §4.1): writes <c>seal.json</c>, an in-toto Statement v1
/// with one subject per sealed file in the manifest's byte order and the predicate of [SEAL-5], a projection of
/// run.json; and, given a signer, <c>attestation.dsse.json</c>, a DSSE envelope over seal.json's exact bytes
/// ([SIG-1]). Nothing in the run is re-encoded: the digests are over the files' bytes ([SEAL-2]).
/// </summary>
public static class AefSealer
{
    /// <summary>The in-toto Statement type ([SEAL-5], [OVL-4]).</summary>
    public const string StatementType = "https://in-toto.io/Statement/v1";

    /// <summary>The predicate type of a run's seal ([SEAL-5], [ENC-12]).</summary>
    public const string EvidencePredicateType = "https://agenteval.dev/aef/1/evidence";

    // The identity the signer's key is given in the trust policy the sealer checks its own signature with.
    private const string SignerIdentity = "aef-sealer:self-check";

    /// <summary>
    /// Seals the run in <paramref name="directory"/>. The run is closed (only a closed run is sealed, [SEAL-1]) and not
    /// sealed yet; a producer seals a run with no problem at all (the run verifier says <c>unsealed</c>, no problem), a
    /// host (<see cref="AefSealedBy.Ingest"/>) a run whose files the reader schemas accept ([SEAL-1]); either way every
    /// path keeps [RUN-3] (a seal's subject names one). After sealing, the seal must verify: the run verifier finds no
    /// seal problem, and, for a producer's seal, says <c>intact</c>; with a signer, the signature verifies for the
    /// signer's key.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The run is running, or already has a <c>seal.json</c> or an <c>attestation.dsse.json</c>; or it is not a run the
    /// sealer seals (the message names the problems); or <c>sealedAt</c> would be earlier than the run's end.
    /// </exception>
    /// <exception cref="ArgumentException"><see cref="AefSealOptions.SealedAt"/> is not an RFC 3339 UTC time.</exception>
    /// <exception cref="AefWriteException">The seal written does not verify; it is removed again.</exception>
    /// <exception cref="IOException">The folder cannot be read or written.</exception>
    public static AefSealResult Seal(string directory, AefSealOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        options ??= new AefSealOptions();
        if (options.SealedAt is { } given)
        {
            try
            {
                AefTime.Parse(given);
            }
            catch (FormatException e)
            {
                throw new ArgumentException($"sealedAt '{given}' is not an RFC 3339 UTC time ([ENC-8]).", nameof(options), e);
            }
        }

        var folder = AefRunFolder.Open(directory);
        if (folder.Has(SealVerifier.SealPath) || folder.Has(SealVerifier.AttestationPath))
        {
            throw new InvalidOperationException($"{directory} is already sealed: a sealed run never changes ([RUN-4]).");
        }

        if (folder.OverFileLimit || folder.PathProblems.Count > 0)
        {
            throw new InvalidOperationException(
                $"{directory} has paths a seal cannot name ([RUN-3]): " + string.Join(", ", folder.PathProblems.Select(p => $"{p.Path} {p.Code}")));
        }

        var documents = AefRunDocuments.Read(folder);
        if (documents.Run is not { } run || documents.RunId is null)
        {
            throw new InvalidOperationException($"{directory} has no run.json that reads: it is not a run ([RUN-1]).");
        }

        if (!documents.IsClosed)
        {
            throw new InvalidOperationException($"{directory} is {documents.Status ?? "not closed"}: only a closed run is sealed ([SEAL-1]).");
        }

        if (options.SealedBy == AefSealedBy.Ingest)
        {
            if (documents.Problems.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{directory}: a host seals only a run whose files are valid against the reader schemas ([SEAL-1]): "
                    + string.Join(", ", documents.Problems.Select(p => $"{p.Path} {p.Code}")));
            }
        }
        else
        {
            var before = AefRunVerifier.Verify(folder);
            if (before.Problems.Count > 0)
            {
                throw new AefWriteException($"{directory}: a producer seals only a run without problems", before.Problems);
            }
        }

        var manifest = folder.Manifest();
        var sealedAt = options.SealedAt ?? AefWire.Time((options.TimeProvider ?? TimeProvider.System).GetUtcNow());
        var closedAt = AefNode.String(run["endedAt"])!;   // as run.json writes it, every fraction digit kept
        if (AefTime.Parse(sealedAt) < AefTime.Parse(closedAt))
        {
            throw new InvalidOperationException($"sealedAt {sealedAt} would be earlier than the run's end {closedAt}: a run is sealed after it closed ([SEAL-1]).");
        }

        var seal = Statement(manifest, run, closedAt, sealedAt, options.SealedBy);
        if (AefSchemas.Writer.Validate("seal", seal) is { } failure)
        {
            throw new InvalidOperationException($"seal.json would not be valid against the writer seal schema ([VER-2]): {failure}.");
        }

        var sealBytes = AefJsonWriter.Document(seal, AefLimits.MaxSealBytes);
        var sealPath = Path.Combine(directory, SealVerifier.SealPath);
        var attestationPath = Path.Combine(directory, SealVerifier.AttestationPath);
        File.WriteAllBytes(sealPath, sealBytes);
        try
        {
            TrustPolicy? policy = null;
            byte[]? attestation = null;
            if (options.Signer is { } signer)
            {
                attestation = DsseEnvelope.Create(Dsse.InTotoPayloadType, sealBytes, signer).ToJson();
                File.WriteAllBytes(attestationPath, attestation);
                policy = new TrustPolicy([new TrustedKey(SignerIdentity, signer.PublicKey)]);
            }

            // The seal verifies (for a producer's seal, the run is intact), and the signature verifies for the signer.
            var after = AefRunVerifier.Verify(directory, new AefVerifyOptions { Policy = policy });
            if (options.SealedBy == AefSealedBy.Producer && (after.Outcome != AefOutcome.Intact || after.Problems.Count > 0))
            {
                throw new AefWriteException("The sealed run is not intact", after.Problems);
            }

            if (options.SealedBy == AefSealedBy.Ingest
                && SealVerifier.Verify(AefRunFolder.Open(directory), run, new HashSet<string>(StringComparer.Ordinal)).Problems is { Count: > 0 } sealProblems)
            {
                throw new AefWriteException("The seal written does not verify", sealProblems);
            }

            if (policy is not null
                && !DsseVerifier.Verify(attestation!, sealBytes, Dsse.InTotoPayloadType, policy).VerifiesForIdentity(SignerIdentity))
            {
                throw new AefWriteException("attestation.dsse.json does not verify for the signer's key ([SIG-5])", []);
            }

            return new AefSealResult(manifest.RunHash, manifest, sealedAt, options.Signer is not null, after);
        }
        catch
        {
            File.Delete(attestationPath);
            File.Delete(sealPath);
            throw;
        }
    }

    // [SEAL-5]: the statement, its subjects in the manifest's order, and the predicate (run.json's producer, subject,
    // deployment, suite and judges, as the predicate schema projects them; null deployment and suite, empty judges,
    // where run.json has none), members in the schema's order.
    private static JsonObject Statement(AefManifest manifest, JsonObject run, string closedAt, string sealedAt, AefSealedBy sealedBy)
    {
        var subjects = new JsonArray();
        foreach (var entry in manifest.Entries)
        {
            subjects.Add(new JsonObject { ["name"] = entry.Path, ["digest"] = new JsonObject { ["sha256"] = entry.Sha256 } });
        }

        return new JsonObject
        {
            ["_type"] = StatementType,
            ["subject"] = subjects,
            ["predicateType"] = EvidencePredicateType,
            ["predicate"] = new JsonObject
            {
                ["schemaVersion"] = AefRunWriter.SchemaVersion,
                ["runId"] = run["runId"]!.DeepClone(),
                ["runHash"] = manifest.RunHash,
                ["producer"] = Project(run["producer"], "name", "version"),
                ["subject"] = Project(run["subject"], "ref", "version"),
                ["deployment"] = run["deployment"] is null ? null : Project(run["deployment"], "ref"),
                ["suite"] = run["suite"] is null ? null : Project(run["suite"], "ref", "version", "digest"),
                ["judges"] = new JsonArray([.. AefNode.Objects(run["judges"]).Select(j => (JsonNode?)Project(j, "model", "rubricDigest"))]),
                ["closedAt"] = closedAt,
                ["sealedBy"] = AefWire.Node(sealedBy),
                ["sealedAt"] = sealedAt,
            },
        };
    }

    // The members of an object that the predicate repeats, as run.json writes them.
    private static JsonObject Project(JsonNode? source, params string[] names)
    {
        var json = new JsonObject();
        foreach (var name in names)
        {
            if (AefNode.Get(source, name) is { } value)
            {
                json[name] = value.DeepClone();
            }
        }

        return json;
    }
}
