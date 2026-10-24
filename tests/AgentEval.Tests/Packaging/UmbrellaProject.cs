// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Xml.Linq;

namespace AgentEval.Tests.Packaging;

/// <summary>The actual package project and its embedded project declarations for package/API guards.</summary>
internal static class UmbrellaProject
{
    internal static string ProjectFile
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentEval.sln")))
                directory = directory.Parent;
            return directory is null
                ? throw new InvalidOperationException("AgentEval.sln not located.")
                : Path.Combine(directory.FullName, "src", "AgentEval", "AgentEval.csproj");
        }
    }

    internal static IReadOnlyList<string> EmbeddedProjectFiles()
    {
        var project = ProjectFile;
        var directory = Path.GetDirectoryName(project)!;
        return XDocument.Load(project).Descendants("ProjectReference")
            .Select(reference => Path.GetFullPath(Path.Combine(directory,
                reference.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();
    }
}
