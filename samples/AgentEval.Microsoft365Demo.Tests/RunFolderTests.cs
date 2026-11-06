// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.IO.Compression;
using AgentEval.Microsoft365Demo.Tests.TestHelpers;

namespace AgentEval.Microsoft365Demo.Tests;

/// <summary>
/// The run folder is written on every run, whatever the screen shows: every file of the contract, the default folder name,
/// the zip, and — at every verbosity, with raw capture on — no token and no <c>Authorization</c> header anywhere: not on
/// the screen, not in any file, not in the raw captures, not in the zip.
/// </summary>
public sealed class RunFolderTests : IDisposable
{
    private static readonly string[] AlwaysWritten =
    [
        "console.log", "trace.jsonl", "issues.jsonl", "readiness.md", "readiness.json",
        "turns.jsonl", "results.md", "results.json", "summary.md",
    ];

    private readonly DemoRig _rig = new();

    public void Dispose() => _rig.Dispose();

    public static TheoryData<string> Levels => new() { "quiet", "normal", "detailed", "diagnostic" };

    [Fact]
    public async Task FullRun_WritesEveryFileOfTheContract()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Judge = new FakeJudgeChatClient();

        var run = await _rig.RunAsync(DemoRig.Consent, "--verbosity", "quiet");

        run.AssertExit(0);
        foreach (var name in AlwaysWritten)
        {
            Assert.True(run.File(name).Exists, $"{name} is missing from the run folder.\n{run.Describe()}");
        }

        Assert.NotEmpty(run.JsonLines("trace.jsonl"));
        Assert.True(new DirectoryInfo(Path.Combine(run.Folder.FullName, "evidence")).EnumerateFiles("*.json").Any());
        Assert.False(new DirectoryInfo(Path.Combine(run.Folder.FullName, "raw")).Exists, "raw/ is written only with --capture-raw.");
        Assert.False(run.Zip.Exists, "The zip is written only with --zip.");
        Assert.False(run.File("m365-issues.jsonl").Exists, "The readiness issues belong in issues.jsonl (one register per run).");

        // summary.md: the header, the stage table, the case table, the issues and what to send back.
        var summary = run.Text("summary.md");
        foreach (var heading in new[] { "| Tenant id | `11111111-1111-1111-1111-111111111111` |", "## Stages", "## Benchmark", "## Issues", "## What to send back" })
        {
            Assert.Contains(heading, summary, StringComparison.Ordinal);
        }

        Assert.Contains("readiness: discovery", summary, StringComparison.Ordinal);
        Assert.Contains("`identity_disclosure`", summary, StringComparison.Ordinal);

        // Both runs that wrote into the folder (readiness and the agent) are in the one trace.
        var codes = run.JsonLines("trace.jsonl").Select(e => e.GetProperty("code").GetString()).ToArray();
        Assert.Contains("doctor.protocol.pass", codes);
        Assert.Contains("turn.outcome", codes);
    }

    [Fact]
    public async Task NoOutOption_CreatesTheTimestampedFolderUnderTheRunsRoot()
    {
        _rig.WithHealthyEndpoint();

        var run = await _rig.RunAsync(["--readiness-only"], addConfig: true, addOut: false);

        run.AssertExit(0);
        var expected = new DirectoryInfo(Path.Combine(_rig.RunsRoot.FullName, "20261105-100000Z"));
        Assert.True(expected.Exists, $"Expected the run folder {expected.FullName}.\n{run.Describe()}");
        Assert.True(File.Exists(Path.Combine(expected.FullName, "summary.md")));
        Assert.Contains(expected.FullName, run.Out, StringComparison.Ordinal);
        Assert.False(run.Folder.Exists);
    }

    [Fact]
    public async Task ZipOption_WritesOneZipBesideTheFolder_ContainingTheSummary()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Judge = new FakeJudgeChatClient();

        var run = await _rig.RunAsync(DemoRig.Consent, "--zip");

        run.AssertExit(0);
        var zips = _rig.Root.EnumerateFiles("*.zip", SearchOption.AllDirectories).ToArray();
        var zip = Assert.Single(zips);
        Assert.Equal(run.Zip.FullName, zip.FullName);
        Assert.Contains(zip.FullName, run.Out, StringComparison.Ordinal);

        using var archive = ZipFile.OpenRead(zip.FullName);
        var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToArray();
        var summaryEntry = Assert.Single(names, n => n.EndsWith("/summary.md", StringComparison.Ordinal) || n == "summary.md");
        Assert.Contains(names, n => n.EndsWith("/console.log", StringComparison.Ordinal) || n == "console.log");
        Assert.Contains(names, n => n.EndsWith("/issues.jsonl", StringComparison.Ordinal) || n == "issues.jsonl");

        using var reader = new StreamReader(archive.GetEntry(summaryEntry)!.Open());
        Assert.Equal(run.Text("summary.md"), reader.ReadToEnd());
    }

    [Fact]
    public async Task ZipFailsAfterTheLogWasClosed_ExitIs3_AndConsoleLogEndsWithTheChangedResult()
    {
        // The zip is written after console.log is closed (the zip holds console.log). When it fails, the reports are
        // written again as ERROR (exit 3); console.log must not end on the earlier table alone.
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Judge = new FakeJudgeChatClient();
        _rig.ZipWriter = _ => throw new IOException("simulated: the disk is full");

        var run = await _rig.RunAsync(DemoRig.Consent, "--zip");

        run.AssertExit(3);
        Assert.Contains("the zip could not be written", run.Err, StringComparison.Ordinal);
        Assert.Contains("exit 3", run.Text("summary.md"), StringComparison.Ordinal);
        var lastLine = run.Text("console.log").TrimEnd('\n').Split('\n')[^1];
        Assert.Contains("after this log was closed, the zip could not be written", lastLine, StringComparison.Ordinal);
        Assert.Contains("(exit 3)", lastLine, StringComparison.Ordinal);
        Assert.False(run.Zip.Exists);
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public async Task NoTokenAndNoAuthorizationHeader_AnywhereTheRunWrote_AtEveryVerbosity(string level)
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Judge = new FakeJudgeChatClient();

        var run = await _rig.RunAsync(DemoRig.Consent, "--verbosity", level, "--capture-raw", "--zip");

        run.AssertExit(0);

        // The token really was used on every request (so a leak would have had something to leak)…
        Assert.True(_rig.Tokens.Calls > 0);
        Assert.NotEmpty(_rig.Handler.Requests);
        Assert.All(_rig.Handler.Requests, r => Assert.Equal("Bearer", r.AuthorizationScheme));

        // …the raw captures and the zip exist (so the scan covered them)…
        var raw = new DirectoryInfo(Path.Combine(run.Folder.FullName, "raw"));
        Assert.True(raw.Exists && raw.EnumerateFiles("*", SearchOption.AllDirectories).Any(), "--capture-raw wrote nothing under raw/.");
        Assert.True(run.Zip.Exists);

        // …and nothing carries the token or the header's scheme.
        var hits = LeakScanner.Scan(run.Screen, run.Folder, run.Zip);
        Assert.True(hits.Count == 0, "Forbidden text found:\n" + string.Join("\n", hits));
    }

    [Fact]
    public async Task FailedRun_StillWritesTheFolder_AndLeaksNothing()
    {
        _rig.Handler.FailWith(AgentEval.Tests.Microsoft365.TestHelpers.FakeWorkIqHandler.CardMethod, System.Net.HttpStatusCode.Unauthorized);

        var run = await _rig.RunAsync(DemoRig.Consent, "--verbosity", "diagnostic", "--capture-raw", "--zip");

        run.AssertExit(1);
        foreach (var name in new[] { "console.log", "trace.jsonl", "issues.jsonl", "readiness.md", "readiness.json", "results.md", "results.json", "summary.md" })
        {
            Assert.True(run.File(name).Exists, $"{name} is missing from a failed run's folder.\n{run.Describe()}");
        }

        Assert.Contains("**Result: FAIL** (exit 1)", run.Text("summary.md"), StringComparison.Ordinal);
        var hits = LeakScanner.Scan(run.Screen, run.Folder, run.Zip);
        Assert.True(hits.Count == 0, "Forbidden text found:\n" + string.Join("\n", hits));
    }
}
