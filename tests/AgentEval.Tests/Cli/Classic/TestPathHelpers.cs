// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Test path discovery for the legacy AgentEval.Cli test suite ported from
// AgentEvalHQ/AgentEval.Cli. The original tests walked up from AppContext.BaseDirectory
// looking for AgentEval.Cli.slnx / .sln; in the joslat/AgentEval monorepo the
// marker is AgentEval.sln. This helper accepts either so the tests are repo-portable.

namespace AgentEval.Tests.Cli.Classic;

internal static class TestPathHelpers
{
    /// <summary>
    /// Walks up from the test binary directory until it finds the repo root
    /// (marked by AgentEval.sln, AgentEval.Cli.slnx, or AgentEval.Cli.sln),
    /// then resolves the requested relative path under it.
    /// </summary>
    public static string GetCliTestDatasetPath(string fileName = "rag-qa.yaml")
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null
            && !File.Exists(Path.Combine(dir.FullName, "AgentEval.sln"))
            && !File.Exists(Path.Combine(dir.FullName, "AgentEval.Cli.slnx"))
            && !File.Exists(Path.Combine(dir.FullName, "AgentEval.Cli.sln")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
            throw new InvalidOperationException(
                "Could not find repo root (looked for AgentEval.sln / AgentEval.Cli.slnx / AgentEval.Cli.sln walking up from the test binary directory).");

        // joslat layout: tests/AgentEval.Tests/Cli/datasets/<file>
        var joslatPath = Path.Combine(dir.FullName, "tests", "AgentEval.Tests", "Cli", "datasets", fileName);
        if (File.Exists(joslatPath)) return joslatPath;

        // External layout: tests/AgentEval.Cli.Tests/datasets/<file>
        var externalPath = Path.Combine(dir.FullName, "tests", "AgentEval.Cli.Tests", "datasets", fileName);
        if (File.Exists(externalPath)) return externalPath;

        throw new FileNotFoundException(
            $"Could not locate test dataset '{fileName}' under either {joslatPath} or {externalPath}.");
    }
}
