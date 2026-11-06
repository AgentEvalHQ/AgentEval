// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net.ServerSentEvents;
using System.Reflection;
using System.Text;
using System.Text.Json;
using A2A;
using Xunit;

namespace AgentEval.Tests.Microsoft365;

/// <summary>
/// Catalog guard for the synthetic Work IQ A2A 1.0 fixture corpus embedded from <c>Microsoft365/Fixtures</c>
/// It proves what the provider's tests rely on: the embedded set equals the catalog in the folder README,
/// every file has the structural shape its consumer expects (JSON-RPC 2.0 envelopes, SSE framing the SDK's own
/// parser accepts, seven unique normalisation ids), the 1.0 envelopes round-trip through the A2A SDK's
/// <see cref="A2AJsonUtilities.DefaultOptions"/>, and the deliberately 0.3-shaped and malformed fixtures do NOT read
/// as a completed 1.0 task. All data is synthetic; see the README in the fixture folder.
/// </summary>
public sealed class FixtureCatalogTests
{
    private const string Marker = ".Microsoft365.Fixtures.";
    private const string InteractionMediaType = "application/vnd.ms-workiq.interaction+json";
    private const string DeveloperInfoMediaType = "application/vnd.ms-workiq.developer-info";
    private const string RetrievalInfoMediaType = "application/vnd.ms-workiq-internal.retrieval";
    private const string WorkIqEndpoint = "https://workiq.svc.cloud.microsoft/a2a/";
    private const string MalformedStream = "sse.stream-malformed-event.txt";
    private const string CrlfStream = "sse.stream-crlf.txt";
    private const char MarkerStart = '';
    private const char MarkerEnd = '';
    private const char MarkerSeparator = '';

    private static readonly Assembly TestAssembly = typeof(FixtureCatalogTests).Assembly;
    private static readonly string[] StreamResponseFields = ["task", "message", "statusUpdate", "artifactUpdate"];

    /// <summary>
    /// Every fixture the folder README lists, as the manifest-relative name (folder and file joined by a dot).
    /// Adding a file without adding it here, or the reverse, fails <see cref="Manifest_EmbedsExactlyTheCatalogedFixtures"/>.
    /// </summary>
    private static readonly string[] ExpectedFixtures =
    [
        "normalization-fixtures.json",
        "envelopes.send-task-completed-with-answer-and-attributions.json",
        "envelopes.send-direct-message.json",
        "envelopes.send-task-submitted.json",
        "envelopes.gettask-completed.json",
        "envelopes.gettask-working.json",
        "envelopes.send-task-failed-with-partial-artifact.json",
        "envelopes.send-task-input-required-confirmation.json",
        "envelopes.send-task-auth-required.json",
        "envelopes.send-task-rejected.json",
        "envelopes.send-task-canceled.json",
        "envelopes.send-task-completed-with-developer-and-retrieval-info.json",
        "envelopes.send-task-completed-two-text-artifacts-ambiguous.json",
        "envelopes.send-task-completed-empty-answer.json",
        "envelopes.send-task-completed-with-file-parts.json",
        "envelopes.rpc-error-on-http-200-version-not-supported.json",
        "envelopes.rpc-error-on-http-200-method-not-found.json",
        "envelopes.http-429-body.json",
        "envelopes.http-429-headers.txt",
        "envelopes.v03-shaped-response.json",
        "envelopes.canceltask-completed-race.json",
        "envelopes.canceltask-not-found-error.json",
        "envelopes.canceltask-not-cancelable-error.json",
        "sse.stream-happy-path.txt",
        "sse.stream-replace-then-append.txt",
        "sse.stream-two-artifacts-interleaved.txt",
        "sse.stream-lastchunk-before-terminal-then-eof.txt",
        "sse.stream-eof-before-terminal.txt",
        "sse.stream-duplicate-frame.txt",
        "sse.stream-multiline-data.txt",
        "sse.stream-crlf.txt",
        "sse.stream-with-comments-and-heartbeats.txt",
        "sse.stream-with-id-and-event-fields.txt",
        "sse.stream-malformed-event.txt",
        "sse.stream-direct-message-only.txt",
        "sse.stream-failed-after-partial.txt",
        "sse.stream-input-required.txt",
        "sse.subscribe-first-frame-snapshot.txt",
        "sse.stream-rpc-error-frame.txt",
        "cards.default-card-two-interfaces.json",
        "cards.agent-specific-card.json",
        "cards.card-untrusted-host.json",
        "cards.card-v03-only.json",
    ];

    // ---------------------------------------------------------------------------------------------------------
    // Theory sources
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Every <c>.json</c> fixture.</summary>
    public static TheoryData<string> JsonFixtures => Catalog(n => n.EndsWith(".json", StringComparison.Ordinal));

    /// <summary>Every JSON-RPC response body under <c>envelopes/</c> (the raw HTTP 429 body is not one).</summary>
    public static TheoryData<string> Envelopes => Catalog(n =>
        n.StartsWith("envelopes.", StringComparison.Ordinal)
        && n.EndsWith(".json", StringComparison.Ordinal)
        && !n.StartsWith("envelopes.http-", StringComparison.Ordinal));

    /// <summary>Every raw SSE stream under <c>sse/</c>.</summary>
    public static TheoryData<string> SseStreams => Catalog(n => n.StartsWith("sse.", StringComparison.Ordinal));

    /// <summary>Every SSE stream whose frames are valid one-of StreamResponses.</summary>
    public static TheoryData<string> WellFormedSseStreams => Catalog(n =>
        n.StartsWith("sse.", StringComparison.Ordinal) && n != MalformedStream);

    /// <summary>Every SSE stream that must use LF line endings only.</summary>
    public static TheoryData<string> LfSseStreams => Catalog(n =>
        n.StartsWith("sse.", StringComparison.Ordinal) && n != CrlfStream);

    /// <summary>Every agent card under <c>cards/</c>.</summary>
    public static TheoryData<string> Cards => Catalog(n => n.StartsWith("cards.", StringComparison.Ordinal));

    /// <summary><c>SendMessage</c> envelopes whose result is a task, with the state each one models.</summary>
    public static TheoryData<string, TaskState> SendTaskEnvelopes => new()
    {
        { "envelopes.send-task-completed-with-answer-and-attributions.json", TaskState.Completed },
        { "envelopes.send-task-submitted.json", TaskState.Submitted },
        { "envelopes.send-task-failed-with-partial-artifact.json", TaskState.Failed },
        { "envelopes.send-task-input-required-confirmation.json", TaskState.InputRequired },
        { "envelopes.send-task-auth-required.json", TaskState.AuthRequired },
        { "envelopes.send-task-rejected.json", TaskState.Rejected },
        { "envelopes.send-task-canceled.json", TaskState.Canceled },
        { "envelopes.send-task-completed-with-developer-and-retrieval-info.json", TaskState.Completed },
        { "envelopes.send-task-completed-two-text-artifacts-ambiguous.json", TaskState.Completed },
        { "envelopes.send-task-completed-empty-answer.json", TaskState.Completed },
        { "envelopes.send-task-completed-with-file-parts.json", TaskState.Completed },
    };

    /// <summary><c>GetTask</c> / <c>CancelTask</c> envelopes: the task sits directly under <c>result</c>.</summary>
    public static TheoryData<string, TaskState> TaskUnderResultEnvelopes => new()
    {
        { "envelopes.gettask-completed.json", TaskState.Completed },
        { "envelopes.gettask-working.json", TaskState.Working },
        { "envelopes.canceltask-completed-race.json", TaskState.Completed },
    };

    // ---------------------------------------------------------------------------------------------------------
    // Catalog and structural checks
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Manifest_EmbedsExactlyTheCatalogedFixtures()
    {
        var embedded = TestAssembly.GetManifestResourceNames()
            .Where(n => n.Contains(Marker, StringComparison.Ordinal))
            .Select(n => n[(n.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..])
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedFixtures.Order(StringComparer.Ordinal).ToArray(), embedded);
    }

    [Theory]
    [MemberData(nameof(JsonFixtures))]
    public void JsonFixture_Always_ParsesAsStrictUtf8JsonWithoutBom(string name)
    {
        var bytes = ReadBytes(name);

        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], $"{name} starts with a UTF-8 BOM");
        Assert.DoesNotContain((byte)'\r', bytes);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Theory]
    [MemberData(nameof(Envelopes))]
    public void Envelope_Always_IsJsonRpc20WithExactlyOneOfResultOrError(string name)
    {
        using var document = ReadJson(name);
        var root = document.RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.True(root.TryGetProperty("id", out _), $"{name} has no JSON-RPC id");
        var hasResult = root.TryGetProperty("result", out _);
        var hasError = root.TryGetProperty("error", out _);
        Assert.True(hasResult ^ hasError, $"{name} must carry exactly one of result/error");
    }

    [Fact]
    public void Http429Fixture_Always_IsAJsonBodyWithRetryAfterHeader()
    {
        using var body = ReadJson("envelopes.http-429-body.json");
        Assert.False(body.RootElement.TryGetProperty("jsonrpc", out _), "the 429 body is an HTTP error document, not a JSON-RPC envelope");
        Assert.Equal("TooManyRequests", body.RootElement.GetProperty("error").GetProperty("code").GetString());

        var headerLines = ReadText("envelopes.http-429-headers.txt")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToArray();
        Assert.StartsWith("HTTP/1.1 429", headerLines[0], StringComparison.Ordinal);
        Assert.Contains("Retry-After: 7", headerLines);
    }

    [Theory]
    [MemberData(nameof(SseStreams))]
    public void SseFixture_Always_HasAtLeastOneDataLine(string name)
    {
        var lines = ReadText(name).Split('\n').Select(l => l.TrimEnd('\r'));

        Assert.Contains(lines, l => l.StartsWith("data:", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(SseStreams))]
    public void SseFixture_Always_FramesAsJsonRpcEnvelopesUnderTheSdkSseParser(string name)
    {
        var frames = ReadSse(name);

        Assert.NotEmpty(frames);
        foreach (var frame in frames)
        {
            using var envelope = JsonDocument.Parse(frame.Data);
            var root = envelope.RootElement;
            Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
            var hasResult = root.TryGetProperty("result", out _);
            var hasError = root.TryGetProperty("error", out _);
            Assert.True(hasResult ^ hasError, $"{name}: a frame must carry exactly one of result/error");
        }
    }

    [Theory]
    [MemberData(nameof(WellFormedSseStreams))]
    public void WellFormedSseFixture_Always_HasExactlyOneStreamResponseFieldPerResultFrame(string name)
    {
        foreach (var frame in ReadSse(name))
        {
            using var envelope = JsonDocument.Parse(frame.Data);
            if (!envelope.RootElement.TryGetProperty("result", out var result))
            {
                continue;
            }

            Assert.Equal(1, PopulatedStreamFields(result));
        }
    }

    [Fact]
    public void MalformedSseFixture_Always_HasAFrameWithTwoStreamResponseFieldsAndStillCompletes()
    {
        var counts = ReadSse(MalformedStream).Select(frame =>
        {
            using var envelope = JsonDocument.Parse(frame.Data);
            return PopulatedStreamFields(envelope.RootElement.GetProperty("result"));
        }).ToArray();

        Assert.Contains(2, counts);
        Assert.DoesNotContain(0, counts);

        // The stream still ends in a completed status: a parser that silently skips the bad frame would pass.
        using var last = JsonDocument.Parse(ReadSse(MalformedStream)[^1].Data);
        var state = last.RootElement.GetProperty("result").GetProperty("statusUpdate").GetProperty("status").GetProperty("state").GetString();
        Assert.Equal("TASK_STATE_COMPLETED", state);
    }

    [Fact]
    public void CrlfSseFixture_Always_UsesCrlfLineEndingsOnly()
    {
        var bytes = ReadBytes(CrlfStream);
        var lineFeeds = bytes.Count(b => b == (byte)'\n');
        var crlfs = 0;
        for (var i = 1; i < bytes.Length; i++)
        {
            if (bytes[i - 1] == (byte)'\r' && bytes[i] == (byte)'\n')
            {
                crlfs++;
            }
        }

        Assert.True(lineFeeds > 0, "the CRLF stream is empty");
        Assert.Equal(lineFeeds, crlfs);
    }

    [Theory]
    [MemberData(nameof(LfSseStreams))]
    public void LfSseFixture_Always_ContainsNoCarriageReturn(string name)
    {
        Assert.DoesNotContain((byte)'\r', ReadBytes(name));
    }

    [Fact]
    public void IdAndEventSseFixture_Always_ExposesIdsAndRetryThroughTheSdkSseParser()
    {
        var frames = ReadSse("sse.stream-with-id-and-event-fields.txt");

        Assert.Equal(4, frames.Count);
        Assert.All(frames, f => Assert.StartsWith("evt-0110-", f.EventId, StringComparison.Ordinal));
        Assert.All(frames, f => Assert.Equal("message", f.EventType));
        Assert.Equal(TimeSpan.FromSeconds(3), frames[0].ReconnectionInterval);
    }

    [Fact]
    public void NormalizationFixtures_Always_HaveSevenUniqueIds()
    {
        using var document = ReadJson("normalization-fixtures.json");
        var root = document.RootElement;

        Assert.True(root.GetProperty("synthetic").GetBoolean());
        Assert.Equal("agenteval.microsoft365.normalization-fixture-catalog/v1", root.GetProperty("schema").GetString());
        var ids = root.GetProperty("fixtures").EnumerateArray().Select(f => f.GetProperty("id").GetString()).ToArray();
        Assert.Equal(7, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
    }

    // ---------------------------------------------------------------------------------------------------------
    // SDK round-trips (A2A 1.0.0-preview2 DTOs with the SDK's own serializer options)
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(SendTaskEnvelopes))]
    public void SendTaskEnvelope_Always_DeserializesAsSdkSendMessageResponseWithExpectedState(string name, TaskState expected)
    {
        using var document = ReadJson(name);

        var response = Deserialize<SendMessageResponse>(document.RootElement.GetProperty("result"));

        var task = Assert.IsType<AgentTask>(response.Task);
        Assert.Null(response.Message);
        Assert.Equal(expected, task.Status.State);
        Assert.False(string.IsNullOrEmpty(task.Id));
        Assert.False(string.IsNullOrEmpty(task.ContextId));
    }

    [Fact]
    public void DirectMessageEnvelope_Always_DeserializesAsSdkMessageWithAttributions()
    {
        using var document = ReadJson("envelopes.send-direct-message.json");

        var response = Deserialize<SendMessageResponse>(document.RootElement.GetProperty("result"));

        Assert.Null(response.Task);
        var message = Assert.IsType<Message>(response.Message);
        Assert.Equal(Role.Agent, message.Role);
        var part = Assert.Single(message.Parts);
        Assert.Contains(MarkerStart.ToString(), part.Text!, StringComparison.Ordinal);
        var metadata = Assert.IsType<Dictionary<string, JsonElement>>(message.Metadata);
        Assert.Single(metadata["attributions"].EnumerateArray());
    }

    [Theory]
    [MemberData(nameof(TaskUnderResultEnvelopes))]
    public void TaskUnderResultEnvelope_Always_DeserializesAsSdkAgentTask(string name, TaskState expected)
    {
        using var document = ReadJson(name);

        var task = Deserialize<AgentTask>(document.RootElement.GetProperty("result"));

        Assert.Equal(expected, task.Status.State);
        Assert.False(string.IsNullOrEmpty(task.Id));
    }

    [Fact]
    public void GetTaskEnvelopes_Always_ShareIdsWithTheSubmittedTask()
    {
        using var submitted = ReadJson("envelopes.send-task-submitted.json");
        using var completed = ReadJson("envelopes.gettask-completed.json");
        using var working = ReadJson("envelopes.gettask-working.json");

        var submittedTask = Deserialize<AgentTask>(submitted.RootElement.GetProperty("result").GetProperty("task"));
        var completedTask = Deserialize<AgentTask>(completed.RootElement.GetProperty("result"));
        var workingTask = Deserialize<AgentTask>(working.RootElement.GetProperty("result"));

        Assert.Equal(TaskState.Submitted, submittedTask.Status.State);
        Assert.Null(submittedTask.Artifacts);
        Assert.Equal(submittedTask.Id, completedTask.Id);
        Assert.Equal(submittedTask.ContextId, completedTask.ContextId);
        Assert.Equal(submittedTask.Id, workingTask.Id);
        Assert.Equal(submittedTask.ContextId, workingTask.ContextId);
        var completedArtifacts = Assert.IsType<List<Artifact>>(completedTask.Artifacts);
        Assert.Single(completedArtifacts);
    }

    [Theory]
    [MemberData(nameof(WellFormedSseStreams))]
    public void WellFormedSseFixture_Always_DeserializesEveryResultFrameAsSdkStreamResponse(string name)
    {
        var taskIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var frame in ReadSse(name))
        {
            using var envelope = JsonDocument.Parse(frame.Data);
            if (!envelope.RootElement.TryGetProperty("result", out var result))
            {
                continue;
            }

            var response = Deserialize<StreamResponse>(result);
            var populated = new object?[] { response.Task, response.Message, response.StatusUpdate, response.ArtifactUpdate }
                .Count(x => x is not null);
            Assert.Equal(1, populated);

            var taskId = response.Task?.Id ?? response.StatusUpdate?.TaskId ?? response.ArtifactUpdate?.TaskId;
            if (taskId is not null)
            {
                taskIds.Add(taskId);
            }
        }

        Assert.True(taskIds.Count <= 1, $"{name} mixes task ids: {string.Join(", ", taskIds)}");
    }

    [Fact]
    public void V03ShapedResponse_Always_IsRejectedAsA10Task()
    {
        using var document = ReadJson("envelopes.v03-shaped-response.json");
        var result = document.RootElement.GetProperty("result");

        // Structural proof that this is the 0.3 dialect: discriminators and lowercase enums.
        Assert.Equal("task", result.GetProperty("kind").GetString());
        Assert.Equal("completed", result.GetProperty("status").GetProperty("state").GetString());

        // As a 1.0 SendMessage result it is neither a task nor a message (0.3 put the task directly under result).
        var asSend = Deserialize<SendMessageResponse>(result);
        Assert.Null(asSend.Task);
        Assert.Null(asSend.Message);

        // As a 1.0 task it does not parse at all: "completed" is not a TASK_STATE_* value.
        Assert.ThrowsAny<JsonException>(() => Deserialize<AgentTask>(result));
    }

    [Fact]
    public void MalformedSseFrame_Always_IsNotAValidOneOfAndNotACompletedTask()
    {
        var frames = ReadSse(MalformedStream);
        using var envelope = JsonDocument.Parse(frames[1].Data);

        var response = Deserialize<StreamResponse>(envelope.RootElement.GetProperty("result"));

        var task = Assert.IsType<AgentTask>(response.Task);
        Assert.NotNull(response.Message);
        Assert.NotEqual(TaskState.Completed, task.Status.State);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Content the normaliser and the decoders depend on
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void CompletedEnvelope_Always_CarriesLiteralCitationMarkersAndTwoAttributions()
    {
        using var document = ReadJson("envelopes.send-task-completed-with-answer-and-attributions.json");
        var task = document.RootElement.GetProperty("result").GetProperty("task");

        var artifact = Assert.Single(task.GetProperty("artifacts").EnumerateArray());
        Assert.Equal("Answer", artifact.GetProperty("name").GetString());
        var parts = artifact.GetProperty("parts").EnumerateArray().ToArray();
        Assert.Equal(2, parts.Length);
        var text = string.Concat(parts.Select(p => p.GetProperty("text").GetString()));
        Assert.Contains($"{MarkerStart}cite{MarkerSeparator}turn0search1{MarkerEnd}", text, StringComparison.Ordinal);
        Assert.Contains($"{MarkerStart}cite{MarkerSeparator}turn0search1{MarkerSeparator}turn0search2{MarkerEnd}", text, StringComparison.Ordinal);

        var statusMessage = task.GetProperty("status").GetProperty("message");
        Assert.Equal("ROLE_AGENT", statusMessage.GetProperty("role").GetString());
        Assert.Empty(statusMessage.GetProperty("parts").EnumerateArray());
        var attributions = statusMessage.GetProperty("metadata").GetProperty("attributions").EnumerateArray().ToArray();
        Assert.Equal(2, attributions.Length);
        Assert.All(attributions, a =>
        {
            Assert.Equal("citation", a.GetProperty("attributionType").GetString());
            Assert.False(string.IsNullOrEmpty(a.GetProperty("attributionSource").GetString()));
            Assert.False(string.IsNullOrEmpty(a.GetProperty("providerDisplayName").GetString()));
            Assert.StartsWith("https://", a.GetProperty("seeMoreWebUrl").GetString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void InputRequiredEnvelope_Always_CarriesAJsonEncodedProposedCallAwaitingConsent()
    {
        using var document = ReadJson("envelopes.send-task-input-required-confirmation.json");
        var message = document.RootElement.GetProperty("result").GetProperty("task").GetProperty("status").GetProperty("message");

        var dataPart = Assert.Single(message.GetProperty("parts").EnumerateArray(), p => p.TryGetProperty("data", out _));
        Assert.Equal(InteractionMediaType, dataPart.GetProperty("mediaType").GetString());
        var interaction = dataPart.GetProperty("data").GetProperty("input-required");
        Assert.Equal("confirmation_required", interaction.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(interaction.GetProperty("sourceRequestId").GetString()));

        var actions = interaction.GetProperty("adaptiveCard").GetProperty("actions").EnumerateArray().ToArray();
        Assert.Equal(2, actions.Length);
        var allow = actions[0].GetProperty("data").GetProperty("message");
        var action = allow.GetProperty("action");
        Assert.Equal(JsonValueKind.String, action.ValueKind);
        Assert.Equal(action.GetString(), allow.GetProperty("actionId").GetString());

        using var calls = JsonDocument.Parse(action.GetString()!);
        var call = Assert.Single(calls.RootElement.EnumerateArray());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(call.GetProperty("id").GetString()));
        var function = call.GetProperty("function");
        Assert.Equal("CreateDemoEvent", function.GetProperty("name").GetString());
        var arguments = function.GetProperty("arguments");
        Assert.Equal(JsonValueKind.String, arguments.ValueKind);
        using var parsedArguments = JsonDocument.Parse(arguments.GetString()!);
        Assert.Equal("Atlas pilot kickoff", parsedArguments.RootElement.GetProperty("subject").GetString());
    }

    [Fact]
    public void DiagnosticsEnvelope_Always_CarriesDeveloperAndRetrievalInfoArtifactsBesideTheAnswer()
    {
        using var document = ReadJson("envelopes.send-task-completed-with-developer-and-retrieval-info.json");

        var task = Deserialize<AgentTask>(document.RootElement.GetProperty("result").GetProperty("task"));

        var artifacts = Assert.IsType<List<Artifact>>(task.Artifacts);
        string?[] expectedNames = ["Answer", "Developer Info", "Retrieval Info"];
        Assert.Equal(expectedNames, artifacts.Select(a => a.Name).ToArray());

        var developerInfo = Assert.Single(artifacts.Single(a => a.Name == "Developer Info").Parts);
        Assert.Equal(DeveloperInfoMediaType, developerInfo.MediaType);
        Assert.NotNull(developerInfo.Data);
        var plugins = developerInfo.Data.Value.GetProperty("plugins");
        Assert.Equal(2, plugins.GetProperty("selectedFunctions").GetArrayLength());
        var execution = Assert.Single(plugins.GetProperty("executions").EnumerateArray());
        Assert.Equal(200, execution.GetProperty("executionStatus").GetProperty("responseStatus").GetInt32());

        var retrievalInfo = Assert.Single(artifacts.Single(a => a.Name == "Retrieval Info").Parts);
        Assert.Equal(RetrievalInfoMediaType, retrievalInfo.MediaType);
        Assert.NotNull(retrievalInfo.Data);
        var retrieval = Assert.Single(retrievalInfo.Data.Value.GetProperty("executions").EnumerateArray());
        var debugInfo = retrieval.GetProperty("debugInfo");
        Assert.Equal("Project Atlas status report", debugInfo.GetProperty("queryString").GetString());
        Assert.Equal(5, debugInfo.GetProperty("maximumNumberOfResults").GetInt32());
        Assert.Equal(42, debugInfo.GetProperty("totalResultCount").GetInt32());
        Assert.Equal(318, debugInfo.GetProperty("latencyMs").GetInt32());
        var hits = debugInfo.GetProperty("retrievalHits").EnumerateArray().ToArray();
        Assert.Equal(2, hits.Length);
        Assert.All(hits, h =>
        {
            Assert.True(h.GetProperty("rank").GetInt32() > 0);
            Assert.StartsWith("https://", h.GetProperty("webUrl").GetString(), StringComparison.Ordinal);
            Assert.NotEmpty(h.GetProperty("extracts").EnumerateArray());
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // Agent cards
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cards))]
    public void Card_Always_DeserializesAsSdkAgentCard(string name)
    {
        using var document = ReadJson(name);

        var card = Deserialize<AgentCard>(document.RootElement);

        Assert.False(string.IsNullOrEmpty(card.Name));
        Assert.NotEmpty(card.SupportedInterfaces);
        Assert.NotEmpty(card.Skills);
        Assert.All(card.SupportedInterfaces, i => Assert.True(Uri.TryCreate(i.Url, UriKind.Absolute, out _), $"{name}: '{i.Url}' is not absolute"));
    }

    [Fact]
    public void DefaultCard_Always_AdvertisesJsonRpc10AtWorkIqAndANonJsonRpcInterface()
    {
        using var document = ReadJson("cards.default-card-two-interfaces.json");

        var card = Deserialize<AgentCard>(document.RootElement);

        Assert.Equal(2, card.SupportedInterfaces.Count);
        var jsonRpc = Assert.Single(card.SupportedInterfaces, i => i.ProtocolBinding == ProtocolBindingNames.JsonRpc);
        Assert.Equal("1.0", jsonRpc.ProtocolVersion);
        Assert.Equal(WorkIqEndpoint, jsonRpc.Url);
        var other = Assert.Single(card.SupportedInterfaces, i => i.ProtocolBinding != ProtocolBindingNames.JsonRpc);
        Assert.Equal("1.0", other.ProtocolVersion);
        Assert.True(card.Capabilities.Streaming);
        Assert.False(card.Capabilities.PushNotifications);
        Assert.True(card.Skills.Count >= 2);
    }

    [Fact]
    public void AgentSpecificCard_Always_KeepsTheOpaqueAgentIdInThePath()
    {
        using var document = ReadJson("cards.agent-specific-card.json");

        var card = Deserialize<AgentCard>(document.RootElement);

        var jsonRpc = Assert.Single(card.SupportedInterfaces);
        Assert.Equal(ProtocolBindingNames.JsonRpc, jsonRpc.ProtocolBinding);
        Assert.Equal("1.0", jsonRpc.ProtocolVersion);
        Assert.Contains("/a2a/U_abc123.def456/", jsonRpc.Url, StringComparison.Ordinal);
        Assert.Equal("workiq.svc.cloud.microsoft", new Uri(jsonRpc.Url).Host);
        Assert.False(string.IsNullOrEmpty(jsonRpc.Tenant));
    }

    [Fact]
    public void UntrustedHostCard_Always_PointsItsJsonRpcInterfaceAtExampleInvalid()
    {
        using var document = ReadJson("cards.card-untrusted-host.json");

        var card = Deserialize<AgentCard>(document.RootElement);

        var jsonRpc = Assert.Single(card.SupportedInterfaces, i => i.ProtocolBinding == ProtocolBindingNames.JsonRpc);
        Assert.Equal("1.0", jsonRpc.ProtocolVersion);
        Assert.Equal("example.invalid", new Uri(jsonRpc.Url).Host);
    }

    [Fact]
    public void V03OnlyCard_Always_HasNoProtocol10Interface()
    {
        using var document = ReadJson("cards.card-v03-only.json");

        var card = Deserialize<AgentCard>(document.RootElement);

        Assert.NotEmpty(card.SupportedInterfaces);
        Assert.All(card.SupportedInterfaces, i => Assert.Equal("0.3", i.ProtocolVersion));
        Assert.DoesNotContain(card.SupportedInterfaces, i => i.ProtocolVersion == "1.0");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private static TheoryData<string> Catalog(Func<string, bool> predicate)
    {
        var data = new TheoryData<string>();
        foreach (var name in ExpectedFixtures.Where(predicate))
        {
            data.Add(name);
        }

        return data;
    }

    private static T Deserialize<T>(JsonElement element)
        => element.Deserialize<T>(A2AJsonUtilities.DefaultOptions)
           ?? throw new InvalidOperationException($"The SDK deserialized {typeof(T).Name} as null.");

    private static int PopulatedStreamFields(JsonElement result)
        => StreamResponseFields.Count(f => result.TryGetProperty(f, out var value) && value.ValueKind != JsonValueKind.Null);

    private static string ResourceNameOf(string relativeName)
    {
        var suffix = Marker + relativeName;
        return TestAssembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal))
               ?? throw new InvalidOperationException($"Fixture '{relativeName}' is not embedded (no manifest resource ends with '{suffix}').");
    }

    private static byte[] ReadBytes(string relativeName)
    {
        using var stream = TestAssembly.GetManifestResourceStream(ResourceNameOf(relativeName))
                           ?? throw new InvalidOperationException($"Fixture '{relativeName}' has no manifest stream.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string ReadText(string relativeName) => Encoding.UTF8.GetString(ReadBytes(relativeName));

    private static JsonDocument ReadJson(string relativeName) => JsonDocument.Parse(ReadBytes(relativeName));

    /// <summary>Frames the raw bytes with the same <see cref="SseParser"/> the A2A SDK client uses.</summary>
    private static IReadOnlyList<SseItem<string>> ReadSse(string relativeName)
    {
        using var stream = new MemoryStream(ReadBytes(relativeName));
        return SseParser.Create(stream).Enumerate().ToList();
    }
}
