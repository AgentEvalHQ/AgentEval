// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#if NET10_0_OR_GREATER
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.MissionControl.GraphQL;
using AgentEval.Output;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Writing;
using AgentEval.Tests.Cli;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentEval.Tests.MissionControl;

public sealed class AefWorkspaceDownloadTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ae-mc-download-");
    private string Workspace => Path.Combine(_root.FullName, ".agenteval");
    public void Dispose() => _root.Delete(true);

    private WebApplicationFactory<Query> Factory() => new WebApplicationFactory<Query>().WithWebHostBuilder(builder =>
    {
        LoopbackTestConnection.Configure(builder);
        builder.UseSetting("AgentEval:Root", _root.FullName);
        builder.UseSetting("AgentEval:Queries:Enabled", "false");
    });

    private async Task<(FileSystemOutputStore Store, string Id, string Directory)> Legacy()
    {
        var store = new FileSystemOutputStore(Workspace);
        await store.InitializeSolutionAsync("download-fixture");
        var subject = new SubjectIdentity(SubjectKind.Agent, "DownloadAgent");
        var manifest = await store.StartRunAsync(subject, new("tests", ".", "offline", null, null, "eval"));
        var id = manifest.Run.RunId;
        await store.WriteScenarioResultAsync(id, new("case", "case", "in", "out", true, 1,
            new Dictionary<string, double>(), [], TimeSpan.Zero, 0));
        await store.AppendTraceAsync(id, new(id, "case", [new(DateTimeOffset.UtcNow, "message", "agent", "original-content")]));
        var directory = store.ResolveRunDirectory(subject, id);
        System.IO.Directory.CreateDirectory(Path.Combine(directory, "reports"));
        File.WriteAllText(Path.Combine(directory, "reports", "report.md"), "original-report");
        return (store, id, directory);
    }

    private string Convert(string directory) => StoreV1Exporter.ExportWorkspaceRun(Workspace, directory,
        Path.Combine(Workspace, "aef", "runs", "captured")).Directory;

    [Fact]
    public async Task Trace_LegacyMissingTimestamp_DoesNotAcquireDeserializerDefault()
    {
        var legacy = await Legacy();
        File.WriteAllText(Path.Combine(legacy.Directory, "traces", "agent-trace.json"),
            $$"""{"runId":"{{legacy.Id}}","scenarioId":"case","events":[{"kind":"message"}]}""");
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/v1/runs/{legacy.Id}/trace");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Trace_LegacySnapshot_PreservesOriginalBytesAndUnknownProperties()
    {
        var legacy = await Legacy();
        var content = $$"""{ "runId":"{{legacy.Id}}", "scenarioId":"case", "events":[], "producerExtra":"preserved" }""";
        File.WriteAllText(Path.Combine(legacy.Directory, "traces", "agent-trace.json"), content);
        using var factory = Factory(); using var client = factory.CreateClient();
        Assert.Equal(content, await client.GetStringAsync($"/api/v1/runs/{legacy.Id}/trace"));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf32le")]
    [InlineData("utf32be")]
    public async Task Trace_LegacyBomEncoding_ProvidesUtf8ViewAndExactOriginalArtifact(string encodingName)
    {
        var legacy = await Legacy();
        Encoding encoding = encodingName switch
        {
            "utf8" => new UTF8Encoding(true), "utf16le" => new UnicodeEncoding(false, true),
            "utf16be" => new UnicodeEncoding(true, true), "utf32le" => new UTF32Encoding(false, true),
            _ => new UTF32Encoding(true, true),
        };
        var content = $$"""{ "runId":"{{legacy.Id}}", "scenarioId":"case", "events":[], "extra":"évidence" }""";
        byte[] original = [.. encoding.GetPreamble(), .. encoding.GetBytes(content)];
        File.WriteAllBytes(Path.Combine(legacy.Directory, "traces", "agent-trace.json"), original);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/v1/runs/{legacy.Id}/trace");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(content, Encoding.UTF8.GetString(bytes));
        using var parsed = JsonDocument.Parse(bytes);
        Assert.Equal("évidence", parsed.RootElement.GetProperty("extra").GetString());
        Assert.Equal(original, await client.GetByteArrayAsync($"/api/v1/runs/{legacy.Id}/artifacts/E-trace"));
    }

    private string SourceForCaptured()
    {
        // The native candidate is discovered before the unlinked legacy sibling.
        return new AefWorkspaceEvidenceReader().Read(Workspace).Runs[0].SourceId;
    }

    private void ArtifactRun(string content, bool legacy = false, JsonObject? reportExt = null, bool external = false,
        bool nativeAlias = false, string? traceFormat = null, byte[]? originalBytes = null)
    {
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var directory = Path.Combine(Workspace, "aef", "runs", "native");
        var writer = AefRunWriter.Create(directory, new()
        {
            RunId = "run:native", Producer = new() { Name = "tests", Version = "1.0" },
            Subject = new() { Ref = "agent:DownloadAgent", Kind = AefSubjectKind.Agent },
            StartedAt = start, ContentCapture = AefContentCapture.On,
            Execution = new() { TargetMode = AefTargetMode.Mocked },
            Imported = legacy ? new AefImported { From = StoreV1Exporter.From, Asserted = [] } : null,
            Ext = legacy || nativeAlias ? new JsonObject { ["agenteval.store-v1"] = new JsonObject() } : null,
        });
        writer.AddEvidence(new()
        {
            EvidenceId = "E-trace", Kind = reportExt is null ? AefEvidenceKind.Transcript : AefEvidenceKind.Document,
            Description = "Producer-defined artifact",
            Link = external ? AefEvidenceLink.ToUri("https://example.invalid/private")
                : AefEvidenceLink.ToBlob(writer.PutBlob(originalBytes ?? Encoding.UTF8.GetBytes(content))),
            Ext = reportExt ?? (traceFormat is null ? null : new JsonObject
                { ["agenteval.store-v1"] = new JsonObject { ["traceFormat"] = traceFormat } }),
        });
        writer.AddResult(new() { CaseId = "case", Path = "check", Evaluator = new("code:check"),
            State = AefState.Passed, Evidence = ["E-trace"] });
        writer.Close(AefRunStatus.Completed, start.AddSeconds(1));
        AefSealer.Seal(directory, new() { SealedBy = legacy ? AefSealedBy.Ingest : AefSealedBy.Producer });
        Assert.Single(new AefWorkspaceEvidenceReader().Read(Workspace).Runs);
    }

    [Fact]
    public async Task Artifact_NativeShape_DownloadsCapturedBytesWithoutLegacyViewer()
    {
        ArtifactRun("producer-specific transcript");
        using var factory = Factory(); using var client = factory.CreateClient();
        using var download = await client.GetAsync("/api/v1/runs/run:native/artifacts/E-trace");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("producer-specific transcript", await download.Content.ReadAsStringAsync());
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType!.MediaType);
        using var viewer = await client.GetAsync("/api/v1/runs/run:native/trace");
        Assert.Equal(HttpStatusCode.NotFound, viewer.StatusCode);
        Assert.Contains("trace_viewer_unsupported", await viewer.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Trace_InvalidUnicode_DoesNotReplaceRecordedCharacters()
    {
        // A lone UTF-16 high surrogate inside an otherwise valid JSON string.
        // Permissive decoding would silently replace it and serve altered evidence.
        byte[] original = [0xff, 0xfe,
            .. Encoding.Unicode.GetBytes("{\"runId\":\"run:native\",\"scenarioId\":\"case\",\"events\":[],\"extra\":\""),
            0x00, 0xd8, .. Encoding.Unicode.GetBytes("\"}")];
        ArtifactRun("unused", legacy: true, originalBytes: original);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/runs/run:native/trace");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(original, await client.GetByteArrayAsync("/api/v1/runs/run:native/artifacts/E-trace"));
    }

    [Fact]
    public async Task Trace_NativeWithLegacyAlias_DoesNotClaimLegacyTraceFormat()
    {
        ArtifactRun("producer-specific transcript", nativeAlias: true);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/runs/run:native/trace");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("trace_viewer_unsupported", await response.Content.ReadAsStringAsync());
        using var listing = await client.PostAsJsonAsync("/graphql", new { query = "{ runArtifacts(runId:\"run:native\") { captured legacyTrace } }" });
        using var json = JsonDocument.Parse(await listing.Content.ReadAsStringAsync());
        var artifact = Assert.Single(json.RootElement.GetProperty("data").GetProperty("runArtifacts").EnumerateArray());
        Assert.True(artifact.GetProperty("captured").GetBoolean());
        Assert.False(artifact.GetProperty("legacyTrace").GetBoolean());
    }

    [Theory]
    [InlineData("E-trace")]
    [InlineData("E-unknown")]
    public async Task Artifact_ExternalOrAbsent_DoesNotFetchContent(string evidenceId)
    {
        ArtifactRun("unused", external: true);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/v1/runs/run:native/artifacts/{evidenceId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"kind\":\"message\"}")]
    [InlineData("{\"timestamp\":\"2026-10-01T00:00:00Z\"}")]
    [InlineData("{\"timestamp\":\"2026-10-01T00:00:00Z\",\"kind\":null}")]
    [InlineData("null")]
    [InlineData("{\"timestamp\":\"2026-10-01T00:00:00\",\"kind\":\"message\"}")]
    [InlineData("{\"timestamp\":\"invalid\",\"kind\":\"message\"}")]
    [InlineData("{\"timestamp\":\"2026-10-01T00:00:00Z\",\"kind\":\"\"}")]
    [InlineData("{\"timestamp\":\"2026-10-01T00:00:00Z\",\"kind\":\"message\",\"payload\":{}}")]
    [InlineData("{\"timestamp\":\"2026-10-01T00:00:00Z\",\"kind\":\"message\",\"name\":42}")]
    public async Task Trace_MissingEventFacts_RefusesInsteadOfInventingValues(string traceEvent)
    {
        ArtifactRun("{\"runId\":\"run:native\",\"scenarioId\":\"case\",\"events\":[" + traceEvent + "]}", legacy: true);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/runs/run:native/trace");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"runId\":\"wrong\",\"scenarioId\":\"case\",\"events\":[]}")]
    [InlineData("{\"runId\":\"run:native\",\"scenarioId\":\"wrong\",\"events\":[]}")]
    [InlineData("{\"runId\":\"run:native\",\"scenarioId\":\"case\",\"events\":null}")]
    public async Task Trace_InvalidEnvelope_RefusesInsteadOfServingUnboundContent(string content)
    {
        ArtifactRun(content, legacy: true);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/runs/run:native/trace");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Theory]
    [InlineData(null, true, 200)]
    [InlineData("agent-trace-v1", false, 200)]
    [InlineData("future-trace", true, 404)]
    public async Task Trace_DeclaredFormatOrOldImport_SelectsOnlySupportedViewer(string? format, bool legacy, int expected)
    {
        const string content = "{\"runId\":\"run:native\",\"scenarioId\":\"case\",\"events\":[]}";
        ArtifactRun(content, legacy: legacy, traceFormat: format);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/runs/run:native/trace");
        Assert.Equal(expected, (int)response.StatusCode);
        if (expected == 200) Assert.Equal(content, await response.Content.ReadAsStringAsync());
        else Assert.Contains("trace_viewer_unsupported", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Report_BothMarkdownSpellings_SelectsExactCapturedFile()
    {
        var legacy = await Legacy();
        File.WriteAllText(Path.Combine(legacy.Directory, "reports", "report.markdown"), "long-spelling-report");
        Convert(legacy.Directory); var source = SourceForCaptured();
        using var factory = Factory(); using var client = factory.CreateClient();
        Assert.Equal("original-report", await client.GetStringAsync($"/api/v1/runs/{legacy.Id}/reports/md?sourceId={source}"));
        Assert.Equal("long-spelling-report", await client.GetStringAsync($"/api/v1/runs/{legacy.Id}/reports/markdown?sourceId={source}"));
    }

    [Fact]
    public async Task Report_UnfamiliarExtensionShape_DoesNotCrashOrInventAReport()
    {
        ArtifactRun("document", reportExt: new JsonObject { ["agenteval.store-v1"] = 42 });
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/runs/run:native/reports/md");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Artifacts_Query_ReportsCapturedAvailabilityWithoutLeakingExternalUri(bool external)
    {
        ArtifactRun("producer-specific transcript", external: external);
        var source = SourceForCaptured();
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/graphql", new { query = $$"""
            { runArtifacts(runId:"run:native",sourceId:"{{source}}") { evidenceId kind description captured reportFormat legacyTrace }
              missing: runArtifacts(runId:"run:native",sourceId:"unknown") { evidenceId } }
            """ });
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        var data = json.RootElement.GetProperty("data");
        var artifact = Assert.Single(data.GetProperty("runArtifacts").EnumerateArray());
        Assert.Equal("E-trace", artifact.GetProperty("evidenceId").GetString());
        Assert.Equal(!external, artifact.GetProperty("captured").GetBoolean());
        Assert.False(artifact.GetProperty("legacyTrace").GetBoolean());
        Assert.Equal(JsonValueKind.Null, artifact.GetProperty("reportFormat").ValueKind);
        Assert.Empty(data.GetProperty("missing").EnumerateArray());
        Assert.DoesNotContain("example.invalid", json.RootElement.ToString());
    }

    [Theory]
    [InlineData("trace", "original-content")]
    [InlineData("reports/md", "original-report")]
    public async Task Download_SelectedSource_ReturnsItsCapturedBytes(string suffix, string expected)
    {
        var legacy = await Legacy(); Convert(legacy.Directory);
        var source = SourceForCaptured();
        await legacy.Store.AppendTraceAsync(legacy.Id, new(legacy.Id, "case",
            [new(DateTimeOffset.UtcNow, "message", "agent", "later-content")]));
        File.WriteAllText(Path.Combine(legacy.Directory, "reports", "report.md"), "later-report");
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/v1/runs/{legacy.Id}/{suffix}?sourceId={source}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("later-", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("trace")]
    [InlineData("reports/md")]
    public async Task Download_CaptureOffNativeAlias_DoesNotRecoverLegacyContent(string suffix)
    {
        var legacy = await Legacy();
        var fixture = ComplianceRenderCommandTests.MakeFixtureEvidence("DownloadAgent");
        AefCompositeRunWriter.Write(Path.Combine(Workspace, "aef", "runs"), fixture.CompositeTree,
            AefCompositeRunWriter.Undescribed, new()
            {
                SubjectRef = "agent:DownloadAgent", TargetMode = AefTargetMode.Mocked, Lane = "gdpr", SuiteRef = "bench:gdpr",
                ContentCapture = AefContentCapture.Off, StoreV1 = new() { RunId = legacy.Id },
            }, fixture.Base.GeneratedAt.AddSeconds(-1), fixture.Base.GeneratedAt);
        var selected = Assert.Single(new AefWorkspaceEvidenceReader().Read(Workspace).Runs);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/v1/runs/{legacy.Id}/{suffix}?sourceId={selected.SourceId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        if (suffix == "trace") Assert.Contains("trace_not_captured", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("trace")]
    [InlineData("reports/md")]
    public async Task Download_UnknownSource_DoesNotFallBack(string suffix)
    {
        var legacy = await Legacy();
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/v1/runs/{legacy.Id}/{suffix}?sourceId=unknown");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        if (suffix == "trace") Assert.Contains("run_not_found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Download_AmbiguousId_RequiresSourceSelection()
    {
        var legacy = await Legacy(); Convert(legacy.Directory);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/v1/runs/{legacy.Id}/trace");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Download_TamperedSelectedSource_RefusesBeforeLegacyFallback()
    {
        var legacy = await Legacy(); var captured = Convert(legacy.Directory); var source = SourceForCaptured();
        File.AppendAllText(Path.Combine(captured, "run.json"), " ");
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/v1/runs/{legacy.Id}/trace?sourceId={source}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain("original-content", await response.Content.ReadAsStringAsync());
    }
}
#endif
