// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// The provenance pointer: <c>skills scan</c> reads a project's <c>skills-lock.json</c> (the shape ChilliCream's
/// <c>skills</c> CLI writes) and shows where each skill came from next to its findings and in the baseline ledger.
/// Until now <c>SkillBaselineEntry.Source</c> was always null: the renderer could print it, nothing filled it.
/// </summary>
[Collection("ConsoleTests")]
public sealed class SkillsLockFileTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "agenteval-skills-lock-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        try { _root.Delete(recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private string Lock(string dir, string json)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SkillsLockFile.FileName);
        File.WriteAllText(path, json);
        return path;
    }

    private const string OneSkill = """
        {"version": 1, "skills": {"bad--hyphen": {"source": "chillicream/agent-skills", "sourceType": "github",
          "ref": "a1b2c3d", "skillPath": "skills/bad--hyphen", "computedHash": "h"}}}
        """;

    /// <summary>A skill MAF's discovery excludes (consecutive hyphens), so the scan reports a finding for it.</summary>
    private DirectoryInfo SkillsDir()
    {
        var skillDir = Path.Combine(_root.FullName, "skills", "bad--hyphen");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: bad--hyphen\ndescription: A fixture skill.\n---\n\nBody.\n");
        return new DirectoryInfo(Path.Combine(_root.FullName, "skills"));
    }

    [Fact]
    public void Read_TakesSourceAndRef_ByName()
    {
        var path = Lock(_root.FullName, OneSkill);

        var p = SkillsLockFile.Read(path)["bad--hyphen"];

        Assert.Equal("chillicream/agent-skills", p.Source);
        Assert.Equal("a1b2c3d", p.Ref);
        Assert.Null(p.SourceUrl);   // the project file does not record one, and none is made up
    }

    [Fact]
    public void Read_DropsAnEntryHoldingAControlCharacter()
    {
        // It would reach the terminal verbatim in the console report.
        var path = Lock(_root.FullName, """{"skills": {"evil": {"source": "a\u001b[2Jb"}, "ok": {"source": "org/repo"}}}""");

        var read = SkillsLockFile.Read(path);

        Assert.Equal(["ok"], read.Keys);
    }

    [Fact]
    public void Read_ALockFileThatIsNotJson_GivesNoProvenance_AndDoesNotThrow()
    {
        var path = Lock(_root.FullName, "not json");

        Assert.Empty(SkillsLockFile.Read(path));
    }

    [Fact]
    public void Find_LooksUpToTheRepositoryRoot_AndNoFurther()
    {
        var repo = Path.Combine(_root.FullName, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var expected = Lock(repo, OneSkill);
        var deep = Path.Combine(repo, ".agents", "skills");
        Directory.CreateDirectory(deep);
        Lock(_root.FullName, OneSkill);   // above the repository root: never read

        Assert.Equal(expected, SkillsLockFile.Find(deep));

        File.Delete(expected);
        Assert.Null(SkillsLockFile.Find(deep));
    }

    [Fact]
    public void Find_WithNoRepositoryRoot_LooksOnlyInTheScannedDirectory()
    {
        var scanned = Path.Combine(_root.FullName, "scanned");
        Directory.CreateDirectory(scanned);
        Lock(_root.FullName, OneSkill);   // a parent, but with no .git above there is no project boundary to trust

        Assert.Null(SkillsLockFile.Find(scanned));
    }

    [Theory]
    [InlineData("console", "from chillicream/agent-skills@a1b2c3d")]
    [InlineData("json", "chillicream/agent-skills")]
    public async Task Scan_ShowsWhereAFlaggedSkillCameFrom(string format, string expected)
    {
        Directory.CreateDirectory(Path.Combine(_root.FullName, ".git"));
        Lock(_root.FullName, OneSkill);
        var output = new FileInfo(Path.Combine(_root.FullName, "report.txt"));

        await SkillsScanCommand.ExecuteAsync(SkillsDir(), format, output, failOnNoncompliant: false, ct: default);

        var report = await File.ReadAllTextAsync(output.FullName);
        Assert.Contains("bad--hyphen", report, StringComparison.Ordinal);
        Assert.Contains(expected, report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_WriteBaseline_StoresTheProvenanceOnTheEntry()
    {
        Directory.CreateDirectory(Path.Combine(_root.FullName, ".git"));
        var skillDir = Path.Combine(_root.FullName, "skills", "expense-report");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: expense-report\ndescription: A fixture skill.\n---\n\nBody.\n");
        Lock(_root.FullName, """{"version": 1, "skills": {"expense-report": {"source": "org/skills", "ref": "v1.2.0"}}}""");
        var ledger = Path.Combine(_root.FullName, "ledger");

        var exit = await SkillsScanCommand.ExecuteAsync(
            new DirectoryInfo(Path.Combine(_root.FullName, "skills")), "console",
            new FileInfo(Path.Combine(_root.FullName, "r.txt")), failOnNoncompliant: false,
            writeBaseline: true, baselineRoot: ledger, ct: default);

        Assert.Equal(ExitCodes.Success, exit);
        var snapshot = Assert.Single(await new JsonFileSkillBaselineStore(ledger).ListAsync(default));
        var entry = Assert.Single(snapshot.Skills);
        Assert.Equal(("org/skills", "v1.2.0"), (entry.Source, entry.Ref));
    }
}
