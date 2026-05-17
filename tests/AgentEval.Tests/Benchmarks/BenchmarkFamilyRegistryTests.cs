// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core.Benchmarks;
using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// Tests for <see cref="BenchmarkFamilyRegistry"/> — Phase 8 / v0.10.0-beta.
/// </summary>
/// <remarks>
/// These tests cover the registry contract:
/// register / lookup-by-name / enumerate-all / unique-name invariant / preset-overlap
/// detection / extensibility (a synthetic external family appears in All) / thread-safety.
/// </remarks>
public class BenchmarkFamilyRegistryTests
{
    // Use a fixed prefix so we can isolate test fixtures from real registrations.
    // Note: we don't call Reset() in [Fact] tests because that would clobber the real
    // registry state observed by other tests in the same assembly. Instead we register
    // synthetic families with unique-per-test names and verify their presence.

    private static BenchmarkFamily MakeSyntheticFamily(string name, CostTier tier = CostTier.Free) =>
        new BenchmarkFamily(
            name: name,
            description: $"Synthetic test family '{name}'",
            defaultCostTier: tier,
            presets:
            [
                new BenchmarkPreset("default", $"Default preset for '{name}'", null),
                new BenchmarkPreset("smoke", $"Smoke preset for '{name}'", CostTier.Low),
            ],
            compositeFactory: (_, _) => throw new NotSupportedException("Synthetic family — no real composite factory."),
            owningAssemblyName: "AgentEval.Tests");

    [Fact]
    public void Register_NewFamily_AppearsInAll()
    {
        var uniqueName = $"reg-test-{Guid.NewGuid():N}";
        var family = MakeSyntheticFamily(uniqueName);

        BenchmarkFamilyRegistry.Register(family);

        Assert.Contains(BenchmarkFamilyRegistry.All, f => f.Name == uniqueName);
    }

    [Fact]
    public void TryGet_ByName_ReturnsRegisteredFamily()
    {
        var uniqueName = $"tryget-test-{Guid.NewGuid():N}";
        var family = MakeSyntheticFamily(uniqueName);
        BenchmarkFamilyRegistry.Register(family);

        var got = BenchmarkFamilyRegistry.TryGet(uniqueName);

        Assert.NotNull(got);
        Assert.Equal(uniqueName, got!.Name);
        Assert.Same(family, got);
    }

    [Fact]
    public void TryGet_UnknownName_ReturnsNull()
    {
        var result = BenchmarkFamilyRegistry.TryGet($"never-registered-{Guid.NewGuid():N}");
        Assert.Null(result);
    }

    [Fact]
    public void All_ReturnsRegisteredFamilies_OrderedAlphabetically()
    {
        // Use prefix sort to verify ordering deterministically
        var n1 = $"order-test-zzz-{Guid.NewGuid():N}";
        var n2 = $"order-test-aaa-{Guid.NewGuid():N}";

        BenchmarkFamilyRegistry.Register(MakeSyntheticFamily(n1));
        BenchmarkFamilyRegistry.Register(MakeSyntheticFamily(n2));

        var all = BenchmarkFamilyRegistry.All;
        var idxA = all.Select((f, i) => (f, i)).First(t => t.f.Name == n2).i;
        var idxZ = all.Select((f, i) => (f, i)).First(t => t.f.Name == n1).i;
        Assert.True(idxA < idxZ, $"Expected alphabetic ordering: '{n2}' (idx {idxA}) before '{n1}' (idx {idxZ}).");
    }

    [Fact]
    public void Register_DuplicateSameContent_IsIdempotent()
    {
        var name = $"idempotent-test-{Guid.NewGuid():N}";
        var first = MakeSyntheticFamily(name);
        var second = MakeSyntheticFamily(name);

        BenchmarkFamilyRegistry.Register(first);
        // Should not throw — same content.
        BenchmarkFamilyRegistry.Register(second);

        var got = BenchmarkFamilyRegistry.TryGet(name);
        Assert.NotNull(got);
    }

    [Fact]
    public void Register_DuplicateDifferentContent_Throws()
    {
        var name = $"conflict-test-{Guid.NewGuid():N}";
        var first = MakeSyntheticFamily(name, CostTier.Free);
        var second = MakeSyntheticFamily(name, CostTier.High);  // different DefaultCostTier → different content

        BenchmarkFamilyRegistry.Register(first);
        Assert.Throws<InvalidOperationException>(() => BenchmarkFamilyRegistry.Register(second));
    }

    [Fact]
    public void Register_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BenchmarkFamilyRegistry.Register(null!));
    }

    [Fact]
    public void BenchmarkFamily_DuplicatePresetNames_ThrowsAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new BenchmarkFamily(
            name: "preset-dupe",
            description: "test",
            defaultCostTier: CostTier.Free,
            presets:
            [
                new BenchmarkPreset("dup", "first", null),
                new BenchmarkPreset("dup", "second", null),
            ],
            compositeFactory: (_, _) => null!));
    }

    [Fact]
    public void BenchmarkFamily_NoFactory_ThrowsAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new BenchmarkFamily(
            name: "no-factory",
            description: "test",
            defaultCostTier: CostTier.Free,
            presets: [new BenchmarkPreset("p", "p", null)]
            // no compositeFactory, no runnerFactory
        ));
    }

    [Fact]
    public void BenchmarkFamily_RunnerFactoryWithoutRunnerType_Throws()
    {
        Assert.Throws<ArgumentException>(() => new BenchmarkFamily(
            name: "no-runner-type",
            description: "test",
            defaultCostTier: CostTier.Free,
            presets: [new BenchmarkPreset("p", "p", null)],
            runnerFactory: _ => new object()
            // no runnerType
        ));
    }

    /// <summary>
    /// The crown-jewel test: a synthetic external "family" can be registered at runtime
    /// and shows up in <see cref="BenchmarkFamilyRegistry.All"/>. This proves the
    /// registry is genuinely a plug-in surface (not a hardcoded enum dressed in a class).
    /// </summary>
    [Fact]
    public void Extensibility_RuntimeRegisteredFamily_AppearsInAll()
    {
        var name = $"plugin-test-{Guid.NewGuid():N}";
        var family = MakeSyntheticFamily(name);

        var countBefore = BenchmarkFamilyRegistry.All.Count;
        BenchmarkFamilyRegistry.Register(family);
        var countAfter = BenchmarkFamilyRegistry.All.Count;

        Assert.True(countAfter >= countBefore + 1,
            $"Adding a synthetic family should increase All.Count: before={countBefore}, after={countAfter}.");
        Assert.Contains(BenchmarkFamilyRegistry.All, f => f.Name == name);
    }

    /// <summary>
    /// Thread-safety: concurrent Register + All calls don't crash or lose entries.
    /// </summary>
    [Fact]
    public async Task ThreadSafety_ConcurrentRegisterAndEnumerate_WorksWithoutLosingEntries()
    {
        const int writerCount = 8;
        const int registrationsPerWriter = 20;

        var prefix = $"threadsafe-{Guid.NewGuid():N}";

        var writers = Enumerable.Range(0, writerCount).Select(writerIdx => Task.Run(() =>
        {
            for (int j = 0; j < registrationsPerWriter; j++)
            {
                var name = $"{prefix}-{writerIdx}-{j}";
                BenchmarkFamilyRegistry.Register(MakeSyntheticFamily(name));
            }
        })).ToList();

        // Readers don't have to assert specific values during the race — just that enumeration
        // doesn't throw under concurrent mutation (which is what ConcurrentDictionary buys us).
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                var snapshot = BenchmarkFamilyRegistry.All;
                Assert.NotNull(snapshot);
            }
        })).ToList();

        await Task.WhenAll(writers.Concat(readers));

        // After everything settles, every registration should be present.
        var all = BenchmarkFamilyRegistry.All;
        for (int w = 0; w < writerCount; w++)
        {
            for (int j = 0; j < registrationsPerWriter; j++)
            {
                var expected = $"{prefix}-{w}-{j}";
                Assert.Contains(all, f => f.Name == expected);
            }
        }
    }

    /// <summary>
    /// Integration: after the CLI's anchor step (touching one type from each expected
    /// assembly), all 8 default families are present in <see cref="BenchmarkFamilyRegistry.All"/>.
    /// </summary>
    [Fact]
    public void AllEightDefaultFamilies_AppearInRegistry()
    {
        AgentEval.Cli.Commands.BenchListCommand.AnchorAssemblies();

        var expectedNames = new[] { "agentic", "gdpr", "eu-ai-act", "owasp", "mitre", "longmemeval", "memory", "perf" };
        foreach (var name in expectedNames)
        {
            Assert.NotNull(BenchmarkFamilyRegistry.TryGet(name));
        }
    }
}
