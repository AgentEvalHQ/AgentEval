// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using System.Net;
using AgentEval.MissionControl.GraphQL;
using AgentEval.Output;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// Locks down the MC1.3.2-6 binary REST endpoints. These can't be tested with the
/// shared <see cref="SeededMissionControlFactory"/> (which uses InMemoryOutputStore
/// — its <c>WorkspaceRoot</c> is null, so binary endpoints all 404). This fixture
/// uses a real <see cref="FileSystemOutputStore"/> against a per-test temp directory.
/// </summary>
public class BinaryEndpointTests : IClassFixture<FilesystemSeededFactory>, IDisposable
{
    private readonly FilesystemSeededFactory _factory;

    public BinaryEndpointTests(FilesystemSeededFactory factory)
    {
        _factory = factory;
    }

    public void Dispose() { /* fixture cleans up its temp dir on its own dispose */ }

    [Fact]
    public async Task Trace_UnknownRunId_Returns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/runs/unknown-run/trace");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Trace_KnownRunId_ReturnsTraceJson()
    {
        using var client = _factory.CreateClient();
        var runId = await _factory.GetSeededRunIdAsync();
        var response = await client.GetAsync($"/api/v1/runs/{runId}/trace");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("runId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reports_UnknownFormat_Returns400()
    {
        using var client = _factory.CreateClient();
        var runId = await _factory.GetSeededRunIdAsync();
        var response = await client.GetAsync($"/api/v1/runs/{runId}/reports/exotic-format");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reports_UnknownFormat_RejectionMessage_ListsAllAcceptedFormats()
    {
        // MNT-13: the rejection message is derived from the single allow-list source of truth, so it
        // includes 'md' and 'xml' (previously omitted) — and can never drift from IsAllowedReportFormat.
        using var client = _factory.CreateClient();
        var runId = await _factory.GetSeededRunIdAsync();

        var response = await client.GetAsync($"/api/v1/runs/{runId}/reports/exotic-format");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("md", body);
        Assert.Contains("xml", body);
        Assert.Contains("markdown", body);
    }

    [Fact]
    public async Task Reports_NoFileGenerated_Returns404()
    {
        // The seed writes `report.md` + `report.html`; everything else is 404.
        using var client = _factory.CreateClient();
        var runId = await _factory.GetSeededRunIdAsync();
        var response = await client.GetAsync($"/api/v1/runs/{runId}/reports/junit");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── Phase-5 Task 5.3 — Content-Disposition contract ─────────────────────

    [Fact]
    public async Task Reports_HtmlFormat_DownloadsAsAttachment()
    {
        // Browser-rendered formats (html / xml / sarif) carry user-controlled
        // content. The endpoint forces attachment disposition so the browser
        // never renders them as same-origin script against /graphql.
        using var client = _factory.CreateClient();
        var runId = await _factory.GetSeededRunIdAsync();
        var response = await client.GetAsync($"/api/v1/runs/{runId}/reports/html");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cd = response.Content.Headers.ContentDisposition;
        Assert.NotNull(cd);
        Assert.Equal("attachment", cd!.DispositionType);
        Assert.Equal("report.html", cd.FileName?.Trim('"'));
    }

    [Fact]
    public async Task Reports_MarkdownFormat_RendersInline()
    {
        // Markdown is plain text — safe to render inline. The benchmark pipeline
        // writes `report.md`, so the URL format token is `md`.
        using var client = _factory.CreateClient();
        var runId = await _factory.GetSeededRunIdAsync();
        var response = await client.GetAsync($"/api/v1/runs/{runId}/reports/md");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cd = response.Content.Headers.ContentDisposition;
        // Inline branch: the endpoint passes `fileDownloadName: null`, so no
        // Content-Disposition header is emitted by Results.File.
        Assert.True(cd is null || cd.DispositionType != "attachment",
            $"Markdown should NOT be forced to attachment; got '{cd?.DispositionType}'.");
    }

    [Fact]
    public async Task ComplianceSchema_Gdpr_ReturnsGdprWrapperSchema()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/compliance/gdpr/schema");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/schema+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        // Phase-8 T2.7 — must return the GDPR wrapper schema specifically.
        Assert.Contains("$schema", body, StringComparison.Ordinal);
        Assert.Contains("gdpr-evidence", body, StringComparison.Ordinal);
        Assert.Contains("gdprAttestation", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComplianceSchema_EuAiAct_ReturnsEuAiActWrapperSchema()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/compliance/eu-ai-act/schema");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/schema+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("$schema", body, StringComparison.Ordinal);
        Assert.Contains("eu-ai-act-evidence", body, StringComparison.Ordinal);
        Assert.Contains("euAiActAttestation", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComplianceSchema_UnknownRegulation_Returns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/compliance/totally-unknown-regulation/schema");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        // The 404 body explains the unknown regulation so an SPA can surface the error
        // rather than treating the response as a generic "not found".
        Assert.Contains("Unknown regulation", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task History_KnownSubject_ReturnsJsonl()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/subjects/agent/TravelAgent/history");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        // The history file may legitimately be empty if the seed didn't append history;
        // the contract is just "200 + ndjson content type".
        Assert.NotNull(body);
    }

    [Fact]
    public async Task Pdf_NotPresent_Returns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/compliance/gdpr/TravelAgent/2026-01-01_00-00-00/report.pdf");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── Path-traversal hardening (post-Wave-8 Opus review) ──────────────────

    [Theory]
    [InlineData("/api/v1/runs/..%2F..%2Fetc%2Fpasswd/trace")]
    [InlineData("/api/v1/runs/run.._dotdot/trace")]
    [InlineData("/api/v1/runs/has%5Cbackslash/trace")]
    [InlineData("/api/v1/runs/has%2Fslash/trace")]
    public async Task Trace_PathTraversal_Returns400(string url)
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(url);
        // Some traversal patterns get normalized by Kestrel before reaching us
        // (e.g. ..%2F.. → 404 because the route doesn't match) — the contract
        // is "must NOT 200 with content from outside the workspace".
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest ||
            response.StatusCode == HttpStatusCode.NotFound,
            $"Expected 400 or 404 for {url}, got {(int)response.StatusCode}.");
    }

    [Theory]
    [InlineData("/api/v1/runs/dummy/reports/..%2F..%2Fetc")]
    [InlineData("/api/v1/runs/dummy/reports/has%2Fslash")]
    public async Task Reports_PathTraversalInFormat_Returns400(string url)
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(url);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest ||
            response.StatusCode == HttpStatusCode.NotFound,
            $"Expected 400 or 404 for {url}, got {(int)response.StatusCode}.");
    }

    [Theory]
    [InlineData("/api/v1/compliance/..%2F..%2Fetc/TravelAgent/2026-01-01_00-00-00/report.pdf")]
    [InlineData("/api/v1/compliance/gdpr/TravelAgent/..%2F..%2Fetc/report.pdf")]
    [InlineData("/api/v1/compliance/has%5Cbackslash/schema")]
    public async Task Compliance_PathTraversal_Returns400(string url)
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(url);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task History_PathTraversalInName_Returns400()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/subjects/agent/..%2F..%2Fetc/history");
        // Route binding may normalize before our handler sees it; either way
        // we must not 200 with content.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}

public class FileSystemLayoutPathSafetyTests
{
    // Direct unit tests of the strict validator. Kestrel may normalize URLs
    // before they reach handlers — this nails down the validator independently.

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../etc")]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData("has..parent")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has\0null")]
    [InlineData("has\nnewline")]
    // Windows reserved device names — opening these on Windows targets a
    // hardware device, not a file. Match case-insensitively + with extensions.
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("CON.json")]
    [InlineData("PRN")]
    [InlineData("NUL.txt")]
    [InlineData("AUX")]
    [InlineData("COM1")]
    [InlineData("LPT9.log")]
    // Trailing dot / space silently strip on NTFS — "foo." opens "foo"
    // creating a write-vs-read alias.
    [InlineData("foo.")]
    [InlineData("foo ")]
    [InlineData("foo.txt.")]
    // Windows-only invalid chars — harden for cross-platform shared workspaces.
    [InlineData("a:b")]
    [InlineData("a|b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a<b>c")]
    [InlineData("a\"b")]
    // Unicode slash lookalikes.
    [InlineData("foo／bar")]   // U+FF0F fullwidth solidus
    [InlineData("foo∕bar")]   // U+2215 division slash
    // Lesser-known reserved device names (post-Opus-deep-review).
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData("CLOCK$")]
    [InlineData("CONIN$.json")]
    // Reserved name with trailing space inside the stem ("CON .json"
    // strips on NTFS to "CON.json" → CON device).
    [InlineData("CON .json")]
    [InlineData("CON.")]
    // Bidirectional / formatting controls — homograph attack vectors.
    [InlineData("foo‎bar")]    // U+200E LTR mark
    [InlineData("foo‮bar")]    // U+202E RTL override
    [InlineData("foo​bar")]    // U+200B zero-width space
    // Length cap (256 chars).
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]  // DevSkim: ignore DS173237
    public void IsSafePathSegment_RejectsHostileInput(string input)
    {
        Assert.False(FileSystemLayout.IsSafePathSegment(input));
    }

    [Theory]
    [InlineData("2026-05-09_14-30-22")]
    [InlineData("a8c3f72e_run-id")]
    [InlineData("gdpr")]
    [InlineData("eu-ai-act")]
    [InlineData("markdown")]
    [InlineData("html")]
    [InlineData("v1.0")]
    [InlineData("TravelAgent")]
    public void IsSafePathSegment_AcceptsLegitimateInput(string input)
    {
        Assert.True(FileSystemLayout.IsSafePathSegment(input));
    }

    [Fact]
    public void IsSafePathSegment_NullIsRejected()
    {
        Assert.False(FileSystemLayout.IsSafePathSegment(null));
    }
}

/// <summary>
/// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> backed by a real
/// <see cref="FileSystemOutputStore"/> in a per-fixture temp directory. Binary
/// endpoints need <see cref="IOutputStoreReader.WorkspaceRoot"/> to be non-null,
/// which the InMemoryOutputStore can't provide.
/// </summary>
public sealed class FilesystemSeededFactory : WebApplicationFactory<Query>, IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemOutputStore _store;
    private readonly string _runId;

    public FilesystemSeededFactory()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "agenteval-mc-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        _store = new FileSystemOutputStore(Path.Combine(_tempDir, ".agenteval"));
        _runId = SeedAsync(_store).GetAwaiter().GetResult();
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOutputStoreReader>();
            services.AddSingleton<IOutputStoreReader>(_store);
        });
    }

    private static async Task<string> SeedAsync(FileSystemOutputStore store)
    {
        // Initialise the solution.json so IsAvailable becomes true.
        var solutionFile = Path.Combine(store.WorkspaceRoot!, "solution.json");
        Directory.CreateDirectory(Path.GetDirectoryName(solutionFile)!);
        await File.WriteAllTextAsync(solutionFile,
            """{"schemaVersion":"1.0","id":"00000000-0000-0000-0000-000000000001","name":"binary-test"}""");

        var agent = new SubjectIdentity(SubjectKind.Agent, "TravelAgent");
        await store.EnsureSubjectAsync(agent);

        var manifest = await store.StartRunAsync(agent, new RunContext(
            EvalProject: "TestProject",
            EvalProjectPath: "samples/Test",
            Harness: "xunit",
            Seed: 1,
            ParentInvocationId: null,
            Kind: "eval"));

        await store.AppendTraceAsync(manifest.Run.RunId, new AgentTrace(
            RunId: manifest.Run.RunId,
            ScenarioId: "smoke",
            Events: new[]
            {
                new TraceEvent(DateTimeOffset.UtcNow, "tool.call", "search", "{}"),
            }));

        await store.CompleteRunAsync(manifest, new RunSummary(
            SchemaVersion: "1.0",
            RunId: manifest.Run.RunId,
            Verdict: "PASS",
            Stats: new RunStats(Total: 1, Passed: 1, Failed: 0, Warnings: 0),
            Metrics: new Dictionary<string, double>()));

        // Phase-5 Task 5.3 — seed report.md + report.html so the Content-Disposition
        // contract (markdown inline / html attachment) can be regression-tested.
        var layout = new FileSystemLayout(store.WorkspaceRoot!);
        var reportsDir = layout.ReportsDir(agent, manifest.Run.RunId);
        Directory.CreateDirectory(reportsDir);
        await File.WriteAllTextAsync(Path.Combine(reportsDir, "report.md"),
            "# Test report\n\nSeeded for Content-Disposition regression.");
        await File.WriteAllTextAsync(Path.Combine(reportsDir, "report.html"),
            "<!doctype html><title>seed</title><h1>seeded for attachment test</h1>");

        return manifest.Run.RunId;
    }

    public Task<string> GetSeededRunIdAsync() => Task.FromResult(_runId);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}

#endif
