// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Reflection;
using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Models;
using Xunit;

namespace AgentEval.NuGetConsumer.Tests;

/// <summary>
/// Plan task 4.2 — AE-04's join, asserted from OUTSIDE, against the published package.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>These are the only tests in this project that run unconditionally.</b> The other nine are
/// credential-gated and skip on any machine without Azure OpenAI configured, which is every CI run —
/// so before this file the package-consumer suite reported <b>0 passed / 9 skipped</b> and proved
/// nothing about the package. A suite in which everything skips is not a suite.
/// </para>
/// <para>
/// AE-04 was a REACHABILITY defect: the library was fine and no path reached it from where a consumer
/// stood. So the test that it is fixed has to be a consumer holding nothing but the package —
/// <b>this project has no <c>ProjectReference</c></b>, and every type below is resolved from
/// <c>PackageReference Include="AgentEval"</c>. Against 0.34.0-beta this file does not compile:
/// the namespace <c>AgentEval.Evals.Meta</c> does not exist in that assembly.
/// </para>
/// <para>
/// Nothing here spends: no <c>IChatClient</c>, no agent, no network.
/// </para>
/// </remarks>
public class PackageReachabilityTests
{
    private sealed class ContainsEval(string needle)
        : AtomicCodeEval("consumer.contains", "Response contains the needle", "test", "1.0.0")
    {
        protected override EvalResult Evaluate(EvalInput input)
        {
            if (input.Response is null)
                return NotApplicable("no response was captured, so nothing here can say what it contained.");

            var hit = input.Response.Contains(needle, StringComparison.Ordinal);
            return Build(hit ? 1.0 : 0.0, hit, hit ? "none" : "medium");
        }
    }

    [Fact]
    public void TheJoinIsReachableFromThePackageAlone()
    {
        // The claim, as an assertion: every load-bearing type of AE-04's join resolves, and all of
        // them come out of the AgentEval package rather than a project in this repository.
        Type[] join =
        [
            typeof(FloorAdmittedEval), typeof(TestRunEvalProjection), typeof(ChanceFloor),
            typeof(AtomicCodeEval), typeof(AdmittedCheck), typeof(BenchmarkDefinition),
            typeof(BenchmarkArm), typeof(BenchmarkRunner), typeof(BenchmarkScore),
        ];

        Assert.All(join, t => Assert.NotNull(t.Assembly.Location));

        // And they come from AgentEval's own assemblies, not from this test project.
        Assert.All(join, t => Assert.StartsWith("AgentEval", t.Assembly.GetName().Name!, StringComparison.Ordinal));
        Assert.DoesNotContain(join, t => t.Assembly == typeof(PackageReachabilityTests).Assembly);
    }

    [Fact]
    public async Task TheDoorRefusesAnEvalWithNoFloor_FromThePackage()
    {
        // The rule the whole release exists to make unreachable-to-violate, checked as a consumer.
        var ex = Assert.Throws<ArgumentException>(
            () => FloorAdmittedEval.Admit(new ContainsEval("yes"), null!));

        Assert.Contains("NO chance floor", ex.Message, StringComparison.Ordinal);

        // …and the other direction: with a floor it admits, and the floor reaches the result.
        var admitted = FloorAdmittedEval.Admit(new ContainsEval("yes"), ChanceFloor.UniformChoice(4));
        var result = await admitted.EvaluateAsync(new EvalInput("q", "yes"));

        Assert.True(result.Score.Passed);
        Assert.Equal(0.25, admitted.Floor.ComparisonBar, 10);
    }

    [Fact]
    public void ADefinitionRefusesACaseWithNoId_FromThePackage()
    {
        // TestCase.Id is optional on the type and required by a definition, because the case is the
        // unit of analysis. Asserted here so a consumer discovers it at authoring, not at the join.
        var ex = Assert.Throws<ArgumentException>(() => new BenchmarkDefinition(
            "bench", "1.0.0",
            [new TestCase { Name = "nameless", Input = "q" }],
            [new AdmittedCheck(new ContainsEval("yes"), ChanceFloor.UniformChoice(4))]));

        Assert.Equal("Cases", ex.ParamName);
    }

    [Fact]
    public async Task AnUndecidableRowIsSkipped_NeverAFailure_FromThePackage()
    {
        // Applicability survives the package boundary: a check that could not look must not report a
        // 0.0, because a 0.0 is a measurement.
        var admitted = FloorAdmittedEval.Admit(new ContainsEval("yes"), ChanceFloor.UniformChoice(4));
        var result = await admitted.EvaluateAsync(new EvalInput("q"));   // no Response at all

        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.NotApplicable, result.Score.CensusBucket());
        Assert.False(result.Score.CountsTowardAggregate());
    }

    [Fact]
    public void AFloorAtTheCeilingIsUndecidable_NeverAPass_FromThePackage()
    {
        // The meta lane, from outside: a perfect arm against a bar luck clears with certainty earns
        // no significance and claims none.
        var observations = Enumerable.Range(0, 8)
            .Select(i => Observation.Measured($"c{i}", "live", 1.0))
            .ToList();

        var ceiling = FloorComparison.Compute(
            observations, "live", ChanceFloor.AvoidsAll(poolSize: 10, forbidden: 0, draws: 3));

        Assert.Equal(8, ceiling.Successes);
        Assert.True(double.IsNaN(ceiling.PValue));
        Assert.False(ceiling.AboveFloor);

        // The positive control, so the NaN above is the BAR's answer and not the code refusing all.
        var derivable = FloorComparison.Compute(observations, "live", ChanceFloor.UniformChoice(4));
        Assert.False(double.IsNaN(derivable.PValue));
        Assert.True(derivable.AboveFloor);
    }

    [Fact]
    public void ThePackagedAssemblyCarriesTheReleaseVersion()
    {
        // Guards against the whole suite passing against a stale local build: the assembly under
        // test must be the released one.
        var info = typeof(FloorAdmittedEval).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.False(string.IsNullOrWhiteSpace(info));
        Assert.StartsWith("0.35.0-beta", info!, StringComparison.Ordinal);
    }
}
