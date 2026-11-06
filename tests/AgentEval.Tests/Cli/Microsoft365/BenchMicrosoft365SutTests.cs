// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Cli.Microsoft365;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Cli.Microsoft365;

/// <summary>
/// <c>bench … --sut microsoft365</c> through <c>BenchTier1SutResolver.Resolve</c> (the pure resolution every bench
/// verb with a <c>--sut</c> seam calls). Mirrors <c>BenchTier1SutResolverTests</c>: a refused gate surfaces as a friendly
/// error (exit 2 at the command), and the <c>sutOverride</c> seam drives the REAL target credential-free.
/// </summary>
public class BenchMicrosoft365SutTests
{
    private static readonly IReadOnlyDictionary<string, ISutTargetOptions?> NoTargetOptions =
        new Dictionary<string, ISutTargetOptions?>();

    [Fact]
    public void BenchTargets_IncludeMicrosoft365()
    {
        Assert.Contains(BenchTargets(), t => t.Sut == "microsoft365");
    }

    [Fact]
    public void Resolve_UnknownSut_NamesMicrosoft365CopilotStudioAndMock()
    {
        var (agent, error) = BenchTier1SutResolver.Resolve(
            sut: "bogus", NoTargetOptions, BenchTargets(), ackLiveSideEffects: true, endpoint: null, model: null, apiKey: null, subject: "MyAgent");

        Assert.Null(agent);
        Assert.NotNull(error);
        Assert.Contains("microsoft365", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("copilot-studio", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mock", error, StringComparison.OrdinalIgnoreCase);
    }

    // ── safety gates surface as a friendly error, not an exception ──

    [Fact]
    public void Resolve_Microsoft365_WithoutConsent_ReturnsError()
    {
        var cfg = WriteValidConfig();
        try
        {
            var (agent, error) = BenchTier1SutResolver.Resolve(
                sut: "microsoft365", Options(cfg), BenchTargets(), ackLiveSideEffects: false, endpoint: null, model: null, apiKey: null, subject: "MyAgent",
                sutOverride: new FakeEvaluableAgent());

            Assert.Null(agent);
            Assert.NotNull(error);
            Assert.Contains("i-understand-live-side-effects", error, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(cfg); }
    }

    [Fact]
    public void Resolve_Microsoft365_WithoutConfig_ReturnsError()
    {
        var (agent, error) = BenchTier1SutResolver.Resolve(
            sut: "microsoft365", Options(null), BenchTargets(), ackLiveSideEffects: true, endpoint: null, model: null, apiKey: null, subject: "MyAgent");

        Assert.Null(agent);
        Assert.NotNull(error);
        Assert.Contains("m365-config", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_Microsoft365_ConfigMissingAField_ReturnsError()
    {
        var cfg = WriteConfig("{ \"appClientId\": \"app-1\" }");   // tenantId missing
        try
        {
            var (agent, error) = BenchTier1SutResolver.Resolve(
                sut: "microsoft365", Options(cfg), BenchTargets(), ackLiveSideEffects: true, endpoint: null, model: null, apiKey: null, subject: "MyAgent",
                sutOverride: new FakeEvaluableAgent());

            Assert.Null(agent);
            Assert.NotNull(error);
            Assert.Contains("tenantId", error, StringComparison.Ordinal);
        }
        finally { TryDelete(cfg); }
    }

    // ── full credential-free resolution via sutOverride, through the REAL target ──

    [Fact]
    public void Resolve_Microsoft365_BenignAgent_ResolvesOffline()
    {
        var cfg = WriteValidConfig();
        try
        {
            IEvaluableAgent fake = new FakeEvaluableAgent();

            var (agent, error) = BenchTier1SutResolver.Resolve(
                sut: "microsoft365", Options(cfg), BenchTargets(), ackLiveSideEffects: true, endpoint: null, model: null, apiKey: null, subject: "MyAgent",
                sutOverride: fake);

            Assert.Null(error);
            var bench = Assert.IsType<Microsoft365BenchAgent>(agent);
            Assert.Same(fake, bench.Inner);
            Assert.Equal(fake.Name, bench.Name);
        }
        finally { TryDelete(cfg); }
    }

    // ── a turn with no selected answer is never graded by a bench family ──

    [Fact]
    public void ForGradedBench_TellsTheTurnSink_SoTheWarningSaysNotMeasured_NotGradedOnAnEmptyText()
    {
        var sink = new CliTurnSink(verbose: false, TextWriter.Null);
        var target = new Microsoft365SutTarget(TextWriter.Null) { TurnSinkForTests = sink };

        Assert.False(sink.UnselectedAnswerIsNotMeasured);
        _ = target.ForGradedBench(new FakeEvaluableAgent());

        Assert.True(sink.UnselectedAnswerIsNotMeasured);
    }

    [Fact]
    public async Task Resolve_Microsoft365_ATurnWithNoSelectedAnswer_ThrowsInsteadOfHandingItsEmptyTextToTheFamily()
    {
        // The rule every host follows: a completed turn with several answer candidates and none selected is not measured.
        // A bench family grades the text it gets, so the agent it drives throws, and the family records an error.
        var cfg = WriteValidConfig();
        try
        {
            var conversation = new AgentEval.Tests.Microsoft365.Agent.FakeWorkIqConversation
            {
                OnRun = (request, _) => Task.FromResult(AgentEval.Tests.Microsoft365.Agent.TurnResults.Completed(request) with
                {
                    AnswerText = null,
                    AnswerCompleteness = AgentEval.Microsoft365.OutputCompleteness.Ambiguous,
                }),
            };
            var real = AgentEval.Microsoft365.Microsoft365AgentFactory.FromConversation(conversation, "m365-ambiguous");
            await using (real)
            {
                var (agent, error) = BenchTier1SutResolver.Resolve(
                    sut: "microsoft365", Options(cfg), BenchTargets(), ackLiveSideEffects: true, endpoint: null, model: null, apiKey: null, subject: "MyAgent",
                    sutOverride: real);

                Assert.Null(error);
                await Assert.ThrowsAsync<Microsoft365NoAnswerSelectedException>(() => agent!.InvokeAsync("When does the pilot start?"));

                // A turn with a selected answer passes through unchanged.
                conversation.OnRun = (request, _) => Task.FromResult(AgentEval.Tests.Microsoft365.Agent.TurnResults.Completed(request));
                Assert.Equal("The pilot starts on 17 November.", (await agent!.InvokeAsync("When does the pilot start?")).Text);
            }
        }
        finally { TryDelete(cfg); }
    }

    [Fact]
    public void Resolve_Microsoft365_ForPerf_TheAgentIsNotWrapped()
    {
        // perf measures time: a turn with no selected answer is still a timed turn, so its agent is left as built.
        var cfg = WriteValidConfig();
        try
        {
            IEvaluableAgent fake = new FakeEvaluableAgent();

            var (agent, error) = BenchTier1SutResolver.Resolve(
                sut: "microsoft365", Options(cfg), BenchTargets(), ackLiveSideEffects: true, endpoint: null, model: null, apiKey: null, subject: "MyAgent",
                sutOverride: fake, gradesAnswers: false);

            Assert.Null(error);
            Assert.Same(fake, agent);
        }
        finally { TryDelete(cfg); }
    }

    // ── every bench verb registers both live targets' flags and ONE consent flag ──

    [Fact]
    public void AddOptionsTo_Bench_RegistersM365AndCopilotStudioOptions_AndOneConsentFlag()
    {
        var command = new Command("bench-test");

        var (sutOpt, ackOpt, targets) = SutTargetResolver.AddOptionsTo(command, "bench");

        Assert.Contains(targets, t => t.Sut == "microsoft365");
        Assert.Contains(targets, t => t.Sut == "copilot-studio");
        Assert.Single(command.Options, o => o.Name == "--i-understand-live-side-effects");
        foreach (var name in new[] { "--m365-config", "--m365-max-turns", "--m365-profile", "--m365-capture-raw", "--m365-verbose", "--m365-trace", "--copilotstudio-config", "--max-credits" })
        {
            Assert.Single(command.Options, o => o.Name == name);
        }

        var parsed = command.Parse("--sut microsoft365 --i-understand-live-side-effects --m365-config c.json");
        Assert.Empty(parsed.Errors);
        Assert.Equal("microsoft365", parsed.GetValue(sutOpt));
        Assert.True(parsed.GetValue(ackOpt));
        Assert.Contains("microsoft365", sutOpt.Description!, StringComparison.Ordinal);
    }

    // ── helpers ──

    private static List<ISutTarget> BenchTargets() =>
        SutTargetResolver.BuiltInTargets().Where(t => t.SupportedVerbs.Contains("bench")).ToList();

    private static Dictionary<string, ISutTargetOptions?> Options(FileInfo? cfg) => new()
    {
        ["microsoft365"] = new Microsoft365SutOptions { ConfigFile = cfg },
    };

    private static FileInfo WriteValidConfig() => WriteConfig("{ \"tenantId\": \"tenant-1\", \"appClientId\": \"app-1\" }");

    private static FileInfo WriteConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-bench-m365-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return new FileInfo(path);
    }

    private static void TryDelete(FileInfo f)
    {
        try { if (f.Exists) { f.Delete(); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }
}
