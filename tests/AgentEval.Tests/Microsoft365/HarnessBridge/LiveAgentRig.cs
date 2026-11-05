// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Microsoft365;
using AgentEval.Tests.Microsoft365.Auth;
using AgentEval.Tests.Microsoft365.Protocol;
using AgentEval.Tests.Microsoft365.TestHelpers;

namespace AgentEval.Tests.Microsoft365.HarnessBridge;

/// <summary>
/// A REAL <see cref="WorkIqEvaluableAgent"/> built by <see cref="Microsoft365AgentFactory.BuildLive"/> — the live auth,
/// capture, A2A client, reducer, normaliser and conversation — with only the two outer edges faked: a token provider that
/// returns <see cref="FakeToken"/> and a <see cref="FakeWorkIqHandler"/> as the innermost HTTP handler. The diagnostics
/// files go to a private temp directory (never the workspace's <c>.agenteval/m365</c>), and every turn result the agent
/// produces is kept in <see cref="Turns"/> through the real <c>onTurn</c> callback. No network, no paid provider.
/// </summary>
internal sealed class LiveAgentRig : IAsyncDisposable
{
    /// <summary>The token the fake provider returns; it must never appear in any output.</summary>
    public const string FakeToken = "fake-token";

    /// <summary>The fixture whose task completes with the two-part answer and two attributions.</summary>
    public const string CompletedFixture = "envelopes/send-task-completed-with-answer-and-attributions.json";

    /// <summary>The fixture whose task pauses on a confirmation card.</summary>
    public const string InputRequiredFixture = "envelopes/send-task-input-required-confirmation.json";

    /// <summary>The fixture whose task fails after a partial answer artifact.</summary>
    public const string FailedFixture = "envelopes/send-task-failed-with-partial-artifact.json";

    /// <summary>The default two-interface agent card.</summary>
    public const string CardFixture = "cards/default-card-two-interfaces.json";

    /// <summary>The server context id every envelope fixture returns.</summary>
    public const string FixtureContextId = "ctx-5e0c7a2b-91d4-4f8e-b6a3-0d2f8c1e7b55";

    private readonly object _gate = new();
    private readonly List<WorkIqTurnResult> _turns = [];

    private LiveAgentRig(FakeWorkIqHandler handler, StaticTokenProvider tokens, string diagnosticsDirectory)
    {
        Handler = handler;
        Tokens = tokens;
        DiagnosticsDirectory = diagnosticsDirectory;
    }

    /// <summary>The scripted innermost handler (every request it received is recorded, headers redacted).</summary>
    public FakeWorkIqHandler Handler { get; }

    /// <summary>The fake token provider (counts its calls).</summary>
    public StaticTokenProvider Tokens { get; }

    /// <summary>Where this rig's trace and issue files are written.</summary>
    public string DiagnosticsDirectory { get; }

    /// <summary>The real agent under test.</summary>
    public WorkIqEvaluableAgent Agent { get; private set; } = null!;

    /// <summary>Every turn result the agent handed to its <c>onTurn</c> callback, in order (a snapshot).</summary>
    public IReadOnlyList<WorkIqTurnResult> Turns
    {
        get
        {
            lock (_gate)
            {
                return _turns.ToArray();
            }
        }
    }

    /// <summary>
    /// Builds the live agent over <paramref name="handler"/>. <paramref name="options"/> is honoured except for the
    /// diagnostics location (always this rig's temp directory) and a run id when none is given.
    /// </summary>
    /// <param name="handler">The scripted fake (card + JSON-RPC replies).</param>
    /// <param name="options">The agent options; null means defaults.</param>
    /// <param name="wrap">Optionally wraps the fake in another innermost handler (for example one that cancels mid-send).</param>
    /// <param name="config">The config; null means the shared test config (default agent, public profile).</param>
    public static LiveAgentRig Create(
        FakeWorkIqHandler handler,
        WorkIqOptions? options = null,
        Func<FakeWorkIqHandler, HttpMessageHandler>? wrap = null,
        Microsoft365Config? config = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var directory = Path.Combine(Path.GetTempPath(), "agenteval-m365-bridge", Guid.NewGuid().ToString("N"));
        var rig = new LiveAgentRig(handler, new StaticTokenProvider(FakeToken), directory);
        var effective = (options ?? new WorkIqOptions()) with
        {
            DiagnosticsDirectory = new DirectoryInfo(directory),
            RunId = options?.RunId ?? "run-bridge-" + Guid.NewGuid().ToString("N")[..8],
        };

        rig.Agent = Microsoft365AgentFactory.BuildLive(
            config ?? WorkIqClientHarness.DefaultConfig(),
            iUnderstandLiveSideEffects: true,
            effective,
            tokenProvider: rig.Tokens,
            onTurn: rig.Record,
            innerHandler: wrap is null ? handler : wrap(handler));
        return rig;
    }

    /// <summary>A fake scripted with the default card and one repeating <c>SendMessage</c> reply.</summary>
    /// <param name="sendFixture">The <c>SendMessage</c> envelope fixture.</param>
    public static FakeWorkIqHandler Scripted(string sendFixture) =>
        new FakeWorkIqHandler().OnCard(CardFixture).OnRpc("SendMessage", sendFixture);

    /// <summary>The answer text of <see cref="CompletedFixture"/>: its <c>Answer</c> artifact's text parts, concatenated in order.</summary>
    public static string CompletedFixtureAnswer()
    {
        var parts = JsonNode.Parse(FixtureLoader.Text(CompletedFixture))!["result"]!["task"]!["artifacts"]![0]!["parts"]!.AsArray();
        return string.Concat(parts.Select(p => p!["text"]!.GetValue<string>()));
    }

    /// <summary>The <c>SendMessage</c> requests the fake received, in order.</summary>
    public IReadOnlyList<RecordedRequest> Sends() =>
        Handler.Requests.Where(r => string.Equals(r.JsonRpcMethod, "SendMessage", StringComparison.Ordinal)).ToArray();

    /// <summary>The outbound <c>params.message</c> of a recorded <c>SendMessage</c>.</summary>
    /// <param name="send">The recorded request.</param>
    public static JsonObject MessageOf(RecordedRequest send) =>
        (JsonObject)JsonNode.Parse(send.BodyJson!)!["params"]!["message"]!;

    /// <summary>The <c>contextId</c> a recorded <c>SendMessage</c> carried, or null when it omitted one.</summary>
    /// <param name="send">The recorded request.</param>
    public static string? ContextIdOf(RecordedRequest send) =>
        MessageOf(send).TryGetPropertyValue("contextId", out var node) && node is not null ? node.GetValue<string>() : null;

    /// <summary>Every file this rig's diagnostics wrote, read with the share mode an open sink allows.</summary>
    public string ReadDiagnosticsFiles()
    {
        if (!Directory.Exists(DiagnosticsDirectory))
        {
            return "";
        }

        return string.Concat(Directory.GetFiles(DiagnosticsDirectory, "*", SearchOption.AllDirectories).Select(path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Agent is not null)
        {
            await Agent.DisposeAsync();
        }

        try
        {
            if (Directory.Exists(DiagnosticsDirectory))
            {
                Directory.Delete(DiagnosticsDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    private void Record(WorkIqTurnResult result)
    {
        lock (_gate)
        {
            _turns.Add(result);
        }
    }
}
