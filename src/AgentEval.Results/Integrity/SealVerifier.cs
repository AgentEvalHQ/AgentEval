// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Schemas;

namespace AgentEval.Results.Integrity;

/// <summary>
/// "A run's run hash" as [SEAL-4] defines it everywhere the specification uses the words (spec 04, §4.1; [OVL-5];
/// checkpoints, §5): for a run with a <c>seal.json</c> valid against the reader seal schema, its
/// <c>predicate.runHash</c> (<paramref name="Sealed"/> true); for a run without one, the run hash recomputed from its
/// files. The seal check (<see cref="SealVerifier.Verify"/>) is what ties the sealed value to the files; with a withheld
/// blob only the sealed value can be known.
/// </summary>
/// <param name="Value">The run hash: 64 lower-case hex characters.</param>
/// <param name="Sealed">Whether it is the sealed value (a seal valid against the reader schema), not the recomputed one.</param>
public readonly record struct AefRunHash(string Value, bool Sealed);

/// <summary>
/// <c>seal.json</c> as read (§4.1): whether the run has one, and, when it reads as an I-JSON document valid against the
/// reader seal schema, the document. Otherwise <see cref="Problem"/> says why not: <c>seal-invalid</c> (not an I-JSON
/// document, or not valid against the reader seal schema), or <c>limit</c> (above the 40 MiB [ENC-17] allows a seal, or
/// nested deeper than 64: refused, [ENC-17], [ENC-18]; the run is then not intact, and its run hash is the recomputed one).
/// </summary>
public sealed class AefSealDocument
{
    private AefSealDocument(bool present, JsonObject? document, string? problem)
    {
        Present = present;
        Document = document;
        Problem = problem;
    }

    /// <summary>The run holds a <c>seal.json</c>.</summary>
    public bool Present { get; }

    /// <summary>The seal, when it is an I-JSON document valid against the reader seal schema; otherwise null.</summary>
    public JsonObject? Document { get; }

    /// <summary><c>seal-invalid</c> or <c>limit</c> when the run has a seal that did not read as a valid one; otherwise null.</summary>
    public string? Problem { get; }

    /// <summary>The seal reads and is valid against the reader seal schema.</summary>
    public bool IsValid => Document is not null;

    /// <summary>The sealed <c>predicate.runHash</c>, when <see cref="IsValid"/>.</summary>
    public string? RunHash => AefNode.String(AefNode.At(Document, "predicate", "runHash"));

    /// <summary>Each subject's name, in the seal's order (repeats kept), when <see cref="IsValid"/>; otherwise none.</summary>
    public IReadOnlyList<string> SubjectNames =>
        [.. AefNode.Objects(Document?["subject"]).Select(s => AefNode.String(s["name"]) ?? "")];

    /// <summary>Reads the seal of a run folder.</summary>
    /// <exception cref="IOException">seal.json cannot be read.</exception>
    public static AefSealDocument Read(AefRunFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!folder.Has(SealVerifier.SealPath))
        {
            return new AefSealDocument(false, null, null);
        }

        JsonObject document;
        try
        {
            document = AefJsonReader.ParseDocument(folder.Read(SealVerifier.SealPath, AefLimits.MaxSealBytes), AefLimits.MaxSealBytes);
        }
        catch (AefLimitException)
        {
            return new AefSealDocument(true, null, "limit");
        }
        catch (AefEncodingException)
        {
            return new AefSealDocument(true, null, "seal-invalid");
        }

        return AefSchemas.Reader.IsValid("seal", document)
            ? new AefSealDocument(true, document, null)
            : new AefSealDocument(true, null, "seal-invalid");
    }
}

/// <summary>
/// The verification of a run's seal (§4.1, [SEAL-6]): its problems, ordered as §3.9 orders problems, and the seal as
/// read. A run with no <c>seal.json</c> is unsealed: no problems, and the seal neither verifies nor fails.
/// </summary>
public sealed class SealVerification
{
    internal SealVerification(AefSealDocument seal, IReadOnlyList<AefProblem> problems)
    {
        Seal = seal;
        Problems = problems;
    }

    /// <summary>seal.json as read.</summary>
    public AefSealDocument Seal { get; }

    /// <summary>The problems of [SEAL-6] (<c>limit</c> at seal.json for one the reader refuses for a limit, [ENC-18]).</summary>
    public IReadOnlyList<AefProblem> Problems { get; }

    /// <summary>The run has a seal, and it verifies: no problem but <c>withheld</c> ([SEAL-6]).</summary>
    public bool Verifies => Seal.Present && Problems.All(p => p.Code == "withheld");

    /// <summary>How many sealed blobs are withheld by an authorized redaction ([OVL-10]; "intact, <i>n</i> withheld").</summary>
    public int Withheld => Problems.Count(p => p.Code == "withheld");
}

/// <summary>
/// Verifies a run's seal (contracts/aef/1/spec/04-integrity.md, §4.1): every file in the run but <c>seal.json</c>,
/// <c>attestation.dsse.json</c> and <c>overlays/</c> is sealed ([SEAL-1]), each by the SHA-256 of its exact bytes
/// ([SEAL-2]); the run hash is the SHA-256 of the manifest ([SEAL-3], [SEAL-4]); <c>seal.json</c> is an in-toto
/// Statement v1 whose predicate repeats what run.json says about the run ([SEAL-5]).
/// </summary>
public static class SealVerifier
{
    /// <summary>The seal's path in a run.</summary>
    public const string SealPath = "seal.json";

    /// <summary>The signature of the seal's path in a run ([SIG-1]).</summary>
    public const string AttestationPath = "attestation.dsse.json";

    /// <summary>
    /// "The run's run hash" ([SEAL-4], [OVL-5]): the sealed <c>predicate.runHash</c> of a <c>seal.json</c> valid against
    /// the reader seal schema, else the run hash recomputed from the files present. The value is not verified here: a
    /// seal whose files no longer match it is a problem of <see cref="Verify"/>, and such a run is not intact.
    /// </summary>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public static AefRunHash RunHashOf(AefRunFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var seal = AefSealDocument.Read(folder);
        return seal.RunHash is { } sealedHash ? new AefRunHash(sealedHash, true) : new AefRunHash(folder.ComputeRunHash(), false);
    }

    /// <summary>
    /// Verifies the seal ([SEAL-6]) and reports every difference as a path and a code. Verification stops after
    /// <c>seal-invalid</c> (and after <c>limit</c>: a seal not read is not checked).
    /// </summary>
    /// <param name="folder">The run folder.</param>
    /// <param name="run">run.json as read (an I-JSON object, valid against the schema or not), or null when it is absent or does not read: then <c>run-id</c>, <c>predicate</c> and <c>run-open</c> are not checked.</param>
    /// <param name="withheld">
    /// The blobs (SHA-256, lower-case hex) that authorized redactions withhold ([OVL-10]): a sealed blob that is gone is
    /// <c>withheld</c> when it is one of them, else <c>missing</c>.
    /// </param>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public static SealVerification Verify(AefRunFolder folder, JsonObject? run, IReadOnlySet<string> withheld)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(withheld);
        var seal = AefSealDocument.Read(folder);
        if (!seal.Present)
        {
            return new SealVerification(seal, []);
        }

        if (seal.Problem is { } unread)
        {
            return new SealVerification(seal, [new AefProblem(SealPath, unread)]);
        }

        var problems = new HashSet<AefProblem>();
        var names = seal.SubjectNames;
        var times = names.GroupBy(n => n, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var subject in AefNode.Objects(seal.Document!["subject"]))
        {
            var name = AefNode.String(subject["name"]) ?? "";
            if (times[name] > 1)
            {
                problems.Add(new AefProblem(name, "duplicate-subject"));   // its digests are not compared
            }
            else
            {
                digests[name] = AefNode.String(AefNode.At(subject, "digest", "sha256")) ?? "";
            }

            if (!AefRunFolder.IsSealed(name))
            {
                problems.Add(new AefProblem(name, "subject-path"));   // seal.json, attestation.dsse.json, overlays/
            }
        }

        // Every sealed file present is listed, with the digest of its bytes.
        foreach (var path in folder.SealedFiles)
        {
            if (!times.ContainsKey(path))
            {
                problems.Add(new AefProblem(path, "not-sealed"));
            }
            else if (digests.TryGetValue(path, out var digest) && !string.Equals(digest, folder.Sha256(path), StringComparison.Ordinal))
            {
                problems.Add(new AefProblem(path, "digest"));
            }
        }

        // §3.9: an entry whose name is not a Unicode string is named by no seal (a subject's name is a string); it is never
        // read, so never hashed.
        foreach (var path in folder.IllFormed)
        {
            problems.Add(new AefProblem(path, "not-sealed"));
        }

        // Every sealed file listed is present, or is a blob an authorized redaction withholds.
        foreach (var name in times.Keys.Where(n => AefRunFolder.IsSealed(n) && !folder.Has(n)))
        {
            problems.Add(new AefProblem(name, AefRunFolder.IsBlobPath(name, out var blob) && withheld.Contains(blob) ? "withheld" : "missing"));
        }

        // The run hash, when every file matches its subject (with a blob gone it cannot be recomputed).
        var predicate = (JsonObject)seal.Document!["predicate"]!;
        if (!problems.Any(p => p.Code is "duplicate-subject" or "digest" or "not-sealed" or "missing" or "withheld")
            && !string.Equals(folder.ComputeRunHash(), seal.RunHash, StringComparison.Ordinal))
        {
            problems.Add(new AefProblem(SealPath, "run-hash"));
        }

        if (run is not null)
        {
            if (!string.Equals(AefNode.String(predicate["runId"]), AefNode.String(run["runId"]), StringComparison.Ordinal))
            {
                problems.Add(new AefProblem(SealPath, "run-id"));
            }

            if (PredicateDiffers(predicate, run) || SealedBeforeClosed(predicate))
            {
                problems.Add(new AefProblem(SealPath, "predicate"));
            }

            if (AefNode.String(run["status"]) == "running")
            {
                problems.Add(new AefProblem("run.json", "run-open"));
            }
        }

        return new SealVerification(seal, AefFolder.PerEntry(AefProblemOrder.Sort(problems), folder.IllFormed));
    }

    /// <summary>
    /// [SEAL-6] <c>predicate</c>, its second half: a <c>sealedAt</c> earlier than <c>closedAt</c>, compared as times
    /// ([ENC-8]). Only a closed run is sealed ([SEAL-1]).
    /// </summary>
    public static bool SealedBeforeClosed(JsonObject predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return AefNode.Time(predicate["sealedAt"]) is { } sealedAt && AefNode.Time(predicate["closedAt"]) is { } closedAt && sealedAt < closedAt;
    }

    /// <summary>
    /// [SEAL-6] <c>predicate</c>: whether the predicate differs from run.json about the producer (name, version), the
    /// subject (ref, version), the deployment (ref), the suite (ref, version, digest), the judges (model, rubric digest,
    /// in order) or <c>closedAt</c> (run.json's <c>endedAt</c>, compared as a time, [ENC-8]). A <c>null</c> deployment
    /// or suite and an empty judges list in the predicate equal the field's absence in run.json ([SEAL-5]).
    /// </summary>
    public static bool PredicateDiffers(JsonObject predicate, JsonObject run)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(run);
        string[][] fields =
        [
            ["producer", "name"], ["producer", "version"], ["subject", "ref"], ["subject", "version"],
            ["deployment", "ref"], ["suite", "ref"], ["suite", "version"], ["suite", "digest"],
        ];
        if (fields.Any(f => !JsonNode.DeepEquals(AefNode.At(predicate, f), AefNode.At(run, f))))
        {
            return true;
        }

        static List<(JsonNode?, JsonNode?)> Judges(JsonObject document) =>
            [.. AefNode.Items(document["judges"]).Select(j => (AefNode.Get(j, "model"), AefNode.Get(j, "rubricDigest")))];
        var (sealedJudges, runJudges) = (Judges(predicate), Judges(run));
        if (sealedJudges.Count != runJudges.Count
            || sealedJudges.Zip(runJudges).Any(p => !JsonNode.DeepEquals(p.First.Item1, p.Second.Item1) || !JsonNode.DeepEquals(p.First.Item2, p.Second.Item2)))
        {
            return true;
        }

        var (closedAt, endedAt) = (predicate["closedAt"], run["endedAt"]);
        return AefNode.Time(closedAt) is { } closed && AefNode.Time(endedAt) is { } ended
            ? closed != ended
            : !JsonNode.DeepEquals(closedAt, endedAt);
    }
}
