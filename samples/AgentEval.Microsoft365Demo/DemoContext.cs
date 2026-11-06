// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Auth;
using AgentEval.Microsoft365.Readiness;

namespace AgentEval.Microsoft365Demo;

/// <summary>
/// What every step of one run shares: the settings, the host seams, the console, the run folder, the provider's sinks,
/// the turn recorder and the run's one token source.
/// </summary>
internal sealed class DemoContext
{
    private IWorkIqTokenProvider? _tokenProvider;

    /// <summary>Creates the context and the sinks every step hands to the provider.</summary>
    /// <param name="settings">The validated settings.</param>
    /// <param name="overrides">The host seams.</param>
    /// <param name="console">The console (screen and console.log).</param>
    /// <param name="folder">The run folder.</param>
    /// <param name="env">Reads an environment variable.</param>
    /// <param name="now">The clock.</param>
    public DemoContext(DemoSettings settings, DemoHostOverrides overrides, DemoConsole console, RunFolder folder, Func<string, string?> env, Func<DateTimeOffset> now)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Overrides = overrides ?? throw new ArgumentNullException(nameof(overrides));
        Console = console ?? throw new ArgumentNullException(nameof(console));
        Folder = folder ?? throw new ArgumentNullException(nameof(folder));
        Env = env ?? throw new ArgumentNullException(nameof(env));
        Now = now ?? throw new ArgumentNullException(nameof(now));
        TraceSink = new DemoTraceSink(console) { OnStage = (e, stage, verdict, requested) => ReadinessStep.PrintStageLine(console, e, stage, verdict, requested) };
        IssueSink = new DemoIssueSink(console);
        Turns = new TurnRecorder(folder, console, settings.ContentPolicy);
        SignIn = new SignInPromptWriter(console);
    }

    /// <summary>The validated settings.</summary>
    public DemoSettings Settings { get; }

    /// <summary>The host seams.</summary>
    public DemoHostOverrides Overrides { get; }

    /// <summary>The console: the screen at the chosen verbosity and console.log.</summary>
    public DemoConsole Console { get; }

    /// <summary>The run folder.</summary>
    public RunFolder Folder { get; }

    /// <summary>Reads an environment variable.</summary>
    public Func<string, string?> Env { get; }

    /// <summary>The clock.</summary>
    public Func<DateTimeOffset> Now { get; }

    /// <summary>The trace sink of the readiness check and the live agent.</summary>
    public DemoTraceSink TraceSink { get; }

    /// <summary>The issue sink of the readiness check and the live agent (and of the demo's own judge issues).</summary>
    public DemoIssueSink IssueSink { get; }

    /// <summary>The live agent's turn sink.</summary>
    public TurnRecorder Turns { get; }

    /// <summary>Where a device-code sign-in prompt goes (screen in full, console.log masked).</summary>
    public SignInPromptWriter SignIn { get; }

    /// <summary>
    /// The run's one token source, shared by the readiness check and the live agent so one sign-in serves both, with the
    /// demo's sign-in prompt writer: the host's <see cref="DemoHostOverrides.TokenProvider"/>, else built once by
    /// <see cref="DemoHostOverrides.TokenProviderFactory"/> (the MSAL device-code sign-in by default). Null when the
    /// config did not load: the readiness check then fails at its config stage before any sign-in.
    /// </summary>
    /// <param name="config">The loaded config.</param>
    public IWorkIqTokenProvider? TokenProviderFor(Microsoft365Config? config)
    {
        if (Overrides.TokenProvider is { } supplied)
        {
            return supplied;
        }

        if (_tokenProvider is not null || config is null)
        {
            return _tokenProvider;
        }

        var factory = Overrides.TokenProviderFactory ?? DemoTokenProviders.DeviceCode;
        return _tokenProvider = factory(config, SignIn);
    }

    /// <summary>
    /// A new pass-through over the host's inner handler for one connection, so that connection's disposal leaves the
    /// host's handler alive (readiness and the live agent each own a chain); null when the host gave none.
    /// </summary>
    public HttpMessageHandler? NewInnerHandler() => Overrides.InnerHandler is { } inner ? new HostOwnedHandler(inner) : null;

    /// <summary>A pass-through handler that leaves its inner handler alive when the connection's chain is disposed.</summary>
    private sealed class HostOwnedHandler : DelegatingHandler
    {
        public HostOwnedHandler(HttpMessageHandler inner)
            : base(inner)
        {
        }

        protected override void Dispose(bool disposing)
        {
            // The host owns the inner handler: release nothing of it (DelegatingHandler disposes its inner only when disposing).
            base.Dispose(disposing: false);
        }
    }
}

/// <summary>What the steps of one run recorded so far. The reports and the exit code are built from it, after a runtime error too.</summary>
internal sealed class RunRecord
{
    /// <summary>Every stage row, in order.</summary>
    public List<StageRow> Stages { get; } = [];

    /// <summary>The readiness report; null when the check did not run.</summary>
    public ReadinessReport? Readiness { get; set; }

    /// <summary>The prompt step's turn, when it produced one.</summary>
    public WorkIqTurnResult? PromptTurn { get; set; }

    /// <summary>The benchmark's outcome; null when it did not run.</summary>
    public BenchmarkOutcome? Benchmark { get; set; }
}

/// <summary>The config as the run loaded it.</summary>
/// <param name="Config">The validated config (with the <c>--agent-id</c> override); null when it did not load.</param>
/// <param name="Overridden">
/// The config with the <c>--agent-id</c> override, as loaded and before validation: the readiness check gets it, so an
/// override that breaks the config fails the check's config stage. Null without the option, or when the file did not load.
/// </param>
/// <param name="Error">Why the config did not load; null when it did.</param>
internal sealed record LoadedConfig(Microsoft365Config? Config, Microsoft365Config? Overridden, string? Error)
{
    /// <summary>Nothing loaded yet.</summary>
    public static LoadedConfig None { get; } = new(null, null, null);

    /// <summary>Loads the config file and applies <c>--agent-id</c>. A config that does not load is recorded, not thrown.</summary>
    /// <param name="settings">The settings.</param>
    public static LoadedConfig Load(DemoSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Microsoft365Config? overridden = null;
        try
        {
            var config = Microsoft365Config.Load(settings.ConfigFile);
            if (settings.AgentId is not null)
            {
                overridden = config with { AgentId = settings.AgentId };
                overridden.Validate();
                config = overridden;
            }

            return new LoadedConfig(config, overridden, null);
        }
        catch (InvalidOperationException ex)
        {
            // The readiness check's config stage reports it in full; the banner says it too.
            return new LoadedConfig(null, overridden, ex.Message);
        }
    }
}
