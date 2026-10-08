// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Schemas;

namespace AgentEval.Results.Integrity;

/// <summary>
/// One batch seal of a run's overlay chain ([OVL-4]): <c>overlays/seal-&lt;nnnn&gt;.json</c>, its number, whether it
/// read as a seal valid against the reader schema, the byte range of <c>overlays/events.ndjson</c> it claims, and
/// whether it is one of the verified batches the effective view is computed from (§4.3).
/// </summary>
/// <param name="Number">The batch number, from the file name.</param>
/// <param name="Path">The seal's path in the run.</param>
/// <param name="Valid">The seal is an I-JSON document valid against the reader <c>overlay-seal</c> schema.</param>
/// <param name="Offset">Where its range starts (0 when not <paramref name="Valid"/>).</param>
/// <param name="Length">The range's length (0 when not <paramref name="Valid"/>).</param>
/// <param name="Verified">The batch is in the verified prefix: it and every batch before it have no problem about the batch itself.</param>
public sealed record OverlayBatch(int Number, string Path, bool Valid, long Offset, long Length, bool Verified)
{
    /// <summary>Where the range ends (exclusive).</summary>
    public long End => Offset + Length;
}

/// <summary>
/// One line of <c>overlays/events.ndjson</c>: its 1-based number, its byte range (the LF included), the event when it is
/// an I-JSON object valid against the reader <c>overlay-event</c> schema, whether it is usable (valid, and neither an
/// <c>event-id</c> nor a <c>target</c> problem), and the verified batch it is in (null when it is after them).
/// </summary>
public sealed record OverlayEventLine(int Number, long Offset, long End, JsonObject? Event, bool Usable, int? Batch)
{
    /// <summary>The line takes part in the effective view (§4.3): usable, and in a verified batch.</summary>
    public bool Verified => Usable && Batch is not null;
}

/// <summary>
/// A run's overlay chain verified (contracts/aef/1/spec/04-integrity.md, §4.2, [OVL-4], [OVL-5]): the problems an
/// overlay verifier reports, ordered as §3.9 orders them, the batches, the verified prefix (§4.3) and the events.
/// Overlay problems never change the run's own seal or its verification outcome (§4.5).
/// </summary>
public sealed partial class OverlayChain
{
    /// <summary>The events file's path in a run.</summary>
    public const string EventsPath = "overlays/events.ndjson";

    private OverlayChain(IReadOnlyList<AefProblem> problems, IReadOnlyList<OverlayBatch> batches, long verifiedEnd, IReadOnlyList<OverlayEventLine> events)
    {
        Problems = problems;
        Batches = batches;
        VerifiedEnd = verifiedEnd;
        Events = events;
    }

    /// <summary>The problems of [OVL-5], in the order of §3.9.</summary>
    public IReadOnlyList<AefProblem> Problems { get; }

    /// <summary>The batch seals present, from <c>seal-0001.json</c> up, in number order (<c>seal-0000.json</c> is not a batch).</summary>
    public IReadOnlyList<OverlayBatch> Batches { get; }

    /// <summary>Where the verified prefix of the events file ends: the end of the last verified batch, or 0.</summary>
    public long VerifiedEnd { get; }

    /// <summary>The lines of the events file, in file order.</summary>
    public IReadOnlyList<OverlayEventLine> Events { get; }

    /// <summary>The events that take part in the effective view, in file order ([OVL-6]).</summary>
    public IEnumerable<OverlayEventLine> VerifiedEvents => Events.Where(e => e.Verified);

    /// <summary>The number of events after the last verified batch: shown as unsealed, with no effect (§4.3).</summary>
    public int UnsealedEvents => Events.Count(e => e.Offset >= VerifiedEnd);

    /// <summary>
    /// The batch signature's path for batch <paramref name="number"/> (<c>overlays/seal-&lt;nnnn&gt;.dsse.json</c>, [SIG-1]).
    /// </summary>
    public static string SignaturePath(int number) => $"overlays/seal-{number.ToString("D4", CultureInfo.InvariantCulture)}.dsse.json";

    /// <summary>The batch seal's path for batch <paramref name="number"/> (<c>overlays/seal-&lt;nnnn&gt;.json</c>).</summary>
    public static string SealPath(int number) => $"overlays/seal-{number.ToString("D4", CultureInfo.InvariantCulture)}.json";

    /// <summary>
    /// Verifies the chain of a run whose documents were read: the run's <c>runId</c> from run.json, "the run's run hash"
    /// of [SEAL-4] (<see cref="SealVerifier.RunHashOf"/>), and its result ids when results.ndjson reads whole.
    /// </summary>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public static OverlayChain Verify(AefRunFolder folder, AefRunDocuments documents)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(documents);
        return Verify(folder, documents.RunId, SealVerifier.RunHashOf(folder).Value, documents.ResultIds);
    }

    /// <summary>
    /// Verifies the chain ([OVL-5]): the seals from <c>seal-0001.json</c> to the highest-numbered one present, the events
    /// file's coverage, and each event.
    /// </summary>
    /// <param name="folder">The run folder.</param>
    /// <param name="runId">The run's <c>runId</c>, or null when run.json does not give one (then neither a batch's nor an event's run is checked).</param>
    /// <param name="runHash">The run's run hash ([SEAL-4]: the sealed value when the run has a valid seal).</param>
    /// <param name="resultIds">The run's result ids, or null when they are not all known (then an event's <c>target.result</c> is not checked).</param>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public static OverlayChain Verify(AefRunFolder folder, string? runId, string runHash, IReadOnlySet<string>? resultIds)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(runHash);
        var problems = new HashSet<AefProblem>();

        // The files under overlays/: the events file, batch seals and batch signatures; anything else is unexpected.
        var seals = new SortedDictionary<int, string>();
        foreach (var path in folder.Files.Where(p => p.StartsWith("overlays/", StringComparison.Ordinal) && p != EventsPath))
        {
            if (BatchSealName().Match(path) is { Success: true } m)
            {
                seals[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = path;
            }
            else if (!BatchSignatureName().IsMatch(path))
            {
                problems.Add(new AefProblem(path, "unexpected-file"));
            }
        }

        var events = ReadEvents(folder, problems, out var parsed);
        if (parsed is null)
        {
            // [OVL-5]: framing that breaks [ENC-5] or [ENC-7] (or a file beyond a limit) is reported once, and the chain
            // is not checked further: no batch verifies, no event takes part in the effective view.
            return new OverlayChain(AefProblemOrder.Sort(problems), [], 0, [.. LineRanges(events).Select(r => new OverlayEventLine(r.Number, r.Offset, r.End, null, false, null))]);
        }

        if (seals.ContainsKey(0))
        {
            problems.Add(new AefProblem(seals[0], "batch-number"));   // batches are 1-based; not checked further
        }

        var batches = new List<OverlayBatch>();
        var covered = new List<(long Start, long End)>();
        var prefixIntact = true;
        long verifiedEnd = 0;
        (long End, bool Known) previous = (0, true);   // where the previous batch ended; unknown after a missing or invalid seal
        var highest = seals.Keys.DefaultIfEmpty(0).Max();
        for (var k = 1; k <= highest; k++)
        {
            var path = SealPath(k);
            if (!seals.ContainsKey(k))
            {
                problems.Add(new AefProblem(path, "missing"));
                prefixIntact = false;
                previous = (0, false);
                continue;
            }

            if (ReadBatchSeal(folder, path, out var unreadCode) is not { } seal)
            {
                problems.Add(new AefProblem(path, unreadCode));
                batches.Add(new OverlayBatch(k, path, false, 0, 0, false));
                prefixIntact = false;
                previous = (0, false);
                continue;
            }

            var own = BatchProblems(folder, seal, k, runId, runHash, previous, events);
            var predicate = seal["predicate"]!;
            var offset = (long)AefNode.Number(predicate["offset"])!.Value;
            var length = (long)AefNode.Number(predicate["length"])!.Value;
            covered.Add((Math.Min(offset, events.Length), Math.Min(offset + length, events.Length)));
            problems.UnionWith(own.Select(code => new AefProblem(path, code)));

            prefixIntact &= own.Count == 0;
            if (prefixIntact)
            {
                verifiedEnd = offset + length;
            }

            batches.Add(new OverlayBatch(k, path, true, offset, length, prefixIntact));
            previous = (offset + length, true);
        }

        if (!Covers(covered, events.Length))
        {
            problems.Add(new AefProblem(EventsPath, "uncovered"));
        }

        var lines = CheckEvents(parsed, runId, runHash, resultIds, batches, problems);
        return new OverlayChain(AefProblemOrder.Sort(problems), batches, verifiedEnd, lines);
    }

    // The events file's bytes (none when it is absent) and its lines, read once; the lines are null when the file is not
    // read: its framing breaks [ENC-5] or [ENC-7], or it is beyond a limit (the problem is added).
    private static byte[] ReadEvents(AefRunFolder folder, HashSet<AefProblem> problems, out AefNdjsonFile? parsed)
    {
        parsed = null;
        if (!folder.Has(EventsPath))
        {
            parsed = AefNdjson.Read([]);
            return [];
        }

        byte[] bytes;
        try
        {
            bytes = folder.Read(EventsPath, AefLimits.MaxNdjsonBytes);   // [ENC-17]: 1 GiB
        }
        catch (AefLimitException)
        {
            problems.Add(new AefProblem(EventsPath, "limit"));
            return [];
        }

        var file = AefNdjson.Read(bytes);
        if (file.Problem is { } whole)
        {
            // Broken framing (encoding), or more lines than [ENC-17] allows (limit, at the file, [ENC-18]): the chain is
            // not checked further. A single line beyond a limit is a problem of that line (CheckEvents).
            problems.Add(new AefProblem(EventsPath, whole.Code));
        }
        else
        {
            parsed = file;
        }

        return bytes;
    }

    // A batch seal as an I-JSON document valid against the reader overlay-seal schema, or null with the code to report:
    // batch-invalid, or limit for one beyond the 40 MiB of [ENC-17] or nested deeper than 64, which is not read
    // ([ENC-18]). Either way it is not checked further, and the batches after it are not verified.
    private static JsonObject? ReadBatchSeal(AefRunFolder folder, string path, out string code)
    {
        code = "batch-invalid";
        try
        {
            var seal = AefJsonReader.ParseDocument(folder.Read(path, AefLimits.MaxSealBytes), AefLimits.MaxSealBytes);
            return AefSchemas.Reader.IsValid("overlay-seal", seal) ? seal : null;
        }
        catch (AefReadException e)
        {
            code = e.Code == "limit" ? "limit" : "batch-invalid";
            return null;
        }
    }

    // The problems of one valid batch seal about the batch itself ([OVL-5]).
    private static List<string> BatchProblems(
        AefRunFolder folder, JsonObject seal, int number, string? runId, string runHash, (long End, bool Known) previous, byte[] events)
    {
        var own = new List<string>();
        var predicate = seal["predicate"]!;
        if (AefNode.Number(predicate["batch"]) != number)
        {
            own.Add("batch-number");
        }

        if (runId is not null && !string.Equals(AefNode.String(predicate["runId"]), runId, StringComparison.Ordinal))
        {
            own.Add("run-id");
        }

        if (!string.Equals(AefNode.String(predicate["runHash"]), runHash, StringComparison.Ordinal))
        {
            own.Add("run-hash");
        }

        var offset = (long)AefNode.Number(predicate["offset"])!.Value;
        var end = offset + (long)AefNode.Number(predicate["length"])!.Value;

        // Batch 1 starts at the first byte; each later one where the previous ended (not checked after a missing or an
        // invalid seal, whose range is not known).
        if (previous.Known && offset != (number == 1 ? 0 : previous.End))
        {
            own.Add("offset");
        }

        var inside = end <= events.Length;
        var startsOnLine = offset == 0 || (offset <= events.Length && events[offset - 1] == (byte)'\n');
        var endsOnLine = inside && events[end - 1] == (byte)'\n';
        if (!inside || !startsOnLine || !endsOnLine)
        {
            own.Add("line-boundary");
        }

        var digest = AefNode.String(AefNode.At(AefNode.Items(seal["subject"]).FirstOrDefault(), "digest", "sha256"));
        if (!inside || !string.Equals(Sha256(events.AsSpan((int)offset, (int)(end - offset))), digest, StringComparison.Ordinal))
        {
            own.Add("batch-digest");
        }

        // Batch 1 names no previous seal; batch n names seal n-1 and the SHA-256 of its bytes, which must be present.
        var named = predicate["previous"];
        var before = SealPath(number - 1);
        var previousHolds = number == 1
            ? named is null
            : AefNode.String(AefNode.Get(named, "path")) == before && folder.Has(before)
              && string.Equals(AefNode.String(AefNode.Get(named, "sha256")), folder.Sha256(before), StringComparison.Ordinal);
        if (!previousHolds)
        {
            own.Add("previous");
        }

        return own;
    }

    // [OVL-5] event-invalid, event-id and target, per line; and which verified batch each line is in.
    private static List<OverlayEventLine> CheckEvents(
        AefNdjsonFile file, string? runId, string runHash, IReadOnlySet<string>? resultIds, List<OverlayBatch> batches, HashSet<AefProblem> problems)
    {
        var lines = new List<OverlayEventLine>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in file.Lines)
        {
            var (start, end) = ((long)line.Offset, (long)line.Offset + line.Length + 1);
            var where = $"{EventsPath}:{line.Number}";
            var batch = batches.FirstOrDefault(b => b.Verified && b.Offset <= start && end <= b.End)?.Number;
            if (line.Value is not { } value || !AefSchemas.Reader.IsValid("overlay-event", value))
            {
                // Not checked for event-id or target, and its id is not recorded. A line beyond the size or depth limit
                // is reported as limit at the line ([ENC-18]), and is as unusable as an invalid one; like event-invalid,
                // it concerns one event and does not end the verified prefix.
                problems.Add(new AefProblem(where, line.Problem is AefLimitException ? "limit" : "event-invalid"));
                lines.Add(new OverlayEventLine(line.Number, start, end, null, false, batch));
                continue;
            }

            var usable = true;
            if (!ids.Add(AefNode.String(value["eventId"]) ?? ""))
            {
                problems.Add(new AefProblem(where, "event-id"));
                usable = false;
            }

            // [OVL-2]: an overlay targets only its own run.
            var target = value["target"];
            var otherRun = runId is not null && !string.Equals(AefNode.String(AefNode.Get(target, "run")), runId, StringComparison.Ordinal);
            var otherHash = AefNode.Has(target, "runHash") && !string.Equals(AefNode.String(AefNode.Get(target, "runHash")), runHash, StringComparison.Ordinal);
            var noResult = AefNode.Has(target, "result") && resultIds is not null && !resultIds.Contains(AefNode.String(AefNode.Get(target, "result")) ?? "");
            if (otherRun || otherHash || noResult)
            {
                problems.Add(new AefProblem(where, "target"));
                usable = false;
            }

            lines.Add(new OverlayEventLine(line.Number, start, end, value, usable, batch));
        }

        return lines;
    }

    // Whether the ranges cover [0, size) (a claimed range counts as covered even when its bytes changed).
    private static bool Covers(IEnumerable<(long Start, long End)> ranges, long size)
    {
        long reached = 0;
        foreach (var (start, end) in ranges.OrderBy(r => r.Start))
        {
            if (start > reached)
            {
                return false;
            }

            reached = Math.Max(reached, end);
        }

        return reached >= size;
    }

    // Every LF-ended line of a file whose events are not read (for the count of unsealed events).
    private static IEnumerable<(int Number, long Offset, long End)> LineRanges(byte[] bytes)
    {
        var (number, start) = (0, 0);
        while (start < bytes.Length && Array.IndexOf(bytes, (byte)'\n', start) is var lf and >= 0)
        {
            yield return (++number, start, lf + 1L);
            start = lf + 1;
        }
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [GeneratedRegex("^overlays/seal-([0-9]{4})\\.json\\z", RegexOptions.CultureInvariant)]
    private static partial Regex BatchSealName();

    [GeneratedRegex("^overlays/seal-[0-9]{4}\\.dsse\\.json\\z", RegexOptions.CultureInvariant)]
    private static partial Regex BatchSignatureName();
}
