// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// ADR-031 <b>C3</b> — <c>EvaluatorCardRegistry</c> out of Mission Control and into
/// <c>AgentEval.Core</c>.
/// </summary>
/// <remarks>
/// <para>
/// The defect was reach, not correctness. Sixty cards carrying <c>defaultThreshold</c>,
/// <c>costTier</c> and <c>expectedInputs</c> lived in an assembly that is
/// <c>IsPackable=false</c>, <b>net10-only</b>, and referenced by the CLI under
/// <c>Condition="'$(TargetFramework)' == 'net10.0'"</c> — so on net8 and net9 the type was not even
/// in the CLI's compilation, and no package has ever carried it.
/// </para>
/// <para>
/// ⚠ <b>This test class runs on all three TFMs, which is the point.</b> A net10-only assertion
/// would have been green before the move as well.
/// </para>
/// </remarks>
public class EvaluatorCardRegistryTests
{
    private static Assembly Agentic => typeof(AgentEval.Benchmarks.AgenticBenchmark).Assembly;

    [Fact]
    public void TheRegistryLivesInCore_NotInMissionControl()
    {
        // The whole item is a relocation, so the assembly IS the assertion. Stated by name so it
        // fails loudly if somebody moves it back or forwards it.
        Assert.Equal("AgentEval.Core", typeof(EvaluatorCardRegistry).Assembly.GetName().Name);
        Assert.Equal("AgentEval.Evals", typeof(EvaluatorCardRegistry).Namespace);
    }

    [Fact]
    public void TheCardsAreReachableOnEveryTargetFramework()
    {
        // Before the move this expression did not COMPILE on net8/net9 — AgentEval.MissionControl
        // is net10-only and the CLI referenced it conditionally.
        var registry = new EvaluatorCardRegistry(Agentic);

        Assert.True(registry.Count > 0,
            "the registry loaded zero cards, which means either the cards moved or the resource "
            + "convention changed — both make every assertion below vacuous");
        Assert.Contains("AgentEval.Evals.Agentic", registry.ScannedAssemblies);
    }

    [Fact]
    public void EveryCardOnDiskIsLoaded()
    {
        // Asserts its own input against the FILE COUNT rather than against a pinned number: the
        // count grows whenever anybody drops a card in, and a pinned digit would be wrong by the
        // next commit. What must hold is that the registry loses none of them.
        string cardsDir = Path.Combine(
            RepositoryRoot(), "src", "AgentEval.Evals.Agentic", "EvaluatorCards");

        Assert.True(Directory.Exists(cardsDir), $"{cardsDir} is not where this test expects it — the scan asserts nothing.");
        int onDisk = Directory.GetFiles(cardsDir, "*.json").Length;
        Assert.True(onDisk > 0, "no card files on disk — the comparison below would be 0 == 0");

        Assert.Equal(onDisk, new EvaluatorCardRegistry(Agentic).Count);
    }

    [Fact]
    public void ARegistryPointedAtNothingIsRefused()
    {
        // "No cards were found" and "nothing was scanned" are the same empty registry to a caller,
        // and the second is a wiring fault wearing the first's clothes. Extreme values are wiring
        // faults until proven otherwise.
        Assert.Throws<ArgumentException>(() => new EvaluatorCardRegistry());
        Assert.Throws<ArgumentException>(() => new EvaluatorCardRegistry(Array.Empty<Assembly>()));
        Assert.Throws<ArgumentException>(() => new EvaluatorCardRegistry((IEnumerable<Assembly>)[]));
    }

    [Fact]
    public void ARegistryOverAnAssemblyWithNoCardsIsEmptyButHonest()
    {
        // The other side of the same rule: an honest zero is allowed, and it can be told apart from
        // the refused one because the registry says what it looked at.
        var registry = new EvaluatorCardRegistry(typeof(EvaluatorCardRegistryTests).Assembly);

        Assert.Equal(0, registry.Count);
        Assert.Single(registry.ScannedAssemblies);
    }

    [Fact]
    public void ScanningTheSameAssemblyTwiceIsNotADuplicateKeyError()
    {
        // A caller composing sources from several places can easily name one twice, and the
        // duplicate-key guard would turn that into a startup crash that reads like a card defect.
        var registry = new EvaluatorCardRegistry(Agentic, Agentic);

        Assert.Equal(new EvaluatorCardRegistry(Agentic).Count, registry.Count);
        Assert.Single(registry.ScannedAssemblies);
    }

    [Fact]
    public void ListAndGetAgreeWithEachOther()
    {
        var registry = new EvaluatorCardRegistry(Agentic);
        var all = registry.List().ToArray();

        Assert.Equal(registry.Count, all.Length);
        foreach (var card in all)
            Assert.Same(card, registry.Get(card.Key));

        Assert.Null(registry.Get("no.such.evaluator.key"));
    }

    [Fact]
    public void FilteringNarrowsRatherThanEmpties()
    {
        var registry = new EvaluatorCardRegistry(Agentic);
        string category = registry.List().First().Category;

        var filtered = registry.List(category: category).ToArray();

        Assert.NotEmpty(filtered);
        Assert.True(filtered.Length <= registry.Count);
        Assert.All(filtered, c => Assert.Equal(category, c.Category, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void NothingUnderSrcStillReferencesTheMissionControlLocation()
    {
        // The relocation is only done if the old home is gone. A type left behind in both places is
        // the fork this move exists to close.
        string oldPath = Path.Combine(
            RepositoryRoot(), "src", "AgentEval.MissionControl", "Services", "EvaluatorCardRegistry.cs");

        Assert.False(File.Exists(oldPath), $"{oldPath} still exists — the registry was copied, not moved.");
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("AgentEval.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"AgentEval.sln was not found above {AppContext.BaseDirectory}.");
    }
}
