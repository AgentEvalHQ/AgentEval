// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Json;
using AgentEval.Results.Schemas;

namespace AgentEval.Results.Runs;

/// <summary>One line of an NDJSON file of a run: its 1-based number, the object it holds (null when it is not an I-JSON object), and whether the object is valid against its reader schema.</summary>
public sealed record AefRunLine(int Number, JsonObject? Value, bool Valid);

/// <summary>
/// One NDJSON file of a run as a verifier read it (§3.9, [ENC-5]–[ENC-7]): whether it is there, whether its lines were
/// read (a file whose framing breaks [ENC-5] or [ENC-7], or beyond a limit of [ENC-17], has none read), and its lines.
/// </summary>
public sealed class AefRunLines
{
    internal AefRunLines(string path, bool present, bool read, IReadOnlyList<AefRunLine> lines)
    {
        Path = path;
        Present = present;
        IsRead = read;
        Lines = lines;
    }

    /// <summary>The file's path in the run.</summary>
    public string Path { get; }

    /// <summary>The run holds the file.</summary>
    public bool Present { get; }

    /// <summary>The file's lines were read (it is present, framed as [ENC-5] and [ENC-7] require, and within the limits).</summary>
    public bool IsRead { get; }

    /// <summary>Every line, in file order, when <see cref="IsRead"/>; otherwise none.</summary>
    public IReadOnlyList<AefRunLine> Lines { get; }

    /// <summary>The lines that hold an I-JSON object (valid against the schema or not), with their numbers.</summary>
    public IEnumerable<(int Number, JsonObject Value)> Objects =>
        Lines.Where(l => l.Value is not null).Select(l => (l.Number, l.Value!));

    /// <summary>Every line was read as an object valid against its schema.</summary>
    public bool AllValid => IsRead && Lines.All(l => l.Valid);
}

/// <summary>
/// The documents of a run as a run verifier reads them (contracts/aef/1/spec/03-run.md, §3.9, first paragraph): each
/// JSON file whole and each NDJSON file line by line (§2.2), and the problems of reading them: <c>encoding</c> (§2.1,
/// §2.2), <c>limit</c> (§2.6, [ENC-18]: at the file, or at <c>&lt;file&gt;:&lt;line&gt;</c> for one line, the other lines
/// still read) and <c>schema</c> (the reader schema, a time that does not exist, or a file [RUN-2] requires that is
/// absent). A file that does not read is not checked further: a JSON file that is not an I-JSON object, or an NDJSON file
/// whose framing breaks [ENC-5] or [ENC-7] (reported once, at the file, and none of its lines read).
/// <c>seal.json</c>, <c>attestation.dsse.json</c> and <c>overlays/</c> are not read here: §4.1, §4.4 and §4.2 read them.
/// </summary>
public sealed class AefRunDocuments
{
    /// <summary>The JSON documents of [RUN-2] and their schemas.</summary>
    public static IReadOnlyList<(string Path, string Schema)> JsonFiles { get; } =
        [("run.json", "run"), ("metrics.json", "metrics"), ("summary.json", "summary")];

    /// <summary>
    /// The NDJSON files of [RUN-2] and the schema of their lines; the OTLP files have none of AEF's ([RUN-14]: OTLP/JSON
    /// <c>TracesData</c> and <c>LogsData</c> objects), so only their encoding is checked.
    /// </summary>
    public static IReadOnlyList<(string Path, string? Schema)> NdjsonFiles { get; } =
    [
        ("results.ndjson", "result"), ("evidence.ndjson", "evidence"), ("gates.ndjson", "gate-decision"),
        ("traces.otlp.jsonl", null), ("logs.otlp.jsonl", null),
    ];

    private readonly Dictionary<string, JsonObject> _documents;
    private readonly Dictionary<string, AefRunLines> _lines;

    private AefRunDocuments(Dictionary<string, JsonObject> documents, Dictionary<string, AefRunLines> lines, IReadOnlyList<AefProblem> problems)
    {
        _documents = documents;
        _lines = lines;
        Problems = problems;
    }

    /// <summary>run.json as read (an I-JSON object, valid against the schema or not), or null when it is absent or does not read.</summary>
    public JsonObject? Run => Document("run.json");

    /// <summary>metrics.json as read, or null.</summary>
    public JsonObject? Metrics => Document("metrics.json");

    /// <summary>summary.json as read, or null.</summary>
    public JsonObject? Summary => Document("summary.json");

    /// <summary>results.ndjson.</summary>
    public AefRunLines Results => _lines["results.ndjson"];

    /// <summary>evidence.ndjson.</summary>
    public AefRunLines Evidence => _lines["evidence.ndjson"];

    /// <summary>gates.ndjson.</summary>
    public AefRunLines Gates => _lines["gates.ndjson"];

    /// <summary>traces.otlp.jsonl.</summary>
    public AefRunLines Traces => _lines["traces.otlp.jsonl"];

    /// <summary>logs.otlp.jsonl.</summary>
    public AefRunLines Logs => _lines["logs.otlp.jsonl"];

    /// <summary>The problems of reading the run: <c>encoding</c>, <c>limit</c> and <c>schema</c>, in the order of §3.9.</summary>
    public IReadOnlyList<AefProblem> Problems { get; }

    /// <summary>
    /// Every file and line read and valid against its schema: the condition under which the rules of §3.9 from
    /// <c>result-id</c> down are checked.
    /// </summary>
    public bool AllRead => Problems.Count == 0;

    /// <summary>run.json's <c>runId</c>, when run.json reads and holds one as a string.</summary>
    public string? RunId => AefNode.String(Run?["runId"]);

    /// <summary>run.json's <c>status</c> as written, or null.</summary>
    public string? Status => AefNode.String(Run?["status"]);

    /// <summary>A closed run ([RUN-4]): run.json's <c>status</c> is <c>completed</c> or <c>aborted</c>.</summary>
    public bool IsClosed => Status is "completed" or "aborted";

    /// <summary>
    /// The ids of the run's results, when results.ndjson reads ([OVL-2]: it is present and is I-JSON within the limits,
    /// with no <c>encoding</c> or <c>limit</c> problem at the file or a line; a <c>schema</c> problem does not stop it).
    /// Null otherwise: a line that does not read could hold any id, so the checks that need them are not made.
    /// </summary>
    public IReadOnlySet<string>? ResultIds =>
        Results.IsRead && Results.Lines.All(l => l.Value is not null)
            ? Results.Objects.Select(o => AefNode.String(o.Value["resultId"])).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : null;

    /// <summary>Reads the documents of a run folder.</summary>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public static AefRunDocuments Read(AefRunFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var problems = new HashSet<AefProblem>();
        var documents = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var lines = new Dictionary<string, AefRunLines>(StringComparer.Ordinal);

        foreach (var (path, schema) in JsonFiles)
        {
            if (folder.Has(path) && ReadDocument(folder, path, schema, problems) is { } document)
            {
                documents[path] = document;
            }
        }

        foreach (var (path, schema) in NdjsonFiles)
        {
            lines[path] = folder.Has(path) ? ReadLines(folder, path, schema, problems) : new AefRunLines(path, false, false, []);
        }

        // [RUN-2]: the files a run always has, and summary.json in a closed run; their absence is a schema problem.
        var closed = AefNode.String(documents.GetValueOrDefault("run.json")?["status"]) is "completed" or "aborted";
        foreach (var required in new[] { "run.json", "results.ndjson", "metrics.json" }.Concat(closed ? ["summary.json"] : []))
        {
            if (!folder.Has(required))
            {
                problems.Add(new AefProblem(required, "schema"));
            }
        }

        // [ENC-17]: a blob is at most 1 GiB.
        foreach (var path in folder.Files)
        {
            if (AefRunFolder.IsBlobPath(path, out _) && folder.Size(path) > AefLimits.MaxBlobBytes)
            {
                problems.Add(new AefProblem(path, "limit"));
            }
        }

        return new AefRunDocuments(documents, lines, AefProblemOrder.Sort(problems));
    }

    /// <summary>
    /// Whether <paramref name="document"/> is valid against the reader schema <paramref name="schema"/>. The validator
    /// also refuses a time that does not exist inside <c>common#/$defs/timestamp</c> ([ENC-8]).
    /// </summary>
    public static bool IsValid(string schema, JsonObject document) => AefSchemas.Reader.IsValid(schema, document);

    private JsonObject? Document(string path) => _documents.GetValueOrDefault(path);

    private static JsonObject? ReadDocument(AefRunFolder folder, string path, string schema, HashSet<AefProblem> problems)
    {
        JsonObject document;
        try
        {
            var limit = AefLimits.MaxBytesOf(path);
            document = AefJsonReader.ParseDocument(folder.Read(path, limit), limit);
        }
        catch (AefReadException e)
        {
            problems.Add(new AefProblem(path, e.Code));
            return null;
        }

        if (!IsValid(schema, document))
        {
            problems.Add(new AefProblem(path, "schema"));
        }

        return document;
    }

    private static AefRunLines ReadLines(AefRunFolder folder, string path, string? schema, HashSet<AefProblem> problems)
    {
        AefNdjsonFile file;
        try
        {
            // [ENC-17]: an NDJSON file is at most 1 GiB; a larger one is refused, as limit at the file, and not read.
            file = AefNdjson.Read(folder.Read(path, AefLimits.MaxNdjsonBytes));
        }
        catch (AefReadException e)
        {
            problems.Add(new AefProblem(path, e.Code));
            return new AefRunLines(path, true, false, []);
        }

        if (file.Problem is { } whole)
        {
            // Framing ([ENC-5], [ENC-7]) or more lines than [ENC-17] allows: reported once at the file, and no line
            // is read ([ENC-18]: a part is never read as the whole).
            problems.Add(new AefProblem(path, whole.Code));
            return new AefRunLines(path, true, false, []);
        }

        var lines = new List<AefRunLine>(file.Lines.Count);
        foreach (var line in file.Lines)
        {
            if (line.Value is null)
            {
                // Not an I-JSON object (encoding), or beyond the size or depth limit (limit): reported at the line;
                // the file's other lines are still read ([ENC-18]).
                problems.Add(new AefProblem($"{path}:{line.Number}", line.Problem?.Code ?? "encoding"));
                lines.Add(new AefRunLine(line.Number, null, false));
                continue;
            }

            var valid = schema is null || IsValid(schema, line.Value);
            if (!valid)
            {
                problems.Add(new AefProblem($"{path}:{line.Number}", "schema"));
            }

            lines.Add(new AefRunLine(line.Number, line.Value, valid));
        }

        return new AefRunLines(path, true, true, lines);
    }
}
