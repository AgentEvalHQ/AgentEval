// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli.Internal;
using AgentEval.Output;

namespace AgentEval.Cli.Commands;

/// <summary>Implements the <c>agenteval init</c> command.</summary>
public static class InitCommand
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Runs the init command, discovering workspace root from the current directory.</summary>
    public static Task<int> RunAsync(string? name) =>
        RunAsync(name, rootOverride: null);

    /// <summary>Runs the init command with an optional explicit root override (used in tests).</summary>
    internal static async Task<int> RunAsync(string? name, string? rootOverride)
    {
        var root = rootOverride ?? WorkspaceRootDiscovery.Find(Directory.GetCurrentDirectory());
        if (root is null)
        {
            Console.Error.WriteLine("✖ Could not find a solution root (.sln, .slnx, or .git).");
            return 1;
        }

        var dir = Path.Combine(root, ".agenteval");
        Directory.CreateDirectory(dir);

        var solutionFile = Path.Combine(dir, "solution.json");
        if (File.Exists(solutionFile))
        {
            Console.WriteLine($"  Already initialized at {dir}");
            return 0;
        }

        var solutionName = name ?? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var solutionDoc = new
        {
            schemaVersion = "1.0",
            id = Guid.NewGuid(),
            name = solutionName
        };

        await using (var stream = File.Create(solutionFile))
        {
            await JsonSerializer.SerializeAsync(stream, solutionDoc, s_json);
        }

        await EmbeddedResource.CopyToAsync("README.template.md", Path.Combine(dir, "README.md"));
        await EmbeddedResource.CopyToAsync("gitignore.template", Path.Combine(dir, ".gitignore"));

        Console.WriteLine($"✔ Initialized .agenteval/ at {dir}");
        return 0;
    }
}
