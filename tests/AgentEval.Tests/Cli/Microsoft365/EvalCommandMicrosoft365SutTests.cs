// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Cli.Microsoft365;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Cli.Microsoft365;

/// <summary>
/// M30: <c>eval --sut microsoft365</c> through the shared <c>ISutTarget</c> seam. Mirrors
/// <c>EvalCommandCopilotStudioSutTests</c>' shape: safety-gate validation (throws before any build/network), the
/// dataset-first precedence, then a full credential-free eval via <c>sutOverride</c> with a benign fake agent (the
/// stand-in is independent of <c>WorkIqEvaluableAgent</c>, whose live turn is another lane's).
/// </summary>
public class EvalCommandMicrosoft365SutTests
{
    // ── safety-gate validation (throws before any build/network) ──

    [Fact]
    public async Task Eval_Microsoft365_WithoutConsent_Refuses()
    {
        var dataset = CreateTempDataset();
        try
        {
            var opts = BaseOptions(dataset, ackConsent: false, config: null);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EvalCommand.ExecuteAsync(opts, default));
            Assert.Contains("i-understand-live-side-effects", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dataset); }
    }

    [Fact]
    public async Task Eval_Microsoft365_WithoutConfig_Refuses()
    {
        var dataset = CreateTempDataset();
        try
        {
            var opts = BaseOptions(dataset, ackConsent: true, config: null);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EvalCommand.ExecuteAsync(opts, default));
            Assert.Contains("m365-config", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dataset); }
    }

    [Fact]
    public async Task Eval_Microsoft365_NegativeMaxTurns_Refuses()
    {
        var dataset = CreateTempDataset();
        var cfg = WriteValidConfig();
        try
        {
            var opts = BaseOptions(dataset, ackConsent: true, config: cfg, maxTurns: -1);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EvalCommand.ExecuteAsync(opts, default));
            Assert.Contains("m365-max-turns", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dataset); TryDelete(cfg); }
    }

    [Fact]
    public async Task Eval_Microsoft365_BadProfile_Refuses()
    {
        var dataset = CreateTempDataset();
        var cfg = WriteValidConfig();
        try
        {
            var opts = BaseOptions(dataset, ackConsent: true, config: cfg, profile: "bogus");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EvalCommand.ExecuteAsync(opts, default));
            Assert.Contains("m365-profile", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dataset); TryDelete(cfg); }
    }

    [Fact]
    public async Task Eval_Microsoft365_ConfigMissingAField_FailsFastBeforeAnyTurn()
    {
        var dataset = CreateTempDataset();
        var cfg = WriteConfig("{ \"tenantId\": \"tenant-1\" }");
        try
        {
            var fake = new FakeEvaluableAgent();
            var opts = BaseOptions(dataset, ackConsent: true, config: cfg);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EvalCommand.ExecuteAsync(opts, default, sutOverride: fake));

            Assert.Contains("appClientId", ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, fake.Invocations);
        }
        finally { TryDelete(dataset); TryDelete(cfg); }
    }

    [Fact]
    public async Task Eval_UnknownSut_NamesMicrosoft365AndCopilotStudio()
    {
        var dataset = CreateTempDataset();
        try
        {
            var opts = BaseOptions(dataset, ackConsent: true, config: null, sut: "bogus");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EvalCommand.ExecuteAsync(opts, default));
            Assert.Contains("microsoft365", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("copilot-studio", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dataset); }
    }

    [Fact]
    public async Task Eval_MissingDataset_ThrowsBeforeSutValidation()
    {
        // Dataset-first precedence on the --sut path (the regression guard the Copilot Studio tests also keep): a
        // missing consent must not mask a typo'd --dataset path.
        var opts = BaseOptions(new FileInfo("/nonexistent/path/to/dataset.yaml"), ackConsent: false, config: null);
        await Assert.ThrowsAsync<FileNotFoundException>(() => EvalCommand.ExecuteAsync(opts, default));
    }

    // ── the command line binds the m365 flags ──

    [Fact]
    public void EvalCommand_Create_ParsesTheM365Options()
    {
        var command = EvalCommand.Create();

        var parsed = command.Parse("--dataset d.yaml --sut microsoft365 --i-understand-live-side-effects --m365-config c.json --m365-max-turns 2 --m365-profile native118 --m365-capture-raw raw --m365-verbose --m365-trace t.jsonl");

        Assert.Empty(parsed.Errors);
    }

    // ── credential-free end-to-end eval via the sutOverride seam ──

    [Fact]
    public async Task Eval_Microsoft365_BenignAgent_RunsCleanOffline()
    {
        var dataset = CreateTempDataset();
        var cfg = WriteValidConfig();
        try
        {
            var fake = new FakeEvaluableAgent();
            var opts = BaseOptions(dataset, ackConsent: true, config: cfg);

            var exit = await EvalCommand.ExecuteAsync(opts, default, sutOverride: fake);

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Equal(1, fake.Invocations);   // one case, one turn: the stand-in was actually driven
        }
        finally { TryDelete(dataset); TryDelete(cfg); }
    }

    [Fact]
    public async Task Eval_Microsoft365_RunsGreaterThan1_WithMetricsAndNoJudge_WarnsInsteadOfThrowing()
    {
        // Same guard as the Copilot Studio twin: `--runs 5 --metrics llm_relevance` with no --judge (a --sut target
        // exposes no raw IChatClient to fall back to) must reach the warning-and-ignore path, never throw.
        var dataset = CreateTempDataset();
        var cfg = WriteValidConfig();
        try
        {
            var fake = new FakeEvaluableAgent();
            var opts = BaseOptions(dataset, ackConsent: true, config: cfg, runs: 5, metrics: "llm_relevance");

            var exit = await EvalCommand.ExecuteAsync(opts, default, sutOverride: fake);

            Assert.True(exit is ExitCodes.Success or ExitCodes.TestFailure);
            Assert.Equal(5, fake.Invocations);
        }
        finally { TryDelete(dataset); TryDelete(cfg); }
    }

    // ── helpers ──

    private static EvalOptions BaseOptions(
        FileInfo dataset, bool ackConsent, FileInfo? config, string? sut = "microsoft365", int runs = 1, string? metrics = null,
        int maxTurns = 0, string profile = "public") => new()
    {
        Dataset = dataset,
        Sut = sut,
        AckLiveSideEffects = ackConsent,   // the common consent flag lives on EvalOptions, not on the target's own options
        TargetOptions = new Dictionary<string, ISutTargetOptions?>
        {
            ["microsoft365"] = new Microsoft365SutOptions { ConfigFile = config, MaxTurns = maxTurns, Profile = profile },
        },
        Format = "json",
        Quiet = true,
        Runs = runs,
        Metrics = metrics,
    };

    private static FileInfo CreateTempDataset()
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-eval-m365-dataset-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, """
            - id: test1
              input: "Hello"
              expectedOutput: "Hi"
            """);
        return new FileInfo(path);
    }

    private static FileInfo WriteValidConfig() => WriteConfig("{ \"tenantId\": \"tenant-1\", \"appClientId\": \"app-1\" }");

    private static FileInfo WriteConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-eval-m365-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return new FileInfo(path);
    }

    private static void TryDelete(FileInfo f)
    {
        try { if (f.Exists) { f.Delete(); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }
}
