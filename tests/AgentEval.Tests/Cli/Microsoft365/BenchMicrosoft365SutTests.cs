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
/// M30: <c>bench … --sut microsoft365</c> through <c>BenchTier1SutResolver.Resolve</c> (the pure resolution every bench
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
