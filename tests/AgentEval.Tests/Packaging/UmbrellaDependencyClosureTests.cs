// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Xml.Linq;
using Xunit;

namespace AgentEval.Tests.Packaging;

/// <summary>
/// ARC-10: the umbrella <c>AgentEval.csproj</c> embeds every sub-project DLL with
/// <c>PrivateAssets="all"</c>, which suppresses transitive NuGet propagation, so each sub-project's
/// external runtime <c>PackageReference</c>s must be RE-DECLARED on the umbrella by hand. Previously a
/// drift (a sub-project adding a runtime dependency the umbrella did not mirror) produced no compile
/// signal and shipped a broken / silently-vulnerable package (SEC-02 was a concrete instance). This test
/// is that signal: it fails when any embedded sub-project references a runtime package the umbrella does
/// not declare.
/// </summary>
public class UmbrellaDependencyClosureTests
{
    // Build-only / dev-only packages that legitimately do NOT need to propagate to NuGet consumers
    // (source generators, analyzers, SourceLink). They are referenced PrivateAssets="all" and excluded
    // from the closure requirement.
    private static readonly HashSet<string> BuildOnlyAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.SourceLink.GitHub",
        "Microsoft.Agents.AI.Workflows.Generators", // source generator — dev-time tooling, see AgentEval.MAF.csproj
    };

    // The sub-projects embedded into the umbrella package (AgentEval.csproj ProjectReferences).
    private static readonly string[] EmbeddedSubProjects =
    [
        "AgentEval.Abstractions", "AgentEval.Core", "AgentEval.Compliance.Core", "AgentEval.DataLoaders",
        "AgentEval.Evals.Agentic", "AgentEval.Compliance.Gdpr", "AgentEval.Compliance.EuAiAct",
        "AgentEval.Evals.Performance", "AgentEval.Rendering.Pdf", "AgentEval.MAF", "AgentEval.Memory",
        "AgentEval.RedTeam",
    ];

    [Fact]
    public void Umbrella_DeclaresEverySubProjectRuntimePackage()
    {
        var srcRoot = Path.Combine(LocateRepoRoot(), "src");

        var umbrellaPackages = RuntimePackageRefs(Path.Combine(srcRoot, "AgentEval", "AgentEval.csproj"));

        var missing = new List<string>();
        foreach (var sub in EmbeddedSubProjects)
        {
            var csproj = Path.Combine(srcRoot, sub, sub + ".csproj");
            Assert.True(File.Exists(csproj), $"Sub-project csproj not found: {csproj}");

            foreach (var pkg in RuntimePackageRefs(csproj))
            {
                if (BuildOnlyAllowList.Contains(pkg)) continue;
                if (!umbrellaPackages.Contains(pkg))
                    missing.Add($"{pkg} (referenced by {sub})");
            }
        }

        Assert.True(missing.Count == 0,
            "The umbrella AgentEval.csproj must re-declare every runtime PackageReference of its embedded " +
            "sub-projects (PrivateAssets=all suppresses transitive propagation). Missing:\n  - " +
            string.Join("\n  - ", missing) +
            "\nAdd the package to AgentEval.csproj's external-deps ItemGroup, or add it to BuildOnlyAllowList " +
            "if it is genuinely build-only (analyzer / source generator).");
    }

    // Reads <PackageReference Include="X"> items that are NOT PrivateAssets="all" (runtime deps).
    // XDocument ignores XML comments, so commented-out example references are not counted.
    private static HashSet<string> RuntimePackageRefs(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pr in doc.Descendants("PackageReference"))
        {
            var include = pr.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include)) continue;

            var privateAssets = pr.Attribute("PrivateAssets")?.Value
                ?? pr.Element("PrivateAssets")?.Value;
            if (string.Equals(privateAssets, "all", StringComparison.OrdinalIgnoreCase)) continue;

            result.Add(include);
        }
        return result;
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("AgentEval.sln not located.");
    }
}
