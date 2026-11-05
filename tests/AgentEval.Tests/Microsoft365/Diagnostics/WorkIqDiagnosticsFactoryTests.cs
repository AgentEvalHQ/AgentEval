// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.RegularExpressions;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Diagnostics;

/// <summary>
/// <see cref="WorkIqDiagnosticsFactory"/>: run ids, the config identity hash, path resolution (options →
/// workspace <c>.agenteval/m365</c> found walking up → temp) without creating anything, and <c>Create</c> that
/// opens the files and traces <c>config.diagnostics_ready</c> with both paths. Every directory is a fresh temp
/// directory; the repo's own workspace is never used.
/// </summary>
public sealed class WorkIqDiagnosticsFactoryTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 13, 45, 2, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenteval-tests", "diag-" + Guid.NewGuid().ToString("N"));

    private static Microsoft365Config Config(string? agentId = null, string? agentName = null) => new()
    {
        TenantId = "00000000-0000-0000-0000-000000000001",
        AppClientId = "00000000-0000-0000-0000-000000000002",
        AgentId = agentId,
        AgentName = agentName,
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string Sub(params string[] segments) => Path.Combine([_root, .. segments]);

    /// <summary>Reads a file the sink still holds open: the sink shares the file for reading, so a reader must ask for a compatible share mode (as a tail tool does).</summary>
    private static string[] ReadLinesWhileOpen(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Run id and config hash
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void NewRunId_Always_UsesUtcStampAndFourHex()
    {
        var runId = WorkIqDiagnosticsFactory.NewRunId(new FakeTimeProvider(Start));

        Assert.Matches("^m365-20261105-134502-[0-9a-f]{4}$", runId);
        Assert.NotEqual(runId, WorkIqDiagnosticsFactory.NewRunId(new FakeTimeProvider(Start)));
    }

    [Fact]
    public void ComputeConfigSha256_Always_IsDeterministicAndIdentityOnly()
    {
        var a = WorkIqDiagnosticsFactory.ComputeConfigSha256(Config(agentId: "U_abc.def"));
        var again = WorkIqDiagnosticsFactory.ComputeConfigSha256(Config(agentId: "U_abc.def"));
        var otherAgent = WorkIqDiagnosticsFactory.ComputeConfigSha256(Config(agentId: "U_other.one"));
        var onlyNameDiffers = WorkIqDiagnosticsFactory.ComputeConfigSha256(Config(agentId: "U_abc.def", agentName: "Display"));

        Assert.Matches("^[0-9a-f]{64}$", a);
        Assert.Equal(a, again);
        Assert.NotEqual(a, otherAgent);
        Assert.Equal(a, onlyNameDiffers);
    }

    [Fact]
    public void ComputeConfigSha256_InvalidProfile_StillHashes()
    {
        var hash = WorkIqDiagnosticsFactory.ComputeConfigSha256(Config() with { DiagnosticProfile = "nonsense" });

        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.NotEqual(hash, WorkIqDiagnosticsFactory.ComputeConfigSha256(Config()));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Describe
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Describe_DiagnosticsDirectoryOption_WinsAndCreatesNothing()
    {
        var dir = Sub("explicit");
        var options = new WorkIqOptions { DiagnosticsDirectory = new DirectoryInfo(dir), RunId = "r1" };

        var paths = WorkIqDiagnosticsFactory.Describe(options);

        Assert.Equal("r1", paths.RunId);
        Assert.Equal(dir, paths.Directory.FullName);
        Assert.Equal(Path.Combine(dir, "m365-trace-r1.jsonl"), paths.TraceFile.FullName);
        Assert.Equal(Path.Combine(dir, "m365-issues.jsonl"), paths.IssueFile.FullName);
        Assert.Equal(WorkIqDiagnosticsDirectorySource.Options, paths.DirectorySource);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Describe_WorkspaceAbove_UsesItsM365Subfolder()
    {
        Directory.CreateDirectory(Sub(".agenteval"));
        var start = Sub("src", "deeper");
        Directory.CreateDirectory(start);

        var paths = WorkIqDiagnosticsFactory.Describe(new WorkIqOptions(), "r2", searchStartDirectory: start, fallbackDirectory: Sub("fallback"));

        Assert.Equal(Sub(".agenteval", "m365"), paths.Directory.FullName);
        Assert.Equal(WorkIqDiagnosticsDirectorySource.Workspace, paths.DirectorySource);
        Assert.Equal(Path.Combine(Sub(".agenteval", "m365"), "m365-trace-r2.jsonl"), paths.TraceFile.FullName);
        Assert.False(Directory.Exists(Sub(".agenteval", "m365")));
    }

    [Fact]
    public void Describe_NoWorkspace_FallsBackToTheGivenDirectory()
    {
        var start = Sub("lonely");
        Directory.CreateDirectory(start);

        // A temp directory's ancestors are outside this test's control: resolve honestly instead of assuming.
        var workspaceAbove = WorkIqDiagnosticsFactory.FindWorkspaceDirectory(start);
        var paths = WorkIqDiagnosticsFactory.Describe(new WorkIqOptions(), "r3", searchStartDirectory: start, fallbackDirectory: Sub("fallback"));

        if (workspaceAbove is null)
        {
            Assert.Equal(Sub("fallback"), paths.Directory.FullName);
            Assert.Equal(WorkIqDiagnosticsDirectorySource.Temp, paths.DirectorySource);
        }
        else
        {
            Assert.Equal(Path.Combine(workspaceAbove, "m365"), paths.Directory.FullName);
            Assert.Equal(WorkIqDiagnosticsDirectorySource.Workspace, paths.DirectorySource);
        }
    }

    [Fact]
    public void Describe_ExplicitFiles_WinOverTheDirectory()
    {
        var options = new WorkIqOptions
        {
            DiagnosticsDirectory = new DirectoryInfo(Sub("dir")),
            TraceFile = new FileInfo(Sub("elsewhere", "t.jsonl")),
            IssueFile = new FileInfo(Sub("elsewhere", "i.jsonl")),
        };

        var paths = WorkIqDiagnosticsFactory.Describe(options, "r4");

        Assert.Equal(Sub("elsewhere", "t.jsonl"), paths.TraceFile.FullName);
        Assert.Equal(Sub("elsewhere", "i.jsonl"), paths.IssueFile.FullName);
        Assert.Equal(Sub("dir"), paths.Directory.FullName);
    }

    [Fact]
    public void Describe_NoRunIdAnywhere_UsesThePlaceholder()
    {
        var paths = WorkIqDiagnosticsFactory.Describe(new WorkIqOptions { DiagnosticsDirectory = new DirectoryInfo(Sub("d")) });

        Assert.Null(paths.RunId);
        Assert.Equal("m365-trace-{runId}.jsonl", paths.TraceFile.Name);
    }

    [Fact]
    public void Describe_BlankOptionRunId_IsTreatedAsNone()
    {
        var paths = WorkIqDiagnosticsFactory.Describe(new WorkIqOptions { DiagnosticsDirectory = new DirectoryInfo(Sub("d")), RunId = "  " });

        Assert.Null(paths.RunId);
    }

    [Fact]
    public void FindWorkspaceDirectory_Always_ReturnsTheNearestAgentEvalFolder()
    {
        Directory.CreateDirectory(Sub(".agenteval"));
        Directory.CreateDirectory(Sub("a", ".agenteval"));
        Directory.CreateDirectory(Sub("a", "b", "c"));

        Assert.Equal(Sub("a", ".agenteval"), WorkIqDiagnosticsFactory.FindWorkspaceDirectory(Sub("a", "b", "c")));
        Assert.Equal(Sub(".agenteval"), WorkIqDiagnosticsFactory.FindWorkspaceDirectory(Sub()));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Create
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Create_Always_OpensBothFilesAndTracesTheReadyEventWithThePaths()
    {
        var config = Config(agentId: "U_abc.def");
        var options = new WorkIqOptions { DiagnosticsDirectory = new DirectoryInfo(Sub("diag")), RunId = "run-x" };
        var clock = new FakeTimeProvider(Start);
        var console = new StringWriter();

        using var diagnostics = WorkIqDiagnosticsFactory.Create(config, options, clock, searchStartDirectory: null, fallbackDirectory: null, console: console);

        Assert.Equal("run-x", diagnostics.RunId);
        Assert.Equal(WorkIqDiagnosticsFactory.ComputeConfigSha256(config), diagnostics.ConfigSha256);
        Assert.False(diagnostics.IsNone);

        var ready = Assert.Single(diagnostics.RecentEvents);
        Assert.Equal(WorkIqDiagnosticsFactory.ReadyEventCode, ready.Code);
        Assert.Equal(WorkIqTraceStage.Config, ready.Stage);
        Assert.Equal(Start, ready.At);
        var traceFile = Path.Combine(Sub("diag"), "m365-trace-run-x.jsonl");
        var issueFile = Path.Combine(Sub("diag"), "m365-issues.jsonl");
        Assert.Equal(traceFile, ready.Data!.Value.GetProperty("traceFile").GetString());
        Assert.Equal(issueFile, ready.Data!.Value.GetProperty("issueFile").GetString());
        Assert.Equal("Options", ready.Data!.Value.GetProperty("directorySource").GetString());
        Assert.Equal("1.0", ready.Data!.Value.GetProperty("a2aVersion").GetString());
        Assert.False(ready.Data!.Value.TryGetProperty("a2AVersion", out _));
        Assert.Contains(traceFile, ready.Message, StringComparison.Ordinal);
        Assert.Contains(issueFile, ready.Message, StringComparison.Ordinal);

        Assert.True(File.Exists(traceFile));
        Assert.True(File.Exists(issueFile));
        var line = Assert.Single(ReadLinesWhileOpen(traceFile));
        using var document = JsonDocument.Parse(line);
        Assert.Equal(WorkIqDiagnosticsFactory.ReadyEventCode, document.RootElement.GetProperty("code").GetString());
        Assert.Equal("run-x", document.RootElement.GetProperty("runId").GetString());
        Assert.Empty(ReadLinesWhileOpen(issueFile));
        Assert.Equal("", console.ToString());
    }

    [Fact]
    public void Create_Verbose_AlsoWritesTheConsoleLine()
    {
        var options = new WorkIqOptions { DiagnosticsDirectory = new DirectoryInfo(Sub("diag")), RunId = "run-v", Verbose = true };
        var console = new StringWriter();

        using var diagnostics = WorkIqDiagnosticsFactory.Create(Config(), options, new FakeTimeProvider(Start), null, null, console);
        diagnostics.Trace(WorkIqTraceStage.Auth, "auth.silent_hit", "cached");

        var lines = console.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("[m365 13:45:02.000 Config/Info config.diagnostics_ready] ", lines[0], StringComparison.Ordinal);
        Assert.Contains("m365-trace-run-v.jsonl", lines[0], StringComparison.Ordinal);
        Assert.Equal("[m365 13:45:02.000 Auth/Info auth.silent_hit] cached", lines[1]);
        Assert.Equal(2, ReadLinesWhileOpen(Path.Combine(Sub("diag"), "m365-trace-run-v.jsonl")).Length);
    }

    [Fact]
    public void Create_BlankRunId_MintsOneAndNamesTheTraceFileAfterIt()
    {
        var options = new WorkIqOptions { DiagnosticsDirectory = new DirectoryInfo(Sub("diag")), RunId = " " };

        using var diagnostics = WorkIqDiagnosticsFactory.Create(Config(), options, new FakeTimeProvider(Start));

        Assert.Matches("^m365-20261105-134502-[0-9a-f]{4}$", diagnostics.RunId);
        Assert.True(File.Exists(Path.Combine(Sub("diag"), $"m365-trace-{diagnostics.RunId}.jsonl")));
    }

    [Fact]
    public void Create_IssueAfterCreate_LandsInTheIssueFileWithTheConfigHash()
    {
        var config = Config();
        var options = new WorkIqOptions { DiagnosticsDirectory = new DirectoryInfo(Sub("diag")), RunId = "run-i" };
        var issueFile = Path.Combine(Sub("diag"), "m365-issues.jsonl");

        using (var diagnostics = WorkIqDiagnosticsFactory.Create(config, options, new FakeTimeProvider(Start)))
        {
            diagnostics.Issue(WorkIqIssue.Create(WorkIqTraceStage.Discovery, WorkIqIssueCategory.Permission, "http.403", "Forbidden", "Consent missing."));
        }

        var line = Assert.Single(File.ReadAllLines(issueFile));
        using var document = JsonDocument.Parse(line);
        Assert.Equal("run-i", document.RootElement.GetProperty("runId").GetString());
        Assert.Equal(WorkIqDiagnosticsFactory.ComputeConfigSha256(config), document.RootElement.GetProperty("configSha256").GetString());
        Assert.Equal(WorkIqDiagnostics.AdapterVersion, document.RootElement.GetProperty("adapterVersion").GetString());
        Assert.Equal("Permission", document.RootElement.GetProperty("category").GetString());
    }

    [Fact]
    public void Create_UnwritableTraceFile_ThrowsNamingThePath()
    {
        Directory.CreateDirectory(_root);
        var blocker = Sub("blocker");
        File.WriteAllText(blocker, "x");
        var options = new WorkIqOptions { TraceFile = new FileInfo(Path.Combine(blocker, "t.jsonl")), IssueFile = new FileInfo(Sub("i.jsonl")) };

        var ex = Assert.Throws<InvalidOperationException>(() => WorkIqDiagnosticsFactory.Create(Config(), options, new FakeTimeProvider(Start)));

        Assert.Contains(Path.Combine(blocker, "t.jsonl"), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => WorkIqDiagnosticsFactory.Create(null!, new WorkIqOptions(), TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => WorkIqDiagnosticsFactory.Create(Config(), null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => WorkIqDiagnosticsFactory.Create(Config(), new WorkIqOptions(), null!));
    }
}
