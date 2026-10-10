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

        // The files under overlays/: the events file, batch seals and batch signatures, each a regular file named as one;
        // anything else is unexpected-file, whatever its name ([OVL-5]: [RUN-3] does not apply there), a link, pipe,
        // socket or device included (never followed or read: a link named seal-0001.json is no batch seal). More files
        // than one events file and two per batch ([ENC-17]: 19,999) is limit at overlays, reported once ([ENC-18]): the
        // other files are then not reported one by one, and the chain is checked as usual from the files it names, so no
        // number of other files voids a batch (round 5).
        if (folder.OverlaysOverLimit)
        {
            problems.Add(new AefProblem(OverlaysPath, "limit"));
        }

        var seals = new SortedDictionary<int, string>();
        var unexpected = new List<string>(folder.OverlayIrregular);
        foreach (var path in folder.Files.Where(p => AefFolder.IsUnderOverlays(p) && p != EventsPath))
        {
            if (BatchSealName().Match(path) is { Success: true } m)
            {
                seals[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = path;
            }
            else if (!BatchSignatureName().IsMatch(path))
            {
                unexpected.Add(path);
            }
        }

        if (!folder.OverlaysOverLimit)
        {
            problems.UnionWith(unexpected.Select(p => new AefProblem(p, "unexpected-file")));
        }

        // [OVL-5]: the events file is read as far as [ENC-17] allows; one that holds more is limit at the file, once.
        var events = EventsFile.Open(folder);
        if (events.OverLimit)
        {
            problems.Add(new AefProblem(EventsPath, "limit"));
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

            var predicate = seal["predicate"]!;
            var offset = (long)AefNode.Number(predicate["offset"])!.Value;
            var length = (long)AefNode.Number(predicate["length"])!.Value;
            if (events.OverLimit && offset + length > events.Read)
            {
                // [OVL-5]: of an events file holding more than [ENC-17] lets a reader read, a batch whose range ends beyond
                // what was read is limit at its seal, which ends the verified prefix. The table of [OVL-5] defines limit
                // at a seal as a seal refused: like batch-invalid, it is not checked further, it claims no bytes (for
                // uncovered), and the next batch's offset is not checked (R4N-2).
                problems.Add(new AefProblem(path, "limit"));
                batches.Add(new OverlayBatch(k, path, true, offset, length, false));
                prefixIntact = false;
                previous = (0, false);
                continue;
            }

            var own = BatchProblems(folder, seal, k, runId, runHash, previous, events);
            covered.Add((Math.Min(offset, events.Size), Math.Min(offset + length, events.Size)));
            problems.UnionWith(own.Select(code => new AefProblem(path, code)));

            prefixIntact &= own.Count == 0;
            if (prefixIntact)
            {
                verifiedEnd = offset + length;
            }

            batches.Add(new OverlayBatch(k, path, true, offset, length, prefixIntact));
            previous = (offset + length, true);
        }

        // Bytes claimed by no batch, the file's whole size counted: an unfinished last line too, and what lies beyond what
        // a reader reads of a file over its limits.
        if (!Covers(covered, events.Size))
        {
            problems.Add(new AefProblem(EventsPath, "uncovered"));
        }

        // [OVL-5]: the events file is judged line by line, inside the batches and after them: a blank line, a CR or a
        // leading byte-order mark is a problem of its line alone, and never changes which batches verify (R4N-9).
        var lines = CheckEvents(events, verifiedEnd, runId, runHash, resultIds, batches, problems);
        return new OverlayChain(AefFolder.PerEntry(AefProblemOrder.Sort(problems), folder.OverlayIrregular), batches, verifiedEnd, lines);
    }

    /// <summary>The path of the overlays folder, where too many files under it are reported, once ([ENC-18], [OVL-5]).</summary>
    public const string OverlaysPath = "overlays";

    /// <summary>
    /// The events file as [OVL-5] reads it: its bytes (all of them when it is within 1 GiB; of a larger file, only the
    /// complete lines a reader reads); its size as listed; how far a reader reads it (<see cref="Read"/>: up to its last
    /// LF within the first 1 GiB and the first 1,000,000 lines, [ENC-17]); and whether it holds more than that
    /// (<see cref="OverLimit"/>). Only the bytes as listed are read: what a concurrent writer appends afterwards is not
    /// seen.
    /// </summary>
    private sealed record EventsFile(byte[] Bytes, long Size, int Read, bool OverLimit)
    {
        public static EventsFile Open(AefRunFolder folder)
        {
            if (!folder.Has(EventsPath))
            {
                return new EventsFile([], 0, 0, false);
            }

            var size = folder.Size(EventsPath);
            if (size > AefLimits.MaxNdjsonBytes)
            {
                // Beyond 1 GiB, nothing past what a reader reads is needed: a batch ending there is limit, unread.
                var end = (int)folder.CompleteLinesEnd(EventsPath, AefLimits.MaxNdjsonBytes, AefLimits.MaxLines);
                return new EventsFile(folder.ReadPrefix(EventsPath, end), size, end, true);
            }

            var bytes = folder.ReadPrefix(EventsPath, (int)size);
            var span = bytes.AsSpan();
            var lfs = span.Count((byte)'\n');
            int read;
            if (lfs > AefLimits.MaxLines)
            {
                read = 0;   // after the 1,000,000th LF
                for (var line = 0; line < AefLimits.MaxLines; line++)
                {
                    read += span[read..].IndexOf((byte)'\n') + 1;
                }
            }
            else
            {
                read = span.LastIndexOf((byte)'\n') + 1;
            }

            return new EventsFile(bytes, size, read, lfs > AefLimits.MaxLines);
        }
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
        AefRunFolder folder, JsonObject seal, int number, string? runId, string runHash, (long End, bool Known) previous, EventsFile file)
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

        // Within the limits the whole file is read (a range past its end is both line-boundary and batch-digest); over
        // them, a range here ends within what was read (one beyond it was refused before this).
        var events = file.Bytes;
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

    // [OVL-5] event-invalid, event-id and target, per line; and which verified batch each line is in. Every line a reader
    // reads is judged on its own, inside the verified batches and after them: a blank line, a line holding a CR, or one
    // that begins with a byte-order mark is event-invalid (U+FEFF inside a string is content), and its batch still
    // verifies; a last line without LF is still being written and is neither shown nor reported. Of a file holding more
    // than [ENC-17] lets a reader read, no line after the verified batches is read.
    private static List<OverlayEventLine> CheckEvents(
        EventsFile file, long verifiedEnd, string? runId, string runHash, IReadOnlySet<string>? resultIds, List<OverlayBatch> batches, HashSet<AefProblem> problems)
    {
        var lines = new List<OverlayEventLine>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var verified = batches.Where(b => b.Verified).ToList();   // contiguous from offset 0, in order
        var next = 0;
        foreach (var (number, start, end) in LineRanges(file.Bytes, file.OverLimit ? (int)verifiedEnd : file.Read))
        {
            var where = $"{EventsPath}:{number}";
            while (next < verified.Count && verified[next].End < end)
            {
                next++;
            }

            var batch = next < verified.Count && verified[next].Offset <= start ? verified[next].Number : (int?)null;
            var content = file.Bytes.AsSpan((int)start, (int)(end - start - 1));   // without its LF
            JsonObject? value = null;
            AefReadException? problem = null;
            try
            {
                value = AefJsonReader.ParseDocument(content);
            }
            catch (AefReadException e)
            {
                problem = e;
            }

            // A JSON reader takes a CR between tokens for whitespace; a blank line, and a line that begins with a byte-order
            // mark, it refuses itself ([ENC-1]); U+FEFF inside a string it reads as text.
            if (value is null || content.Contains((byte)'\r') || !AefSchemas.Reader.IsValid("overlay-event", value))
            {
                // Not checked for event-id or target, and its id is not recorded. A line beyond the size or depth limit
                // is reported as limit at the line ([ENC-18], whatever else is wrong with it), and is as unusable as an
                // invalid one; like event-invalid, it concerns one event and does not end the verified prefix.
                problems.Add(new AefProblem(where, problem is AefLimitException ? "limit" : "event-invalid"));
                lines.Add(new OverlayEventLine(number, start, end, null, false, batch));
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

            lines.Add(new OverlayEventLine(number, start, end, value, usable, batch));
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

    // Every LF-ended line of the first `length` bytes (which end in an LF, or are none): its number, where it starts and
    // where it ends, its LF included.
    private static IEnumerable<(int Number, long Offset, long End)> LineRanges(byte[] bytes, int length)
    {
        var (number, start) = (0, 0);
        while (start < length && Array.IndexOf(bytes, (byte)'\n', start, length - start) is var lf and >= 0)
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
