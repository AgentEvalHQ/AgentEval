// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using AgentEval.Samples;
using AgentEval.Tests.Output;
using Xunit;

namespace AgentEval.Tests.Samples;

/// <summary>
/// The offline sample check (<c>dotnet run --project samples/AgentEval.Samples -- --offline-check</c>): its checked-in
/// table must cover the menu exactly, its parser refuses a table that would expect a crash, and its verdict rule fails a
/// hang, a crash and any unexpected code. The end-to-end cases start real processes through a shell, not the samples.
/// </summary>
[Collection("ConsoleTests")]
public sealed class OfflineSampleCheckTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("AgentEval.sln not found");
    }

    private static string SamplesDir => Path.Combine(RepoRoot(), "samples", "AgentEval.Samples");

    /// <summary>
    /// The stable id of every menu entry, read from the catalogue in Program.cs the way the runner derives it: the class
    /// whose <c>RunAsync</c> the entry runs.
    /// </summary>
    private static IReadOnlyList<string> CatalogueIds()
    {
        var program = File.ReadAllText(Path.Combine(SamplesDir, "Program.cs"));
        var start = program.IndexOf("IReadOnlyList<SampleGroup> Groups =", StringComparison.Ordinal);
        var end = program.IndexOf("//  Entry point", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "the sample catalogue (Groups) was not found in Program.cs");
        return Regex.Matches(program[start..end], @"\b(\w+)\.RunAsync\b").Select(m => m.Groups[1].Value).ToList();
    }

    private static IReadOnlyList<OfflineSampleCheck.Expectation> CheckedInTable() =>
        OfflineSampleCheck.Parse(File.ReadAllText(Path.Combine(SamplesDir, OfflineSampleCheck.ExpectationsFileName)));

    [Fact]
    public void TheCheckedInTable_HasExactlyOneRowPerMenuSample()
    {
        var ids = CatalogueIds();
        Assert.True(ids.Count >= 100, $"expected the whole menu, read {ids.Count} entries");

        var catalogue = ids.Select((id, i) => new OfflineSampleCheck.CatalogueEntry($"#{i + 1}", id)).ToList();
        var problems = OfflineSampleCheck.CoverageProblems(catalogue, CheckedInTable());

        // A sample added to the menu without a row, or a row left behind by a removed sample, lands here.
        Assert.Empty(problems);
    }

    [Fact]
    public void TheCheckedInTable_ExpectsOnlyCodesASampleMayGiveOffline()
    {
        var table = CheckedInTable();

        Assert.All(table, row => Assert.Contains(row.ExitCode, new[] { 0, 1, 10, 11 }));
        Assert.All(table, row => Assert.False(string.IsNullOrWhiteSpace(row.Note), $"{row.Id} has no reason in its row"));
    }

    [Theory]
    [InlineData("HelloWorld 3 threw", "exit 3")]
    [InlineData("HelloWorld 2 unknown", "exit 2")]
    [InlineData("HelloWorld eleven needs a model", "expected '<id> <exit code> <note>'")]
    [InlineData("HelloWorld", "expected '<id> <exit code> <note>'")]
    [InlineData("HelloWorld 11 a\nhelloworld 0 b", "second row")]
    public void Parse_RefusesATableThatCannotBeTrusted(string text, string because)
    {
        var ex = Assert.Throws<FormatException>(() => OfflineSampleCheck.Parse(text));

        Assert.Contains(because, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SkipsCommentsAndBlankLines_AndKeepsTheNote()
    {
        var table = OfflineSampleCheck.Parse("# header\r\n\r\nHelloWorld   11  needs a chat provider\r\nDatasetLoaders 0 offline\r\n");

        Assert.Equal(2, table.Count);
        Assert.Equal(new OfflineSampleCheck.Expectation("HelloWorld", 11, "needs a chat provider"), table[0]);
        Assert.Equal(0, table[1].ExitCode);
    }

    [Fact]
    public void CoverageProblems_NameAMissingRow_AStaleRow_AndASharedId()
    {
        var catalogue = new[]
        {
            new OfflineSampleCheck.CatalogueEntry("A1", "HelloWorld"),
            new OfflineSampleCheck.CatalogueEntry("A2", "NewSample"),
            new OfflineSampleCheck.CatalogueEntry("B1", "HelloWorld"),
        };
        var table = OfflineSampleCheck.Parse("HelloWorld 11 x\nRemovedSample 0 y");

        var problems = OfflineSampleCheck.CoverageProblems(catalogue, table);

        Assert.Contains(problems, p => p.Contains("A2 NewSample has no row", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("row for RemovedSample", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("share the id HelloWorld", StringComparison.Ordinal));
    }

    [Fact]
    public void CoverageProblems_AMockRow_IsExtra_AndMustNameASample()
    {
        var catalogue = new[] { new OfflineSampleCheck.CatalogueEntry("A1", "HelloWorld") };
        var table = OfflineSampleCheck.Parse("HelloWorld 11 x\nHelloWorld+mock 0 y\nGhost+mock 0 z");

        var problems = OfflineSampleCheck.CoverageProblems(catalogue, table);

        // The +mock row does not stand in for the plain one, and one for a sample that does not exist is stale.
        Assert.Equal(["HelloWorld+mock"], table.Where(OfflineSampleCheck.IsMockRow).Select(r => r.Id).Take(1));
        Assert.DoesNotContain(problems, p => p.Contains("HelloWorld has no row", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("row for Ghost+mock", StringComparison.Ordinal));
        Assert.Single(problems);
        Assert.Contains(problems, p => p.Contains("Ghost", StringComparison.Ordinal));
        Assert.NotEmpty(OfflineSampleCheck.CoverageProblems(catalogue, OfflineSampleCheck.Parse("HelloWorld+mock 0 y")));
    }

    [Fact]
    public async Task RunAsync_AnOnlyThatNamesNoSample_IsAUsageError_NotAnEmptyPass()
    {
        // "--only C4" used to match ids only, ran nothing and printed "0/0 sample(s) exited as expected", exit 0.
        var dir = Directory.CreateTempSubdirectory("offline-only-");
        try
        {
            var table = Path.Combine(dir.FullName, OfflineSampleCheck.ExpectationsFileName);
            await File.WriteAllTextAsync(table, "HelloWorld 11 x\n");
            var catalogue = new[] { new OfflineSampleCheck.CatalogueEntry("A1", "HelloWorld") };

            var exit = await OfflineSampleCheck.RunAsync(catalogue, table, "no-such-host", [], TimeSpan.FromSeconds(5),
                Path.Combine(dir.FullName, "logs"), new HashSet<string>(["C4"], StringComparer.OrdinalIgnoreCase));

            Assert.Equal(SampleOutcome.UsageError, exit);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Judge_FailsAHangACrashAndAnUnexpectedCode_AndPassesOnlyTheExpectedOne()
    {
        var sample = new OfflineSampleCheck.CatalogueEntry("C2", "WorkflowEvaluationReal");
        var row = new OfflineSampleCheck.Expectation("WorkflowEvaluationReal", 11, "needs a provider");
        OfflineSampleCheck.RunResult Run(int? code, bool timedOut = false) =>
            new(sample, code, timedOut, TimeSpan.FromSeconds(1), "log");

        Assert.Null(OfflineSampleCheck.Judge(Run(11), row));
        Assert.Contains("hang", OfflineSampleCheck.Judge(Run(null, timedOut: true), row), StringComparison.Ordinal);
        Assert.Contains("threw", OfflineSampleCheck.Judge(Run(3), row), StringComparison.Ordinal);
        // The defect this check exists for: a sample that skips without a provider used to exit 0.
        Assert.Contains("expected 11", OfflineSampleCheck.Judge(Run(0), row), StringComparison.Ordinal);
        Assert.Contains("no row", OfflineSampleCheck.Judge(Run(0), null), StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderVariables_AreEveryCredentialAndSwitch_ButNotTheToolchain()
    {
        var names = new[]
        {
            "BITDEER_API_KEY", "AZURE_OPENAI_ENDPOINT", "AI_INFERENCE_PROVIDER", "OPENAI_COMPATIBLE_ENDPOINT",
            "FOUNDRY_API_KEY", "TYPESAFE_API_KEY", "OPENROUTER_API_KEY", "COPILOTSTUDIO_CONFIG_PATH", "azure_foundry_endpoint",
            "AGENTEVAL_SAMPLES_MOCK", "AGENTEVAL_SAMPLES_DRY_RUN", "AGENTEVAL_A2A_BASE_URL",
            "PATH", "DOTNET_ROOT", "HOME", "AGENTEVAL_GATEKEEPER_FORCE_OFFLINE",
        };

        var stripped = OfflineSampleCheck.ProviderVariables(names);

        Assert.DoesNotContain("PATH", stripped);
        Assert.DoesNotContain("DOTNET_ROOT", stripped);
        Assert.DoesNotContain("HOME", stripped);
        Assert.DoesNotContain("AGENTEVAL_GATEKEEPER_FORCE_OFFLINE", stripped);
        Assert.Equal(12, stripped.Count);
    }

    [Fact]
    public async Task RunAsync_EndToEnd_PassesTheExpectedExit_AndFailsACrashAndAHang()
    {
        using var temp = TempWorkspace.Create("OfflineCheck");
        var table = Path.Combine(temp.Path, OfflineSampleCheck.ExpectationsFileName);

        // The check appends `--sample <id>`; a shell command ignores it, so each "sample" is a command that exits or waits.
        async Task<int> Check(string command, string id, TimeSpan timeout)
        {
            var (host, args) = OperatingSystem.IsWindows()
                ? ("cmd.exe", new[] { "/c", command + " & rem" })
                : ("/bin/sh", new[] { "-c", command });
            var catalogue = new[] { new OfflineSampleCheck.CatalogueEntry("X1", id) };
            var onlyThisRow = $"{id} {(id == "Completes" ? 0 : 11)} the row under test\n";
            await File.WriteAllTextAsync(table, onlyThisRow);
            return await OfflineSampleCheck.RunAsync(catalogue, table, host, args, timeout, Path.Combine(temp.Path, $"logs-{id}-{Guid.NewGuid():N}"));
        }

        Assert.Equal(0, await Check("exit 0", "Completes", TimeSpan.FromSeconds(30)));
        Assert.Equal(0, await Check("exit 11", "NeedsModel", TimeSpan.FromSeconds(30)));
        Assert.Equal(1, await Check("exit 0", "NeedsModel", TimeSpan.FromSeconds(30)));    // a skip that exits 0
        Assert.Equal(1, await Check("exit 3", "Completes", TimeSpan.FromSeconds(30)));     // a crash
        var hang = OperatingSystem.IsWindows() ? "ping -n 60 127.0.0.1 >nul" : "sleep 60";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(1, await Check(hang, "Completes", TimeSpan.FromSeconds(2)));          // a hang, killed
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"the hang was not killed at its timeout ({watch.Elapsed})");
    }
}
