// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AgentEval.Tests.Packaging;

/// <summary>
/// AEF (<c>contracts/aef/</c>) is licensed under Apache-2.0; the rest of AgentEval under MIT. A package that carries
/// AEF content must say so in its licence expression and ship AEF's <c>LICENSE</c> and <c>NOTICE</c>, as Apache-2.0
/// section 4 requires of anyone who redistributes the Work. <c>AgentEval.Results</c> embeds the AEF schemas, and a
/// .NET tool package carries its dependencies, so <c>AgentEval.Cli</c> carries them. Through 0.43.0-beta no package
/// carried AEF; the CLI as first built after AEF arrived (2026-10-10) declared MIT alone and packed neither file.
/// </summary>
public class AefLicencePackagingTests
{
    [Fact]
    public void EveryPackableProjectThatCarriesAef_DeclaresApacheAndPacksAefLicenceAndNotice()
    {
        var root = LocateRepoRoot();
        var projects = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(Path.GetFullPath)
            .ToDictionary(p => p, XDocument.Load, StringComparer.OrdinalIgnoreCase);
        var aefLicence = Path.Combine(root, "contracts", "aef", "LICENSE");
        var aefNotice = Path.Combine(root, "contracts", "aef", "NOTICE");
        var inherited = InheritedLicenceExpression(root);

        var carriers = projects.Keys
            .Where(p => !string.Equals(Property(projects[p], "IsPackable"), "false", StringComparison.OrdinalIgnoreCase))
            .Where(p => Closure(p, projects).Any(q => CarriesAefDirectly(projects[q])))
            .ToList();

        // A census that finds nothing proves nothing: the CLI is known to carry AEF through AgentEval.Results.
        Assert.Contains(carriers, p => Path.GetFileNameWithoutExtension(p) == "AgentEval.Cli");

        var problems = new List<string>();
        foreach (var project in carriers)
        {
            var name = Path.GetFileNameWithoutExtension(project);
            var expression = Property(projects[project], "PackageLicenseExpression") ?? inherited;
            var tokens = Regex.Split(expression, @"[\s()]+").Where(t => t.Length > 0).ToList();
            if (!tokens.Contains("Apache-2.0"))
                problems.Add($"{name}: PackageLicenseExpression '{expression}' does not name Apache-2.0.");
            // OR would let a consumer take the package under MIT alone, which AEF's licence does not allow.
            if (tokens.Contains("OR", StringComparer.OrdinalIgnoreCase))
                problems.Add($"{name}: PackageLicenseExpression '{expression}' uses OR; AEF content needs AND.");

            var packed = projects[project].Descendants("None")
                .Where(n => string.Equals(n.Attribute("Pack")?.Value, "true", StringComparison.OrdinalIgnoreCase))
                .Select(n => Resolve(project, n.Attribute("Include")?.Value ?? ""))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var required in new[] { aefLicence, aefNotice })
            {
                if (!packed.Contains(required))
                    problems.Add($"{name}: does not pack {Path.GetRelativePath(root, required)}.");
                else if (!File.Exists(required))
                    problems.Add($"{name}: packs {Path.GetRelativePath(root, required)}, which does not exist.");
            }
        }

        Assert.True(problems.Count == 0,
            "A package that carries AEF content (contracts/aef/, Apache-2.0) must declare Apache-2.0 with AND in its " +
            "licence expression and pack contracts/aef/LICENSE and contracts/aef/NOTICE:\n  - " + string.Join("\n  - ", problems));
    }

    // Any item that takes a file from contracts/aef/ (embedded schemas, linked content, packed files).
    // XDocument ignores XML comments, so a comment that mentions the folder does not count.
    private static bool CarriesAefDirectly(XDocument csproj) =>
        csproj.Descendants()
            .Select(e => e.Attribute("Include")?.Value)
            .Any(v => v is not null && v.Replace('\\', '/').Contains("contracts/aef/", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Closure(string project, IReadOnlyDictionary<string, XDocument> projects)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>([project]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current) || !projects.TryGetValue(current, out var doc)) continue;
            foreach (var reference in doc.Descendants("ProjectReference"))
                pending.Push(Resolve(current, reference.Attribute("Include")?.Value ?? ""));
        }
        return seen;
    }

    private static string Resolve(string project, string include) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, include.Replace('\\', Path.DirectorySeparatorChar)));

    private static string? Property(XDocument doc, string name) =>
        doc.Descendants(name).Select(e => e.Value.Trim()).LastOrDefault(v => v.Length > 0);

    private static string InheritedLicenceExpression(string root) =>
        Property(XDocument.Load(Path.Combine(root, "Directory.Build.props")), "PackageLicenseExpression")
        ?? throw new InvalidOperationException("Directory.Build.props sets no PackageLicenseExpression.");

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("AgentEval.sln not located.");
    }
}
