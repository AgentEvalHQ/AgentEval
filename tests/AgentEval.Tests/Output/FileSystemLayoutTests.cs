// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;

namespace AgentEval.Tests.Output;

public class FileSystemLayoutTests
{
    private static FileSystemLayout Layout(string? root = null)
        => new(root ?? Path.Combine(Path.GetTempPath(), "agenteval-layout-test-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void AllPaths_AreUnderRoot()
    {
        var layout = Layout();
        var subject = new SubjectIdentity(SubjectKind.Agent, "MyAgent");
        var runId = "2026-01-01_00-00-00_abcd1234";

        Assert.StartsWith(layout.Root, layout.SolutionFile);
        Assert.StartsWith(layout.Root, layout.SubjectFile(subject));
        Assert.StartsWith(layout.Root, layout.ManifestFile(subject, runId));
        Assert.StartsWith(layout.Root, layout.SummaryFile(subject, runId));
        Assert.StartsWith(layout.Root, layout.TraceFile(subject, runId));
        Assert.StartsWith(layout.Root, layout.HistoryFile(subject));
        Assert.StartsWith(layout.Root, layout.BaselineFile(subject));
        Assert.StartsWith(layout.Root, layout.RecentRunsFile);
        Assert.StartsWith(layout.Root, layout.ComplianceEvidenceFile("SOC2", subject, "2026-01-01_00-00-00"));
    }

    [Fact]
    public void Sanitize_HandlesSlashesAndColons()
    {
        var layout = Layout();
        var evilSubject = new SubjectIdentity(SubjectKind.Agent, "evil/name:bad");

        var subjectFile = layout.SubjectFile(evilSubject);
        var segment = Path.GetFileName(Path.GetDirectoryName(subjectFile)!);

        Assert.DoesNotContain("/", segment);
        Assert.DoesNotContain("\\", segment);
        Assert.DoesNotContain(":", segment);
    }

    [Fact]
    public void SameNameDifferentKinds_ResolveSeparately()
    {
        var layout = Layout();
        var agentSubject = new SubjectIdentity(SubjectKind.Agent, "MySubject");
        var workflowSubject = new SubjectIdentity(SubjectKind.Workflow, "MySubject");

        var agentDir = layout.SubjectDir(agentSubject);
        var workflowDir = layout.SubjectDir(workflowSubject);

        Assert.NotEqual(agentDir, workflowDir);
        Assert.Contains("agents", agentDir);
        Assert.Contains("workflows", workflowDir);
    }

    [Fact]
    public void SubjectFile_ReturnsExpectedShape()
    {
        var layout = Layout("/workspace");
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");

        var subjectFile = layout.SubjectFile(subject);

        Assert.Equal(Path.Combine("/workspace", "subjects", "agents", "TestAgent", "subject.json"), subjectFile);
    }

    [Fact]
    public void RunDir_IncludesRunId()
    {
        var layout = Layout();
        var subject = new SubjectIdentity(SubjectKind.Workflow, "MyWorkflow");
        var runId = "2026-05-08_10-30-00_deadbeef";

        var runDir = layout.RunDir(subject, runId);

        Assert.EndsWith(runId, runDir);
        Assert.StartsWith(layout.Root, runDir);
    }

    // ── Sanitize hash-suffix (batch-2 surface) ───────────────────────────────

    [Fact]
    public void Sanitize_LossyNamesGetDistinctHashSuffix_NoCollision()
    {
        // "Foo/Bar" and "Foo-Bar" both trim to "Foo-Bar"; without the hash-suffix
        // they'd map to the same on-disk folder. Verify the hash differentiates
        // them.
        var layout = Layout();
        var slashSubject = new SubjectIdentity(SubjectKind.Agent, "Foo/Bar");
        var dashSubject  = new SubjectIdentity(SubjectKind.Agent, "Foo-Bar");

        var slashDir = Path.GetFileName(layout.SubjectDir(slashSubject));
        var dashDir  = Path.GetFileName(layout.SubjectDir(dashSubject));

        Assert.NotEqual(slashDir, dashDir);
        // The lossy one (slash → dash) carries the suffix; the clean one stays
        // unmodified.
        Assert.StartsWith("Foo-Bar__", slashDir);
        Assert.Equal("Foo-Bar", dashDir);
    }

    [Fact]
    public void Sanitize_LossyInput_HashSuffixIsDeterministic()
    {
        // Same lossy input must produce the same hash suffix across calls,
        // otherwise sequential writes to the same subject would land in
        // different directories.
        var layout = Layout();
        var subject1 = new SubjectIdentity(SubjectKind.Agent, "Foo/Bar/Baz");
        var subject2 = new SubjectIdentity(SubjectKind.Agent, "Foo/Bar/Baz");

        var dir1 = layout.SubjectDir(subject1);
        var dir2 = layout.SubjectDir(subject2);

        Assert.Equal(dir1, dir2);
    }

    [Fact]
    public void Sanitize_NonLossyName_NoHashSuffixAppended()
    {
        // Clean names should NOT get the suffix — the suffix is for collision
        // disambiguation only, not a blanket mangling.
        var layout = Layout();
        var subject = new SubjectIdentity(SubjectKind.Agent, "TravelAgent");

        var dirName = Path.GetFileName(layout.SubjectDir(subject));

        Assert.Equal("TravelAgent", dirName);
        Assert.DoesNotContain("__", dirName);
    }

    // ── IsSafePathSegment defensive cases ────────────────────────────────────

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData("foo..bar")]
    [InlineData("CON")]
    [InlineData("PRN")]
    [InlineData("NUL")]
    [InlineData("CON.txt")]
    [InlineData("LPT1")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsSafePathSegment_RejectsUnsafeInputs(string segment)
    {
        Assert.False(FileSystemLayout.IsSafePathSegment(segment),
            $"'{segment}' must be rejected as an unsafe path segment");
    }

    [Theory]
    [InlineData("MyAgent")]
    [InlineData("foo-bar-baz")]
    [InlineData("subject_v2")]
    [InlineData("2026-05-10_14-30-00_abcd1234")]
    public void IsSafePathSegment_AcceptsSafeInputs(string segment)
    {
        Assert.True(FileSystemLayout.IsSafePathSegment(segment),
            $"'{segment}' must be accepted as a safe path segment");
    }
}
