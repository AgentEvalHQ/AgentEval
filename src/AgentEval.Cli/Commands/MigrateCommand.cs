// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;

namespace AgentEval.Cli.Commands;

/// <summary>Implements the <c>agenteval migrate</c> command.</summary>
public static class MigrateCommand
{
    /// <summary>Runs the migrate command, discovering workspace root from the current directory.</summary>
    public static Task<int> RunAsync(bool apply, string? root) =>
        RunAsync(apply, root, rootOverride: null);

    /// <summary>Runs the migrate command with an optional explicit root override (used in tests).</summary>
    internal static async Task<int> RunAsync(bool apply, string? root, string? rootOverride)
    {
        var workspaceRoot = rootOverride
            ?? root
            ?? WorkspaceRootDiscovery.Find(Directory.GetCurrentDirectory());

        if (workspaceRoot is null)
        {
            Console.Error.WriteLine("✖ Could not find a solution root (.sln, .slnx, or .git).");
            return 1;
        }

        var findings = LegacyPathScanner.Scan(workspaceRoot).ToList();
        if (findings.Count == 0)
        {
            Console.WriteLine("Nothing to migrate.");
            return 0;
        }

        var tag = apply ? "[APPLIED]" : "[DRY-RUN]";

        foreach (var finding in findings)
        {
            if (finding.Path.StartsWith(".AgentEval/", StringComparison.OrdinalIgnoreCase) &&
                !finding.Path.StartsWith(".agenteval/benchmarks", StringComparison.OrdinalIgnoreCase))
            {
                await HandleUppercaseFolderAsync(workspaceRoot, finding, apply, tag);
            }
            else if (finding.Path == "TestResults/traces/")
            {
                await HandleTraceArtifactsAsync(workspaceRoot, finding, apply, tag);
            }
            else if (finding.Path == ".agenteval/benchmarks/")
            {
                await HandleBenchmarksAsync(workspaceRoot, finding, apply, tag);
            }
            else
            {
                Console.WriteLine($"{tag} Manual migration required: {finding.Path} — {finding.Reason}");
            }
        }

        return 0;
    }

    private static Task HandleUppercaseFolderAsync(string workspaceRoot, LegacyPathScanner.Finding finding, bool apply, string tag)
    {
        var src = Path.Combine(workspaceRoot, ".AgentEval");
        var dst = Path.Combine(workspaceRoot, ".agenteval");

        // Check if it is a sample-specific sub-path like .AgentEval/ECS2026MAF_Evals/
        if (Directory.Exists(src))
        {
            // Check for sample sub-paths
            var sampleDirs = Directory.GetDirectories(src)
                .Select(d => Path.GetFileName(d))
                .Where(n => n is not null && (n.Contains("_Evals") || n.Contains("Evals")))
                .ToList();

            if (sampleDirs.Count > 0)
            {
                Console.WriteLine($"{tag} Manual migration: sample-specific format in {src}; copy to .agenteval/samples/... if needed.");
                return Task.CompletedTask;
            }

            if (OperatingSystem.IsWindows())
            {
                // On Windows, filesystem is case-insensitive: .AgentEval and .agenteval refer to the same path.
                // Only rename if the on-disk name is literally ".AgentEval" (checked by LegacyPathScanner).
                // Strategy: move to a temp name, then move to the lowercase target.
                var tmp = src + ".__tmp__";

                if (!Directory.Exists(dst) || string.Equals(
                    new DirectoryInfo(dst).Name, ".AgentEval", StringComparison.Ordinal))
                {
                    Console.WriteLine($"{tag} Rename .AgentEval → .agenteval (via temp intermediate on Windows).");
                    if (apply)
                    {
                        try
                        {
                            Directory.Move(src, tmp);
                            Directory.Move(tmp, dst);
                            Console.WriteLine($"  ✔ Renamed {src} → {dst}");
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"  ✖ Rename failed: {ex.Message}. Rename manually: {src} → {dst}");
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"  ⚠ Both .AgentEval/ and .agenteval/ exist. Manual merge required.");
                }
            }
            else
            {
                // Non-Windows: direct rename
                Console.WriteLine($"{tag} Rename .AgentEval → .agenteval.");
                if (apply)
                {
                    try
                    {
                        Directory.Move(src, dst);
                        Console.WriteLine($"  ✔ Renamed {src} → {dst}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  ✖ Rename failed: {ex.Message}.");
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    private static async Task HandleTraceArtifactsAsync(string workspaceRoot, LegacyPathScanner.Finding finding, bool apply, string tag)
    {
        var tracesDir = Path.Combine(workspaceRoot, "TestResults", "traces");
        if (!Directory.Exists(tracesDir)) return;

        var agentEvalDir = Path.Combine(workspaceRoot, ".agenteval");
        var agentsDir = Path.Combine(agentEvalDir, "subjects", "agents");

        foreach (var file in Directory.GetFiles(tracesDir, "*.json"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            // Expected pattern: {name}_{ts}_{suffix}
            // Try to parse: split on underscore groups for date
            var parts = fileName.Split('_');
            if (parts.Length < 2)
            {
                Console.WriteLine($"{tag} ⚠ Cannot parse trace filename pattern: {fileName}; skipping.");
                continue;
            }

            // Find date-like segment: yyyy-MM-dd
            int dateIdx = -1;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 10 && parts[i][4] == '-' && parts[i][7] == '-')
                {
                    dateIdx = i;
                    break;
                }
            }

            string agentName;
            string tsDerived;

            if (dateIdx > 0)
            {
                agentName = string.Join("_", parts[..dateIdx]);
                tsDerived = string.Join("_", parts[dateIdx..]);
            }
            else
            {
                // Fallback: first part is name, rest is ts
                agentName = parts[0];
                tsDerived = string.Join("_", parts[1..]);
            }

            var subjectDir = Path.Combine(agentsDir, agentName);
            if (!Directory.Exists(subjectDir))
            {
                Console.WriteLine($"{tag} ⚠ No subject found for trace file {Path.GetFileName(file)} (agent: {agentName}); skipping.");
                continue;
            }

            var destDir = Path.Combine(subjectDir, "runs", tsDerived, "traces");
            var destFile = Path.Combine(destDir, Path.GetFileName(file));

            Console.WriteLine($"{tag} Move trace: {file}");
            Console.WriteLine($"       → {destFile}");

            if (apply)
            {
                try
                {
                    Directory.CreateDirectory(destDir);
                    SafeMove(file, destFile);
                    Console.WriteLine($"  ✔ Moved");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  ✖ Move failed: {ex.Message}");
                }
            }

            await Task.CompletedTask;
        }
    }

    private static async Task HandleBenchmarksAsync(string workspaceRoot, LegacyPathScanner.Finding finding, bool apply, string tag)
    {
        var benchmarksDir = Path.Combine(workspaceRoot, ".agenteval", "benchmarks");
        if (!Directory.Exists(benchmarksDir)) return;

        // Track per-agent baseline version counters
        var versionCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var agentDir in Directory.GetDirectories(benchmarksDir))
        {
            var agentName = Path.GetFileName(agentDir);
            var baselinesDir = Path.Combine(agentDir, "baselines");

            if (!Directory.Exists(baselinesDir)) continue;

            foreach (var file in Directory.GetFiles(baselinesDir, "*.json"))
            {
                var key = agentName;
                if (!versionCounters.TryGetValue(key, out var n))
                    n = 1;

                var destAgentDir = Path.Combine(workspaceRoot, ".agenteval", "subjects", "agents", agentName, "baselines");
                var destFile = Path.Combine(destAgentDir, $"v{n}.json");

                Console.WriteLine($"{tag} Move baseline: {file}");
                Console.WriteLine($"        → {destFile}");

                if (apply)
                {
                    try
                    {
                        Directory.CreateDirectory(destAgentDir);
                        SafeMove(file, destFile);
                        Console.WriteLine($"  ✔ Moved to {destFile}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  ✖ Move failed: {ex.Message}");
                    }
                }

                versionCounters[key] = n + 1;

                // Handle adjacent manifest.json → memory-index.json
                var manifestFile = Path.Combine(Path.GetDirectoryName(file)!, "manifest.json");
                if (File.Exists(manifestFile))
                {
                    var destMemory = Path.Combine(destAgentDir, "memory-index.json");
                    Console.WriteLine($"{tag} Rename baseline manifest: {manifestFile}");
                    Console.WriteLine($"        → {destMemory}");

                    if (apply)
                    {
                        try
                        {
                            SafeMove(manifestFile, destMemory);
                            Console.WriteLine($"  ✔ Renamed to {destMemory}");
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"  ✖ Rename failed: {ex.Message}");
                        }
                    }
                }
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>Copies a file and deletes the source only after a successful copy. Never silently loses data.</summary>
    private static void SafeMove(string source, string destination)
    {
        if (File.Exists(destination))
        {
            // Don't overwrite — warn and skip
            throw new InvalidOperationException($"Destination already exists: {destination}");
        }

        // Copy first, verify, then delete source
        File.Copy(source, destination, overwrite: false);
        if (new FileInfo(destination).Length == new FileInfo(source).Length)
        {
            File.Delete(source);
        }
        else
        {
            File.Delete(destination);
            throw new InvalidOperationException($"Copy verification failed for {source} → {destination} (size mismatch).");
        }
    }
}
