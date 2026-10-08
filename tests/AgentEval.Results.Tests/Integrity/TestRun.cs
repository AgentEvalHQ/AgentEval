using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Tests.Integrity;

/// <summary>
/// A small, valid, closed run in a temporary folder, built from documents a test changes before (or after) writing them:
/// four result lines (a composite <c>k1/q</c> with two children, and <c>k2/q</c>), one evidence record citing a blob, one
/// gate decision, a metric <c>m</c>, and a summary entry for <c>main</c>/<c>m</c>/<c>q</c>. <see cref="Seal"/> seals it
/// and <see cref="AppendBatch"/> appends an overlay batch, signed or not.
/// </summary>
internal sealed class TestRun : IDisposable
{
    public const string RunId = "run-t";
    public const string Alice = "git:alice@example.com";
    public const string Bob = "git:bob@example.com";

    public static readonly byte[] Reasoning = Encoding.UTF8.GetBytes("The answer misses the refund policy: ünïcode, and a CRLF\r\n");
    public static readonly string ReasoningSha = Hex(SHA256.HashData(Reasoning));
    public static readonly string ReasoningPath = AefRunFolder.BlobPath(ReasoningSha);

    public TestRun()
    {
        Directory.CreateDirectory(Dir);
        Run = Obj($$"""
            {"schemaVersion": "1.0", "runId": "{{RunId}}", "status": "completed",
             "producer": {"name": "test", "version": "1.0"}, "subject": {"ref": "agent:t", "kind": "agent", "version": "1"},
             "startedAt": "2026-10-01T10:00:00Z", "endedAt": "2026-10-01T10:05:00Z", "execution": {"targetMode": "live"} }
            """);
        Metrics = Obj("""
            {"schemaVersion": "1.0", "metrics": [
              {"id": "m", "kind": "score", "direction": "higher_better", "scale": {"min": 0, "max": 1}},
              {"id": "pass", "kind": "rate", "direction": "higher_better", "scale": {"min": 0, "max": 1}}]}
            """);
        Results =
        [
            Obj($$"""
                {"schemaVersion": "1.0", "resultId": "{{R1}}", "caseId": "k1", "path": "q", "evaluator": {"id": "composite:q"},
                 "state": "failed", "severity": "medium", "scores": [{"metric": "m", "value": 0.4}],
                 "aggregation": {"strategy": "Min", "threshold": 0.5, "score": 0.4, "rulePath": "threshold", "measured": 2, "total": 2,
                                 "unmeasured": {"not_measured": 0, "not_applicable": 0, "skipped": 0, "error": 0}, "decisive": ["{{R3}}"]},
                 "evidence": ["E-1"]}
                """),
            Obj($$"""
                {"schemaVersion": "1.0", "resultId": "{{R2}}", "parentResultId": "{{R1}}", "caseId": "k1", "path": "q/a",
                 "evaluator": {"id": "code:a"}, "state": "passed", "scores": [{"metric": "m", "value": 1.0}], "component": {"weight": 1, "required": true} }
                """),
            Obj($$"""
                {"schemaVersion": "1.0", "resultId": "{{R3}}", "parentResultId": "{{R1}}", "caseId": "k1", "path": "q/b",
                 "evaluator": {"id": "llm:b"}, "state": "failed", "severity": "medium", "scores": [{"metric": "m", "value": 0.4}],
                 "component": {"weight": 1, "required": true}, "reasoning": {"blob": "sha256:{{ReasoningSha}}", "bytes": {{Reasoning.Length}} } }
                """),
            Obj($$"""
                {"schemaVersion": "1.0", "resultId": "{{R4}}", "caseId": "k2", "path": "q", "evaluator": {"id": "composite:q"},
                 "state": "passed", "scores": [{"metric": "m", "value": 0.9}]}
                """),
        ];
        Evidence =
        [
            Obj($$"""
                {"schemaVersion": "1.0", "evidenceId": "E-1", "kind": "judge_reasoning", "digest": "sha256:{{ReasoningSha}}",
                 "link": {"blob": "sha256:{{ReasoningSha}}"} }
                """),
        ];
        Gates =
        [
            Obj($$"""
                {"schemaVersion": "1.0", "gateId": "gate:ci", "decisionId": "d-1", "rule": {"strategy": "fail-on"},
                 "inputs": {"results": ["{{R1}}", "{{R4}}"]}, "comparability": "not_applicable", "outcome": "no_ship",
                 "decisive": ["{{R1}}"], "decidedAt": "2026-10-01T10:05:00Z"}
                """),
        ];
        Summary = Obj($$"""
            {"schemaVersion": "1.0", "runId": "{{RunId}}", "lanes": [{"lane": "main", "metrics": [
              {"metric": "m", "path": "q", "N": 2, "n": 2, "notMeasured": 0, "value": 0.65, "verdict": "failed", "sum": 1.3}]}]}
            """);
        Files[ReasoningPath] = Reasoning;
    }

    public static string R1 => Id("k1", "q");

    public static string R2 => Id("k1", "q/a");

    public static string R3 => Id("k1", "q/b");

    public static string R4 => Id("k2", "q");

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"aef-run-{Guid.NewGuid():N}");

    public JsonObject Run { get; set; }

    public JsonObject Metrics { get; set; }

    public JsonObject? Summary { get; set; }

    public List<JsonObject> Results { get; }

    public List<JsonObject> Evidence { get; }

    public List<JsonObject> Gates { get; }

    /// <summary>Other files by path (blobs, traces, ext/…), written as they are.</summary>
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public static string Id(string caseId, string path, long? trial = null) => AefResultId.Compute(RunId, caseId, path, trial);

    public static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>A trust policy listing <paramref name="signer"/>'s key for <paramref name="identity"/>, allowed to redact or not ([SIG-4]).</summary>
    public static TrustPolicy Policy(IAefSigner signer, string identity = Alice, bool redact = false) =>
        new([new TrustedKey(identity, signer.PublicKey, redact ? new[] { TrustPolicy.Redact } : null)]);

    /// <summary>Writes the documents and files.</summary>
    public TestRun Write()
    {
        WriteBytes("run.json", AefJsonWriter.Document(Run));
        WriteBytes("metrics.json", AefJsonWriter.Document(Metrics));
        if (Summary is not null)
        {
            WriteBytes("summary.json", AefJsonWriter.Document(Summary));
        }

        WriteLines("results.ndjson", Results);
        if (Evidence.Count > 0)
        {
            WriteLines("evidence.ndjson", Evidence);
        }

        if (Gates.Count > 0)
        {
            WriteLines("gates.ndjson", Gates);
        }

        foreach (var (path, bytes) in Files)
        {
            WriteBytes(path, bytes);
        }

        return this;
    }

    /// <summary>Seals the files present now: a seal.json valid against the schemas, which <paramref name="edit"/> may change.</summary>
    public TestRun Seal(Action<JsonObject>? edit = null)
    {
        var manifest = AefRunFolder.Open(Dir).Manifest();
        var subjects = new JsonArray();
        foreach (var entry in manifest.Entries)
        {
            subjects.Add(new JsonObject { ["name"] = entry.Path, ["digest"] = new JsonObject { ["sha256"] = entry.Sha256 } });
        }

        var seal = new JsonObject
        {
            ["_type"] = "https://in-toto.io/Statement/v1",
            ["subject"] = subjects,
            ["predicateType"] = "https://agenteval.dev/aef/1/evidence",
            ["predicate"] = new JsonObject
            {
                ["schemaVersion"] = "1.0",
                ["runId"] = Run["runId"]?.DeepClone(),
                ["runHash"] = manifest.RunHash,
                ["producer"] = Run["producer"]!.DeepClone(),
                ["subject"] = new JsonObject { ["ref"] = Run["subject"]!["ref"]!.DeepClone(), ["version"] = Run["subject"]!["version"]?.DeepClone() },
                ["deployment"] = null,
                ["suite"] = null,
                ["judges"] = new JsonArray(),
                ["closedAt"] = Run["endedAt"]?.DeepClone() ?? "2026-10-01T10:05:00Z",
                ["sealedBy"] = "producer",
                ["sealedAt"] = "2026-10-01T10:06:00Z",
            },
        };
        edit?.Invoke(seal);
        WriteBytes(SealVerifier.SealPath, AefJsonWriter.Document(seal, AefLimits.MaxSealBytes));
        return this;
    }

    /// <summary>Signs seal.json into attestation.dsse.json.</summary>
    public TestRun Attest(IAefSigner signer)
    {
        WriteBytes(SealVerifier.AttestationPath, DsseEnvelope.Create(Dsse.InTotoPayloadType, File.ReadAllBytes(Full(SealVerifier.SealPath)), signer).ToJson());
        return this;
    }

    /// <summary>
    /// Appends a batch of events and its seal (offset, length, digest, previous, the run's run hash), signed by
    /// <paramref name="signer"/> when one is given; <paramref name="edit"/> may change the seal's predicate. Returns the batch number.
    /// </summary>
    public int AppendBatch(IEnumerable<JsonObject> events, IAefSigner? signer = null, Action<JsonObject>? edit = null)
    {
        var path = Full(OverlayChain.EventsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var before = File.Exists(path) ? File.ReadAllBytes(path) : [];
        var added = events.SelectMany(e => AefJsonWriter.Line(e)).ToArray();
        File.WriteAllBytes(path, [.. before, .. added]);

        var number = 1;   // the next number after the batch seals present
        while (File.Exists(Full(OverlayChain.SealPath(number))))
        {
            number++;
        }

        var previous = number == 1 ? null : new JsonObject
        {
            ["path"] = OverlayChain.SealPath(number - 1),
            ["sha256"] = Hex(SHA256.HashData(File.ReadAllBytes(Full(OverlayChain.SealPath(number - 1))))),
        };
        var predicate = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["runId"] = RunId,
            ["runHash"] = SealVerifier.RunHashOf(AefRunFolder.Open(Dir)).Value,
            ["batch"] = number,
            ["offset"] = before.Length,
            ["length"] = added.Length,
            ["previous"] = previous,
        };
        edit?.Invoke(predicate);
        var seal = new JsonObject
        {
            ["_type"] = "https://in-toto.io/Statement/v1",
            ["subject"] = new JsonArray(new JsonObject { ["name"] = OverlayChain.EventsPath, ["digest"] = new JsonObject { ["sha256"] = Hex(SHA256.HashData(added)) } }),
            ["predicateType"] = "https://agenteval.dev/aef/1/overlay-batch",
            ["predicate"] = predicate,
        };
        var sealBytes = AefJsonWriter.Document(seal, AefLimits.MaxSealBytes);
        WriteBytes(OverlayChain.SealPath(number), sealBytes);
        if (signer is not null)
        {
            WriteBytes(OverlayChain.SignaturePath(number), DsseEnvelope.Create(Dsse.InTotoPayloadType, sealBytes, signer).ToJson());
        }

        return number;
    }

    /// <summary>An overlay event by <paramref name="by"/> (self-attested), at 2026-10-02 plus <paramref name="day"/> days.</summary>
    public static JsonObject Event(string id, string kind, JsonObject target, string by = Alice, int day = 0, Action<JsonObject>? edit = null)
    {
        target["run"] ??= RunId;
        var e = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["eventId"] = id,
            ["kind"] = kind,
            ["target"] = target,
            ["by"] = new JsonObject { ["identity"] = by, ["assurance"] = "self-attested" },
            ["at"] = $"2026-10-{2 + day:D2}T10:00:00Z",
        };
        edit?.Invoke(e);
        return e;
    }

    public void WriteBytes(string path, byte[] bytes)
    {
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    public void WriteText(string path, string text) => WriteBytes(path, Encoding.UTF8.GetBytes(text));

    public void Delete(string path) => File.Delete(Full(path));

    public byte[] ReadBytes(string path) => File.ReadAllBytes(Full(path));

    public AefRunVerification Verify(TrustPolicy? policy = null, IReadOnlyCollection<string>? anchors = null) =>
        AefRunVerifier.Verify(Dir, new AefVerifyOptions { Policy = policy, Anchors = anchors });

    /// <summary>The run verifier's problems as "path code" strings, in order.</summary>
    public IReadOnlyList<string> Problems(TrustPolicy? policy = null) => [.. Verify(policy).Problems.Select(p => $"{p.Path} {p.Code}")];

    public OverlayChain Chain()
    {
        var folder = AefRunFolder.Open(Dir);
        return OverlayChain.Verify(folder, AefRunDocuments.Read(folder));
    }

    public IReadOnlyList<string> ChainProblems() => [.. Chain().Problems.Select(p => $"{p.Path} {p.Code}")];

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Full(string path) => Path.Combine(Dir, path.Replace('/', Path.DirectorySeparatorChar));

    private void WriteLines(string path, IEnumerable<JsonObject> lines) => WriteBytes(path, [.. lines.SelectMany(l => AefJsonWriter.Line(l))]);
}
