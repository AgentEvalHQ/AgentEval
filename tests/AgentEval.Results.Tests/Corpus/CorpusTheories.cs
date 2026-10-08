using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Conformance;

namespace AgentEval.Results.Tests.Corpus;

/// <summary>
/// The conformance corpus, run in-process through the aef-dotnet driver (<see cref="Program.Dispatch"/>), one theory
/// per kind of vector this component covers: fast feedback while the code changes. A conformance claim rests on the
/// Python runner driving the published driver (run-corpus.sh), not on these.
/// </summary>
public class CorpusTheories
{
    private static readonly JsonArray Index =
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(AefCorpus.Conformance, "index.json")))!["vectors"]!.AsArray();

    public static TheoryData<string> Vectors(string kind) =>
        new(Index.Where(v => (string?)v!["kind"] == kind).Select(v => (string)v!["id"]!));

    public static TheoryData<int> Items(string file) =>
        new(Enumerable.Range(0, JsonNode.Parse(File.ReadAllBytes(Path.Combine(AefCorpus.Conformance, file)))!.AsArray().Count));

    [Theory]
    [MemberData(nameof(Vectors), "decision")]
    public void Decision(string id)
    {
        var file = Folder(id);
        var vector = JsonNode.Parse(File.ReadAllBytes(file))!;
        var result = Run("decide", file);

        if (vector["expectedError"] is { } refusal)
        {
            Assert.True(result["error"] is not null, $"decided what it must refuse ({refusal}): {result.ToJsonString()}");
        }
        else
        {
            Assert.True(JsonNode.DeepEquals(vector["expected"], result["output"]),
                $"expected {vector["expected"]!.ToJsonString()}, got {result.ToJsonString()}");
        }

        if (vector["schemaInvalid"] is not null || vector["readerOnly"] is not null)
        {
            var input = Path.Combine(Path.GetTempPath(), $"aef-decision-input-{Guid.NewGuid():N}.json");
            File.WriteAllBytes(input, Encoding.UTF8.GetBytes(vector["input"]!.ToJsonString()));
            try
            {
                var verdicts = Run("document", "decision#/$defs/input", input);
                if (vector["readerOnly"] is not null)
                {
                    Assert.Equal("invalid", (string?)verdicts["writer"]);
                    Assert.Equal("valid", (string?)verdicts["reader"]);
                }
                else
                {
                    Assert.Equal("invalid", (string?)verdicts["reader"]);
                }
            }
            finally
            {
                File.Delete(input);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Vectors), "document")]
    [MemberData(nameof(Vectors), "reader-only")]
    [MemberData(nameof(Vectors), "plan")]
    public void Document(string id)
    {
        var folder = Folder(id);
        var expected = Expected(folder);
        var result = Run("document", (string)expected["schema"]!, Path.Combine(folder, (string?)expected["document"] ?? "document.json"));

        Assert.Equal((string?)expected["writer"], (string?)result["writer"]);
        Assert.Equal((string?)expected["reader"], (string?)result["reader"]);
        foreach (var (field, value) in expected["reads"]?.AsObject() ?? new JsonObject())
        {
            Assert.True(result["reads"]!.AsObject().TryGetPropertyValue(field, out var read), $"{field} was not read");
            Assert.True(JsonNode.DeepEquals(value, read), $"{field}: expected {value?.ToJsonString()}, got {read?.ToJsonString()}");
        }
    }

    [Theory]
    [MemberData(nameof(Vectors), "matching")]
    public void Matching(string id)
    {
        var folder = Folder(id);
        var result = Run("match", Path.Combine(folder, "plan.json"), Path.Combine(folder, "runner.json"));

        Assert.Equal((bool)Expected(folder)["matches"]!, (bool)result["matches"]!);
    }

    [Theory]
    [MemberData(nameof(Vectors), "stream")]
    public void Stream(string id)
    {
        var folder = Folder(id);
        var expected = Expected(folder);
        var result = Run("stream", Path.Combine(folder, "events.ndjson"), Path.Combine(folder, (string)expected["plan"]!));

        Assert.Equal(
            expected["problems"]!.AsArray().Select(p => $"{(string)p!["where"]!} {(string)p["problem"]!}"),
            result["problems"]!.AsArray().Select(p => $"{(string)p![0]!} {(string)p[1]!}"));
    }

    [Theory]
    [MemberData(nameof(Items), "result-ids.json")]
    public void ResultId(int item)
    {
        var v = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AefCorpus.Conformance, "result-ids.json")))![item]!;
        string[] args = ["result-id", (string)v["runId"]!, (string)v["caseId"]!, (string)v["path"]!];
        if (v["trial"] is { } trial)
        {
            args = [.. args, trial.ToJsonString()];   // as written: 3.0 is trial 3
        }

        Assert.Equal((string)v["resultId"]!, (string)Run(args)["resultId"]!);
    }

    [Theory]
    [MemberData(nameof(Items), "paths.json")]
    public void Paths(int item)
    {
        var v = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AefCorpus.Conformance, "paths.json")))![item]!;
        var list = Path.Combine(Path.GetTempPath(), $"aef-paths-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(list, Encoding.UTF8.GetBytes(v["paths"]!.ToJsonString()));
        try
        {
            var result = Run("paths", list);

            Assert.True(JsonNode.DeepEquals(v["problems"], result["problems"]),
                $"{v["name"]}: expected {v["problems"]!.ToJsonString()}, got {result["problems"]!.ToJsonString()}");
        }
        finally
        {
            File.Delete(list);
        }
    }

    [Fact]
    public void EveryKindThisComponentCovers_HasVectors()
    {
        foreach (var kind in new[] { "decision", "document", "reader-only", "plan", "matching", "stream", "result-id", "paths" })
        {
            Assert.Contains(Index, v => (string?)v!["kind"] == kind);
        }
    }

    // A vector's folder (or file, for a decision vector), from index.json.
    private static string Folder(string id) =>
        Path.Combine(AefCorpus.Conformance, (string)Index.Single(v => (string?)v!["id"] == id)!["path"]!);

    private static JsonNode Expected(string folder) => JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, "expected.json")))!;

    private static JsonNode Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Program.Dispatch(args, stdout, stderr);

        Assert.True(code == 0, $"exit {code}: {stderr}");
        Assert.EndsWith("\n", stdout.ToString(), StringComparison.Ordinal);
        return JsonNode.Parse(stdout.ToString())!;
    }
}
