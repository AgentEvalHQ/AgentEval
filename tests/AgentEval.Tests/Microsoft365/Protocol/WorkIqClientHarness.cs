// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Auth;
using AgentEval.Microsoft365.Capture;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Protocol;
using AgentEval.Tests.Microsoft365.Auth;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Protocol;

/// <summary>
/// The real transport pipeline of the provider (<c>WorkIqAuthHandler → RawCaptureHandler → FakeWorkIqHandler</c>)
/// behind a <see cref="WorkIqA2AClient"/>, with list sinks for the diagnostics and a <see cref="WaiterTrackingTimeProvider"/>
/// so backoff and polling are deterministic. Shared by the protocol, auth and conversation tests.
/// </summary>
internal sealed class WorkIqClientHarness : IDisposable
{
    /// <summary>The fake clock's start.</summary>
    public static readonly DateTimeOffset Start = new(2026, 11, 5, 14, 0, 0, TimeSpan.Zero);

    /// <summary>The run id stamped on every event.</summary>
    public const string RunId = "run-test";

    /// <summary>
    /// Real-time bound for <see cref="PumpAsync{T}"/> while the task is neither complete nor waiting on the clock:
    /// every fake round-trip in these tests completes in milliseconds, so this only fires on a genuine hang.
    /// </summary>
    public static readonly TimeSpan PumpIdleTimeout = TimeSpan.FromSeconds(30);

    private WorkIqClientHarness(
        FakeWorkIqHandler handler,
        IWorkIqTokenProvider tokenProvider,
        RawCaptureHandler capture,
        WorkIqAuthHandler auth,
        HttpClient http,
        WorkIqA2AClient client,
        WorkIqDiagnostics diagnostics,
        ListTraceSink trace,
        ListIssueSink issues,
        WaiterTrackingTimeProvider clock,
        Microsoft365Config config,
        WorkIqOptions options)
    {
        Handler = handler;
        TokenProvider = tokenProvider;
        Capture = capture;
        Auth = auth;
        Http = http;
        Client = client;
        Diagnostics = diagnostics;
        Trace = trace;
        Issues = issues;
        Clock = clock;
        Config = config;
        Options = options;
    }

    public FakeWorkIqHandler Handler { get; }

    public IWorkIqTokenProvider TokenProvider { get; }

    /// <summary>The token provider as the static fake, when the harness created it.</summary>
    public StaticTokenProvider? Tokens => TokenProvider as StaticTokenProvider;

    public RawCaptureHandler Capture { get; }

    public WorkIqAuthHandler Auth { get; }

    public HttpClient Http { get; }

    public WorkIqA2AClient Client { get; }

    public WorkIqDiagnostics Diagnostics { get; }

    public ListTraceSink Trace { get; }

    public ListIssueSink Issues { get; }

    /// <summary>The fake clock; it reports whether anything is waiting on it, which <see cref="PumpAsync{T}"/> relies on.</summary>
    public WaiterTrackingTimeProvider Clock { get; }

    public Microsoft365Config Config { get; }

    public WorkIqOptions Options { get; }

    /// <summary>Builds the pipeline; every argument has a sensible default (default card not scripted — script it per test).</summary>
    public static WorkIqClientHarness Create(
        FakeWorkIqHandler? handler = null,
        Microsoft365Config? config = null,
        WorkIqOptions? options = null,
        IWorkIqTokenProvider? tokenProvider = null,
        WaiterTrackingTimeProvider? clock = null)
    {
        handler ??= new FakeWorkIqHandler();
        config ??= DefaultConfig();
        options ??= new WorkIqOptions();
        clock ??= new WaiterTrackingTimeProvider(Start);
        tokenProvider ??= new StaticTokenProvider();

        var trace = new ListTraceSink();
        var issues = new ListIssueSink();
        var diagnostics = new WorkIqDiagnostics(RunId, "sha-test", [trace], [issues], clock);
        var capture = new RawCaptureHandler { InnerHandler = handler, Diagnostics = diagnostics };
        var auth = new WorkIqAuthHandler(tokenProvider)
        {
            InnerHandler = capture,
            Diagnostics = diagnostics,
            Policy = new WorkIqTrustPolicy(config.TrustedHosts),
            ExpectedScope = config.Scope,
        };
        var http = new HttpClient(auth, disposeHandler: true);
        var client = new WorkIqA2AClient(config, options, http, capture, clock, diagnostics);
        return new WorkIqClientHarness(handler, tokenProvider, capture, auth, http, client, diagnostics, trace, issues, clock, config, options);
    }

    /// <summary>A valid config for the public Work IQ endpoint (no secret; nothing is contacted).</summary>
    public static Microsoft365Config DefaultConfig(string? agentId = null, string? timeZone = null, string? diagnosticProfile = null) => new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111",
        AppClientId = "22222222-2222-2222-2222-222222222222",
        AgentId = agentId,
        TimeZone = timeZone,
        DiagnosticProfile = diagnosticProfile,
    };

    /// <summary>Scripts the default two-interface card.</summary>
    public WorkIqClientHarness WithDefaultCard()
    {
        Handler.OnCard("cards/default-card-two-interfaces.json");
        return this;
    }

    /// <summary>The traced events with <paramref name="code"/>.</summary>
    public IReadOnlyList<WorkIqTraceEvent> Events(string code) => Trace.Events.Where(e => string.Equals(e.Code, code, StringComparison.Ordinal)).ToArray();

    /// <summary>Fails if any trace event or issue carries the token (or anything JWT-looking).</summary>
    public void AssertNoSecretInDiagnostics()
    {
        var token = Tokens?.Token ?? StaticTokenProvider.DefaultToken;
        foreach (var e in Trace.Events)
        {
            Assert.DoesNotContain(token, e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("eyJ", e.Message, StringComparison.Ordinal);
            if (e.Data is { } data)
            {
                Assert.DoesNotContain("eyJ", data.GetRawText(), StringComparison.Ordinal);
            }
        }

        foreach (var issue in Issues.Issues)
        {
            Assert.DoesNotContain("eyJ", issue.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("eyJ", issue.SuggestedCause, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Drives a task that waits on the fake clock (<c>Task.Delay(…, clock, …)</c>, deadline sources) to completion.
    /// The clock advances only while some timer is armed on it — up to <paramref name="step"/> at a time, and never
    /// past the earliest due time, so a wait fires exactly when it is due; while nothing is armed the pump waits real
    /// time instead, bounded by <paramref name="idleTimeout"/> (default <see cref="PumpIdleTimeout"/>). Any armed timer
    /// counts, a deadline source included: a task that keeps a deadline armed while a continuation travels through the
    /// HTTP pipeline on real thread-pool hops sees that deadline run ahead of the I/O. A test whose deadline must not
    /// race real I/O therefore pumps on the wait it means (as <c>PumpRecoveryAsync</c> does on <c>recovery.waiting</c>).
    /// Stepping blindly on every yield spent the whole step budget within milliseconds under load, before the task
    /// had armed its next wait, and ran deadlines ahead of the code that was meant to meet them.
    /// </summary>
    /// <param name="task">The task to drive.</param>
    /// <param name="clock">The clock the task waits on.</param>
    /// <param name="step">The most the clock moves per step.</param>
    /// <param name="maxSteps">The most clock steps before giving up.</param>
    /// <param name="idleTimeout">The most real time the task may spend neither complete nor waiting on the clock.</param>
    public static async Task<T> PumpAsync<T>(Task<T> task, WaiterTrackingTimeProvider clock, TimeSpan step, int maxSteps = 20_000, TimeSpan? idleTimeout = null)
    {
        await PumpAsync((Task)task, clock, step, maxSteps, idleTimeout);
        return await task;
    }

    /// <summary>Same as <see cref="PumpAsync{T}"/> for a task without a result.</summary>
    public static async Task PumpAsync(Task task, WaiterTrackingTimeProvider clock, TimeSpan step, int maxSteps = 20_000, TimeSpan? idleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);
        var idleBudget = idleTimeout ?? PumpIdleTimeout;
        var steps = 0;
        var idle = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (clock.NextDue is { } due)
            {
                if (++steps > maxSteps)
                {
                    throw new TimeoutException(
                        $"The task did not complete after {maxSteps} clock steps of {step} " +
                        $"(clock at {clock.GetUtcNow():O}, {clock.ArmedTimers} timer(s) still armed).");
                }

                var remaining = due - clock.GetUtcNow();
                clock.Advance(remaining < step ? (remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining) : step);
                idle.Restart();
                continue;
            }

            if (idle.Elapsed > idleBudget)
            {
                throw new TimeoutException(
                    $"The task neither completed nor waited on the clock within {idleBudget} of real time " +
                    $"({steps} clock step(s) driven, clock at {clock.GetUtcNow():O}).");
            }

            await Task.Delay(1);
        }

        await task;
    }

    public void Dispose()
    {
        Http.Dispose();
        Diagnostics.Dispose();
    }
}
