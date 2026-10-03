// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AgentEval.Tests.Packaging;

/// <summary>
/// THIRD-PARTY-NOTICES.md must travel inside both published NuGet packages. <c>AgentEval.Cli</c> is a .NET
/// tool, so its package redistributes third-party assemblies, among them ChilliCream.Nitro.App, whose
/// ChilliCream License 1.0 requires that "anyone who gets a copy of any part of the software from you also
/// gets a copy of these terms", and Google.Protobuf (BSD-3-Clause), whose notice must be reproduced with
/// binary redistributions. Before this test the file was in neither package and quoted neither text.
/// </summary>
public class ThirdPartyNoticesPackagingTests
{
    [Theory]
    [InlineData("AgentEval")]
    [InlineData("AgentEval.Cli")]
    public void PackableProject_PacksThirdPartyNoticesAtThePackageRoot(string project)
    {
        var projectDir = Path.Combine(LocateRepoRoot(), "src", project);
        var csproj = Path.Combine(projectDir, project + ".csproj");
        Assert.True(File.Exists(csproj), $"Project file not found: {csproj}");

        var packed = XDocument.Load(csproj).Descendants("None")
            .Where(n => string.Equals(n.Attribute("Pack")?.Value, "true", StringComparison.OrdinalIgnoreCase))
            .Select(n => new
            {
                Include = n.Attribute("Include")?.Value ?? "",
                PackagePath = n.Attribute("PackagePath")?.Value ?? "",
            })
            .Where(n => Path.GetFileName(n.Include.Replace('\\', '/')) == "THIRD-PARTY-NOTICES.md")
            .ToList();

        var item = Assert.Single(packed);
        Assert.True(item.PackagePath is "\\" or "/", $"{project}: THIRD-PARTY-NOTICES.md must be packed at the package root, not '{item.PackagePath}'.");

        var resolved = Path.GetFullPath(Path.Combine(projectDir, item.Include.Replace('\\', Path.DirectorySeparatorChar)));
        Assert.Equal(Path.Combine(LocateRepoRoot(), "THIRD-PARTY-NOTICES.md"), resolved);
        Assert.True(File.Exists(resolved), $"{project}: packed notices file does not exist: {resolved}");
    }

    [Theory]
    // ChilliCream License 1.0, "Notices" section (the condition that applies to redistribution).
    [InlineData("You must ensure that anyone who gets a copy of any part of the software from you also gets a copy of these terms.")]
    [InlineData("You may not move, change, disable, or circumvent the license key functionality in the software")]
    // BSD-3-Clause, Google.Protobuf: copyright line, binary-redistribution condition and disclaimer.
    [InlineData("Copyright 2008 Google Inc. All rights reserved.")]
    [InlineData("Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.")]
    [InlineData("Neither the name of Google Inc. nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.")]
    // Apache License 2.0 (OpenTelemetry.Api): the redistribution section.
    [InlineData("You must give any other recipients of the Work or Derivative Works a copy of this License")]
    // MIT permission notice.
    [InlineData("The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.")]
    public void NoticesFile_ReproducesTheLicenceTextsThatMustTravelWithTheBinaries(string requiredText)
    {
        var notices = File.ReadAllText(Path.Combine(LocateRepoRoot(), "THIRD-PARTY-NOTICES.md"));

        // The texts are quoted with their original line breaks; compare with whitespace collapsed.
        Assert.Contains(Collapse(requiredText), Collapse(notices), StringComparison.Ordinal);
    }

    private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ");

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("AgentEval.sln not located.");
    }
}
