// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Auth;
using AgentEval.Microsoft365.Readiness;
using AgentEval.Tests.Microsoft365.Auth;
using AgentEval.Tests.Microsoft365.TestHelpers;

namespace AgentEval.Tests.Microsoft365.Readiness;

/// <summary>
/// One offline readiness run: a temp folder for the config, the diagnostics and any raw capture, the scripted
/// <see cref="FakeWorkIqHandler"/> behind the real handler chain, a host token provider that hands out
/// <see cref="FakeToken"/> (or throws), and a console writer. Nothing touches the network or a real tenant; the fake
/// answers immediately. A test of the stage timers runs the check on a fake clock and advances it.
/// </summary>
internal sealed class ReadinessTestRig : IDisposable
{
    /// <summary>The token the fake provider hands out; it must never appear in any output.</summary>
    public const string FakeToken = "fake-token";

    /// <summary>A JSON-RPC TaskNotFound reply (the protocol stage's expected answer).</summary>
    public const string TaskNotFoundReply = "{\"jsonrpc\":\"2.0\",\"id\":\"rpc-1\",\"error\":{\"code\":-32001,\"message\":\"Task not found\"}}";

    /// <summary>The default two-interface card fixture.</summary>
    public const string DefaultCard = "cards/default-card-two-interfaces.json";

    /// <summary>A completed task with an answer and two attributions.</summary>
    public const string CompletedWithAttributions = "envelopes/send-task-completed-with-answer-and-attributions.json";

    /// <summary>A completed task with the native118 Developer Info and Retrieval Info artifacts.</summary>
    public const string CompletedWithDiagnostics = "envelopes/send-task-completed-with-developer-and-retrieval-info.json";

    /// <summary>A JSON-RPC TaskNotCancelable (-32002) reply.</summary>
    public const string NotCancelable = "envelopes/canceltask-not-cancelable-error.json";

    private const string ValidConfigJson =
        "{\"tenantId\":\"11111111-1111-1111-1111-111111111111\",\"appClientId\":\"22222222-2222-2222-2222-222222222222\",\"agentName\":\"Doctor test agent\"}";

    public ReadinessTestRig()
    {
        Root = Directory.CreateTempSubdirectory("agenteval-m365-doctor-");
        Diagnostics = new DirectoryInfo(Path.Combine(Root.FullName, "diag"));
    }

    /// <summary>The temp folder of this run.</summary>
    public DirectoryInfo Root { get; }

    /// <summary>Where the trace and issue files go.</summary>
    public DirectoryInfo Diagnostics { get; }

    /// <summary>The scripted transport.</summary>
    public FakeWorkIqHandler Handler { get; } = new();

    /// <summary>The host token provider.</summary>
    public StaticTokenProvider Tokens { get; } = new(FakeToken);

    /// <summary>The console the device-code message and the verbose trace go to.</summary>
    public StringWriter Console { get; } = new();

    /// <summary>Writes a config file (the valid default when <paramref name="json"/> is null).</summary>
    public FileInfo WriteConfig(string? json = null, string name = "workiq.json")
    {
        var path = Path.Combine(Root.FullName, name);
        File.WriteAllText(path, json ?? ValidConfigJson, new UTF8Encoding(false));
        return new FileInfo(path);
    }

    /// <summary>The options of a run under this rig (verbose console into <see cref="Console"/>, files under <see cref="Diagnostics"/>).</summary>
    public ReadinessCheckOptions Options(
        IReadOnlySet<ReadinessStage>? stages = null,
        bool probe = false,
        bool consent = false,
        bool continueAfterFailure = false,
        DiagnosticProfile? profile = null,
        DirectoryInfo? rawCapture = null) => new()
        {
            Stages = stages,
            Probe = probe,
            IUnderstandLiveSideEffects = consent,
            ContinueAfterFailure = continueAfterFailure,
            Profile = profile,
            RawCaptureDirectory = rawCapture,
            DiagnosticsDirectory = Diagnostics,
            Console = Console,
            Verbose = true,
        };

    /// <summary>Scripts the default card and the expected TaskNotFound answer to the protocol stage's GetTask.</summary>
    public ReadinessTestRig WithHealthyEndpoint(IReadOnlyDictionary<string, string>? getTaskHeaders = null)
    {
        Handler.OnCard(DefaultCard).OnRpc("GetTask", TaskNotFoundReply, headers: getTaskHeaders);
        return this;
    }

    /// <summary>Runs the check through the internal seam (fake transport, host token provider).</summary>
    public Task<ReadinessReport> RunAsync(ReadinessCheckOptions options, FileInfo? configFile = null) =>
        Microsoft365ReadinessCheck.RunAsync(null, configFile ?? WriteConfig(), options, Handler, Tokens);

    /// <summary>Runs the check on an injected clock: every stage timer and sign-in wait runs on <paramref name="clock"/>, so a test advances it instead of waiting.</summary>
    public Task<ReadinessReport> RunAsync(ReadinessCheckOptions options, TimeProvider clock, IWorkIqTokenProvider tokens, CancellationToken cancellationToken) =>
        Microsoft365ReadinessCheck.RunAsync(null, WriteConfig(), options, Handler, tokens, clock, cancellationToken);

    /// <summary>The text of a file the run wrote (empty when absent).</summary>
    public static string ReadText(string? path) => path is not null && File.Exists(path) ? File.ReadAllText(path) : "";

    /// <summary>The non-empty lines of a JSONL file.</summary>
    public static string[] ReadLines(string? path) =>
        ReadText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The <c>code</c> of every JSONL line of a trace or issue file, in order.</summary>
    public static IReadOnlyList<string> Codes(string? path) =>
        ReadLines(path).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("code").GetString() ?? "";
        }).ToArray();

    /// <summary>The value of the evidence row <paramref name="name"/> of a stage (fails when absent).</summary>
    public static string EvidenceOf(ReadinessStageResult stage, string name)
    {
        var match = stage.Evidence.Where(e => string.Equals(e.Key, name, StringComparison.Ordinal)).ToArray();
        if (match.Length != 1)
        {
            Xunit.Assert.Fail(
                $"Stage {stage.Stage} has {match.Length} evidence row(s) named '{name}'. Rows: {string.Join("; ", stage.Evidence.Select(e => e.Key + "=" + e.Value))}");
        }

        return match[0].Value;
    }

    public void Dispose()
    {
        Console.Dispose();
        Handler.Dispose();
        try
        {
            Root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds is left for the OS to clean.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
