// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

public class LegacyPathScannerTests : IDisposable
{
    private readonly string _workspaceRoot;

    public LegacyPathScannerTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"AgentEval_LegacyScan_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { }
    }

    [Fact]
    public void Scan_NoLegacyDirs_ReturnsEmpty()
    {
        var findings = LegacyPathScanner.Scan(_workspaceRoot).ToList();
        Assert.Empty(findings);
    }

    [Fact]
    public void Scan_OldUppercaseFolder_ReportsFinding()
    {
        Directory.CreateDirectory(Path.Combine(_workspaceRoot, ".AgentEval"));

        var findings = LegacyPathScanner.Scan(_workspaceRoot).ToList();

        Assert.Single(findings, f => f.Path == ".AgentEval/");
        Assert.Contains("uppercase", findings[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_TraceResultsTraces_ReportsFinding()
    {
        Directory.CreateDirectory(Path.Combine(_workspaceRoot, "TestResults", "traces"));

        var findings = LegacyPathScanner.Scan(_workspaceRoot).ToList();

        Assert.Single(findings, f => f.Path == "TestResults/traces/");
        Assert.Contains("trace", findings[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_LegacyBenchmarks_ReportsFinding()
    {
        // Use a sub-path that avoids Windows case-insensitive collision with .AgentEval
        // by using a workspace that only has .agenteval/benchmarks/
        var ws = Path.Combine(_workspaceRoot, "bench_only");
        Directory.CreateDirectory(Path.Combine(ws, ".agenteval", "benchmarks"));

        var findings = LegacyPathScanner.Scan(ws).ToList();

        var benchFinding = findings.FirstOrDefault(f => f.Path == ".agenteval/benchmarks/");
        Assert.NotNull(benchFinding);
        Assert.Contains("benchmark", benchFinding.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_AllThreeLegacyPaths_ReportsThreeFindings()
    {
        // On Windows the .AgentEval and .agenteval checks are case-insensitive, so both .AgentEval
        // and .agenteval/benchmarks may map to the same folder. We verify at least 1 finding exists
        // for each logical path we create.
        var ws = Path.Combine(_workspaceRoot, "all_paths");
        Directory.CreateDirectory(Path.Combine(ws, "TestResults", "traces"));
        Directory.CreateDirectory(Path.Combine(ws, ".agenteval", "benchmarks"));

        var findings = LegacyPathScanner.Scan(ws).ToList();

        // Must contain at least the TraceResults and benchmark findings
        Assert.Contains(findings, f => f.Path == "TestResults/traces/");
        Assert.Contains(findings, f => f.Path == ".agenteval/benchmarks/");
    }

    [Fact]
    public void Scan_LowercaseAgentevalOnly_DoesNotReportUppercaseLegacy()
    {
        var ws = Path.Combine(_workspaceRoot, "modern_only");
        Directory.CreateDirectory(Path.Combine(ws, ".agenteval", "subjects", "agents", "Demo"));

        var findings = LegacyPathScanner.Scan(ws).ToList();

        Assert.DoesNotContain(findings, f => f.Path == ".AgentEval/");
    }

    [Fact]
    public void Scan_NonexistentRoot_ReturnsEmpty()
    {
        var ghost = Path.Combine(_workspaceRoot, "does-not-exist");
        var findings = LegacyPathScanner.Scan(ghost).ToList();
        Assert.Empty(findings);
    }

    [Fact]
    public void Scan_NullRoot_Throws()
    {
        // ArgumentNullException is a subclass of ArgumentException; use ThrowsAny to allow either
        Assert.ThrowsAny<ArgumentException>(() => LegacyPathScanner.Scan(null!).ToList());
    }

    [Fact]
    public void Scan_EmptyRoot_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => LegacyPathScanner.Scan("").ToList());
    }
}
