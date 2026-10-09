// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Schemas;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Checkpoints;

/// <summary>What a checkpoint verifier is given beyond the manifest and its runs (§5.5).</summary>
public sealed class CheckpointVerifyOptions
{
    /// <summary>
    /// The evaluation time ([ENC-8]) for a checkpoint with no recorded input ([LANE-9]: the age of a lane none of whose
    /// runs found has closed). A decided checkpoint uses its <c>decisionInput.evaluatedAt</c> instead.
    /// </summary>
    public required string At { get; init; }

    /// <summary>
    /// The caller's trust policy ([SIG-4]), or null: the runs' redactions it authorizes withhold blobs ([OVL-10]) (give
    /// the same policy to the <see cref="AefRunStore"/>), and the checkpoint's envelope is verified against it ([CKP-9]).
    /// </summary>
    public TrustPolicy? Policy { get; init; }

    /// <summary>
    /// The bytes of the envelope beside the manifest (<c>&lt;name&gt;.dsse.json</c>, <c>&lt;name&gt;</c> the manifest's file
    /// name without <c>.json</c>, §4.4), or null. For a file, <see cref="EnvelopeFile"/> does not read one beyond the
    /// limit.
    /// </summary>
    public byte[]? Envelope { get; init; }

    /// <summary>
    /// The path of the envelope file, used when <see cref="Envelope"/> is null: a file beyond the 56 MiB [ENC-17] allows
    /// an envelope is <c>malformed</c> without being read, and verifies for no one ([SIG-1]).
    /// </summary>
    public string? EnvelopeFile { get; init; }
}

/// <summary>A checkpoint verifier's report (§5.5).</summary>
public sealed class CheckpointVerification
{
    internal CheckpointVerification(
        IReadOnlyList<string> manifestProblems, IReadOnlyList<LaneEvaluation> lanes, IReadOnlyList<AefProblem> problems,
        DsseVerification? signature, IReadOnlyList<string> anchors)
    {
        ManifestProblems = manifestProblems;
        Lanes = lanes;
        Problems = problems;
        Signature = signature;
        Anchors = anchors;
    }

    /// <summary>The problems of the manifest alone ([CKP-7]): codes, in code order.</summary>
    public IReadOnlyList<string> ManifestProblems { get; }

    /// <summary>Each lane's recomputed result (§5.3), in manifest order.</summary>
    public IReadOnlyList<LaneEvaluation> Lanes { get; }

    /// <summary>The problems against the runs ([CKP-8]), ordered as §3.9 orders problems.</summary>
    public IReadOnlyList<AefProblem> Problems { get; }

    /// <summary>The envelope's per-signature results (§4.4), when an envelope and a trust policy were given ([CKP-9]).</summary>
    public DsseVerification? Signature { get; }

    /// <summary>The identities the envelope verifies for, in policy order; none without an envelope or a policy.</summary>
    public IReadOnlyList<string> SignedBy => Signature?.VerifiesFor ?? [];

    /// <summary>
    /// The run hashes the checkpoint anchors ([CKP-9], [SIG-8]): those of the runs its lanes name and of their baselines,
    /// each once, in byte order, when it verifies with no problem (of [CKP-7] or [CKP-8]) and its signature verifies for a
    /// trusted identity; none otherwise.
    /// </summary>
    public IReadOnlyList<string> Anchors { get; }
}

/// <summary>
/// The checkpoint verifier (contracts/aef/1/spec/05-checkpoints.md, §5.5): the manifest alone ([CKP-7],
/// <see cref="CheckpointManifest"/>), against its runs ([CKP-8]: each run found and intact, each lane's result
/// recomputed with §5.3 and, for a decided checkpoint, compared with the recorded input), and its signature ([CKP-9]).
/// </summary>
public static class CheckpointVerifier
{
    /// <summary>Verifies a manifest from its exact bytes (the bytes its envelope signs).</summary>
    /// <exception cref="FormatException">The manifest is not an I-JSON document valid against the reader checkpoint schema.</exception>
    /// <exception cref="IOException">A run's file cannot be read.</exception>
    public static CheckpointVerification Verify(ReadOnlySpan<byte> manifest, AefRunStore runs, CheckpointVerifyOptions options)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(options);
        var document = Read(manifest);
        var (lanes, problems) = Lanes(document, runs, options.At);
        var manifestProblems = CheckpointManifest.Verify(document);

        DsseVerification? signature = null;
        if (options.Policy is { } policy)
        {
            if (options.Envelope is { } envelope)
            {
                signature = DsseVerifier.Verify(envelope, manifest, Dsse.CheckpointPayloadType, policy);
            }
            else if (options.EnvelopeFile is { } envelopeFile)
            {
                signature = DsseVerifier.VerifyFile(envelopeFile, manifest, Dsse.CheckpointPayloadType, policy);
            }
        }

        // [CKP-9], [SIG-8]: the run hashes of its lanes' runs and comparison baselines, each once, in byte order.
        IReadOnlyList<string> anchors = [];
        if (manifestProblems.Count == 0 && problems.Count == 0 && signature?.VerifiesFor.Count > 0)
        {
            anchors = [.. AefNode.Objects(document["lanes"])
                .SelectMany(l => AefNode.Objects(l["runs"]).Append(AefNode.Get(l["rule"], "baseline") as JsonObject).OfType<JsonObject>())
                .Select(r => AefNode.String(r["runHash"])).OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
        }

        return new CheckpointVerification(manifestProblems, lanes, problems, signature, anchors);
    }

    /// <summary>
    /// [CKP-8] and §5.3 for a manifest already read: each lane's recomputed result, in manifest order, and the problems
    /// against the runs (<c>run-missing</c>, <c>run-unverified</c> at <c>lanes/&lt;lane&gt;/runs/&lt;runId&gt;</c>;
    /// for a decided checkpoint, <c>lane-result</c>, <c>lane-version</c> and <c>oldest-closed</c> at
    /// <c>lanes/&lt;lane&gt;</c>, or only <c>unverifiable</c> there for a lane whose recomputation reads something this
    /// version does not know in a document that declares a later minor: a rule not valid against its writer schema in such
    /// a checkpoint, or an unknown value in such a run.json, result line or metrics.json), ordered as §3.9 orders problems.
    /// </summary>
    /// <param name="manifest">The manifest, valid against the reader checkpoint schema.</param>
    /// <param name="runs">Where the runs are found.</param>
    /// <param name="at">The evaluation time for a checkpoint with no recorded input ([LANE-9]).</param>
    /// <exception cref="FormatException">The manifest is not one the reader schema accepts.</exception>
    /// <exception cref="IOException">A run's file cannot be read.</exception>
    public static (IReadOnlyList<LaneEvaluation> Lanes, IReadOnlyList<AefProblem> Problems) Lanes(JsonObject manifest, AefRunStore runs, string at)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(at);
        var subject = CheckpointSubject.Read(manifest);
        var input = AefNode.Get(manifest, "decisionInput") as JsonObject;
        var fallback = AefNode.String(input?["evaluatedAt"]) ?? at;

        var lanes = new List<LaneEvaluation>();
        var problems = new HashSet<AefProblem>();
        foreach (var lane in AefNode.Objects(manifest["lanes"]))
        {
            var evaluation = LaneEvaluator.Evaluate(lane, subject, runs, fallback, AefNode.String(manifest["schemaVersion"]));
            lanes.Add(evaluation);
            problems.UnionWith(evaluation.Problems);

            // A decided checkpoint: the recorded input's result for this lane, compared with the one recomputed, unless
            // recomputing it read something this version does not know in a document that declares a later minor (a later
            // minor recorded what it cannot recompute): its rule is not valid against the writer schema in a checkpoint
            // that does, or a run it reads holds an unknown value it computes with in a document that does
            // (LaneEvaluator.ReadsUnknown). That lane is unverifiable, never lane-result. In a document that declares this
            // version, such a value is read as §7.3 says and compared as usual (round 5).
            if (AefNode.String(manifest["state"]) == "decided" && input is not null
                && AefNode.Objects(input["lanes"]).FirstOrDefault(l => AefNode.String(l["lane"]) == evaluation.Lane) is { } recorded)
            {
                IReadOnlyList<string> codes = evaluation.ReadsUnknown ? ["unverifiable"] : Differences(recorded["result"] as JsonObject, evaluation.Result);
                foreach (var code in codes)
                {
                    problems.Add(new AefProblem($"lanes/{evaluation.Lane}", code));
                }
            }
        }

        return (lanes, LaneEvaluator.ByBytes(problems));
    }

    /// <summary>
    /// [CKP-8]: how a recorded result differs from the recomputed one: <c>lane-result</c> (another status or other axes,
    /// or a result where none was recorded or the reverse), <c>lane-version</c> (another <c>subjectVersion</c>, byte for
    /// byte), <c>oldest-closed</c> (another <c>oldestClosedAt</c>, compared as a time at full precision, [ENC-8]).
    /// </summary>
    public static IReadOnlyList<string> Differences(JsonObject? recorded, LaneResult? recomputed)
    {
        if (recorded is null || recomputed is null)
        {
            return (recorded is null) == (recomputed is null) ? [] : ["lane-result"];
        }

        var codes = new List<string>();
        var axes = AefNode.Strings(recorded["axes"]).ToList();
        if (AefNode.String(recorded["status"]) != LaneResult.StatusName(recomputed.Status)
            || !axes.SequenceEqual(recomputed.Axes ?? [], StringComparer.Ordinal))
        {
            codes.Add("lane-result");
        }

        if (AefNode.String(recorded["subjectVersion"]) != recomputed.SubjectVersion)
        {
            codes.Add("lane-version");
        }

        if (AefNode.Time(recorded["oldestClosedAt"]) is not { } time || time != AefTime.Parse(recomputed.OldestClosedAt))
        {
            codes.Add("oldest-closed");
        }

        return codes;
    }

    /// <summary>Reads a manifest: an I-JSON document ([ENC-1]–[ENC-3]) valid against the reader checkpoint schema.</summary>
    /// <exception cref="FormatException">Anything else.</exception>
    public static JsonObject Read(ReadOnlySpan<byte> manifest)
    {
        JsonObject document;
        try
        {
            document = AefJsonReader.ParseDocument(manifest);
        }
        catch (AefReadException e)
        {
            throw new FormatException($"The checkpoint manifest is not an I-JSON document: {e.Message}", e);
        }

        return AefSchemas.Reader.Validate("checkpoint", document) is { } why
            ? throw new FormatException($"The checkpoint manifest is not valid against the reader checkpoint schema: {why}")
            : document;
    }
}
