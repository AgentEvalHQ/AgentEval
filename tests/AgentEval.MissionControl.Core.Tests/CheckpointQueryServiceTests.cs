using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Indexing;
using AgentEval.Results;

namespace AgentEval.MissionControl.Core.Tests;

public sealed partial class SuiteQueryServiceTests
{
    private static byte[] CheckpointBytes(string run, string hash) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["schemaVersion"] = "1.0", ["checkpointId"] = "checkpoint:test", ["state"] = "evidence_complete", ["outcome"] = null,
        ["subject"] = new JsonObject { ["ref"] = "agent:query", ["version"] = "1" },
        ["lanes"] = new JsonArray(new JsonObject
        {
            ["lane"] = "quality", ["blocking"] = true,
            ["rule"] = new JsonObject { ["kind"] = "evidence-present", ["runs"] = 1 },
            ["runs"] = new JsonArray(new JsonObject { ["runId"] = run, ["runHash"] = hash, ["origin"] = "launched" })
        })
    }.ToJsonString());

    [Fact]
    public void Checkpoint_Unconfigured_PreservesTypedOutcomeAndCancellation()
    {
        var service = new CheckpointQueryService();
        Assert.Equal(QueryState.NotConfigured, service.List().State);
        Assert.Equal(QueryState.NotConfigured, service.Explain("", [], default).State);
        Assert.Throws<OperationCanceledException>(() => service.List(new(true)));
        Assert.Throws<OperationCanceledException>(() => service.Explain("", [], default, new(true)));
    }

    [Fact]
    public void Checkpoint_ConfiguredBytesAreOwned_ExplicitSourcesRetainRevisionAndMissingEvidence()
    {
        using var writer = Writer(); writer.BindRoots([Root]); var id = WriteDefinition(writer, "checkpoint-run", true, false);
        var lookup = new SqliteEvidenceSourceLookup(Options); var selected = lookup.Find(id)!;
        var bytes = CheckpointBytes("checkpoint-run", selected.Run!.RunHash!);
        var configured = new ConfiguredCheckpoint("release", bytes);
        Array.Fill(bytes, (byte)'x');
        var service = new CheckpointQueryService(lookup, new BoundedCheckpointReader(), [configured], [Root]);
        var descriptor = Assert.Single(service.List().Data!);
        Assert.Equal("release", descriptor.Id); Assert.Equal("evidence_complete", descriptor.DeclaredState);
        Assert.Null(descriptor.DeclaredOutcome);
        var at = AefTime.Parse("2026-10-14T00:00:00.123456789Z");
        var outcome = service.Explain("release", [id, id], at);
        Assert.Equal(QueryState.Available, outcome.State);
        Assert.Equal([id], outcome.Data!.SourceIds);
        Assert.Equal(selected.Generation, outcome.Data.IndexGeneration);
        Assert.Equal(selected.InstanceId, outcome.Data.IndexInstanceId);
        Assert.Equal(descriptor.ManifestDigest, outcome.Data.Explanation.ManifestDigest);
        Assert.Equal("passed", Assert.Single(outcome.Data.Explanation.Lanes).EvidenceStatus);
        Assert.Empty(outcome.Data.Explanation.Anchors);
        Assert.DoesNotContain(_root, JsonSerializer.Serialize(outcome.Data));
        var empty = service.Explain("release", [], at);
        Assert.Equal(QueryState.Available, empty.State); Assert.Null(empty.Data!.IndexGeneration);
        Assert.Contains(empty.Data.Explanation.Problems, p => p.Code == "run-missing");
    }

    [Fact]
    public void Checkpoint_InvalidMissingMixedRevisionAndTamperedSources_RefuseWithoutPartialReport()
    {
        using var writer = Writer(); writer.BindRoots([Root]);
        var first = WriteDefinition(writer, "checkpoint-first", true, false);
        var second = WriteDefinition(writer, "checkpoint-second", true, false);
        var lookup = new SqliteEvidenceSourceLookup(Options);
        var configured = new ConfiguredCheckpoint("release", CheckpointBytes("checkpoint-first", lookup.Find(first)!.Run!.RunHash!));
        var service = new CheckpointQueryService(lookup, new BoundedCheckpointReader(), [configured], [Root]);
        var at = AefTime.Parse("2026-10-14T00:00:00Z");
        Assert.Equal(QueryState.InvalidRequest, service.Explain("release", [], new(0, -1)).State);
        Assert.Equal(QueryState.NotFound, service.Explain("absent", [], at).State);
        Assert.Equal(QueryState.InvalidRequest, service.Explain("release", ["malformed"], at).State);
        Assert.Equal(QueryState.NotFound, service.Explain("release", [new string('0', 64)], at).State);
        Assert.Equal(QueryState.ResourceLimit, service.Explain("release", Enumerable.Repeat(first, 9).ToArray(), at).State);
        var changing = new CheckpointQueryService(new ChangingLookup(lookup), new BoundedCheckpointReader(), [configured], [Root]);
        var mixed = changing.Explain("release", [first, second], at);
        Assert.Equal(QueryState.StaleCursor, mixed.State); Assert.Null(mixed.Data);
        File.AppendAllText(Path.Combine(Root.Directory, "checkpoint-first", "results.ndjson"), " ");
        var tampered = service.Explain("release", [first], at);
        Assert.Equal(QueryState.Unavailable, tampered.State); Assert.Null(tampered.Data);
        Assert.Equal("checkpoint-evidence-unavailable", tampered.Code);
    }

    [Fact]
    public void Checkpoint_Configuration_IsBoundedAndDistinct()
    {
        Assert.Throws<ArgumentException>(() => new ConfiguredCheckpoint("release", new byte[(1 << 20) + 1]));
        var checkpoint = new ConfiguredCheckpoint("release", CheckpointBytes("missing", new string('a', 64)));
        Assert.Throws<ArgumentException>(() => new CheckpointQueryService(new SqliteEvidenceSourceLookup(Options),
            new BoundedCheckpointReader(), [checkpoint, checkpoint], [Root]));
        var empty = new CheckpointQueryService(new SqliteEvidenceSourceLookup(Options), new BoundedCheckpointReader(), [], [Root]);
        Assert.Equal(QueryState.Empty, empty.List().State); Assert.Empty(empty.List().Data!);
    }
}
