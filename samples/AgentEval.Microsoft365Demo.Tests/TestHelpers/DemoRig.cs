// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Microsoft.Extensions.AI;

namespace AgentEval.Microsoft365Demo.Tests.TestHelpers;

/// <summary>
/// One offline demo run: a temp folder for the config, the dataset and the run folder; the scripted
/// <see cref="FakeWorkIqHandler"/> (behind a <see cref="PromptRoutingHandler"/>) as the innermost handler of every Work IQ
/// connection the demo opens, so the REAL provider (readiness check, A2A client, conversation, evidence normaliser,
/// diagnostics) runs end to end; a <see cref="FakeTokenProvider"/>; an optional offline judge; an empty environment, so
/// no variable of the developer's shell (a judge provider, <c>M365_CONFIG_PATH</c>, the consent variable) can reach a
/// run; and a fixed clock. Nothing touches the network, a tenant or a paid model.
/// </summary>
internal sealed class DemoRig : IDisposable
{
    /// <summary>The default two-interface card (the default Work IQ agent).</summary>
    public const string DefaultCard = "cards/default-card-two-interfaces.json";

    /// <summary>The card of the agent <see cref="AgentId"/>.</summary>
    public const string AgentCard = "cards/agent-specific-card.json";

    /// <summary>A card whose JSON-RPC interface sits on a host outside the trusted hosts.</summary>
    public const string UntrustedHostCard = "cards/card-untrusted-host.json";

    /// <summary>A completed task with an answer and two attributions.</summary>
    public const string CompletedWithAnswer = "envelopes/send-task-completed-with-answer-and-attributions.json";

    /// <summary>A task paused for the user's confirmation of a proposed calendar event.</summary>
    public const string InputRequiredConfirmation = "envelopes/send-task-input-required-confirmation.json";

    /// <summary>The JSON-RPC TaskNotFound reply the readiness check's protocol stage expects for its random task id.</summary>
    public const string TaskNotFoundReply = "{\"jsonrpc\":\"2.0\",\"id\":\"rpc-1\",\"error\":{\"code\":-32001,\"message\":\"Task not found\"}}";

    /// <summary>The opaque id of the agent behind <see cref="AgentCard"/> (prefix and dot included).</summary>
    public const string AgentId = "U_abc123.def456";

    /// <summary>The start of the answer every completed fixture turn carries.</summary>
    public const string AnswerStart = "Project Atlas is on schedule";

    /// <summary>The consent flag.</summary>
    public const string Consent = "--i-understand-live-side-effects";

    /// <summary>The ids of the five embedded default cases, in order.</summary>
    public static readonly string[] DefaultCaseIds =
    [
        "identity_disclosure",
        "capability_summary",
        "grounded_summary_with_citation",
        "explicit_date_reasoning",
        "out_of_scope_live_data",
    ];

    /// <summary>The clock every run reads (it names default run folders and stamps the reports).</summary>
    public static readonly DateTimeOffset FixedNow = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private const string ValidConfigJson =
        "{\"tenantId\":\"11111111-1111-1111-1111-111111111111\",\"appClientId\":\"22222222-2222-2222-2222-222222222222\",\"agentName\":\"Demo test agent\"}";

    public DemoRig()
    {
        Root = Directory.CreateTempSubdirectory("agenteval-m365-demo-");
        Router = new PromptRoutingHandler(Handler);
        ConfigFile = WriteConfig();
    }

    /// <summary>The temp folder of this rig.</summary>
    public DirectoryInfo Root { get; }

    /// <summary>The scripted transport (card, GetTask, and every SendMessage no route claims).</summary>
    public FakeWorkIqHandler Handler { get; } = new();

    /// <summary>The innermost handler the demo receives: routes marked prompts, forwards the rest to <see cref="Handler"/>.</summary>
    public PromptRoutingHandler Router { get; }

    /// <summary>The token source of every Work IQ connection (unless <see cref="TokenProviderFactory"/> is set).</summary>
    public FakeTokenProvider Tokens { get; } = new();

    /// <summary>
    /// When set, the demo builds its token source with this factory (the demo's own seam, given the loaded config and the
    /// demo's sign-in prompt writer) instead of receiving <see cref="Tokens"/>.
    /// </summary>
    public Func<AgentEval.Microsoft365.Microsoft365Config, TextWriter, AgentEval.Microsoft365.Auth.IWorkIqTokenProvider>? TokenProviderFactory { get; set; }

    /// <summary>Forces the screen's ASCII markers on or off; null lets the demo decide (a StringWriter screen: off).</summary>
    public bool? AsciiScreen { get; set; }

    /// <summary>The judge the demo receives through its host seam; null means "resolve from the (empty) environment", that is: none.</summary>
    public IChatClient? Judge { get; set; }

    /// <summary>The demo's whole environment (empty unless a test sets a variable).</summary>
    public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The config file the runs use unless a test passes its own <c>--config</c>.</summary>
    public FileInfo ConfigFile { get; private set; }

    /// <summary>The run folder (<c>--out</c>) of every run of this rig.</summary>
    public DirectoryInfo RunFolder => new(Path.Combine(Root.FullName, "run"));

    /// <summary>Where default run folders would go (the demo's <c>RunsRoot</c> seam).</summary>
    public DirectoryInfo RunsRoot => new(Path.Combine(Root.FullName, "runs"));

    /// <summary>Writes a config file (the valid default when <paramref name="json"/> is null) and makes it the rig's config.</summary>
    /// <param name="json">The config JSON.</param>
    /// <param name="name">The file name.</param>
    public FileInfo WriteConfig(string? json = null, string name = "workiq.json")
    {
        var path = Path.Combine(Root.FullName, name);
        File.WriteAllText(path, json ?? ValidConfigJson, new UTF8Encoding(false));
        ConfigFile = new FileInfo(path);
        return ConfigFile;
    }

    /// <summary>Writes a <c>--dataset</c> file.</summary>
    /// <param name="json">The dataset JSON (the format of samples/datasets/m365-smoke.json).</param>
    /// <param name="name">The file name.</param>
    public FileInfo WriteDataset(string json, string name = "cases.json")
    {
        var path = Path.Combine(Root.FullName, name);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return new FileInfo(path);
    }

    /// <summary>Scripts <paramref name="card"/> for every card GET and the expected TaskNotFound for the protocol stage's GetTask.</summary>
    /// <param name="card">The card fixture.</param>
    public DemoRig WithHealthyEndpoint(string card = DefaultCard)
    {
        Handler.OnCard(card).OnRpc("GetTask", TaskNotFoundReply);
        return this;
    }

    /// <summary>Scripts every SendMessage no route claims to complete with <see cref="CompletedWithAnswer"/>.</summary>
    public DemoRig WithCompletedTurns()
    {
        Handler.OnRpc("SendMessage", CompletedWithAnswer);
        return this;
    }

    /// <summary>
    /// Runs the demo in process through its host seam (<c>DemoHost.RunAsync</c>) and returns the exit code, the screen and the
    /// run folder. <c>--config</c> (the rig's config) and <c>--out</c> (<see cref="RunFolder"/>) are added unless
    /// <paramref name="args"/> carries them or the flags say not to.
    /// </summary>
    /// <param name="args">The demo's command line.</param>
    public Task<DemoRunResult> RunAsync(params string[] args) => RunAsync(args, addConfig: true, addOut: true);

    /// <summary>Runs the demo; see <see cref="RunAsync(string[])"/>.</summary>
    /// <param name="args">The demo's command line.</param>
    /// <param name="addConfig">Whether to add <c>--config</c> when <paramref name="args"/> has none.</param>
    /// <param name="addOut">Whether to add <c>--out</c> when <paramref name="args"/> has none.</param>
    public async Task<DemoRunResult> RunAsync(string[] args, bool addConfig, bool addOut)
    {
        ArgumentNullException.ThrowIfNull(args);
        var all = new List<string>(args);
        if (addConfig && !all.Contains("--config"))
        {
            all.AddRange(["--config", ConfigFile.FullName]);
        }

        if (addOut && !all.Contains("--out"))
        {
            all.AddRange(["--out", RunFolder.FullName]);
        }

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var overrides = new DemoHostOverrides
        {
            InnerHandler = Router,
            TokenProvider = TokenProviderFactory is null ? Tokens : null,
            TokenProviderFactory = TokenProviderFactory,
            AsciiScreen = AsciiScreen,
            Out = stdout,
            Err = stderr,
            RunsRoot = RunsRoot,
            Now = () => FixedNow,
            JudgeClient = Judge,
            GetEnvironmentVariable = name => Environment.TryGetValue(name, out var value) ? value : null,
        };

        // A hang is a failure, not a stuck test run: the demo ends the stage in progress and still writes the folder.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var exit = await DemoHost.RunAsync(all.ToArray(), overrides, deadline.Token);
        return new DemoRunResult(exit, stdout.ToString(), stderr.ToString(), RunFolder, all.ToArray());
    }

    public void Dispose()
    {
        Handler.Dispose();
        try
        {
            Root.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp folder another process still holds is left for the OS to clean.
        }
    }
}

/// <summary>What one demo run returned and left behind.</summary>
/// <param name="ExitCode">The exit code.</param>
/// <param name="Out">Everything written to the screen's standard output.</param>
/// <param name="Err">Everything written to the screen's error output.</param>
/// <param name="Folder">The run folder (<c>--out</c>).</param>
/// <param name="Args">The full command line the demo received.</param>
internal sealed record DemoRunResult(int ExitCode, string Out, string Err, DirectoryInfo Folder, string[] Args)
{
    /// <summary>Both screen outputs.</summary>
    public string Screen => Out + Err;

    /// <summary>The screen's standard output split into lines.</summary>
    public string[] OutLines => Out.ReplaceLineEndings("\n").Split('\n');

    /// <summary>The zip <c>--zip</c> writes beside the run folder.</summary>
    public FileInfo Zip => new(Folder.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".zip");

    /// <summary>A file of the run folder.</summary>
    /// <param name="relativeName">The name relative to the run folder.</param>
    public FileInfo File(string relativeName) => new(Path.Combine(Folder.FullName, relativeName));

    /// <summary>The text of a run-folder file; fails the test, with the screen, when it is missing.</summary>
    /// <param name="relativeName">The name relative to the run folder.</param>
    public string Text(string relativeName)
    {
        var file = File(relativeName);
        Assert.True(file.Exists, $"The run folder has no {relativeName}.\n{Describe()}");
        return System.IO.File.ReadAllText(file.FullName);
    }

    /// <summary>The non-empty lines of a run-folder JSONL file, each parsed (the caller disposes nothing: the documents are cloned).</summary>
    /// <param name="relativeName">The name relative to the run folder.</param>
    public IReadOnlyList<JsonElement> JsonLines(string relativeName) =>
        Text(relativeName)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToArray();

    /// <summary>A run-folder JSON file, parsed.</summary>
    /// <param name="relativeName">The name relative to the run folder.</param>
    public JsonElement Json(string relativeName)
    {
        using var document = JsonDocument.Parse(Text(relativeName));
        return document.RootElement.Clone();
    }

    /// <summary>Fails the test with the whole screen when the exit code is not <paramref name="expected"/>.</summary>
    /// <param name="expected">The expected exit code.</param>
    public void AssertExit(int expected) =>
        Assert.True(ExitCode == expected, $"Expected exit {expected}, got {ExitCode}.\n{Describe()}");

    /// <summary>The exit code and both screen outputs, for an assertion message.</summary>
    public string Describe() =>
        $"args: {string.Join(' ', Args)}\nexit: {ExitCode}\n----- stdout -----\n{Out}\n----- stderr -----\n{Err}";
}
