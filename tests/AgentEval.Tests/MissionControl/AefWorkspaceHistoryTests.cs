// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#if NET10_0_OR_GREATER
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using AgentEval.MissionControl.GraphQL;
using AgentEval.Output;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Writing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentEval.Tests.MissionControl;

public sealed class AefWorkspaceHistoryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ae-mc-history-");
    private string Workspace => Path.Combine(_root.FullName, ".agenteval");
    private static readonly SubjectIdentity Subject = new(SubjectKind.Agent, "HistoryAgent");
    private static readonly DateTimeOffset When = DateTimeOffset.Parse("2026-10-23T12:30:00+02:00");
    public void Dispose() => _root.Delete(true);

    private WebApplicationFactory<Query> Factory(IOutputStoreReader? store = null) => new WebApplicationFactory<Query>().WithWebHostBuilder(builder =>
    {
        LoopbackTestConnection.Configure(builder);
        builder.UseSetting("AgentEval:Root", _root.FullName);
        builder.UseSetting("AgentEval:Queries:Enabled", "false");
        if (store is not null) builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOutputStoreReader>();
            services.AddSingleton(store);
        });
    });

    private string Native(string kind, JsonObject? ext = null, AefImported? imported = null, string? history = null)
    {
        var directory = Path.Combine(Workspace, "aef", "runs", "native");
        var writer = AefRunWriter.Create(directory, new()
        {
            RunId = "run:native", Producer = new() { Name = "tests", Version = "1.0" },
            Subject = new() { Ref = kind + ":HistoryAgent", Kind = kind == "model" ? AefSubjectKind.Model : AefSubjectKind.Agent },
            StartedAt = When, ContentCapture = history is null ? AefContentCapture.Off : AefContentCapture.On,
            Execution = new() { TargetMode = AefTargetMode.Mocked },
            Ext = ext, Imported = imported,
        });
        if (history is not null) writer.AddEvidence(new()
        {
            EvidenceId = "E-history", Kind = AefEvidenceKind.Document,
            Link = AefEvidenceLink.ToBlob(writer.PutBlob(Encoding.UTF8.GetBytes(history))),
        });
        writer.AddResult(new() { CaseId = "case", Path = "check", Evaluator = new("code:check"), State = AefState.Passed });
        writer.Close(AefRunStatus.Completed, When.AddSeconds(1));
        AefSealer.Seal(directory, new() { SealedBy = imported is null ? AefSealedBy.Producer : AefSealedBy.Ingest });
        return directory;
    }

    private async Task<(FileSystemOutputStore Store, string Id, string Directory)> Legacy()
    {
        var store = new FileSystemOutputStore(Workspace);
        await store.InitializeSolutionAsync("history-fixture");
        var manifest = await store.StartRunAsync(Subject, new("tests", ".", "offline", null, null, "eval"));
        return (store, manifest.Run.RunId, store.ResolveRunDirectory(Subject, manifest.Run.RunId));
    }

    private static async Task<JsonElement[]> Rows(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        return (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => { using var json = JsonDocument.Parse(line); return json.RootElement.Clone(); }).ToArray();
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("model")]
    public async Task History_NativeOnlyWithoutSolution_ReturnsVerifiedSummaryWithUnknownScore(string kind)
    {
        Native(kind);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/v1/subjects/{kind}/HistoryAgent/history");
        var row = Assert.Single(await Rows(response));
        Assert.Equal("run:native", row.GetProperty("runId").GetString());
        Assert.Equal("execution_summary", row.GetProperty("recordKind").GetString());
        Assert.Equal(When, row.GetProperty("timestamp").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("score").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("verdict").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("notes").ValueKind);
        Assert.Equal("intact", row.GetProperty("evidence").GetProperty("integrity").GetString());
        Assert.False(row.GetProperty("evidence").GetProperty("imported").GetBoolean());
        Assert.StartsWith("source-", row.GetProperty("sourceId").GetString());
    }

    [Fact]
    public async Task History_DetachedConversion_KeepsRecordedNotesDuplicatesAndOrder()
    {
        var legacy = await Legacy();
        var entry = new HistoryEntry(legacy.Id, When, "ANNOTATED", 7.5, "original note\nwith quotes: \"kept\"");
        await legacy.Store.AppendHistoryEntryAsync(Subject, entry);
        await legacy.Store.AppendHistoryEntryAsync(Subject, entry);
        StoreV1Exporter.ExportWorkspaceRun(Workspace, legacy.Directory, Path.Combine(Workspace, "aef", "runs", "captured"));
        Directory.Delete(Path.Combine(Workspace, "subjects"), true);
        File.Delete(Path.Combine(Workspace, "solution.json"));
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/subjects/agent/HistoryAgent/history");
        var rows = await Rows(response);
        Assert.Equal(2, rows.Length);
        Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.GetProperty("sourceOrdinal").GetInt32()));
        foreach (var row in rows)
        {
            Assert.Equal(entry.RunId, row.GetProperty("runId").GetString());
            Assert.Equal(entry.Timestamp, row.GetProperty("timestamp").GetDateTimeOffset());
            Assert.Equal(entry.Verdict, row.GetProperty("verdict").GetString());
            Assert.Equal(entry.Score, row.GetProperty("score").GetDouble());
            Assert.Equal(entry.Notes, row.GetProperty("notes").GetString());
            Assert.Equal("legacy_index", row.GetProperty("recordKind").GetString());
            Assert.True(row.GetProperty("evidence").GetProperty("imported").GetBoolean());
            Assert.Equal("not_checked", row.GetProperty("evidence").GetProperty("signatureTrust").GetString());
        }
    }

    [Fact]
    public async Task History_LegacyInterleavedAndOrphanEntries_KeepsOriginalIndexOrder()
    {
        var legacy = await Legacy();
        var entries = new[]
        {
            new HistoryEntry("orphan", When, "NOTE", -4, "before"),
            new HistoryEntry(legacy.Id, When.AddMinutes(-1), "PASS", 0, "recorded zero"),
            new HistoryEntry("orphan", When, "NOTE", -4, "before"),
        };
        foreach (var entry in entries) await legacy.Store.AppendHistoryEntryAsync(Subject, entry);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/subjects/agent/HistoryAgent/history");
        var rows = await Rows(response);
        Assert.Equal(entries.Select(e => e.RunId), rows.Select(r => r.GetProperty("runId").GetString()));
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.GetProperty("sourceOrdinal").GetInt32()));
        Assert.Single(rows.Select(r => r.GetProperty("sourceId").GetString()).Distinct());
        Assert.Equal(-4, rows[0].GetProperty("score").GetDouble());
    }

    [Fact]
    public async Task History_InvalidIndexAfterValidEntry_RefusesBeforeWritingAnyHistory()
    {
        var legacy = await Legacy();
        await legacy.Store.AppendHistoryEntryAsync(Subject, new(legacy.Id, When, "PASS", 1, "VALID_PREFIX_MUST_NOT_LEAK"));
        await File.AppendAllTextAsync(new FileSystemLayout(Workspace).HistoryFile(Subject),
            "{\"runId\":\"orphan\",\"timestamp\":\"2026-10-23T10:00:00Z\",\"verdict\":\"PASS\"}\n");
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/subjects/agent/HistoryAgent/history");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain("VALID_PREFIX_MUST_NOT_LEAK", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task History_StoredCaptureOffAlias_DoesNotRecoverSiblingNotes(bool converted)
    {
        var legacy = await Legacy();
        await legacy.Store.AppendHistoryEntryAsync(Subject, new(legacy.Id, When, "NOTE", 0, "SIBLING_NOTE_MUST_STAY_UNAVAILABLE"));
        if (converted) StoreV1Exporter.ExportWorkspaceRun(Workspace, legacy.Directory,
            Path.Combine(Workspace, "aef", "runs", "captured"), new() { ContentCapture = AefContentCapture.Off });
        else Native("agent", new() { [StoreV1Exporter.ExtName] = new JsonObject { ["runId"] = legacy.Id } });
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/subjects/agent/HistoryAgent/history");
        var row = Assert.Single(await Rows(response));
        Assert.Equal("execution_summary", row.GetProperty("recordKind").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("notes").ValueKind);
        Assert.DoesNotContain("SIBLING_NOTE_MUST_STAY_UNAVAILABLE", row.GetRawText());
        if (converted)
        {
            Assert.Equal("not_captured", row.GetProperty("historyAvailability").GetString());
            Assert.Equal(1, row.GetProperty("recordedEntryCount").GetInt32());
        }
    }

    [Fact]
    public async Task History_ImporterAssertedStart_DoesNotDateOriginalExecution()
    {
        Native("agent", imported: new() { From = StoreV1Exporter.From, Asserted = ["startedAt"] });
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/subjects/agent/HistoryAgent/history");
        var row = Assert.Single(await Rows(response));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("timestamp").ValueKind);
        Assert.Contains("startedAt", row.GetProperty("evidence").GetProperty("asserted").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task History_TamperedNativeEvidence_RefusesDownload()
    {
        var directory = Native("agent");
        await File.AppendAllTextAsync(Path.Combine(directory, "results.ndjson"), " ");
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/subjects/agent/HistoryAgent/history");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task History_MultipleStoredAliases_RefusesArbitrarySelection()
    {
        var legacy = await Legacy();
        await legacy.Store.AppendHistoryEntryAsync(Subject, new(legacy.Id, When, "NOTE", 1, null));
        var directory = Native("agent", new() { [StoreV1Exporter.ExtName] = new JsonObject { ["runId"] = legacy.Id } });
        var copy = Path.Combine(Workspace, "aef", "runs", "duplicate");
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.EnumerateFiles(directory)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/subjects/agent/HistoryAgent/history");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Theory]
    [InlineData("unsupported-version")]
    [InlineData("wrong-count")]
    [InlineData("duplicate-order")]
    [InlineData("unrelated-run")]
    [InlineData("missing-score")]
    [InlineData("missing-blob")]
    public async Task History_InvalidCapturedEnvelope_RefusesRecordedClaims(string mutation)
    {
        var marker = new JsonObject { ["version"] = 1, ["entries"] = 1, ["captured"] = true };
        var body = JsonNode.Parse("""{"version":1,"entries":[{"sourceOrdinal":1,"entry":{"runId":"run:native","timestamp":"2026-10-23T10:00:00Z","verdict":"NOTE","score":0}}]}""")!;
        if (mutation == "unsupported-version") marker["version"] = 2;
        if (mutation == "wrong-count") marker["entries"] = 2;
        if (mutation == "duplicate-order")
        {
            body["entries"]!.AsArray().Add(body["entries"]![0]!.DeepClone());
            marker["entries"] = 2;
        }
        if (mutation == "unrelated-run") body["entries"]![0]!["entry"]!["runId"] = "another-execution";
        if (mutation == "missing-score") body["entries"]![0]!["entry"]!.AsObject().Remove("score");
        Native("agent", new() { [StoreV1Exporter.ExtName] = new JsonObject { ["history"] = marker } },
            new() { From = StoreV1Exporter.From, Asserted = [] }, mutation == "missing-blob" ? null : body.ToJsonString());
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/subjects/agent/HistoryAgent/history");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task History_SemanticStore_PreservesOrphanNotes()
    {
        var store = new InMemoryOutputStore();
        await store.AppendHistoryEntryAsync(Subject, new("orphan", When, "NOTE", 0, "semantic-only note"));
        using var factory = Factory(store); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/subjects/agent/HistoryAgent/history");
        var row = Assert.Single(await Rows(response));
        Assert.Equal("semantic-only note", row.GetProperty("notes").GetString());
        Assert.Equal("orphan", row.GetProperty("runId").GetString());
        Assert.Equal("legacy_index", row.GetProperty("recordKind").GetString());
        Assert.True(row.GetProperty("evidence").GetProperty("imported").GetBoolean());
    }

    [Fact]
    public async Task History_ConvertedEmptyIndex_ReturnsSummaryWithExplicitEmptyIndex()
    {
        var legacy = await Legacy();
        await File.WriteAllTextAsync(new FileSystemLayout(Workspace).HistoryFile(Subject), "");
        StoreV1Exporter.ExportWorkspaceRun(Workspace, legacy.Directory, Path.Combine(Workspace, "aef", "runs", "captured"));
        Directory.Delete(Path.Combine(Workspace, "subjects"), true);
        File.Delete(Path.Combine(Workspace, "solution.json"));
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/subjects/agent/HistoryAgent/history");
        var row = Assert.Single(await Rows(response));
        Assert.Equal("execution_summary", row.GetProperty("recordKind").GetString());
        Assert.Equal("recorded_empty", row.GetProperty("historyAvailability").GetString());
        Assert.Equal(0, row.GetProperty("recordedEntryCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("recordedEntry").ValueKind);
    }
}
#endif
