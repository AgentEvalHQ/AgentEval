// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text;
using AgentEval.Tests.MAF.Gatekeeper;
using AgentEval.Tests.Snapshots;

namespace AgentEval.Tests.Packaging;

/// <summary>
/// Guards library scopes not covered by the existing Core, Abstractions, red-team and Gatekeeper snapshots.
/// Scope follows actual package declarations; a missing assembly fails instead of silently losing its API.
/// Beta/Experimental APIs retain their existing maturity when their current shape is snapshotted.
/// </summary>
public class BundledLibraryPublicApiSnapshotTests
{
    [Fact]
    public Task TheRemainingBundledSurface_MatchesTheApprovedSnapshot()
    {
        var assemblies = UmbrellaProject.EmbeddedProjectFiles().Append(UmbrellaProject.ProjectFile)
            .Select(path => Assembly.Load(new AssemblyName(Path.GetFileNameWithoutExtension(path))))
            .ToHashSet();
        var coveredAssemblies = new HashSet<Assembly>
        {
            typeof(AgentEval.Core.IMetric).Assembly,
            typeof(AgentEval.Assertions.ResponseAssertions).Assembly,
            typeof(AgentEval.RedTeam.RedTeamResult).Assembly,
            typeof(AgentEval.RedTeam.Gatekeeper.CanaryToolGate).Assembly,
        };
        var gatekeeperAssemblies = GatekeeperPublicApiSnapshotTests.Assemblies().ToHashSet();

        var output = new StringBuilder();
        foreach (var assembly in assemblies.OrderBy(a => a.GetName().Name, StringComparer.Ordinal))
        {
            output.Append("assembly ").AppendLine(assembly.GetName().Name);
            var types = assembly.GetExportedTypes()
                .Where(type => !coveredAssemblies.Contains(assembly)
                    && !(gatekeeperAssemblies.Contains(assembly) && GatekeeperPublicApiSnapshotTests.InScope(type)));
            output.Append(PublicApiSurface.Render(types, type => assemblies.Contains(type.Assembly)));
        }
        return Verify(output.ToString());
    }
}
