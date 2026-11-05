// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Cli.Microsoft365;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Results.Integrity;
using AgentEval.Tests.Microsoft365.Auth;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Cli.Microsoft365;

/// <summary>
/// M31: the AEF run of <c>eval --sut microsoft365</c>. Offline, through the real command and a REAL
/// <see cref="WorkIqEvaluableAgent"/> stand-in (built by <see cref="Microsoft365AgentFactory.BuildLive"/>; only the token
/// provider and the innermost HTTP handler are fakes): the run's config carries the provenance keys of HLD D15, with
/// content capture on each case carries its evidence document (schema id inside), with capture off it carries none,
/// and the in-process AEF verifier passes on the folder. The stand-in makes the run <c>mocked</c>, never <c>live</c>.
/// </summary>
[Collection("ConsoleTests")]
public sealed class Microsoft365AefProvenanceTests : IDisposable
{
    private const string FakeToken = "fake-token";
    private const string CaseId = "atlas-1";
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string AppClientId = "22222222-2222-2222-2222-222222222222";
    private const string CompletedFixture = "envelopes/send-task-completed-with-answer-and-attributions.json";
    private const string InputRequiredFixture = "envelopes/send-task-input-required-confirmation.json";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenteval-m365-aef-" + Guid.NewGuid().ToString("N"));

    public Microsoft365AefProvenanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // The run provenance and the per-case evidence document
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Eval_Microsoft365_ContentCaptureOn_RunCarriesTheProvenanceAndEachCaseItsEvidenceDocument()
    {
        var config = WriteConfig();
        var (agent, handler, _) = StandIn(config, CompletedFixture);
        await using (agent)
        {
            var opts = Options(config);

            var exit = await RunEvalAsync(contentCapture: true, opts, agent);

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Equal(1, handler.CountOf("SendMessage"));
            var dir = RunDirectory();
            AssertProvenance(dir, config, expectContentPolicy: "SelectedContent");
            AssertVerified(dir);

            var document = Assert.Single(DocumentTexts(dir));
            var node = JsonNode.Parse(document)!;
            Assert.Equal(Microsoft365Evidence.SchemaVersion, (string?)node["schemaVersion"]);
            Assert.Equal(CaseId, (string?)node["identity"]!["caseId"]);   // the turn identity carries the AEF case id
            Assert.Equal("TASK_STATE_COMPLETED", (string?)node["taskState"]);
            Assert.StartsWith("Project Atlas is on schedule", (string?)node["answer"]!["rawText"]!["value"], StringComparison.Ordinal);
            Assert.Equal(Microsoft365Evidence.SchemaVersion, Microsoft365EvidenceJson.Deserialize(document).SchemaVersion);
            AssertCaseReferencesTheDocument(dir);
            AssertNoTokenIn(dir);
        }
    }

    [Fact]
    public async Task Eval_Microsoft365_ContentCaptureOff_RunCarriesTheProvenanceButNoDocument()
    {
        var config = WriteConfig();
        var (agent, handler, sink) = StandIn(config, CompletedFixture);
        await using (agent)
        {
            var opts = Options(config);

            var exit = await RunEvalAsync(contentCapture: false, opts, agent);

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Equal(1, sink.Count);   // the turn ran and was observed: the absence below is the capture policy, not a dead sink
            var dir = RunDirectory();
            AssertProvenance(dir, config, expectContentPolicy: "SelectedContent");
            AssertVerified(dir);
            Assert.Empty(DocumentTexts(dir));
            Assert.False(Directory.Exists(Path.Combine(dir, "blobs")) && Directory.EnumerateFiles(Path.Combine(dir, "blobs"), "*", SearchOption.AllDirectories).Any());
            Assert.DoesNotContain("Project Atlas is on schedule", AllText(dir), StringComparison.Ordinal);
            AssertNoTokenIn(dir);
        }
    }

    [Fact]
    public async Task Eval_Microsoft365_InputRequiredTurn_ErrorRowStillCarriesTheInterruptedTurnsDocument()
    {
        var config = WriteConfig();
        var (agent, _, _) = StandIn(config, InputRequiredFixture);
        await using (agent)
        {
            var opts = Options(config);

            var exit = await RunEvalAsync(contentCapture: true, opts, agent);

            Assert.Equal(ExitCodes.GateIndeterminate, exit);   // an error row has no verdict; nothing failed
            var dir = RunDirectory();
            AssertVerified(dir);
            var node = JsonNode.Parse(Assert.Single(DocumentTexts(dir)))!;
            Assert.Equal("TASK_STATE_INPUT_REQUIRED", (string?)node["taskState"]);
            Assert.Equal("Provided", (string?)node["approvalIntents"]!["availability"]);
            // SelectedContent: the proposal is kept, its argument payload is not.
            Assert.Equal("Redacted", (string?)node["approvalIntents"]!["items"]![0]!["proposedArgumentsRawText"]!["state"]);
            Assert.DoesNotContain("Atlas pilot kickoff", AllText(dir), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Eval_Microsoft365_RunsThree_EachTrialCarriesItsOwnTurnsDocument()
    {
        var config = WriteConfig();
        var (agent, handler, _) = StandIn(config, CompletedFixture);
        await using (agent)
        {
            var opts = Options(config, runs: 3);

            var exit = await RunEvalAsync(contentCapture: true, opts, agent);

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Equal(3, handler.CountOf("SendMessage"));
            var dir = RunDirectory();
            AssertProvenance(dir, config, expectContentPolicy: "SelectedContent");
            AssertVerified(dir);
            var documents = DocumentTexts(dir).Select(d => JsonNode.Parse(d)!).ToArray();
            Assert.Equal(3, documents.Length);
            Assert.All(documents, d => Assert.Equal(CaseId, (string?)d["identity"]!["caseId"]));
            Assert.Equal(3, documents.Select(d => (string?)d["identity"]!["turnId"]).Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public async Task Eval_Microsoft365_StandInWithoutATurnSink_ContentCaptureOn_WritesNoDocumentRatherThanInventingOne()
    {
        var config = WriteConfig();
        var (agent, _, sink) = StandIn(config, CompletedFixture, attachSink: false);
        await using (agent)
        {
            var opts = Options(config);

            var exit = await RunEvalAsync(contentCapture: true, opts, agent);

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Equal(1, sink.Count);
            var dir = RunDirectory();
            AssertProvenance(dir, config, expectContentPolicy: "SelectedContent");
            AssertVerified(dir);
            Assert.Empty(DocumentTexts(dir));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // The target's provenance and sink seams
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void AefRunSettings_BeforeValidate_IsNull()
    {
        var target = new Microsoft365SutTarget(TextWriter.Null);

        Assert.Null(target.AefRunSettings(EvidenceContentPolicy.SelectedContent));
    }

    [Fact]
    public void AefRunSettings_AgentIdEndpointAndProfile_RecordedAsConfiguredWithoutTheTenantOrApp()
    {
        var config = WriteConfig(
            $$"""{ "tenantId": "{{TenantId}}", "appClientId": "{{AppClientId}}", "agentId": "T_contoso.sales-agent", "endpoint": "https://workiq.contoso.example/a2a/", "trustedHosts": ["workiq.contoso.example"] }""");
        var target = new Microsoft365SutTarget(TextWriter.Null);
        target.Validate(
            new CommonTargetOptions { Sut = Microsoft365SutTarget.SutName, AckLiveSideEffects = true },
            new Microsoft365SutOptions { ConfigFile = config, Profile = "native118" });

        var settings = target.AefRunSettings(contentPolicy: null)!;

        Assert.Equal("1.0", (string?)settings["a2aVersion"]);
        Assert.Equal("Native118Diagnostics", (string?)settings["m365Profile"]);
        Assert.Equal("T_contoso.sales-agent", (string?)settings["m365AgentId"]);
        Assert.Equal("workiq.contoso.example", (string?)settings["m365EndpointHost"]);
        Assert.Equal(Microsoft365Config.Load(config).ComputeIdentitySha256(), (string?)settings["m365ConfigSha256"]);
        Assert.False(settings.ContainsKey("m365ContentPolicy"));   // unknown policy: not claimed
        Assert.DoesNotContain(TenantId, settings.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(AppClientId, settings.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeIdentitySha256_IsTheConfigHashTheIssuesCarry()
    {
        var config = Microsoft365Config.Load(WriteConfig());
        var other = config with { AgentId = "T_contoso.other-agent" };

        var hash = config.ComputeIdentitySha256();

        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(WorkIqDiagnosticsFactory.ComputeConfigSha256(config), hash);
        Assert.Equal(hash, Microsoft365Config.Load(WriteConfig()).ComputeIdentitySha256());
        Assert.NotEqual(hash, other.ComputeIdentitySha256());
    }

    [Fact]
    public async Task CliTurnSink_LatestFor_ReturnsTheCasesTurnSinceTheCursorOnly()
    {
        var config = WriteConfig();
        var (agent, _, sink) = StandIn(config, CompletedFixture);
        await using (agent)
        {
            foreach (var id in new[] { "c1", "c1", "c2" })
            {
                agent.NextCaseId = id;
                await agent.InvokeAsync("Summarize the status of Project Atlas and cite the sources.");
            }

            var all = sink.All;
            Assert.Equal(3, all.Count);
            Assert.Same(all[1], sink.LatestFor("c1", since: 0));   // the later of the two c1 turns
            Assert.Same(all[1], sink.LatestFor("c1", since: 1));
            Assert.Null(sink.LatestFor("c1", since: 2));         // c1's turns are older than the cursor
            Assert.Same(all[2], sink.LatestFor(" c2 ", since: 0)); // compared trimmed, as the agent records it
            Assert.Null(sink.LatestFor("c3", since: 0));
        }
    }

    [Fact]
    public async Task SinkFor_StandIn_ReturnsTheAttachedSinkOrNull()
    {
        var config = WriteConfig();
        var (attached, _, sink) = StandIn(config, CompletedFixture);
        var (detached, _, _) = StandIn(config, CompletedFixture, attachSink: false);
        await using (attached)
        await using (detached)
        {
            var target = new Microsoft365SutTarget(TextWriter.Null);

            Assert.Same(sink, target.SinkFor(attached));
            Assert.Same(sink, CliTurnSink.AttachedTo(attached));
            Assert.Null(target.SinkFor(detached));
            Assert.Null(CliTurnSink.AttachedTo(detached));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Runs <c>eval</c> under the shared AEF controls with <c>--aef-content on|off</c>, the way the CLI invokes it.</summary>
    private static Task<int> RunEvalAsync(bool contentCapture, EvalOptions opts, WorkIqEvaluableAgent standIn)
    {
        var command = new Command("eval-m365-aef-test");
        command.Options.Add(new Option<DirectoryInfo?>("--aef"));
        var controls = new AefCliOptions(command);
        return controls.InvokeAsync(
            command.Parse(["--aef-content", contentCapture ? "on" : "off"]),
            () => EvalCommand.ExecuteAsync(opts, CancellationToken.None, sutOverride: standIn));
    }

    private (WorkIqEvaluableAgent Agent, FakeWorkIqHandler Handler, CliTurnSink Sink) StandIn(FileInfo config, string sendFixture, bool attachSink = true)
    {
        var handler = new FakeWorkIqHandler()
            .OnCard("cards/default-card-two-interfaces.json")
            .OnRpc("SendMessage", sendFixture);
        var sink = new CliTurnSink(verbose: false, TextWriter.Null);
        var agent = Microsoft365AgentFactory.BuildLive(
            Microsoft365Config.Load(config),
            iUnderstandLiveSideEffects: true,
            new WorkIqOptions
            {
                ConversationPolicy = ConversationPolicy.FreshContextPerInvocation,
                ProfileOverride = DiagnosticProfile.PublicAnswerOnly,
                ContentPolicy = EvidenceContentPolicy.SelectedContent,
                DiagnosticsDirectory = new DirectoryInfo(Path.Combine(_root, "m365-diagnostics", Guid.NewGuid().ToString("N"))),
                RunId = "run-aef-" + Guid.NewGuid().ToString("N")[..8],
            },
            tokenProvider: new StaticTokenProvider(FakeToken),
            onTurn: sink.Record,
            innerHandler: handler);
        if (attachSink)
        {
            sink.AttachTo(agent);
        }

        return (agent, handler, sink);
    }

    private EvalOptions Options(FileInfo config, int runs = 1)
    {
        var dataset = Path.Combine(_root, "dataset.yaml");
        File.WriteAllText(dataset, $"- id: {CaseId}\n  input: \"Summarize the status of Project Atlas and cite the sources.\"\n  expectedOutput: \"on schedule\"\n");
        return new EvalOptions
        {
            Dataset = new FileInfo(dataset),
            Sut = Microsoft365SutTarget.SutName,
            AckLiveSideEffects = true,
            TargetOptions = new Dictionary<string, ISutTargetOptions?>(StringComparer.OrdinalIgnoreCase)
            {
                [Microsoft365SutTarget.SutName] = new Microsoft365SutOptions { ConfigFile = config },
            },
            Format = "json",
            Output = new FileInfo(Path.Combine(_root, "report.json")),
            Aef = new DirectoryInfo(Path.Combine(_root, "aef")),
            SubjectVersion = "1",
            Quiet = true,
            Runs = runs,
        };
    }

    private FileInfo WriteConfig(string? json = null)
    {
        var path = Path.Combine(_root, "m365-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json ?? $$"""{ "tenantId": "{{TenantId}}", "appClientId": "{{AppClientId}}" }""");
        return new FileInfo(path);
    }

    private string RunDirectory() =>
        Path.GetDirectoryName(Assert.Single(Directory.GetFiles(Path.Combine(_root, "aef"), "run.json", SearchOption.AllDirectories)))!;

    private static JsonObject Document(string dir, string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(dir, name)))!.AsObject();

    private static void AssertProvenance(string dir, FileInfo config, string expectContentPolicy)
    {
        var run = Document(dir, "run.json");
        var settings = run["config"]!;
        Assert.Equal("1.0", (string?)settings["a2aVersion"]);
        Assert.Equal("PublicAnswerOnly", (string?)settings["m365Profile"]);
        Assert.Equal(Microsoft365Evidence.NormalizerVersion, (string?)settings["normalizerVersion"]);
        Assert.Equal(Microsoft365Evidence.ProviderProfileVersion, (string?)settings["providerProfileVersion"]);
        Assert.Equal(Microsoft365Config.Load(config).ComputeIdentitySha256(), (string?)settings["m365ConfigSha256"]);
        Assert.Matches("^[0-9a-f]{64}$", (string?)settings["m365ConfigSha256"]);
        Assert.Equal("default", (string?)settings["m365AgentId"]);
        Assert.Equal("workiq.svc.cloud.microsoft", (string?)settings["m365EndpointHost"]);
        Assert.Equal(expectContentPolicy, (string?)settings["m365ContentPolicy"]);
        Assert.Equal("mocked", (string?)run["execution"]!["targetMode"]);   // a stand-in is never recorded as live
        Assert.DoesNotContain(TenantId, run.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(AppClientId, run.ToJsonString(), StringComparison.Ordinal);
    }

    private static void AssertVerified(string dir)
    {
        var report = AefRunVerifier.Verify(dir);
        Assert.Empty(report.Problems);
        Assert.Equal(AefOutcome.Intact, report.Outcome);
    }

    private static IReadOnlyList<JsonNode> DocumentEvidence(string dir)
    {
        var path = Path.Combine(dir, "evidence.ndjson");
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!).Where(l => (string?)l["kind"] == "document").ToArray()
            : [];
    }

    private static IReadOnlyList<string> DocumentTexts(string dir) =>
        DocumentEvidence(dir).Select(line => BlobText(dir, (string)line["link"]!["blob"]!)).ToArray();

    private static string BlobText(string dir, string digest)
    {
        var hex = digest["sha256:".Length..];
        var path = Directory.GetFiles(Path.Combine(dir, "blobs"), "*", SearchOption.AllDirectories)
            .Single(p => string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))), hex, StringComparison.OrdinalIgnoreCase));
        return File.ReadAllText(path);
    }

    private static void AssertCaseReferencesTheDocument(string dir)
    {
        var id = (string)Assert.Single(DocumentEvidence(dir))["evidenceId"]!;
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToArray();
        var line = Assert.Single(lines, l => l["evidence"]?.AsArray().Any(e => (string?)e == id) == true);
        Assert.Equal(CaseId, (string?)line["caseId"]);
    }

    /// <summary>Every file under <paramref name="dir"/>, read with the share mode an open diagnostics sink allows.</summary>
    private static string AllText(string dir) =>
        string.Concat(Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));

    /// <summary>
    /// The token reaches no output: not the AEF run, not the report, not the provider's trace and issue files (all under
    /// the test root). The trace is checked to exist first, so the absence is not that of an unwritten file.
    /// </summary>
    private void AssertNoTokenIn(string runDirectory)
    {
        Assert.Contains("turn.outcome", AllText(Path.Combine(_root, "m365-diagnostics")), StringComparison.Ordinal);
        Assert.DoesNotContain(FakeToken, AllText(runDirectory), StringComparison.Ordinal);
        Assert.DoesNotContain(FakeToken, AllText(_root), StringComparison.Ordinal);
    }
}
