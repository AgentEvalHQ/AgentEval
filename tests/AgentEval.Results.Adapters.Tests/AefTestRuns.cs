// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace AgentEval.Results.Adapters.Tests;

/// <summary>
/// What the conversion tests share: the repository, a temporary folder, a converted run's files, and the reference
/// verifier (contracts/aef/tools/aef_verify.py), run as a separate process exactly as the conformance runner runs it.
/// </summary>
internal static class AefTestRuns
{
    /// <summary>The repository root: the first ancestor of the test's base directory that holds <c>contracts/aef</c>.</summary>
    public static readonly string RepoRoot = FindRepoRoot();

    /// <summary>The ASSERT sample run (samples/interop/assert-ai, written by hand in ASSERT's formats).</summary>
    public static readonly string AssertSampleRun = Path.Combine(RepoRoot, "samples", "interop", "assert-ai", "example-run", "results", "billing-safety", "run-1");

    /// <summary>A fresh folder under the system's temporary folder (not created).</summary>
    public static string TempPath(string prefix) => Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");

    /// <summary>The lines of a run's results.ndjson, in order.</summary>
    public static List<JsonObject> Results(string run) =>
        [.. File.ReadAllLines(Path.Combine(run, "results.ndjson")).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject())];

    /// <summary>A JSON document of the run.</summary>
    public static JsonObject Document(string run, string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(run, file)))!.AsObject();

    /// <summary>
    /// <c>aef_verify.py run DIR</c>'s JSON output (<c>{"outcome", "problems"}</c>), or null when python3 is not on the
    /// PATH (the Windows host; the Linux test container and CI have it).
    /// </summary>
    public static JsonObject? ReferenceVerify(string run)
    {
        var verifier = Path.Combine(RepoRoot, "contracts", "aef", "tools", "aef_verify.py");
        var start = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-X", "utf8", "-I", verifier, "run", run })
        {
            start.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception)
        {
            return null;
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"aef_verify.py exited {process.ExitCode}: {stderr.Result}");
            return JsonNode.Parse(stdout.Result)!.AsObject();
        }
    }

    /// <summary>Deletes a folder made for a test, whatever is in it.</summary>
    public static void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "contracts", "aef")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("No ancestor of the test's base directory holds contracts/aef.");
    }
}
