// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

internal static class WorkspaceRootDiscovery
{
    /// <summary>Walks up from <paramref name="startDir"/> looking for a .sln/.slnx file or .git directory.</summary>
    public static string? Find(string startDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDir);

        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (dir.GetFiles("*.sln").Length > 0 ||
                dir.GetFiles("*.slnx").Length > 0)
                return dir.FullName;

            if (dir.GetDirectories(".git").Length > 0)
                return dir.FullName;

            dir = dir.Parent;
        }
        return null;
    }
}
