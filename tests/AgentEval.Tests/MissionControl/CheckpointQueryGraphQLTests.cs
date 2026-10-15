#if NET10_0_OR_GREATER
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Indexing;
using AgentEval.MissionControl.Core;
using AgentEval.Results;
using AgentEval.Results.Signatures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Serialization;

namespace AgentEval.Tests.MissionControl;

public sealed partial class SuiteQueryGraphQLTests
{
    private string CheckpointFile(string run, string hash)
    {
        var path = Path.Combine(_root, "checkpoint.json");
        File.WriteAllText(path, new JsonObject
        {
            ["schemaVersion"] = "1.0", ["checkpointId"] = "cp:test", ["state"] = "evidence_complete", ["outcome"] = null,
            ["subject"] = new JsonObject { ["ref"] = "agent:query", ["version"] = "1" },
            ["lanes"] = new JsonArray(new JsonObject
            {
                ["lane"] = "quality", ["blocking"] = true, ["rule"] = new JsonObject { ["kind"] = "evidence-present", ["runs"] = 1 },
                ["runs"] = new JsonArray(new JsonObject { ["runId"] = run, ["runHash"] = hash, ["origin"] = "launched" })
            })
        }.ToJsonString());
        return path;
    }
    private static string ExplainCheckpoint(string sources) => $$"""
        { checkpointExplanation(checkpointId:"release",sourceIds:{{sources}},at:"2026-10-14T00:00:00.123456789Z") {
          state code data { checkpointId sourceIds indexInstanceId indexGeneration explanation {
            manifestDigest policyDigest requestedAt evaluatedAt manifestJson manifestProblems
            problems { path code } lanes { lane ruleKind evidenceStatus subjectVersion oldestClosedAt axes readsUnknown problems { path code } }
            signedBy anchors envelopePresent signatureChecked envelopeResult signatures { keyId result identity }
          } } } }
        """;

    [Fact]
    public async Task Checkpoint_ConfiguredWithoutCatalogue_ExposesVerifiedEvidenceAndExactRevision()
    {
        using var writer = new SqliteEvidenceIndexWriter(new() { DatabasePath = Database }, new SqliteEvidenceIndexSchema());
        writer.BindRoots([Root]); var id = WriteDefinition(writer, "checkpoint-run", true, false);
        var selected = new SqliteEvidenceSourceLookup(new() { DatabasePath = Database }).Find(id)!;
        var manifest = CheckpointFile("checkpoint-run", selected.Run!.RunHash!);
        using var factory = Factory(checkpoint: manifest); using var client = factory.CreateClient();
        using var list = await Read(client, "{ checkpoints { state data { id checkpointId subjectRef subjectVersion declaredState declaredOutcome manifestDigest } } governanceStatus(subjectRef:\"a\",subjectVersion:\"1\",sourceIds:[],at:\"2026-10-14T00:00:00Z\") { state } }");
        Assert.Equal("NOT_CONFIGURED", list.RootElement.GetProperty("data").GetProperty("governanceStatus").GetProperty("state").GetString());
        var descriptor = Assert.Single(list.RootElement.GetProperty("data").GetProperty("checkpoints").GetProperty("data").EnumerateArray());
        Assert.Equal("release", descriptor.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, descriptor.GetProperty("declaredOutcome").ValueKind);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(factory.Services.GetRequiredService<ICheckpointQueryService>().List().Data,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), JsonNode.Parse(list.RootElement.GetProperty("data").GetProperty("checkpoints").GetProperty("data").GetRawText())));
        using var json = await Read(client, ExplainCheckpoint(JsonSerializer.Serialize(new[] { id })));
        var outcome = json.RootElement.GetProperty("data").GetProperty("checkpointExplanation");
        Assert.Equal("AVAILABLE", outcome.GetProperty("state").GetString());
        var data = outcome.GetProperty("data");
        AssertCheckpointParity(factory.Services.GetRequiredService<ICheckpointQueryService>(), [id], data);
        Assert.Equal(selected.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), data.GetProperty("indexGeneration").GetString());
        var explanation = data.GetProperty("explanation");
        Assert.Equal("2026-10-14T00:00:00.123456789Z", explanation.GetProperty("evaluatedAt").GetString());
        Assert.Equal("passed", Assert.Single(explanation.GetProperty("lanes").EnumerateArray()).GetProperty("evidenceStatus").GetString());
        Assert.False(explanation.GetProperty("envelopePresent").GetBoolean());
        Assert.False(explanation.GetProperty("signatureChecked").GetBoolean());
        Assert.Empty(explanation.GetProperty("anchors").EnumerateArray());
        Assert.DoesNotContain(_root, json.RootElement.ToString());
        using var missing = await Read(client, ExplainCheckpoint("[]"));
        var missingData = missing.RootElement.GetProperty("data").GetProperty("checkpointExplanation").GetProperty("data");
        AssertCheckpointParity(factory.Services.GetRequiredService<ICheckpointQueryService>(), [], missingData);
        Assert.Equal(JsonValueKind.Null, missingData.GetProperty("indexGeneration").ValueKind);
        Assert.NotEmpty(missingData.GetProperty("explanation").GetProperty("problems").EnumerateArray());
    }

    [Fact]
    public async Task Checkpoint_SignedEnvelope_ExposesActualTrustAndFailsClosedOnTampering()
    {
        using var writer = new SqliteEvidenceIndexWriter(new() { DatabasePath = Database }, new SqliteEvidenceIndexSchema());
        writer.BindRoots([Root]); var id = WriteDefinition(writer, "signed-checkpoint", true, false);
        var hash = new SqliteEvidenceSourceLookup(new() { DatabasePath = Database }).Find(id)!.Run!.RunHash!;
        var manifest = CheckpointFile("signed-checkpoint", hash);
        using var signer = EcdsaP256Signer.Generate();
        var envelope = Path.Combine(_root, "checkpoint.dsse.json");
        File.WriteAllBytes(envelope, DsseEnvelope.Create(Dsse.CheckpointPayloadType, File.ReadAllBytes(manifest), signer).ToJson());
        var policy = Path.Combine(_root, "checkpoint-policy.json");
        File.WriteAllText(policy, new TrustPolicy([new TrustedKey("reviewer:test", signer.PublicKey)]).ToJson().ToJsonString());
        using var factory = Factory(checkpoint: manifest, checkpointEnvelope: envelope, policy: policy); using var client = factory.CreateClient();
        var query = ExplainCheckpoint(JsonSerializer.Serialize(new[] { id }));
        using var signed = await Read(client, query);
        var explanation = signed.RootElement.GetProperty("data").GetProperty("checkpointExplanation").GetProperty("data").GetProperty("explanation");
        AssertCheckpointParity(factory.Services.GetRequiredService<ICheckpointQueryService>(), [id], signed.RootElement.GetProperty("data").GetProperty("checkpointExplanation").GetProperty("data"));
        Assert.Equal(hash, Assert.Single(explanation.GetProperty("anchors").EnumerateArray()).GetString());
        Assert.Equal("VERIFIED", Assert.Single(explanation.GetProperty("signatures").EnumerateArray()).GetProperty("result").GetString());
        File.AppendAllText(Path.Combine(Root.Directory, "signed-checkpoint", "results.ndjson"), " ");
        using var tampered = await Read(client, query);
        var outcome = tampered.RootElement.GetProperty("data").GetProperty("checkpointExplanation");
        Assert.Equal("UNAVAILABLE", outcome.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null, outcome.GetProperty("data").ValueKind);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Checkpoint_MissingConfiguration_ReturnsNotConfigured(bool enabled, bool roots, bool manifest)
    {
        using var factory = Factory(enabled: enabled, roots: roots, checkpoint: manifest ? "not-read-when-disabled" : null);
        using var client = factory.CreateClient();
        using var json = await Read(client, ExplainCheckpoint("[]"));
        Assert.Equal("NOT_CONFIGURED", json.RootElement.GetProperty("data").GetProperty("checkpointExplanation").GetProperty("state").GetString());
        Assert.False(File.Exists(Database));
    }

    [Fact]
    public async Task Checkpoint_AliasedExplanation_IsLimitedByProductionCostRules()
    {
        using var factory = Factory(enabled: false).WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = factory.CreateClient();
        var field = "checkpointExplanation(checkpointId:\"release\",sourceIds:[],at:\"2026-10-14T00:00:00Z\") { state }";
        using var response = await client.PostAsJsonAsync("/graphql", new { query = "{ " + string.Join(" ", Enumerable.Range(0, 20).Select(i => $"q{i}: {field}")) + " }" });
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Checkpoint_CrossOriginRequest_IsRefusedBeforeQueryExecution()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        using var response = await client.PostAsJsonAsync("/graphql", new { query = "{ checkpoints { state } }" });
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(File.Exists(Database));
    }

    [Fact]
    public void Checkpoint_RelativeManifestPath_IsRejectedDuringStartup()
    {
        using var factory = Factory(checkpoint: "relative.json");
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("absolute query configuration path", error.ToString());
    }

    private static void AssertCheckpointParity(ICheckpointQueryService service, string[] sources, JsonElement transported)
    {
        var direct = service.Explain("release", sources, AefTime.Parse("2026-10-14T00:00:00.123456789Z"));
        Assert.Equal(QueryState.Available, direct.State);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
        var expected = JsonSerializer.SerializeToNode(direct.Data, options)!.AsObject();
        expected["indexGeneration"] = direct.Data!.IndexGeneration?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(transported.GetRawText())), "Complete checkpoint evidence must match the shared service, with exact integer text and GraphQL enum names.");
    }
}
#endif
