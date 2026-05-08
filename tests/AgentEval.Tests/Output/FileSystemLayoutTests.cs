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
}
