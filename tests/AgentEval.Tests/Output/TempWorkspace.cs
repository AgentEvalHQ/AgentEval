// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Tests.Output;

internal sealed class TempWorkspace : IDisposable
{
    public string Path { get; }

    private TempWorkspace(string path)
    {
        Path = path;
    }

    public static TempWorkspace Create(string? solutionName = "TestSolution")
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agenteval-tw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        if (solutionName is not null)
        {
            var solution = new { schemaVersion = "1.0", id = Guid.NewGuid(), name = solutionName };
            File.WriteAllText(
                System.IO.Path.Combine(path, "solution.json"),
                JsonSerializer.Serialize(solution, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }

        return new TempWorkspace(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
