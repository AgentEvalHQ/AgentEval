// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands.Targets;
using AgentEval.Cli.Microsoft365;
using AgentEval.Microsoft365;
using AgentEval.Tests.Microsoft365.Auth;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Cli.Microsoft365;

/// <summary>
/// The LIVE <see cref="Microsoft365SutTarget.Build"/> path (no <c>sutOverride</c>), offline. Only
/// the two outer edges are replaced through the target's test seams — the token source and the innermost HTTP handler —
/// and the diagnostics go to a temp folder. Everything the CLI does on a tester's machine runs: the option mapping,
/// the honesty badge with the exact trace and issue paths, the turn sink and a real turn.
/// </summary>
public sealed class Microsoft365SutTargetBuildTests : IDisposable
{
    private const string CompletedFixture = "envelopes/send-task-completed-with-answer-and-attributions.json";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("agenteval-m365-build-");

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public async Task Build_Live_MapsTheOptions_PrintsTheBadgeWithTheRealPaths_AndRunsATurnThroughTheSink()
    {
        var handler = new FakeWorkIqHandler().OnCard("cards/default-card-two-interfaces.json").OnRpc("SendMessage", CompletedFixture);
        var tokens = new StaticTokenProvider();
        var err = new StringWriter();
        var diagnostics = new DirectoryInfo(Path.Combine(_root.FullName, "diag"));
        var target = new Microsoft365SutTarget(err)
        {
            TokenProviderForTests = tokens,
            InnerHandlerForTests = handler,
            DiagnosticsDirectoryForTests = diagnostics,
        };
        var common = new CommonTargetOptions { Sut = Microsoft365SutTarget.SutName, AckLiveSideEffects = true };
        var own = new Microsoft365SutOptions { ConfigFile = WriteConfig(), MaxTurns = 2, Profile = "public", Verbose = true };
        target.Validate(common, own);

        var built = target.Build(common, own, sutOverride: null);

        var agent = Assert.IsType<WorkIqEvaluableAgent>(built);
        await using (agent)
        {
            Assert.Equal(ConversationPolicy.FreshContextPerInvocation, agent.Options.ConversationPolicy);
            Assert.Equal(2, agent.Options.MaxTurns);
            Assert.Equal(DiagnosticProfile.PublicAnswerOnly, agent.Options.ProfileOverride);
            Assert.Equal(EvidenceContentPolicy.SelectedContent, agent.Options.ContentPolicy);
            Assert.True(agent.Options.Verbose);
            handler.AssertNoRequests();   // building makes no network call
            Assert.Equal(0, tokens.Calls);

            // The badge names the exact files the run writes (the run id is minted before the build, no placeholder).
            var traceFile = Path.Combine(diagnostics.FullName, $"m365-trace-{agent.Diagnostics.RunId}.jsonl");
            var badge = err.ToString();
            Assert.Contains(Microsoft365SutTarget.TargetBadge, badge, StringComparison.Ordinal);
            Assert.Contains(traceFile, badge, StringComparison.Ordinal);
            Assert.Contains("Turn cap: 2", badge, StringComparison.Ordinal);
            Assert.DoesNotContain("{runId}", badge, StringComparison.Ordinal);

            agent.NextCaseId = "case-build";
            var response = await agent.InvokeAsync("Summarize the status of Project Atlas and cite the sources.");

            Assert.StartsWith("Project Atlas is on schedule", response.Text, StringComparison.Ordinal);
            Assert.NotNull(target.TurnSink);
            Assert.Equal(1, target.TurnSink.Count);
            var turn = target.TurnSink.LatestFor("case-build", since: 0);
            Assert.NotNull(turn);
            Assert.Equal("case-build", turn.Identity.CaseId);
            Assert.Contains("[m365 turn] case=case-build", err.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(traceFile));
            Assert.Equal(1, handler.CountOf("SendMessage"));
        }
    }

    [Fact]
    public async Task Build_Live_CaptureRaw_SelectsFullEvidence()
    {
        var target = new Microsoft365SutTarget(TextWriter.Null)
        {
            TokenProviderForTests = new StaticTokenProvider(),
            InnerHandlerForTests = new FakeWorkIqHandler(),
            DiagnosticsDirectoryForTests = new DirectoryInfo(Path.Combine(_root.FullName, "diag")),
        };
        var common = new CommonTargetOptions { Sut = Microsoft365SutTarget.SutName, AckLiveSideEffects = true };
        var raw = new DirectoryInfo(Path.Combine(_root.FullName, "raw"));
        var own = new Microsoft365SutOptions { ConfigFile = WriteConfig(), Profile = "native118", CaptureRaw = raw };
        target.Validate(common, own);

        var agent = Assert.IsType<WorkIqEvaluableAgent>(target.Build(common, own, sutOverride: null));
        await using (agent)
        {
            Assert.Equal(EvidenceContentPolicy.FullEvidence, agent.Options.ContentPolicy);
            Assert.Equal(raw.FullName, agent.Options.RawCaptureDirectory!.FullName);
            Assert.Equal(DiagnosticProfile.Native118Diagnostics, agent.Options.ProfileOverride);
        }
    }

    private FileInfo WriteConfig()
    {
        var path = Path.Combine(_root.FullName, "m365-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ \"tenantId\": \"11111111-1111-1111-1111-111111111111\", \"appClientId\": \"22222222-2222-2222-2222-222222222222\" }");
        return new FileInfo(path);
    }
}
