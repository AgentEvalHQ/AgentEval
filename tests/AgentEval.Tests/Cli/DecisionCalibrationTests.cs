// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Decisions;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// <c>bench gdpr calibrate --decisions</c> and <c>bench eu-ai-act calibrate --decisions</c>
/// (<see cref="DecisionCalibration"/>): the decision model grades the golden datasets, and the report names it.
/// </summary>
/// <remarks>
/// Before, <c>Program.cs</c> passed the <see cref="DecisionJudge"/> as an evaluator override with no identity, so the
/// report said <c>Judge provider: unknown: an evaluator supplied by the caller (DecisionJudge)</c> and
/// <c>Judge model: unknown</c>. The report tests below fail on that behaviour: they assert the provider and the
/// requested model instead. No test here makes a network call: the decision client is a fake.
/// </remarks>
[Collection("EnvVarTests")]
public class DecisionCalibrationTests : IDisposable
{
    private const string FakeKey = "fake-decision-key-not-a-secret-4b1d";

    private readonly string _root;

    public DecisionCalibrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-decision-calibrate-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Answers every question with the same probability, and records that it was used and disposed.</summary>
    private sealed class FakeDecisionClient : IDecisionClient, IDisposable
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool Disposed { get; private set; }

        public Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var answers = request.Questions.Keys.ToDictionary(
                k => k, _ => (DecisionAnswer)new BinaryAnswer(0.9), StringComparer.Ordinal);
            return Task.FromResult(new DecisionResponse("jev-1.13-served-build", answers, new DecisionUsage(10, 1)));
        }

        public void Dispose() => Disposed = true;
    }

    // ── The switch ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutDecisions_RunsWithNoOverride_AndResolvesNoTransport()
    {
        var ran = false;
        IEvaluator? seenJudge = null;
        CalibrationJudgeIdentity? seenIdentity = null;

        var exitCode = await DecisionCalibration.RunAsync(
            decisions: false,
            (judge, identity) => { ran = true; seenJudge = judge; seenIdentity = identity; return Task.FromResult(7); },
            resolveOptions: () => throw new InvalidOperationException("The transport must not be resolved without --decisions."));

        Assert.True(ran);
        Assert.Equal(7, exitCode);
        Assert.Null(seenJudge);
        Assert.Null(seenIdentity);
    }

    [Fact]
    public async Task WithDecisions_NoTransport_ReturnsRuntimeError_AndDoesNotRunTheCalibration()
    {
        var ran = false;

        var exitCode = await DecisionCalibration.RunAsync(
            decisions: true,
            (_, _) => { ran = true; return Task.FromResult(0); },
            resolveOptions: () => (null, "Neither TYPESAFE_API_KEY nor OPENROUTER_API_KEY is set."));

        Assert.Equal(ExitCodes.RuntimeError, exitCode);
        Assert.False(ran);
    }

    [Fact]
    public async Task WithDecisions_PassesADecisionJudgeAndItsIdentity_AndDisposesTheClient()
    {
        var client = new FakeDecisionClient();
        IEvaluator? seenJudge = null;
        CalibrationJudgeIdentity? seenIdentity = null;

        var exitCode = await DecisionCalibration.RunAsync(
            decisions: true,
            (judge, identity) => { seenJudge = judge; seenIdentity = identity; return Task.FromResult(0); },
            resolveOptions: () => (SystemOneClientOptions.ForTypeSafe(FakeKey, "jev-1.13"), null),
            createClient: _ => client);

        Assert.Equal(0, exitCode);
        Assert.IsType<DecisionJudge>(seenJudge);
        // Before: no identity reached the report, which then named the judge "unknown".
        Assert.NotNull(seenIdentity);
        Assert.Equal("TypeSafe decision model (--decisions)", seenIdentity!.Provider);
        Assert.StartsWith("jev-1.13 (requested;", seenIdentity.Model, StringComparison.Ordinal);
        Assert.True(client.Disposed, "The decision client must be disposed once the calibration has run.");
    }

    // ── The identity ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("typesafe", "TypeSafe")]
    [InlineData("openrouter", "OpenRouter")]
    public void IdentityOf_NamesTheProviderAndTheRequestedModel_NeverTheKeyOrTheEndpoint(string transport, string providerName)
    {
        var options = transport == "typesafe"
            ? SystemOneClientOptions.ForTypeSafe(FakeKey, "jev-1.13")
            : SystemOneClientOptions.ForOpenRouter(FakeKey, "typesafe/jev-1.13");

        var identity = DecisionCalibration.IdentityOf(options);

        Assert.StartsWith(providerName + " ", identity.Provider, StringComparison.Ordinal);
        Assert.StartsWith(options.Model + " ", identity.Model, StringComparison.Ordinal);
        // The model is what was asked for; the provider may answer with another build under that name.
        Assert.Contains("requested", identity.Model, StringComparison.Ordinal);
        foreach (var text in new[] { identity.Provider, identity.Model })
        {
            Assert.DoesNotContain(FakeKey, text, StringComparison.Ordinal);
            Assert.DoesNotContain(options.Endpoint.Host, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unknown", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── The report ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GdprCalibrateWithDecisions_ReportNamesTheDecisionModel()
    {
        var outPath = Path.Combine(_root, "gdpr-decisions.md");
        var client = new FakeDecisionClient();

        // The same call Program.cs makes for `bench gdpr calibrate --decisions`, with the transport faked.
        await DecisionCalibration.RunAsync(
            decisions: true,
            (judge, identity) => BenchCalibrateCommand.RunCoreAsync(
                _root, outPath, evaluatorOverride: judge, evaluatorOverrideIdentity: identity),
            resolveOptions: () => (SystemOneClientOptions.ForTypeSafe(FakeKey, "jev-1.13"), null),
            createClient: _ => client);

        AssertReportNamesTheDecisionModel(await File.ReadAllTextAsync(outPath), client);
    }

    [Fact]
    public async Task EuAiActCalibrateWithDecisions_ReportNamesTheDecisionModel()
    {
        var outPath = Path.Combine(_root, "eu-ai-act-decisions.md");
        var client = new FakeDecisionClient();

        // The same call Program.cs makes for `bench eu-ai-act calibrate --decisions`, with the transport faked.
        await DecisionCalibration.RunAsync(
            decisions: true,
            (judge, identity) => BenchEuAiActCalibrateCommand.RunCoreAsync(
                _root, outPath, evaluatorOverride: judge, evaluatorOverrideIdentity: identity),
            resolveOptions: () => (SystemOneClientOptions.ForTypeSafe(FakeKey, "jev-1.13"), null),
            createClient: _ => client);

        AssertReportNamesTheDecisionModel(await File.ReadAllTextAsync(outPath), client);
    }

    private static void AssertReportNamesTheDecisionModel(string report, FakeDecisionClient client)
    {
        Assert.True(client.Calls > 0, "The fake decision client was never asked: the report does not come from the decision judge.");
        Assert.Contains("Judge provider: TypeSafe decision model (--decisions)", report);
        Assert.Contains("Judge model: jev-1.13 (requested;", report);
        Assert.DoesNotContain("Judge provider: unknown", report);
        Assert.DoesNotContain("Judge model: unknown", report);
        Assert.DoesNotContain(FakeKey, report);
    }
}
