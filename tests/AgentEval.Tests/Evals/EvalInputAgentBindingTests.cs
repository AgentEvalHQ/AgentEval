// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// 7.1 — the legacy <c>Metadata["agent"]</c> convention has ONE owner, and an absent subject is a
/// skipped result rather than a zero.
/// </summary>
public class EvalInputAgentBindingTests
{
    private sealed class StubAgent : IEvaluableAgent
    {
        public string Name => "stub";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("binding is not invocation");
    }

    private static EvalInput With(object? value) =>
        new("q")
        {
            Metadata = value is null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object> { [EvalInputAgentBinding.AgentMetadataKey] = value },
        };

    // ── Both directions ───────────────────────────────────────────────────────

    [Fact]
    public void AnAgentUnderTheCanonicalKey_IsRead()
    {
        var expected = new StubAgent();

        Assert.True(EvalInputAgentBinding.TryReadAgent(With(expected), out var agent));
        Assert.Same(expected, agent);
    }

    [Fact]
    public void NoMetadataAtAll_IsRefused()
    {
        Assert.False(EvalInputAgentBinding.TryReadAgent(new EvalInput("q"), out var agent));
        Assert.Null(agent);
    }

    [Fact]
    public void AnEmptyBag_IsRefused() =>
        Assert.False(EvalInputAgentBinding.TryReadAgent(With(null), out _));

    [Fact]
    public void SomethingThatIsNotAnAgent_IsRefused_NotCastToNullSilently()
    {
        // The hand-rolled form was `raw as IEvaluableAgent`, which turns a wrong TYPE into the same
        // null as a missing key. That collapse is deliberate here and asserted, so nobody "fixes" it
        // into a throw that would take a run down at the point of least information.
        Assert.False(EvalInputAgentBinding.TryReadAgent(With("not-an-agent"), out var agent));
        Assert.Null(agent);
    }

    [Fact]
    public void ANullInput_Throws() =>
        Assert.Throws<ArgumentNullException>(() => EvalInputAgentBinding.TryReadAgent(null!, out _));

    // ── The refusal text ──────────────────────────────────────────────────────

    [Fact]
    public void TheRefusalNamesTheFamily_TheTypedWayIn_AndRefusesTheZeroReading()
    {
        var reason = EvalInputAgentBinding.AbsentAgentReason("OWASP", "OwaspBenchmarkRun.ScanAsync(agent)");

        Assert.Contains("OWASP", reason, StringComparison.Ordinal);
        Assert.Contains("OwaspBenchmarkRun.ScanAsync(agent)", reason, StringComparison.Ordinal);
        Assert.Contains("measured NOTHING", reason, StringComparison.Ordinal);
        Assert.Contains("not a zero and not a pass", reason, StringComparison.Ordinal);
        Assert.Contains("BenchmarkArm", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("", "x")]
    [InlineData("OWASP", null)]
    [InlineData("OWASP", "  ")]
    public void ABlankFamilyOrEntryPoint_IsRefused(string? family, string? entry) =>
        // ThrowsAny, not Throws: ThrowIfNullOrWhiteSpace raises ArgumentNullException for null and
        // ArgumentException for blank, and an exact-type assertion would pass on half the inputs.
        Assert.ThrowsAny<ArgumentException>(() => EvalInputAgentBinding.AbsentAgentReason(family!, entry!));

    // ── The census: one owner, not four copies ────────────────────────────────

    [Fact]
    public void NoFamilyHandRollsTheReadAnyMore()
    {
        // The four families each wrote `TryGetValue("agent") … as IEvaluableAgent` by hand: one
        // convention, four implementations, four messages, four chances to diverge. A reflection
        // test cannot see a string key, so this reads the shipped SOURCE — and asserts the scan
        // found the files, because a scan that matched nothing would pass for the wrong reason.
        var root = RepositoryRoot();
        Assert.NotNull(root);

        var sources = Directory.GetFiles(Path.Combine(root!, "src"), "*.cs", SearchOption.AllDirectories);
        Assert.True(sources.Length > 200, $"the scan found only {sources.Length} source files, so it measured nothing");

        var handRolled = sources
            .Where(f => !Path.GetFileName(f).Equals("EvalInputAgentBinding.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("TryGetValue(\"agent\"", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(handRolled);
    }

    private static string? RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("AgentEval.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}

/// <summary>
/// 7.1 — the weights-only aggregation path is reachable THROUGH the interface.
/// </summary>
public class AggregationStrategyInterfaceTests
{
    private static readonly IAggregationStrategy[] s_shipped =
    [
        WeightedSumAggregation.Instance,
        MinAggregation.Instance,
        CapByWorstAggregation.Instance,
        MajorityVoteAggregation.Instance,
        WeightedMedianAggregation.Instance,
    ];

    private static EvalResult Leaf(double value, string severity = "none") => new(
        new("k", "n", "c", "1.0.0"),
        new(value, null, value >= 1.0 ? "pass" : "fail", value >= 1.0, null, severity, null),
        new(null, null, null, null, null),
        new("atomic-code", null, null, null, null, 0, false),
        DateTimeOffset.UtcNow);

    [Fact]
    public void EveryShippedStrategy_ExposesTheWeightsOnlyPath()
    {
        // Before 7.1 this was a `public static` per strategy and therefore unreachable through the
        // interface: a caller holding an IAggregationStrategy had to manufacture an EvalComponent per
        // weight, and an EvalComponent demands an IEval. Four throwing IEval stubs existed for
        // exactly that reason and were deleted in 1.1; this member is what keeps them deleted.
        var results = new[] { Leaf(1.0), Leaf(0.0) };
        var weights = new[] { 1.0, 1.0 };

        foreach (var strategy in s_shipped)
        {
            var (score, severity) = strategy.AggregateWeights(results, weights);

            Assert.True(double.IsFinite(score), $"{strategy.Name} returned a non-finite score");
            Assert.False(string.IsNullOrWhiteSpace(severity));
        }
    }

    [Fact]
    public void TheInterfacePath_AgreesWithTheComponentPath()
    {
        // The two must not diverge: Aggregate forwards to AggregateWeights in all five, and this is
        // the assertion that keeps a future edit from splitting them.
        var results = new[] { Leaf(1.0), Leaf(0.5, "medium"), Leaf(0.0, "high") };
        var weights = new[] { 0.5, 0.3, 0.2 };
        var components = weights.Select(w => new EvalComponent(new NeverRunEval(), w)).ToArray();

        foreach (var strategy in s_shipped)
        {
            var viaWeights = strategy.AggregateWeights(results, weights);
            var viaComponents = strategy.Aggregate(results, components);

            Assert.Equal(viaComponents.Score, viaWeights.Score, 12);
            Assert.Equal(viaComponents.Severity, viaWeights.Severity);
        }
    }

    [Fact]
    public void MisalignedWeights_AreRefused_RatherThanTruncated()
    {
        foreach (var strategy in s_shipped)
        {
            Assert.Throws<InvalidOperationException>(
                () => strategy.AggregateWeights([Leaf(1.0), Leaf(0.0)], [1.0]));
        }
    }

    /// <summary>Present only to carry a weight; aggregation never runs it, which is the point of 1.1.</summary>
    private sealed class NeverRunEval : IEval
    {
        public string Key => "never_run";
        public string Name => "never run";
        public string Category => "test";
        public string Version => "1.0.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            throw new NotSupportedException("aggregation reads Weight and nothing else");
    }
}
