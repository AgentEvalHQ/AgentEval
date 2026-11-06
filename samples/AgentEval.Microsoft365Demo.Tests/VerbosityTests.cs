// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365Demo.Tests.TestHelpers;

namespace AgentEval.Microsoft365Demo.Tests;

/// <summary>
/// <c>--verbosity</c> changes the screen only: quiet shows the final table and errors; normal adds stage, turn and issue
/// lines; detailed adds every trace event (the provider's one-line format, without payloads) and an issue detail block;
/// diagnostic adds the event payloads and timings. <c>console.log</c> holds the diagnostic view at every level. The
/// scenario has something of every kind: readiness stages, a completed prompt, a completed case, and a case that pauses for
/// confirmation (a turn that did not complete, with its issue).
/// </summary>
public sealed class VerbosityTests : IDisposable
{
    private const string PauseMarker = "PAUSE-MARKER-41d0";
    private const string TracePrefix = "[m365 ";

    private readonly DemoRig _rig = new();

    public void Dispose() => _rig.Dispose();

    public static TheoryData<string> Levels => new() { "quiet", "normal", "detailed", "diagnostic" };

    [Fact]
    public async Task Quiet_ShowsTheFinalTable_ButNoBannerStageTurnIssueOrTraceLines()
    {
        var run = await RunScenarioAsync("quiet");

        Assert.Contains("Result: NOT MEASURED (exit 11)", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("Readiness (no prompt is sent)", run.Out, StringComparison.Ordinal);
        Assert.Empty(TraceLines(run));
        Assert.DoesNotContain(run.OutLines, l => l.TrimStart().StartsWith("! issue ", StringComparison.Ordinal));
        Assert.DoesNotContain(run.OutLines, l => l.TrimStart().StartsWith("! ISSUE ", StringComparison.Ordinal));
        Assert.DoesNotContain(run.OutLines, l => l.TrimStart().StartsWith("turn  ", StringComparison.Ordinal));

        // The stage appears once: in the final table, not as a live stage line.
        Assert.Single(run.OutLines, l => l.Contains("✓ readiness: auth", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Normal_ShowsStageTurnAndIssueLines_ButNoTraceLinesOrDetailBlocks()
    {
        var run = await RunScenarioAsync("normal");

        Assert.Contains("Readiness (no prompt is sent)", run.Out, StringComparison.Ordinal);
        Assert.True(
            run.OutLines.Count(l => l.Contains("✓ readiness: auth", StringComparison.Ordinal)) >= 2,
            "normal shows the live stage line and the final table's row.\n" + run.Describe());
        Assert.Contains(run.OutLines, l => l.Contains("turn  prompt  Completed", StringComparison.Ordinal));
        Assert.Contains(run.OutLines, l => l.Contains("turn  case_pause  InputRequired", StringComparison.Ordinal));
        Assert.Contains(run.OutLines, l => l.TrimStart().StartsWith("! issue ", StringComparison.Ordinal) && l.Contains("case=case_pause", StringComparison.Ordinal));

        Assert.Empty(TraceLines(run));
        Assert.DoesNotContain(run.OutLines, l => l.TrimStart().StartsWith("! ISSUE ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Detailed_ShowsTraceLinesWithoutPayloads_AndIssueDetailBlocks()
    {
        var run = await RunScenarioAsync("detailed");

        var trace = TraceLines(run);
        Assert.NotEmpty(trace);
        Assert.Contains(trace, l => l.Contains("doctor.discovery.pass", StringComparison.Ordinal));
        Assert.Contains(trace, l => l.Contains("turn.outcome", StringComparison.Ordinal));
        Assert.DoesNotContain(trace, l => l.Contains(" data=", StringComparison.Ordinal));
        Assert.DoesNotContain(trace, l => l.Contains(" ms=", StringComparison.Ordinal));

        AssertIssueDetailBlock(run.OutLines, "case_pause");
        Assert.Contains(run.OutLines, l => l.TrimStart().StartsWith("card name: Work IQ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diagnostic_ShowsTraceLinesWithTheirPayloadsAndTimings()
    {
        var run = await RunScenarioAsync("diagnostic");

        var trace = TraceLines(run);
        Assert.Contains(trace, l => l.Contains("doctor.discovery.pass", StringComparison.Ordinal) && l.Contains(" data=", StringComparison.Ordinal));
        Assert.Contains(trace, l => l.Contains(" ms=", StringComparison.Ordinal));
        AssertIssueDetailBlock(run.OutLines, "case_pause");
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public async Task ConsoleLog_HoldsTheDiagnosticViewAtEveryVerbosity(string level)
    {
        var run = await RunScenarioAsync(level);

        var log = run.Text("console.log");
        var lines = log.Split('\n');
        Assert.Contains(lines, l => l.StartsWith(TracePrefix, StringComparison.Ordinal) && l.Contains("doctor.discovery.pass", StringComparison.Ordinal) && l.Contains(" data=", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith(TracePrefix, StringComparison.Ordinal) && l.Contains("turn.outcome", StringComparison.Ordinal) && l.Contains(" ms=", StringComparison.Ordinal));
        AssertIssueDetailBlock(lines, "case_pause");
        Assert.Contains("Result: NOT MEASURED (exit 11)", log, StringComparison.Ordinal);

        // Plain text: no colour codes and no carriage returns, whatever the screen was.
        Assert.False(log.Contains('\u001b', StringComparison.Ordinal), "console.log must not carry colour codes.");
        Assert.False(log.Contains('\r', StringComparison.Ordinal), "console.log must use LF line endings only.");

        // Every trace event of the run reached console.log: as many trace lines as trace.jsonl has events.
        var events = run.JsonLines("trace.jsonl").Count;
        Assert.True(
            lines.Count(l => l.StartsWith(TracePrefix, StringComparison.Ordinal)) >= events,
            $"console.log has fewer trace lines than trace.jsonl has events ({events}).");
    }

    [Fact]
    public async Task AsciiScreen_RedirectedOutputGetsMarkers_ConsoleLogKeepsTheUtf8Symbols()
    {
        // A redirected screen on Windows is in the legacy code page, where ✓ ✗ – print as '?'.
        _rig.AsciiScreen = true;

        var run = await RunScenarioAsync("normal");

        Assert.Contains(run.OutLines, l => l.StartsWith("  [PASS] readiness: auth", StringComparison.Ordinal));
        Assert.Contains(run.OutLines, l => l.StartsWith("  [SKIP] benchmark", StringComparison.Ordinal) && l.Contains("NOT MEASURED", StringComparison.Ordinal));
        foreach (var symbol in DemoConsole.Symbols)
        {
            Assert.DoesNotContain(symbol.ToString(), run.Out, StringComparison.Ordinal);
        }

        var bytes = File.ReadAllBytes(run.File("console.log").FullName);
        var log = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        Assert.Contains("✓ readiness: auth", log, StringComparison.Ordinal);
        Assert.DoesNotContain("[PASS] readiness: auth", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShortAlias_SelectsTheSameVerbosity()
    {
        var run = await RunScenarioAsync("detailed", alias: true);

        Assert.NotEmpty(TraceLines(run));
    }

    private async Task<DemoRunResult> RunScenarioAsync(string level, bool alias = false)
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Router.RouteSendMessage(PauseMarker, DemoRig.InputRequiredConfirmation);
        _rig.Judge = new FakeJudgeChatClient();
        var dataset = _rig.WriteDataset($$"""
            [
              { "id": "case_ok", "input": "In one sentence, what is on my calendar?", "evaluation_criteria": ["Answers in one sentence"] },
              { "id": "case_pause", "input": "Describe the kickoff {{PauseMarker}} in one sentence.", "evaluation_criteria": ["Does not create anything"] }
            ]
            """);

        var run = await _rig.RunAsync(alias ? "-v" : "--verbosity", level, DemoRig.Consent, "--dataset", dataset.FullName);

        // ERROR (a turn that did not complete) is not measured, never a failure.
        run.AssertExit(11);
        return run;
    }

    private static string[] TraceLines(DemoRunResult run) =>
        run.OutLines.Where(l => l.StartsWith(TracePrefix, StringComparison.Ordinal)).ToArray();

    /// <summary>The detail block of the paused case's issue: header, then the fields the tester reads.</summary>
    private static void AssertIssueDetailBlock(IReadOnlyList<string> lines, string caseId)
    {
        var header = -1;
        for (var i = 0; i < lines.Count && header < 0; i++)
        {
            if (lines[i].TrimStart().StartsWith("! ISSUE ", StringComparison.Ordinal)
                && lines.Skip(i + 1).TakeWhile(l => l.StartsWith("      ", StringComparison.Ordinal)).Any(l => l.Contains(caseId, StringComparison.Ordinal)))
            {
                header = i;
            }
        }

        Assert.True(header >= 0, $"No issue detail block names {caseId}.");
        var fields = lines.Skip(header + 1)
            .TakeWhile(l => l.StartsWith("      ", StringComparison.Ordinal))
            .Select(l => l.Trim())
            .ToArray();
        foreach (var name in new[] { "stage", "category", "code", "codes", "request-id", "task/context", "message", "cause", "action" })
        {
            Assert.Contains(fields, f => f.StartsWith(name + " ", StringComparison.Ordinal));
        }
    }
}
