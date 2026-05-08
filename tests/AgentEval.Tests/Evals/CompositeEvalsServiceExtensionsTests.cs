// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.Tests.Evals;

public class CompositeEvalsServiceExtensionsTests
{
    // ── Stub ─────────────────────────────────────────────────────────────────

    private sealed class FakeAggregation : IAggregationStrategy
    {
        public string Name => "Fake";
        public (double Score, string Severity) Aggregate(
            IReadOnlyList<EvalResult> r,
            IReadOnlyList<EvalComponent> c) => (0, "none");
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AddCompositeEvals_RegistersWeightedSumAsDefault()
    {
        var services = new ServiceCollection();

        services.AddCompositeEvals();

        var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IAggregationStrategy>();
        Assert.Same(WeightedSumAggregation.Instance, resolved);
    }

    [Fact]
    public void AddCompositeEvals_DoesNotOverrideExistingRegistration()
    {
        var services = new ServiceCollection();
        var fake = new FakeAggregation();
        services.AddSingleton<IAggregationStrategy>(fake);

        services.AddCompositeEvals();

        var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IAggregationStrategy>();
        Assert.Same(fake, resolved);
    }

    [Fact]
    public void AddCompositeEvals_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => CompositeEvalsServiceExtensions.AddCompositeEvals(null!));
    }
}
