using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Indexing;
using AgentEval.Results;
using AgentEval.Results.Requirements;
using AgentEval.Results.Signatures;
using AgentEval.Results.Suites;
using AgentEval.Results.Writing;
using AgentEval.Results.Observations;
using System.Text;

const string TestTraceId = "0123456789abcdef0123456789abcdef"; // DevSkim: ignore DS173237 - synthetic OTLP trace identity, not a credential

// Offline fixture: all values and reviewer identities are test data, with no provider calls.
// Require a new directory so acceptance cannot accidentally reuse old index/evidence state.
var workspace = Path.GetFullPath(args.Single());
if (Directory.Exists(workspace)) throw new IOException("Fixture directory must not already exist.");
Directory.CreateDirectory(workspace);
var evidence = Path.Combine(workspace, "evidence");
var directory = Path.Combine(evidence, "offline-run");
var started = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
var run = AefRunWriter.Create(directory, new AefRunHeader
{
    RunId = "offline-run", Producer = new() { Name = "offline-browser-fixture", Version = "1" },
    Subject = new() { Ref = "agent:offline", Kind = AefSubjectKind.Agent, Version = "1" },
    StartedAt = started, ContentCapture = AefContentCapture.Off,
    // Synthetic live-target declaration exercises requirement eligibility; no agent is invoked.
    Execution = new() { TargetMode = AefTargetMode.Live, Stimulus = AefStimulus.Generated }
});
run.SetMetrics([new() { Id = "quality", Kind = AefMetricKind.Score,
    Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1) }]);
run.AddResult(new() { CaseId = "case-a", Path = "quality", Evaluator = new("code:fixture"),
    State = AefState.Passed, Scores = [new() { Metric = "quality", Value = 0.9 }] });
run.SetSummary(new() { Lanes = [new AefSummaryLane("main", [new() { Metric = "quality", Path = "quality" }])] });
run.Close(AefRunStatus.Completed, started.AddSeconds(1));
AefSealer.Seal(directory);

var database = Path.Combine(workspace, "index.sqlite");
var options = new EvidenceIndexOptions { DatabasePath = database };
using var index = new SqliteEvidenceIndexWriter(options, new SqliteEvidenceIndexSchema());
index.BindRoots([new("offline", evidence, IndexedEvidenceKind.Aef)]);
var reader = new AefIndexSourceReader(new SuiteIdentityReader());
var sourceId = index.Upsert(reader.Read("offline", "offline-run", directory)).SourceId;
var resultId = new SqliteEvidenceDetailReader(options).ReadResults(new(sourceId)).Items[0].ResultId;
using var signer = EcdsaP256Signer.Generate();
var overlay = AefOverlayWriter.Open(directory);
overlay.Append(new AefOverlayEvent
{
    Kind = AefOverlayKind.Override, Target = new() { Result = resultId },
    By = new("reviewer:offline", AefAssurance.Signed), At = AefTime.Parse("2026-10-01T10:15:00.123456789Z"),
    State = AefState.Warn, Reason = "offline reviewed result"
});
overlay.Append(new AefOverlayEvent
{
    Kind = AefOverlayKind.Waive, Target = new() { Requirement = "team:quality" },
    By = new("reviewer:offline", AefAssurance.Signed), At = AefTime.Parse("2026-10-01T10:30:00Z"),
    Expires = AefTime.Parse("2026-10-01T12:00:00Z"), Reason = "offline temporary exception"
});
overlay.SealBatch(signer);
index.Upsert(reader.Read("offline", "offline-run", directory));
var hash = new SqliteEvidenceSourceLookup(options).Find(sourceId)!.Run!.RunHash!;
using var observations = new OtlpObservationStore(new()
{
    Root = Path.Combine(workspace, "observations"), Source = "offline-observer"
}, new OtlpContentPolicy(), new OtlpSessionGrouper(new OtlpTraceNormalizer()));
observations.Accept(new OtlpJsonTraceDecoder().Decode(Encoding.UTF8.GetBytes($$$"""
    {"resourceSpans":[{"scopeSpans":[{"spans":[{
    "traceId":"{{{TestTraceId}}}","spanId":"0123456789abcdef",
    "startTimeUnixNano":"18446744073709551615",
    "attributes":[{"key":"gen_ai.usage.input_tokens","value":{"intValue":"9007199254740993"}},
    {"key":"gen_ai.usage.output_tokens","value":{"intValue":"12"}},
    {"key":"gen_ai.usage.completion_tokens","value":{"intValue":"13"}}]}]}]}]}
    """)));
var imported = new OtlpObservationIndexer(index).Import("observations", observations);
if (!imported.Complete || imported.Changed != 1) throw new InvalidOperationException("Observation fixture import failed.");
var observationId = index.ListSources("observations").Single().Checkpoint.SourceId;
var catalogue = Path.Combine(workspace, "catalogue.json");
File.WriteAllText(catalogue, """
    {"schemaVersion":"1.0","requirements":[{"id":"team:quality",
    "selector":{"lane":"main","metric":"quality","path":"quality"},
    "acceptance":{"kind":"threshold","operator":">=","value":0.95},
    "aggregation":{"kind":"all","missing":"not_measured"},
    "freshnessSeconds":86400,"allowEffectiveOverrides":false}]}
    """);
var policy = Path.Combine(workspace, "policy.json");
File.WriteAllText(policy, new TrustPolicy([new TrustedKey("reviewer:offline", signer.PublicKey,
    [RequirementEvidenceReader.WaivePermission("team:quality")])]).ToJson().ToJsonString());
var checkpoint = Path.Combine(workspace, "checkpoint.json");
File.WriteAllText(checkpoint, new JsonObject
{
    ["schemaVersion"] = "1.0", ["checkpointId"] = "cp:offline", ["state"] = "evidence_complete", ["outcome"] = null,
    ["subject"] = new JsonObject { ["ref"] = "agent:offline", ["version"] = "1" },
    ["lanes"] = new JsonArray(new JsonObject
    {
        ["lane"] = "quality", ["blocking"] = true,
        ["rule"] = new JsonObject { ["kind"] = "evidence-present", ["runs"] = 1 },
        ["runs"] = new JsonArray(new JsonObject { ["runId"] = "offline-run", ["runHash"] = hash, ["origin"] = "launched" })
    })
}.ToJsonString());
var envelope = Path.Combine(workspace, "checkpoint.dsse.json");
File.WriteAllBytes(envelope, DsseEnvelope.Create(Dsse.CheckpointPayloadType, File.ReadAllBytes(checkpoint), signer).ToJson());
File.WriteAllText(Path.Combine(workspace, "fixture.json"), JsonSerializer.Serialize(new
{
    sourceId, resultId, hash, directory, workspace, observationId,
    configuration = new Dictionary<string, string>
    {
        ["AgentEval:Root"] = workspace,
        ["AgentEval:Queries:Enabled"] = "true",
        ["AgentEval:Queries:IndexPath"] = database,
        ["AgentEval:Queries:AefRoots:0:RootId"] = "offline",
        ["AgentEval:Queries:AefRoots:0:Directory"] = evidence,
        ["AgentEval:Queries:CataloguePath"] = catalogue,
        ["AgentEval:Queries:TrustPolicyPath"] = policy,
        ["AgentEval:Queries:Checkpoints:0:Id"] = "release",
        ["AgentEval:Queries:Checkpoints:0:ManifestPath"] = checkpoint,
        ["AgentEval:Queries:Checkpoints:0:EnvelopePath"] = envelope
    }
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(Path.Combine(workspace, "fixture.json"));
