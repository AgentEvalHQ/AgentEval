using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentEval.Results.Json;

namespace AgentEval.Results.Tests.Corpus;

/// <summary>
/// Every JSON document and NDJSON line of the conformance corpus that is I-JSON, with the schema it is checked against
/// (found from its file name: <c>run.json</c> is a <c>run</c>, a line of <c>results.ndjson</c> a <c>result</c>, a vector's
/// <c>document.json</c> what its <c>expected.json</c> names, a decision vector's <c>input</c> and <c>expected</c> a
/// decision input and output). Bytes that are not I-JSON (the encoding vectors) are left out: no schema sees them.
/// </summary>
internal static partial class CorpusDocuments
{
    public static IReadOnlyList<(string Id, string Schema, JsonNode Document)> All { get; } = Find();

    private static List<(string Id, string Schema, JsonNode Document)> Find()
    {
        var found = new List<(string, string, JsonNode)>();
        foreach (var file in Directory.GetFiles(AefCorpus.Conformance, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(AefCorpus.Conformance, file).Replace('\\', '/');
            var name = Path.GetFileName(rel);
            if (rel.StartsWith("decision-vectors/", StringComparison.Ordinal))
            {
                if (Document(file) is { } vector)
                {
                    found.Add(($"{rel}#input", "decision#/$defs/input", vector["input"]!));
                    if (vector["expected"] is { } output) found.Add(($"{rel}#expected", "decision", output));
                }

                continue;
            }

            var schema = name switch
            {
                "run.json" => "run",
                "metrics.json" => "metrics",
                "summary.json" => "summary",
                "seal.json" => "seal",
                "results.ndjson" => "result",
                "evidence.ndjson" => "evidence",
                "gates.ndjson" => "gate-decision",
                "events.ndjson" => rel.Contains("/overlays/", StringComparison.Ordinal) ? "overlay-event" : "runner-event",
                "checkpoint.json" => "checkpoint",
                "runner.json" when rel.StartsWith("protocol/", StringComparison.Ordinal) => "runner",
                _ when name.StartsWith("document.", StringComparison.Ordinal) => VectorSchema(file, name),
                _ when rel.StartsWith("protocol/", StringComparison.Ordinal) && PlanFile().IsMatch(name) => "run-plan",
                _ when OverlaySeal().IsMatch(rel) => "overlay-seal",
                _ => null,
            };
            if (schema is null)
            {
                continue;
            }

            if (name.EndsWith(".ndjson", StringComparison.Ordinal))
            {
                var lines = AefNdjson.Read(File.ReadAllBytes(file));
                found.AddRange(lines.Lines.Where(l => l.Value is not null).Select(l => ($"{rel}:{l.Number}", schema, (JsonNode)l.Value!)));
            }
            else if (Document(file) is { } document)
            {
                found.Add((rel, schema, document));
            }
        }

        return found;
    }

    // A document vector's document (document.json, or the file its expected.json names) and the schema it names.
    private static string? VectorSchema(string file, string name) =>
        Document(Path.Combine(Path.GetDirectoryName(file)!, "expected.json")) is { } expected
        && ((string?)expected["document"] ?? "document.json") == name
            ? (string?)expected["schema"]
            : null;

    private static JsonObject? Document(string file)
    {
        try
        {
            return File.Exists(file) ? AefJsonReader.ParseDocument(File.ReadAllBytes(file)) : null;
        }
        catch (AefReadException)
        {
            return null;
        }
    }

    [GeneratedRegex("^plan(-[a-z0-9-]+)?\\.json\\z")]
    private static partial Regex PlanFile();

    [GeneratedRegex("/overlays/seal-[0-9]{4}\\.json\\z")]
    private static partial Regex OverlaySeal();
}
