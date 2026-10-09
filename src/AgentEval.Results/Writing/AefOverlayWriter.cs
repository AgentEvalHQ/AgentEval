// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Schemas;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Writing;

/// <summary>An overlay event's <c>kind</c> ([OVL-1]).</summary>
public enum AefOverlayKind
{
    /// <summary><c>approve</c>: the run or a result; its review status ([OVL-8]).</summary>
    [AefName("approve")] Approve,

    /// <summary><c>reject</c>: the run or a result; its review status ([OVL-8]).</summary>
    [AefName("reject")] Reject,

    /// <summary><c>override</c>: a result, with the state it sets and a reason ([OVL-7]).</summary>
    [AefName("override")] Override,

    /// <summary><c>adjudicate</c>: a result, with the state it sets and a reason, resolving a split ([OVL-7]).</summary>
    [AefName("adjudicate")] Adjudicate,

    /// <summary><c>acknowledge</c>: recorded, no effect on states.</summary>
    [AefName("acknowledge")] Acknowledge,

    /// <summary><c>accept_baseline</c>: recorded, no effect on states.</summary>
    [AefName("accept_baseline")] AcceptBaseline,

    /// <summary><c>waive</c>: a result or a requirement, with a reason and an expiry ([OVL-9]).</summary>
    [AefName("waive")] Waive,

    /// <summary><c>annotate</c>: recorded, no effect on states.</summary>
    [AefName("annotate")] Annotate,

    /// <summary><c>redact</c>: a blob of the run, with a reason; withholds it when authorized ([OVL-10]).</summary>
    [AefName("redact")] Redact,
}

/// <summary>What an overlay event's writer claims about its identity ([OVL-3]); a reader shows it only as far as it verified it.</summary>
public enum AefAssurance
{
    /// <summary><c>self-attested</c>.</summary>
    [AefName("self-attested")] SelfAttested,

    /// <summary><c>signed</c>: a batch signature verifies for the identity.</summary>
    [AefName("signed")] Signed,

    /// <summary><c>authenticated</c>: a host authenticated it.</summary>
    [AefName("authenticated")] Authenticated,
}

/// <summary>An overlay event's <c>by</c>: an identity (a stable opaque id rather than an e-mail address, [SEC-4]) and the assurance claimed for it.</summary>
/// <param name="Identity">Who.</param>
/// <param name="Assurance">What the writer claims.</param>
public sealed record AefIdentity(string Identity, AefAssurance Assurance);

/// <summary>
/// What an overlay event targets ([OVL-1], [OVL-2]): the run itself (none of the three given), a result, a
/// requirement, or a blob of the run. The writer adds the run's own <c>runId</c> and its run hash ([OVL-2]).
/// </summary>
public sealed record AefOverlayTarget
{
    /// <summary>The run itself.</summary>
    public static AefOverlayTarget TheRun { get; } = new();

    /// <summary>A result of the run, by its <c>resultId</c>.</summary>
    public string? Result { get; init; }

    /// <summary>A requirement, by its id.</summary>
    public string? Requirement { get; init; }

    /// <summary>A blob of the run, by its SHA-256 (64 lower-case hex characters).</summary>
    public string? Blob { get; init; }

    internal JsonObject ToJson(string runId, string runHash)
    {
        var json = new JsonObject { ["run"] = runId, ["runHash"] = runHash };
        json.Put("result", Result);
        json.Put("requirement", Requirement);
        json.Put("blob", Blob);
        return json;
    }
}

/// <summary>One event of <c>overlays/events.ndjson</c> ([OVL-1]).</summary>
public sealed record AefOverlayEvent
{
    /// <summary>The event's id, <c>ov_</c> and 1–64 letters or digits, unique in the file; the writer makes one when null.</summary>
    public string? EventId { get; init; }

    /// <summary>What the event does.</summary>
    public required AefOverlayKind Kind { get; init; }

    /// <summary>What it targets.</summary>
    public required AefOverlayTarget Target { get; init; }

    /// <summary>The state an <c>override</c> or <c>adjudicate</c> sets (never <c>pending</c>).</summary>
    public AefState? State { get; init; }

    /// <summary>Why: required for <c>override</c>, <c>adjudicate</c>, <c>waive</c> and <c>redact</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>When a <c>waive</c> expires.</summary>
    public DateTimeOffset? Expires { get; init; }

    /// <summary>Who, and the assurance claimed ([OVL-3]).</summary>
    public required AefIdentity By { get; init; }

    /// <summary>When ([OVL-6]: shown, never used to reorder).</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Producer extensions ([ENC-19]).</summary>
    public JsonObject? Ext { get; init; }

    internal JsonObject ToJson(string eventId, string runId, string runHash)
    {
        var json = new JsonObject
        {
            ["schemaVersion"] = AefRunWriter.SchemaVersion,
            ["eventId"] = eventId,
            ["kind"] = AefWire.Node(Kind),
            ["target"] = Target.ToJson(runId, runHash),
        };
        json.Put("state", State is { } s ? AefWire.Node(s) : null);
        json.Put("reason", Reason);
        json.Put("expires", Expires is { } expires ? AefWire.Time(expires) : null);
        json["by"] = new JsonObject { ["identity"] = By.Identity, ["assurance"] = AefWire.Node(By.Assurance) };
        json["at"] = AefWire.Time(At);
        json.Put("ext", AefWire.Ext(Ext));
        return json;
    }
}

/// <summary>A batch <see cref="AefOverlayWriter.SealBatch"/> sealed ([OVL-4]).</summary>
/// <param name="Number">The batch number (1-based).</param>
/// <param name="SealPath">Its seal, <c>overlays/seal-&lt;nnnn&gt;.json</c>.</param>
/// <param name="SignaturePath">Its signature, <c>overlays/seal-&lt;nnnn&gt;.dsse.json</c>, or null when unsigned.</param>
/// <param name="Offset">Where its bytes start in <c>overlays/events.ndjson</c>.</param>
/// <param name="Length">How many bytes it covers.</param>
/// <param name="Events">How many events it holds.</param>
public sealed record AefSealedBatch(int Number, string SealPath, string? SignaturePath, long Offset, long Length, int Events);

/// <summary>
/// Appends overlay events to a closed run and seals them in batches (contracts/aef/1/spec/04-integrity.md, §4.2):
/// <see cref="Append"/> adds whole lines to <c>overlays/events.ndjson</c> ([OVL-1]: append-only), and
/// <see cref="SealBatch"/> writes <c>overlays/seal-&lt;nnnn&gt;.json</c> over the bytes appended since the last batch,
/// bound to the run's run hash and chained to the previous seal by <c>previous</c> ([OVL-4]), optionally signed
/// (<c>overlays/seal-&lt;nnnn&gt;.dsse.json</c>, [SIG-1]). An overlay never edits a sealed file; the one exception is
/// <see cref="DeleteRedactedBlob"/>, the deletion [OVL-10] allows.
/// </summary>
/// <remarks>
/// A batch holds every byte appended since the previous batch, so events another writer appended without sealing are
/// sealed (and signed) with this writer's: <see cref="UnsealedAtOpen"/> says how many there were when the writer
/// opened the run, and a file that changed since then is refused. A writer is not safe for use by several threads at
/// once.
/// </remarks>
public sealed class AefOverlayWriter
{
    /// <summary>The highest batch number a seal's file name can hold (<c>seal-9999.json</c>, [OVL-4]).</summary>
    public const int MaxBatches = 9999;

    /// <summary>The predicate type of a batch seal ([OVL-4], [ENC-12]).</summary>
    public const string OverlayBatchPredicateType = "https://agenteval.dev/aef/1/overlay-batch";

    // The codes of OverlayChain that concern single events: the chain still verifies with them (§4.3), and so does
    // a single line's limit (at overlays/events.ndjson:<line>).
    private static readonly HashSet<string> EventCodes = new(StringComparer.Ordinal) { "event-invalid", "event-id", "target" };

    private readonly string _directory;
    private readonly IReadOnlySet<string>? _resultIds;
    private readonly HashSet<string> _blobs;
    private readonly HashSet<string> _eventIds;
    private readonly MemoryStream _tail = new();
    private int _lines;
    private long _sealedEnd;

    private AefOverlayWriter(string directory, string runId, string runHash, IReadOnlySet<string>? resultIds, HashSet<string> blobs, OverlayChain chain, byte[] events)
    {
        _directory = directory;
        RunId = runId;
        RunHash = runHash;
        _resultIds = resultIds;
        _blobs = blobs;
        _eventIds = chain.Events.Where(e => e.Event is not null).Select(e => AefNode.String(e.Event!["eventId"]) ?? "").ToHashSet(StringComparer.Ordinal);
        Batches = chain.Batches.Count;
        _sealedEnd = chain.VerifiedEnd;
        _tail.Write(events.AsSpan((int)_sealedEnd));
        _lines = chain.Events.Count;
        UnsealedAtOpen = chain.UnsealedEvents;
    }

    /// <summary>The run's <c>runId</c>: every event targets it ([OVL-2]).</summary>
    public string RunId { get; }

    /// <summary>"The run's run hash" ([SEAL-4], [OVL-5]): the sealed one for a sealed run. Every batch and event names it.</summary>
    public string RunHash { get; }

    /// <summary>The batches sealed so far.</summary>
    public int Batches { get; private set; }

    /// <summary>Events after the last batch when the writer opened the run: the next batch seals them too.</summary>
    public int UnsealedAtOpen { get; }

    /// <summary>
    /// Opens a closed run for overlays. Its overlay chain, if any, must verify up to its last batch (events after it,
    /// unsealed, are allowed, and so are problems of single events, which have no effect, §4.3: a blank line, a CR or a
    /// byte-order mark is a problem of its line alone, [OVL-5]). An unfinished last line (a writer still writing it, or
    /// one that crashed) is ended with an LF by the first <see cref="Append"/>, so it reads as one invalid event of the
    /// next batch rather than joining the next event ([OVL-5]: a crash costs one line, never the chain).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The folder has no run.json that reads, the run is running (overlays are what is added after close, §4.2), or its
    /// chain has a problem about a batch or the events file.
    /// </exception>
    /// <exception cref="IOException">The folder cannot be read.</exception>
    public static AefOverlayWriter Open(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var folder = AefRunFolder.Open(directory);
        var documents = AefRunDocuments.Read(folder);
        if (documents.RunId is not { } runId)
        {
            throw new InvalidOperationException($"{directory} has no run.json that reads: it is not a run ([RUN-1]).");
        }

        if (!documents.IsClosed)
        {
            throw new InvalidOperationException($"{directory} is {documents.Status ?? "not closed"}: overlays are what is added to a run after it closed (§4.2, [RUN-4]).");
        }

        var runHash = SealVerifier.RunHashOf(folder).Value;
        var chain = OverlayChain.Verify(folder, runId, runHash, documents.ResultIds);
        var broken = chain.Problems.Where(p => !OfOneEvent(p) && !(p is { Path: OverlayChain.EventsPath, Code: "uncovered" })).ToList();
        if (broken.Count > 0)
        {
            throw new AefWriteException($"{directory}: the overlay chain does not verify, and a batch appended to it would not either", broken);
        }

        // The run's blobs: those present, and those its seal lists (a withheld blob is still the run's).
        var blobs = folder.Files.Concat(AefSealDocument.Read(folder).SubjectNames)
            .Select(p => AefRunFolder.IsBlobPath(p, out var sha) ? sha : null).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var events = folder.Has(OverlayChain.EventsPath) ? folder.Read(OverlayChain.EventsPath, Array.MaxLength) : [];
        return new AefOverlayWriter(directory, runId, runHash, documents.ResultIds, blobs, chain, events);
    }

    /// <summary>
    /// Appends one event to <c>overlays/events.ndjson</c> ([OVL-1]), targeting this run and its run hash ([OVL-2]). It
    /// has no effect until a batch seals it (§4.3). When the file ends in an unfinished line, that line is ended with an LF
    /// first ([OVL-5]): it then reads as an invalid event, which the next batch seals with this one.
    /// </summary>
    /// <returns>The event's id.</returns>
    /// <exception cref="ArgumentException">
    /// The event is not valid against the writer overlay-event schema (a kind's required members and targets among
    /// it); its id is taken; it targets a result that is no line of the run, or a blob that is not the run's ([OVL-2],
    /// [OVL-5] <c>target</c>).
    /// </exception>
    /// <exception cref="InvalidOperationException">The events file holds as many lines as [ENC-17] allows.</exception>
    public string Append(AefOverlayEvent overlayEvent)
    {
        ArgumentNullException.ThrowIfNull(overlayEvent);
        var eventId = overlayEvent.EventId ?? "ov_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        if (_eventIds.Contains(eventId))
        {
            throw new ArgumentException($"The event id {eventId} is taken: an event's id is unique ([OVL-1], [OVL-5] event-id).", nameof(overlayEvent));
        }

        if (overlayEvent.Target.Result is { } result && (_resultIds is null || !_resultIds.Contains(result)))
        {
            throw new ArgumentException($"{result} is no result of the run {RunId}: an overlay targets only its own run ([OVL-2]).", nameof(overlayEvent));
        }

        if (overlayEvent.Target.Blob is { } blob && !_blobs.Contains(blob))
        {
            throw new ArgumentException($"{blob} is no blob of the run {RunId} ([OVL-1], [OVL-2]).", nameof(overlayEvent));
        }

        var json = overlayEvent.ToJson(eventId, RunId, RunHash);
        if (AefSchemas.Writer.Validate("overlay-event", json) is { } failure)
        {
            throw new ArgumentException($"The event {eventId} is not valid against the writer overlay-event schema ([VER-2]): {failure}.", nameof(overlayEvent));
        }

        // [OVL-5]: an unfinished last line (a writer still writing it, or one that crashed) is ended with an LF first, so it
        // reads as one invalid event and the next batch can claim it; it is never joined to this event.
        var unfinished = _tail.Length > 0 && _tail.GetBuffer()[_tail.Length - 1] != (byte)'\n';
        var line = AefJsonWriter.Line(json);
        if (unfinished)
        {
            line = [(byte)'\n', .. line];
        }

        if (_lines + (unfinished ? 2 : 1) > AefLimits.MaxLines)
        {
            throw new InvalidOperationException($"{OverlayChain.EventsPath} holds {AefLimits.MaxLines} lines, the most [ENC-17] allows.");
        }

        if (_sealedEnd + _tail.Length + line.Length > AefLimits.MaxNdjsonBytes)
        {
            throw new InvalidOperationException($"{OverlayChain.EventsPath} would exceed the {AefLimits.MaxNdjsonBytes} bytes [ENC-17] allows an NDJSON file.");
        }

        var path = Full(OverlayChain.EventsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            stream.Write(line);
        }

        _tail.Write(line);
        _lines += unfinished ? 2 : 1;
        _eventIds.Add(eventId);
        return eventId;
    }

    /// <summary>
    /// Seals the events appended since the last batch as the next batch ([OVL-4]): <c>overlays/seal-&lt;nnnn&gt;.json</c>,
    /// an in-toto Statement v1 whose subject is the batch's bytes of the events file and whose predicate names the
    /// batch, the run, its run hash, the range and the previous seal; signed by <paramref name="signer"/> when one is
    /// given. Afterwards the chain must verify up to this batch, and the signature for the signer's key.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Nothing to seal; batch <see cref="MaxBatches"/> already sealed; or the events file changed since this writer
    /// read it.
    /// </exception>
    /// <exception cref="AefWriteException">The batch written does not verify; its files are removed again.</exception>
    public AefSealedBatch SealBatch(IAefSigner? signer = null)
    {
        if (_tail.Length == 0)
        {
            throw new InvalidOperationException("No event was appended since the last batch: a batch holds at least one ([OVL-4]).");
        }

        if (Batches >= MaxBatches)
        {
            throw new InvalidOperationException($"Batch {MaxBatches} is sealed: a batch seal's name has four digits ([OVL-4]).");
        }

        var events = File.ReadAllBytes(Full(OverlayChain.EventsPath));
        var batch = _tail.ToArray();
        if (events.Length != _sealedEnd + batch.Length || !events.AsSpan((int)_sealedEnd).SequenceEqual(batch))
        {
            throw new InvalidOperationException($"{OverlayChain.EventsPath} changed since this writer read it: it would seal events it did not see.");
        }

        var number = Batches + 1;
        JsonObject? previous = null;
        if (number > 1)
        {
            var before = OverlayChain.SealPath(number - 1);
            previous = new JsonObject { ["path"] = before, ["sha256"] = AefWire.Sha256(File.ReadAllBytes(Full(before))) };
        }

        var seal = new JsonObject
        {
            ["_type"] = AefSealer.StatementType,
            ["subject"] = new JsonArray(new JsonObject
            {
                ["name"] = OverlayChain.EventsPath,
                ["digest"] = new JsonObject { ["sha256"] = AefWire.Sha256(batch) },
            }),
            ["predicateType"] = OverlayBatchPredicateType,
            ["predicate"] = new JsonObject
            {
                ["schemaVersion"] = AefRunWriter.SchemaVersion,
                ["runId"] = RunId,
                ["batch"] = number,
                ["offset"] = AefWire.Integer(_sealedEnd, "offset"),
                ["length"] = AefWire.Integer(batch.Length, "length"),
                ["previous"] = previous,
                ["runHash"] = RunHash,
            },
        };
        if (AefSchemas.Writer.Validate("overlay-seal", seal) is { } failure)
        {
            throw new InvalidOperationException($"The batch seal would not be valid against the writer overlay-seal schema ([VER-2]): {failure}.");
        }

        var sealPath = OverlayChain.SealPath(number);
        var signaturePath = signer is null ? null : OverlayChain.SignaturePath(number);
        var sealBytes = AefJsonWriter.Document(seal, AefLimits.MaxSealBytes);
        File.WriteAllBytes(Full(sealPath), sealBytes);
        try
        {
            if (signer is not null)
            {
                File.WriteAllBytes(Full(signaturePath!), DsseEnvelope.Create(Dsse.InTotoPayloadType, sealBytes, signer).ToJson());
            }

            Check(number, signer);
        }
        catch
        {
            File.Delete(Full(sealPath));
            if (signaturePath is not null)
            {
                File.Delete(Full(signaturePath));
            }

            throw;
        }

        var count = batch.Count(b => b == (byte)'\n');
        var sealedBatch = new AefSealedBatch(number, sealPath, signaturePath, _sealedEnd, batch.Length, count);
        _sealedEnd += batch.Length;
        _tail.SetLength(0);
        Batches = number;
        return sealedBatch;
    }

    /// <summary>
    /// Deletes a sealed blob that an authorized redaction withholds ([OVL-10], [SEC-5]): a <c>redact</c> event naming it,
    /// in a verified batch whose signature verifies, under <paramref name="policy"/>, for the event's identity, which
    /// the policy lets redact. The seal then reports the blob <c>withheld</c>, not <c>missing</c>, and the run stays
    /// intact ("intact, <i>n</i> withheld").
    /// </summary>
    /// <param name="directory">The run folder.</param>
    /// <param name="sha256">The blob's SHA-256, 64 lower-case hex characters.</param>
    /// <param name="policy">The trust policy that decides which redactions are authorized ([SIG-4]).</param>
    /// <exception cref="InvalidOperationException">
    /// The run has no valid seal listing the blob (only a sealed blob is withheld, [OVL-10]), the blob is not there, or
    /// no authorized redaction names it.
    /// </exception>
    /// <exception cref="AefWriteException">After the deletion the run does not verify as before with the blob withheld; the blob is restored.</exception>
    public static void DeleteRedactedBlob(string directory, string sha256, TrustPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentNullException.ThrowIfNull(policy);
        var folder = AefRunFolder.Open(directory);
        var path = AefRunFolder.BlobPath(sha256);
        if (!AefSealDocument.Read(folder).SubjectNames.Contains(path, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"{path} is not a blob the run's seal lists: only a sealed blob is withheld ([OVL-10]).");
        }

        if (!folder.Has(path))
        {
            throw new InvalidOperationException($"{path} is not in the run.");
        }

        var chain = OverlayChain.Verify(folder, AefRunDocuments.Read(folder));
        if (!EffectiveView.AuthorizedRedactions(folder, chain, policy).Contains(sha256, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"No authorized redaction names {sha256}: a blob is withheld only by a signed redaction whose identity the policy lets redact ([OVL-10]).");
        }

        var options = new AefVerifyOptions { Policy = policy };
        var before = AefRunVerifier.Verify(folder, options);
        var full = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
        var bytes = File.ReadAllBytes(full);
        File.Delete(full);
        RemoveEmptyFolders(directory, Path.GetDirectoryName(full)!);

        var after = AefRunVerifier.Verify(directory, options);
        var expected = AefProblemOrder.Sort(before.Problems.Append(new AefProblem(path, "withheld")));
        if (!after.Problems.SequenceEqual(expected))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            throw new AefWriteException($"Without {path} the run does not verify as before with the blob withheld", after.Problems);
        }
    }

    // The chain verifies up to batch `number` (problems of single events aside), and its signature for the signer.
    private void Check(int number, IAefSigner? signer)
    {
        var folder = AefRunFolder.Open(_directory);
        var documents = AefRunDocuments.Read(folder);
        var chain = OverlayChain.Verify(folder, RunId, SealVerifier.RunHashOf(folder).Value, documents.ResultIds);
        var broken = chain.Problems.Where(p => !OfOneEvent(p)).ToList();
        if (broken.Count > 0 || chain.Batches.LastOrDefault() is not { Verified: true } last || last.Number != number)
        {
            throw new AefWriteException($"Batch {number.ToString(CultureInfo.InvariantCulture)} does not verify", broken);
        }

        const string identity = "aef-overlay-writer:self-check";
        if (signer is not null
            && !EffectiveView.BatchSigners(folder, number, new TrustPolicy([new TrustedKey(identity, signer.PublicKey)])).Contains(identity, StringComparer.Ordinal))
        {
            throw new AefWriteException($"The signature of batch {number.ToString(CultureInfo.InvariantCulture)} does not verify for the signer's key ([SIG-5])", []);
        }
    }

    private static bool OfOneEvent(AefProblem problem) =>
        EventCodes.Contains(problem.Code) || (problem.Code == "limit" && problem.Path.StartsWith(OverlayChain.EventsPath + ":", StringComparison.Ordinal));

    // A deleted blob leaves no empty folder behind (blobs/sha256/<ab>, then blobs/sha256 and blobs when they empty).
    private static void RemoveEmptyFolders(string root, string folder)
    {
        var top = Path.GetFullPath(root);
        for (var current = Path.GetFullPath(folder);
             current.Length > top.Length && Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any();
             current = Path.GetDirectoryName(current)!)
        {
            Directory.Delete(current);
        }
    }

    private string Full(string path) => Path.Combine(_directory, path.Replace('/', Path.DirectorySeparatorChar));
}
