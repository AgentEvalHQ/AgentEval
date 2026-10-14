// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER
using System.Text.Json;
using System.Net.Http.Json;
using AgentEval.Indexing;
using AgentEval.Results;
using AgentEval.Results.Requirements;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Tests.MissionControl;

public sealed partial class SuiteQueryGraphQLTests
{
    private string CatalogueFile()
    {
        var path = Path.Combine(_root, "catalogue.json");
        File.WriteAllText(path, """
            {"schemaVersion":"1.0","requirements":[{"id":"team:quality",
              "selector":{"lane":"main","metric":"quality","path":"quality"},
              "acceptance":{"kind":"threshold","operator":">=","value":0.95},
              "aggregation":{"kind":"all","missing":"not_measured"},
              "freshnessSeconds":86400,"allowEffectiveOverrides":false}]}
            """);
        return path;
    }

    private static string GovernanceQuery(string id, string at = "2026-10-01T11:00:00Z") => $$"""
        { governanceStatus(subjectRef:"agent:query",subjectVersion:"1",sourceIds:["{{id}}"],at:"{{at}}") {
          state code data { catalogueDigest policyDigest at subject { ref version } selectedRunHashes
            evidence { runId runHash overlayDigest }
            requirements { id baseStatus effectiveStatus status diagnostics
              observations { runHash resultId lane metric path sealedState effectiveState value endedAt }
              waivers { event identity assurance expires active authorized }
              reviews { runHash target status event } } } } }
        """;

    [Fact]
    public async Task Governance_SignedWaiver_UsesConfiguredAuthorityAndExpiry()
    {
        using var writer = new SqliteEvidenceIndexWriter(new() { DatabasePath = Database }, new SqliteEvidenceIndexSchema());
        writer.BindRoots([Root]); var id = WriteDefinition(writer, "waiver-http", true, false);
        using var signer = EcdsaP256Signer.Generate();
        var overlay = AefOverlayWriter.Open(Path.Combine(Root.Directory, "waiver-http"));
        overlay.Append(new AefOverlayEvent
        {
            Kind = AefOverlayKind.Waive, Target = new() { Requirement = "team:quality" },
            By = new("reviewer:http", AefAssurance.Signed), At = AefTime.Parse("2026-10-01T10:30:00Z"),
            Expires = AefTime.Parse("2026-10-01T12:00:00Z"), Reason = "temporary exception"
        });
        overlay.SealBatch(signer);
        var catalogue = CatalogueFile();
        var policy = Path.Combine(_root, "policy.json");
        File.WriteAllText(policy, new TrustPolicy([new TrustedKey("reviewer:http", signer.PublicKey,
            [RequirementEvidenceReader.WaivePermission("team:quality")])]).ToJson().ToJsonString());
        using var factory = Factory(catalogue: catalogue, policy: policy); using var client = factory.CreateClient();
        using var active = await Read(client, GovernanceQuery(id));
        var outcome = active.RootElement.GetProperty("data").GetProperty("governanceStatus");
        Assert.Equal("AVAILABLE", outcome.GetProperty("state").GetString());
        var report = outcome.GetProperty("data");
        var status = Assert.Single(report.GetProperty("requirements").EnumerateArray());
        Assert.Equal("waived", status.GetProperty("status").GetString());
        var waiver = Assert.Single(status.GetProperty("waivers").EnumerateArray());
        Assert.True(waiver.GetProperty("authorized").GetBoolean());
        Assert.Equal("signed", waiver.GetProperty("assurance").GetString());
        Assert.Single(report.GetProperty("selectedRunHashes").EnumerateArray());
        using var expired = await Read(client, GovernanceQuery(id, "2026-10-01T13:00:00Z"));
        Assert.NotEqual("waived", expired.RootElement.GetProperty("data").GetProperty("governanceStatus")
            .GetProperty("data").GetProperty("requirements")[0].GetProperty("status").GetString());
        var noAuthority = Path.Combine(_root, "no-authority.json");
        File.WriteAllText(noAuthority, new TrustPolicy([new TrustedKey("reviewer:http", signer.PublicKey)]).ToJson().ToJsonString());
        using var otherFactory = Factory(catalogue: catalogue, policy: noAuthority); using var other = otherFactory.CreateClient();
        using var refused = await Read(other, GovernanceQuery(id));
        var untrusted = refused.RootElement.GetProperty("data").GetProperty("governanceStatus").GetProperty("data");
        Assert.NotEqual(report.GetProperty("policyDigest").GetString(), untrusted.GetProperty("policyDigest").GetString());
        Assert.False(untrusted.GetProperty("requirements")[0].GetProperty("waivers")[0].GetProperty("authorized").GetBoolean());
        Assert.NotEqual("waived", untrusted.GetProperty("requirements")[0].GetProperty("status").GetString());
        Assert.DoesNotContain(_root, active.RootElement.ToString());
    }

    [Fact]
    public async Task Governance_EmptySelectionAndConfiguredCountLimit_PreserveSharedOutcomes()
    {
        using var factory = Factory(catalogue: CatalogueFile(), maxSources: 1); using var client = factory.CreateClient();
        using var json = await Read(client, """
            { empty:governanceStatus(subjectRef:"agent:query",subjectVersion:"1",sourceIds:[],at:"2026-10-01T11:00:00.000000001Z") {
                state data { at requirements { status } } } }
            """);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("AVAILABLE", data.GetProperty("empty").GetProperty("state").GetString());
        Assert.Equal("2026-10-01T11:00:00.000000001Z", data.GetProperty("empty").GetProperty("data").GetProperty("at").GetString());
        Assert.Equal("not_measured", data.GetProperty("empty").GetProperty("data").GetProperty("requirements")[0].GetProperty("status").GetString());
        using var limited = await Read(client, """
            { governanceStatus(subjectRef:"agent:query",subjectVersion:"1",sourceIds:["a","b"],at:"2026-10-01T11:00:00Z") {
                state code data { at } } }
            """);
        var refusal = limited.RootElement.GetProperty("data").GetProperty("governanceStatus");
        Assert.Equal("RESOURCE_LIMIT", refusal.GetProperty("state").GetString());
        Assert.Equal("evidence-count-limit", refusal.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, refusal.GetProperty("data").ValueKind);
        Assert.False(File.Exists(Database));
    }

    [Fact]
    public async Task Governance_AliasedExpensiveQueries_KeepHostOperationCostLimit()
    {
        using var factory = Factory(catalogue: CatalogueFile()); using var client = factory.CreateClient();
        const string field = "governanceStatus(subjectRef:\"a\",subjectVersion:\"1\",sourceIds:[],at:\"2026-10-01T00:00:00Z\") { state data { at } }";
        using var response = await client.PostAsJsonAsync("/graphql", new { query = "{ a:" + field + " b:" + field + " }" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("HC0047", body.RootElement.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString());
        Assert.False(File.Exists(Database));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Governance_RelativeOrOversizedConfiguration_RefusesStartup(bool policy, bool oversized)
    {
        var path = "relative.json";
        if (oversized)
        {
            path = Path.Combine(_root, "oversized.json");
            File.WriteAllText(path, new string(' ', (1 << 20) + 1));
        }
        using var factory = Factory(catalogue: policy ? CatalogueFile() : path, policy: policy ? path : null);
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains(oversized ? "exceeds the limit" : "relative path", error.Message);
        Assert.False(File.Exists(Database));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Governance_MissingConfiguration_IsExplicit(bool enabled, bool roots, bool catalogue)
    {
        using var factory = Factory(enabled, roots, catalogue: catalogue ? CatalogueFile() : null); using var client = factory.CreateClient();
        using var json = await Read(client, GovernanceQuery("none"));
        Assert.Equal("NOT_CONFIGURED", json.RootElement.GetProperty("data").GetProperty("governanceStatus").GetProperty("state").GetString());
        Assert.False(File.Exists(Database));
    }
}
#endif
