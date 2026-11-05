// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Auth;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Readiness;
using AgentEval.Tests.Microsoft365.Protocol;
using AgentEval.Tests.Microsoft365.Readiness;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Auth;

/// <summary>
/// Review M1 / L2: the bearer token is acquired before each HTTP call, outside <c>HttpClient.Timeout</c>. A device-code
/// sign-in slower than the HTTP timeout therefore still works; one that never finishes is reported as "sign-in not
/// completed — nothing was sent", never as a network timeout that may have reached the service; and the readiness check
/// bounds a sign-in by its code's own expiry instead of the stage timeout. Real time, sub-second to two seconds per test.
/// </summary>
public sealed class SignInOutsideHttpTimeoutTests : IDisposable
{
    private const string CompletedFixture = "envelopes/send-task-completed-with-answer-and-attributions.json";
    private const string CardFixture = "cards/default-card-two-interfaces.json";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenteval-m365-signin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // The live agent (BuildLive)
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BuildLive_SignInSlowerThanTheHttpTimeout_TheTurnStillCompletes()
    {
        var handler = new FakeWorkIqHandler().OnCard(CardFixture).OnRpc("SendMessage", CompletedFixture);
        var tokens = new SlowSignInTokenProvider { FirstDelay = TimeSpan.FromMilliseconds(1500) };
        var agent = Build(handler, tokens, sendTimeout: TimeSpan.FromMilliseconds(400));
        await using (agent)
        {
            var response = await agent.InvokeAsync("Summarize the status of Project Atlas and cite the sources.");

            Assert.StartsWith("Project Atlas is on schedule", response.Text, StringComparison.Ordinal);
            Assert.Equal(1, handler.CardRequestCount);
            Assert.Equal(1, handler.CountOf("SendMessage"));
            Assert.Contains(agent.Diagnostics.RecentEvents, e => e.Code == "auth.token_acquired" && e.Message.Contains("before the HTTP call", StringComparison.Ordinal));
            Assert.DoesNotContain(agent.Diagnostics.RecentEvents, e => e.Message.Contains("timed out", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task BuildLive_SignInNeverCompletes_CallerStops_SignInNotCompletedNothingSentNoTimeout()
    {
        var handler = new FakeWorkIqHandler().OnCard(CardFixture).OnRpc("SendMessage", CompletedFixture);
        var tokens = new SlowSignInTokenProvider { FirstDelay = Timeout.InfiniteTimeSpan };
        var agent = Build(handler, tokens, sendTimeout: TimeSpan.FromMilliseconds(200));
        await using (agent)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));

            var ex = await Assert.ThrowsAsync<WorkIqTurnCanceledException>(() => agent.InvokeAsync("Summarize the status of Project Atlas.", stop.Token));

            var failure = ex.TurnResult.Failure!;
            Assert.Equal(WorkIqFailureCategory.Unauthorized, failure.Category);
            Assert.Equal(OutcomeCertainty.Known, failure.Certainty);
            Assert.Null(failure.HttpStatus);
            Assert.Contains("sign-in not completed", failure.SafeMessage, StringComparison.Ordinal);
            Assert.Contains("nothing was sent", failure.SafeMessage, StringComparison.Ordinal);
            Assert.Equal(WorkIqTurnOutcome.Failed, ex.TurnResult.Outcome);
            handler.AssertNoRequests();

            var events = agent.Diagnostics.RecentEvents;
            Assert.Contains(events, e => e.Code == "auth.sign_in_not_completed");
            Assert.DoesNotContain(events, e => e.Message.Contains("may have reached the service", StringComparison.Ordinal));
            var issues = ReadShared(Path.Combine(_root, "m365-issues.jsonl"));
            Assert.Contains("auth.sign_in_not_completed", issues, StringComparison.Ordinal);
            Assert.Contains("agenteval m365 doctor --stage auth", issues, StringComparison.Ordinal);
            Assert.DoesNotContain("The bearer token was rejected", issues, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // The readiness check
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Readiness_DiscoveryWithASignInThatNeverFinishes_FailsAsSignInNotCompleted_NotANetworkTimeout()
    {
        using var rig = new ReadinessTestRig();
        rig.Handler.OnCard(CardFixture);
        var tokens = new SlowSignInTokenProvider { FirstDelay = Timeout.InfiniteTimeSpan };
        var options = rig.Options(stages: new HashSet<ReadinessStage> { ReadinessStage.Config, ReadinessStage.Discovery }) with
        {
            StageTimeout = TimeSpan.FromMilliseconds(300),
        };

        var report = await Microsoft365ReadinessCheck.RunAsync(null, rig.WriteConfig(), options, rig.Handler, tokens);

        var discovery = report.Find(ReadinessStage.Discovery)!;
        Assert.Equal(ReadinessResult.Fail, discovery.Result);
        Assert.Contains("sign-in not completed", discovery.Summary, StringComparison.Ordinal);
        Assert.Contains("nothing was sent", discovery.Summary, StringComparison.Ordinal);
        Assert.Contains("not a network timeout", discovery.Meaning, StringComparison.Ordinal);
        Assert.Contains("--stage auth", discovery.Action, StringComparison.Ordinal);
        Assert.DoesNotContain("no answer within the stage timeout", discovery.Summary, StringComparison.Ordinal);
        rig.Handler.AssertNoRequests();
    }

    [Fact]
    public async Task Readiness_DeviceCodeSignInLongerThanTheStageTimeout_WaitsForTheCodeAndPasses()
    {
        using var rig = new ReadinessTestRig();
        var tokens = new SlowSignInTokenProvider
        {
            FirstDelay = TimeSpan.FromMilliseconds(1200),
            PromptExpiresIn = TimeSpan.FromSeconds(30),
        };
        var options = rig.Options(stages: new HashSet<ReadinessStage> { ReadinessStage.Config, ReadinessStage.Auth }) with
        {
            StageTimeout = TimeSpan.FromMilliseconds(400),
        };

        var report = await Microsoft365ReadinessCheck.RunAsync(null, rig.WriteConfig(), options, rig.Handler, tokens);

        Assert.Equal(ReadinessResult.Pass, report.Find(ReadinessStage.Auth)!.Result);
        Assert.Contains("doctor.sign_in_wait", ReadinessTestRig.Codes(report.TraceFile));
        rig.Handler.AssertNoRequests();
    }

    [Fact]
    public async Task Readiness_DeviceCodeExpiresBeforeTheSignIn_FailsNamingTheExpiry()
    {
        using var rig = new ReadinessTestRig();
        var tokens = new SlowSignInTokenProvider
        {
            FirstDelay = Timeout.InfiniteTimeSpan,
            // Already 9.4 s past expiry: with the 10 s grace the stage waits about 0.6 s, longer than its 0.3 s timeout.
            PromptExpiresIn = TimeSpan.FromSeconds(-9.4),
        };
        var options = rig.Options(stages: new HashSet<ReadinessStage> { ReadinessStage.Config, ReadinessStage.Auth }) with
        {
            StageTimeout = TimeSpan.FromMilliseconds(300),
        };

        var report = await Microsoft365ReadinessCheck.RunAsync(null, rig.WriteConfig(), options, rig.Handler, tokens);

        var auth = report.Find(ReadinessStage.Auth)!;
        Assert.Equal(ReadinessResult.Fail, auth.Result);
        Assert.Contains("the device code expired at", auth.Summary, StringComparison.Ordinal);
        Assert.Contains("Nothing was sent", auth.Meaning, StringComparison.Ordinal);
        Assert.Contains(auth.Evidence, e => e.Key == "device code expired at (UTC)");
    }

    // ---------------------------------------------------------------------------------------------------------
    // The trace copy of the device-code message (review L1)
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code ABCD12345 to authenticate.", "ABCD12345",
        "To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code [redacted] to authenticate.")]
    [InlineData("enter the code XYZ", null, "enter the code XYZ")]
    [InlineData("enter the code XYZ", "  ", "enter the code XYZ")]
    [InlineData("", "ABC", "")]
    public void MaskUserCode_Message_HidesOnlyTheUserCode(string message, string? userCode, string expected) =>
        Assert.Equal(expected, WorkIqDeviceCodeTokenProvider.MaskUserCode(message, userCode));

    // ---------------------------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Reads a diagnostics file the live agent still holds open.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private WorkIqEvaluableAgent Build(FakeWorkIqHandler handler, IWorkIqTokenProvider tokens, TimeSpan sendTimeout) =>
        Microsoft365AgentFactory.BuildLive(
            WorkIqClientHarness.DefaultConfig(),
            iUnderstandLiveSideEffects: true,
            new WorkIqOptions { SendTimeout = sendTimeout, DiagnosticsDirectory = new DirectoryInfo(_root), RunId = "run-signin" },
            tokens,
            innerHandler: handler);

    /// <summary>
    /// A token provider whose first acquisition is a slow interactive sign-in (it can announce a sign-in code expiry
    /// the way the device-code provider does); every later acquisition is an immediate cache hit.
    /// </summary>
    private sealed class SlowSignInTokenProvider : IWorkIqTokenProvider, IWorkIqSignInWaitSource
    {
        private int _calls;

        public TimeSpan FirstDelay { get; init; }

        public TimeSpan? PromptExpiresIn { get; init; }

        public Action<DateTimeOffset>? SignInPrompted { get; set; }

        public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                if (PromptExpiresIn is { } expiresIn)
                {
                    SignInPrompted?.Invoke(DateTimeOffset.UtcNow + expiresIn);
                }

                await Task.Delay(FirstDelay, cancellationToken);
            }

            return StaticTokenProvider.DefaultToken;
        }
    }
}
