// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Xml.Linq;
using Xunit;

namespace AgentEval.Tests.Packaging;

/// <summary>
/// Package validation runs as part of <c>dotnet pack</c>, which skips an <c>IsPackable=false</c> project: set there,
/// <c>EnablePackageValidation</c> never runs and checks nothing. Core and Abstractions had it while not packable;
/// it belongs on the umbrella package, which embeds them.
/// </summary>
public class PackageValidationPlacementTests
{
    [Fact]
    public void EnablePackageValidation_IsOnlyWhereAPackRunsIt()
    {
        var srcRoot = Path.Combine(LocateRepoRoot(), "src");

        var inert = Directory.EnumerateFiles(srcRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(p => IsTrue(p, "EnablePackageValidation") && IsFalse(p, "IsPackable"))
            .Select(p => Path.GetFileName(p))
            .ToList();

        Assert.True(inert.Count == 0,
            "EnablePackageValidation is set in a project that never packs (IsPackable=false): " + string.Join(", ", inert));
        Assert.True(IsTrue(Path.Combine(srcRoot, "AgentEval", "AgentEval.csproj"), "EnablePackageValidation"),
            "The umbrella AgentEval package, which ships Core and Abstractions, must enable package validation.");
    }

    private static bool IsTrue(string csproj, string property) => Value(csproj, property) is { } v && v.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static bool IsFalse(string csproj, string property) => Value(csproj, property) is { } v && v.Equals("false", StringComparison.OrdinalIgnoreCase);

    // The last unconditioned value in the project file; XDocument ignores comments.
    private static string? Value(string csproj, string property) =>
        XDocument.Load(csproj).Descendants(property).Where(e => e.Attribute("Condition") is null).LastOrDefault()?.Value.Trim();

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("AgentEval.sln not located.");
    }
}
