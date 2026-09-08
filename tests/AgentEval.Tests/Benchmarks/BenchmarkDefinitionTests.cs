// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// The definition records are data, and the only things they refuse are the ones that make a run
/// unscoreable without saying so.
/// </summary>
/// <remarks>
/// Every refusal is asserted in BOTH directions — the refusing case AND the accepting case — because
/// a guard never observed accepting is a wall, and a guard never observed refusing is decoration.
/// </remarks>
public class BenchmarkDefinitionTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private sealed class StubEval(string key) : IEval
    {
        public string Key { get; } = key;
        public string Name => "stub";
        public string Category => "test";
        public string Version => "1.0.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(new EvalResult(
                new(Key, Name, Category, Version),
                new(1.0, null, "pass", true, null, "none", null),
                new(null, null, null, null, null),
                new("atomic-code", null, null, null, null, 0, false),
                DateTimeOffset.UtcNow));
    }

    private static readonly ChanceFloor Floor = ChanceFloor.UniformChoice(4);

    private static TestCase Case(string? id, string name = "c") =>
        new() { Id = id, Name = name, Input = "q" };

    private static AdmittedCheck Check(string key) => new(new StubEval(key), Floor);

    private static BenchmarkDefinition Definition(
        IReadOnlyList<TestCase>? cases = null, IReadOnlyList<AdmittedCheck>? checks = null) =>
        new("bench", "1.0.0", cases ?? [Case("c1")], checks ?? [Check("k1")]);

    // ── The accepting direction, first ────────────────────────────────────────

    [Fact]
    public void AWellFormedDefinition_IsAccepted()
    {
        var definition = Definition([Case("c1"), Case("c2")], [Check("k1"), Check("k2")]);

        Assert.Equal("bench", definition.Key);
        Assert.Equal("1.0.0", definition.Version);
        Assert.Equal(2, definition.Cases.Count);
        Assert.Equal(2, definition.Checks.Count);
    }

    // ── The refusals ──────────────────────────────────────────────────────────

    [Fact]
    public void EmptyCases_AreRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => Definition(cases: []));

        Assert.Equal("Cases", ex.ParamName);
        Assert.Contains("at least one case", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACaseWithNoId_IsRefused_BecauseTheCaseIsTheUnitOfAnalysis()
    {
        // TestCase.Id is optional on the type and required here. A definition whose cases have no ids
        // cannot be scored, and the failure would otherwise surface at the join, not at authoring.
        var ex = Assert.Throws<ArgumentException>(() => Definition(cases: [Case("c1"), Case(null, "nameless")]));

        Assert.Equal("Cases", ex.ParamName);
        Assert.Contains("index 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nameless", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ACaseWithABlankId_IsRefused_NotOnlyANullOne(string blank)
    {
        var ex = Assert.Throws<ArgumentException>(() => Definition(cases: [Case(blank)]));

        Assert.Equal("Cases", ex.ParamName);
    }

    [Fact]
    public void DuplicateCaseIds_AreRefused()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Definition(cases: [Case("c1"), Case("c2"), Case("c1")]));

        Assert.Equal("Cases", ex.ParamName);
        Assert.Contains("Duplicate case Id", ex.Message, StringComparison.Ordinal);
        Assert.Contains("index 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CaseIdsAreCaseSensitive_SoNearDuplicatesAreAccepted()
    {
        // The other direction on the same guard: ids are opaque keys, not display strings, and
        // folding 'c1' into 'C1' would silently merge two cases into one observation.
        var definition = Definition(cases: [Case("c1"), Case("C1")]);

        Assert.Equal(2, definition.Cases.Count);
    }

    [Fact]
    public void EmptyChecks_AreRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => Definition(checks: []));

        Assert.Equal("Checks", ex.ParamName);
        Assert.Contains("at least one check", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateCheckKeys_AreRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => Definition(checks: [Check("k1"), Check("k1")]));

        Assert.Equal("Checks", ex.ParamName);
        Assert.Contains("Duplicate check key", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "1.0.0")]
    [InlineData("  ", "1.0.0")]
    [InlineData("bench", null)]
    [InlineData("bench", "  ")]
    public void ABlankKeyOrVersion_IsRefused(string? key, string? version)
    {
        Assert.Throws<ArgumentException>(
            () => new BenchmarkDefinition(key!, version!, [Case("c1")], [Check("k1")]));
    }

    // ── The `with` path, which is where this guard is usually written wrongly ──

    [Fact]
    public void AWithCopy_IsGuardedToo_NotOnlyTheConstructor()
    {
        // Measured, not assumed: an auto-property with a validating INITIALIZER throws on `new(...)`
        // and does NOT throw on `x with { … }` — the copy runs the compiler-generated accessor, which
        // has no guard in it. That object is one the constructor would have refused.
        var definition = Definition();

        Assert.Throws<ArgumentException>(() => definition with { Cases = [] });
        Assert.Throws<ArgumentException>(() => definition with { Checks = [] });
        Assert.Throws<ArgumentException>(() => definition with { Key = "  " });
        Assert.Throws<ArgumentException>(() => definition with { Version = "  " });
        Assert.Throws<ArgumentException>(() => definition with { Cases = [Case("c1"), Case("c1")] });
    }

    [Fact]
    public void AWithCopy_ThatIsValid_StillWorks()
    {
        var definition = Definition() with { Version = "2.0.0" };

        Assert.Equal("2.0.0", definition.Version);
        Assert.Equal("bench", definition.Key);
    }

    // ── The door, not a second copy of it ─────────────────────────────────────

    [Fact]
    public void AdmittedCheck_WithNoFloor_ThrowsTheDoorsOwnRefusal()
    {
        // AdmittedCheck does not re-implement the floor rules. This asserts the message comes from
        // FloorAdmittedEval, so a future divergence between the two shows up here.
        var check = new AdmittedCheck(new StubEval("k1"), null!);

        var ex = Assert.Throws<ArgumentException>(() => check.Admit());

        Assert.Contains("NO chance floor", ex.Message, StringComparison.Ordinal);
        Assert.Contains("k1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdmittedCheck_WithAFloor_Admits()
    {
        var admitted = Check("k1").Admit();

        Assert.Equal("k1", admitted.Key);
        Assert.Same(Floor, admitted.Floor);
    }

    [Fact]
    public void AdmittedCheck_DoesNotValidateAtConstruction_TheDoorDoes()
    {
        // Recorded on purpose: constructing the pair is not admission. A definition can be written
        // down with a floor that will be refused, and the refusal happens at Admit() with the door's
        // own message rather than at authoring with a paraphrase of it.
        var check = new AdmittedCheck(new StubEval("k1"), null!);

        Assert.Null(check.Floor);
    }

    // ── Arms ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnArmWithNoAgentBehindIt_IsAnOrdinaryArm()
    {
        // A control, a replay, a fixture and an oracle are all arms. Defining an arm as "a way to
        // produce an EvalInput" is what lets one be written without inventing a fake agent.
        var arm = BenchmarkArm.From("control", (c, _) =>
            Task.FromResult(new EvalInput(Query: c.Input, Response: "fixed") { CaseId = c.Id }));

        var input = await arm.Observe(Case("c1"), CancellationToken.None);

        Assert.Equal("control", arm.ArmId);
        Assert.Equal("c1", input.CaseId);
        Assert.Equal("fixed", input.Response);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnArmWithNoId_IsRefused(string? armId) =>
        Assert.Throws<ArgumentException>(
            () => BenchmarkArm.From(armId!, (_, _) => Task.FromResult(new EvalInput("q"))));

    [Fact]
    public void AnArmWithNoObserver_IsRefused() =>
        Assert.Throws<ArgumentNullException>(() => BenchmarkArm.From("a", null!));

    // ── The lane rule: these are records, not evals ───────────────────────────

    [Theory]
    [InlineData(typeof(AdmittedCheck))]
    [InlineData(typeof(BenchmarkDefinition))]
    [InlineData(typeof(BenchmarkArm))]
    [InlineData(typeof(CheckObservation))]
    [InlineData(typeof(BenchmarkRun))]
    public void DefinitionRecords_AreNotEvals(Type type)
    {
        Assert.False(typeof(IEval).IsAssignableFrom(type),
            $"{type.Name} implements IEval. A definition that is also an eval is one more result model, "
            + "and it is the one holding pass/fail authority.");
    }

    [Theory]
    [InlineData(typeof(AdmittedCheck))]
    [InlineData(typeof(BenchmarkDefinition))]
    [InlineData(typeof(BenchmarkArm))]
    [InlineData(typeof(CheckObservation))]
    [InlineData(typeof(BenchmarkRun))]
    public void DefinitionRecords_LiveInTheBenchmarksNamespace(Type type) =>
        Assert.Equal("AgentEval.Benchmarks", type.Namespace);

    [Fact]
    public void TheDefinition_HasNoJudgeSlot_NoControlsSlot_AndNoContentHash()
    {
        // ADR-032's open questions are open. A property standing in for a decision nobody has made is
        // a decision made by whoever adds the property.
        var names = typeof(BenchmarkDefinition)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Cases", "Checks", "Key", "Version"], names);
    }
}
