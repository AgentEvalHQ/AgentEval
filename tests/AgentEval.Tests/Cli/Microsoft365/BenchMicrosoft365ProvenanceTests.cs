// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Cli.Microsoft365;
using AgentEval.Microsoft365;
using AgentEval.Results.Integrity;
using Xunit;

namespace AgentEval.Tests.Cli.Microsoft365;

/// <summary>
/// M60 / phase-2 open issue: a <c>bench … --sut microsoft365</c> run records the same Microsoft 365 provenance keys in
/// its AEF run config as <c>eval</c> does (a2aVersion, m365Profile, normalizerVersion, providerProfileVersion,
/// m365ConfigSha256, m365AgentId, m365EndpointHost). <see cref="Microsoft365SutTarget.BenchRunSettings"/> derives them
/// from the validated target, and every bench family with a <c>--sut</c> seam threads them into
/// <c>BenchAef.Write</c> / <c>WriteScan</c> / <c>WriteResult</c>. No per-case evidence document exists in a bench run,
/// so no content policy is claimed. Offline: the scan families run their mock, the compliance families grade a supplied
/// answer with a passing stand-in evaluator.
/// </summary>
[Collection("ConsoleTests")]
public sealed class BenchMicrosoft365ProvenanceTests : IDisposable
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string AppClientId = "22222222-2222-2222-2222-222222222222";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenteval-m365-bench-aef-" + Guid.NewGuid().ToString("N"));

    public BenchMicrosoft365ProvenanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public void BenchRunSettings_Microsoft365_IsTheValidatedTargetsProvenanceWithoutAContentPolicy()
    {
        var (target, config) = ValidatedTarget(profile: "native118");

        var settings = Microsoft365SutTarget.BenchRunSettings([target], " Microsoft365 ")!;

        Assert.Equal("1.0", (string?)settings["a2aVersion"]);
        Assert.Equal("Native118Diagnostics", (string?)settings["m365Profile"]);
        Assert.Equal(AgentEval.Microsoft365.Evidence.Microsoft365Evidence.NormalizerVersion, (string?)settings["normalizerVersion"]);
        Assert.Equal(AgentEval.Microsoft365.Evidence.Microsoft365Evidence.ProviderProfileVersion, (string?)settings["providerProfileVersion"]);
        Assert.Equal(config.ComputeIdentitySha256(), (string?)settings["m365ConfigSha256"]);
        Assert.Equal("default", (string?)settings["m365AgentId"]);
        Assert.Equal("workiq.svc.cloud.microsoft", (string?)settings["m365EndpointHost"]);
        Assert.False(settings.ContainsKey("m365ContentPolicy"));   // a bench run projects no evidence document
        Assert.DoesNotContain(TenantId, settings.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BenchRunSettings_OtherSutOrUnvalidatedTarget_IsNull()
    {
        var (target, _) = ValidatedTarget();

        Assert.Null(Microsoft365SutTarget.BenchRunSettings([target], "copilot-studio"));
        Assert.Null(Microsoft365SutTarget.BenchRunSettings([target], null));
        Assert.Null(Microsoft365SutTarget.BenchRunSettings([new Microsoft365SutTarget(TextWriter.Null)], "microsoft365"));
        Assert.Null(Microsoft365SutTarget.BenchRunSettings([], "microsoft365"));
    }

    [Theory]
    [InlineData("owasp")]
    [InlineData("mitre")]
    [InlineData("nist")]
    [InlineData("perf")]
    public async Task ScanAndPerfFamilies_SubjectSettings_ReachTheRunConfig(string family)
    {
        var (target, config) = ValidatedTarget();
        var settings = Microsoft365SutTarget.BenchRunSettings([target], "microsoft365")!;
        var aef = new DirectoryInfo(Path.Combine(_root, "aef-" + family));
        Directory.CreateDirectory(Path.Combine(_root, ".agenteval"));

        var exit = family switch
        {
            "owasp" => (await BenchOwaspCommand.RunAsync("smoke", "m365-subject", _root, null, null, null, mock: true, aef: aef, subjectSettings: settings)).ExitCode,
            "mitre" => (await BenchMitreCommand.RunAsync("smoke", "m365-subject", _root, null, null, null, mock: true, aef: aef, subjectSettings: settings)).ExitCode,
            "nist" => (await BenchNistCommand.RunAsync("smoke", "m365-subject", _root, null, null, null, mock: true, aef: aef, subjectSettings: settings)).ExitCode,
            _ => await BenchPerfCommand.RunAsync("latency", "m365-subject", "test input", _root, null, mock: true, aef: aef, subjectSettings: settings),
        };

        Assert.Equal(ExitCodes.GateIndeterminate, exit);
        AssertProvenance(aef, config);
    }

    [Theory]
    [InlineData("gdpr")]
    [InlineData("eu-ai-act")]
    public async Task ComplianceFamilies_SubjectSettings_ReachTheRunConfig(string family)
    {
        var (target, config) = ValidatedTarget();
        var settings = Microsoft365SutTarget.BenchRunSettings([target], "microsoft365")!;
        var aef = new DirectoryInfo(Path.Combine(_root, "aef-" + family));
        await new AgentEval.Output.FileSystemOutputStore(Path.Combine(_root, ".agenteval")).InitializeSolutionAsync("m365-bench-provenance");

        var exit = family == "gdpr"
            ? await BenchCommand.RunGdprAsync("standard", "m365-subject", _root, "question", new PassingEvaluator(), responseText: "answer", aef: aef, subjectSettings: settings)
            : await BenchEuAiActCommand.RunAsync("standard", "m365-subject", _root, "question", new PassingEvaluator(), responseText: "answer", aef: aef, subjectSettings: settings);

        Assert.Equal(ExitCodes.Success, exit);
        AssertProvenance(aef, config);
    }

    [Fact]
    public void AddSubjectSettings_KeyTheCommandAlreadyWrote_ThrowsInsteadOfReplacingIt()
    {
        var settings = new JsonObject { ["preset"] = "smoke" };

        Assert.Throws<ArgumentException>(() => BenchAef.AddSubjectSettings(settings, new JsonObject { ["preset"] = "other" }));
        Assert.Equal("smoke", (string?)settings["preset"]);

        BenchAef.AddSubjectSettings(settings, null);
        Assert.Single(settings);
    }

    // ---------------------------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------------------------

    private (Microsoft365SutTarget Target, Microsoft365Config Config) ValidatedTarget(string profile = "public")
    {
        var path = Path.Combine(_root, "m365-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, $$"""{ "tenantId": "{{TenantId}}", "appClientId": "{{AppClientId}}" }""");
        var target = new Microsoft365SutTarget(TextWriter.Null);
        target.Validate(
            new CommonTargetOptions { Sut = Microsoft365SutTarget.SutName, AckLiveSideEffects = true },
            new Microsoft365SutOptions { ConfigFile = new FileInfo(path), Profile = profile });
        return (target, Microsoft365Config.Load(new FileInfo(path)));
    }

    private static void AssertProvenance(DirectoryInfo aef, Microsoft365Config config)
    {
        var runFile = Assert.Single(Directory.GetFiles(aef.FullName, "run.json", SearchOption.AllDirectories));
        var verification = AefRunVerifier.Verify(Path.GetDirectoryName(runFile)!);
        Assert.Empty(verification.Problems);
        Assert.Equal(AefOutcome.Intact, verification.Outcome);
        var run = JsonNode.Parse(File.ReadAllText(runFile))!;
        var settings = run["config"]!;
        Assert.NotNull(settings["preset"]);   // the command's own keys are still there
        Assert.Equal("1.0", (string?)settings["a2aVersion"]);
        Assert.Equal("PublicAnswerOnly", (string?)settings["m365Profile"]);
        Assert.Equal(config.ComputeIdentitySha256(), (string?)settings["m365ConfigSha256"]);
        Assert.Equal("default", (string?)settings["m365AgentId"]);
        Assert.Equal("workiq.svc.cloud.microsoft", (string?)settings["m365EndpointHost"]);
        Assert.Null(settings["m365ContentPolicy"]);
        Assert.DoesNotContain(TenantId, run.ToJsonString(), StringComparison.Ordinal);
    }

    private sealed class PassingEvaluator : AgentEval.Core.IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default) =>
            Task.FromResult(new AgentEval.Core.EvaluationResult
            {
                OverallScore = 100,
                Summary = "test",
                CriteriaResults = criteria.Select(c => new AgentEval.Core.CriterionResult { Criterion = c, Met = true, Explanation = "test" }).ToList(),
            });
    }
}
