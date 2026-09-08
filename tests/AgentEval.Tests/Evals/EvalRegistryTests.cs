// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// ADR-031 C1 — IEvalRegistry, built to the CORRECTED signature
// (Key → Func<IEvaluator?, string?, IEval>), not to C1's filed Key → IEval.
// MEASUREMENT_STATUS §67.6 records why the filed signature is refuted: the 40
// entries it was meant to hold are all `new XEval(judge, judgeModel: …)`, i.e.
// factories over a judge that does not exist at module-initialisation time.
//
// Everything below runs against a FRESH EvalRegistry instance, never
// EvalRegistry.Shared, so these tests are order-independent and cannot be made
// to pass or fail by whatever else the test host has loaded.

using AgentEval.Core;
using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

public class EvalRegistryTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────

    private sealed class StubEval : IEval
    {
        public string Key => "stub";
        public string Name => "stub";
        public string Category => "test";
        public string Version => "1.0.0";
        public IEvaluator? SeenJudge { get; init; }
        public string? SeenJudgeModel { get; init; }

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
            => throw new NotSupportedException("Registry tests never evaluate.");
    }

    private sealed class OtherStubEval : IEval
    {
        public string Key => "other";
        public string Name => "other";
        public string Category => "test";
        public string Version => "1.0.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
            => throw new NotSupportedException("Registry tests never evaluate.");
    }

    private sealed class StubEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
            => Task.FromResult(new EvaluationResult { OverallScore = 100, Summary = "stub" });
    }

    private sealed class NotAnEval { }

    private static EvalRegistration StubEntry(string key = "stub_key")
        => new(key, typeof(StubEval), (j, m) => new StubEval { SeenJudge = j, SeenJudgeModel = m });

    // ── Register / TryGet / All ──────────────────────────────────────────────

    [Fact]
    public void Register_ThenTryGet_ReturnsTheRegistration()
    {
        var registry = new EvalRegistry();
        registry.Register(StubEntry());

        var found = registry.TryGet("stub_key");

        Assert.NotNull(found);
        Assert.Equal("stub_key", found!.Key);
        Assert.Equal(typeof(StubEval), found.EvalType);
    }

    [Fact]
    public void TryGet_UnknownKey_ReturnsNull()
    {
        var registry = new EvalRegistry();
        registry.Register(StubEntry());

        Assert.Null(registry.TryGet("no_such_key"));
    }

    [Fact] // The dictionary this registry replaced used StringComparer.OrdinalIgnoreCase.
    public void TryGet_IsCaseInsensitive()
    {
        var registry = new EvalRegistry();
        registry.Register(StubEntry("Task_Completion"));

        Assert.NotNull(registry.TryGet("TASK_COMPLETION"));
        Assert.NotNull(registry.TryGet("task_completion"));
    }

    [Fact]
    public void All_IsOrderedByKey_AndReflectsEveryRegistration()
    {
        var registry = new EvalRegistry();
        registry.Register(StubEntry("zulu"));
        registry.Register(StubEntry("alpha"));
        registry.Register(StubEntry("mike"));

        Assert.Equal(new[] { "alpha", "mike", "zulu" }, registry.All.Select(e => e.Key));
    }

    [Fact]
    public void All_OnAnEmptyRegistry_IsEmpty()
    {
        Assert.Empty(new EvalRegistry().All);
    }

    // ── Resolve — the thing the corrected signature exists for ───────────────

    [Fact] // The load-bearing one: the judge is supplied AT RESOLUTION, not at registration.
    public void Resolve_PassesTheJudgeAndJudgeModelToTheFactory()
    {
        var registry = new EvalRegistry();
        registry.Register(StubEntry());
        var judge = new StubEvaluator();

        var eval = registry.Resolve("stub_key", judge, "test-deployment-x");

        var stub = Assert.IsType<StubEval>(eval);
        Assert.Same(judge, stub.SeenJudge);
        Assert.Equal("test-deployment-x", stub.SeenJudgeModel);
    }

    [Fact] // A registration is a factory: two resolutions are two objects, and the judge can differ.
    public void Resolve_BuildsAFreshInstanceEachCall()
    {
        var registry = new EvalRegistry();
        registry.Register(StubEntry());

        var first = registry.Resolve("stub_key", new StubEvaluator(), "a");
        var second = registry.Resolve("stub_key", new StubEvaluator(), "b");

        Assert.NotSame(first, second);
        Assert.Equal("a", ((StubEval)first!).SeenJudgeModel);
        Assert.Equal("b", ((StubEval)second!).SeenJudgeModel);
    }

    [Fact]
    public void Resolve_UnknownKey_ReturnsNull()
    {
        var registry = new EvalRegistry();
        registry.Register(StubEntry());

        Assert.Null(registry.Resolve("no_such_key", new StubEvaluator(), "m"));
    }

    [Fact] // Nothing is constructed at registration time — the factory must not have run yet.
    public void Register_DoesNotInvokeTheFactory()
    {
        var registry = new EvalRegistry();
        var calls = 0;

        registry.Register(new EvalRegistration("counted", typeof(StubEval), (_, _) => { calls++; return new StubEval(); }));

        Assert.Equal(0, calls);
        registry.Resolve("counted", new StubEvaluator(), null);
        Assert.Equal(1, calls);
    }

    // ── Idempotence and conflict ─────────────────────────────────────────────

    [Fact] // Register() is called by a [ModuleInitializer] AND explicitly by the CLI. Both must be safe.
    public void Register_SameKeySameEvalType_IsIdempotent()
    {
        var registry = new EvalRegistry();
        registry.Register(StubEntry());
        registry.Register(StubEntry());

        Assert.Single(registry.All);
    }

    [Fact]
    public void Register_SameKeyDifferentEvalType_Throws()
    {
        var registry = new EvalRegistry();
        registry.Register(StubEntry("clash"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new EvalRegistration("clash", typeof(OtherStubEval), (_, _) => new OtherStubEval())));

        Assert.Contains("clash", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(OtherStubEval), ex.Message, StringComparison.Ordinal);
    }

    [Fact] // Case-insensitive keys mean a differing-case re-register is the SAME key, not a second one.
    public void Register_SameKeyDifferentCase_DifferentEvalType_Throws()
    {
        var registry = new EvalRegistry();
        registry.Register(StubEntry("Clash"));

        Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new EvalRegistration("CLASH", typeof(OtherStubEval), (_, _) => new OtherStubEval())));
    }

    [Fact] // Two keys naming one type is legal: prompt_leak and system_prompt_leakage both build SystemPromptLeakageEval.
    public void Register_TwoKeysForTheSameEvalType_IsNotAConflict()
    {
        var registry = new EvalRegistry();
        registry.Register(StubEntry("alias_a"));
        registry.Register(StubEntry("alias_b"));

        Assert.Equal(2, registry.All.Count);
    }

    // ── Argument validation ──────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EvalRegistration_BlankKey_Throws(string key)
        => Assert.Throws<ArgumentException>(() => new EvalRegistration(key, typeof(StubEval), (_, _) => new StubEval()));

    [Fact]
    public void EvalRegistration_NullKey_Throws()
        => Assert.Throws<ArgumentNullException>(() => new EvalRegistration(null!, typeof(StubEval), (_, _) => new StubEval()));

    [Fact]
    public void EvalRegistration_NullEvalType_Throws()
        => Assert.Throws<ArgumentNullException>(() => new EvalRegistration("k", null!, (_, _) => new StubEval()));

    [Fact]
    public void EvalRegistration_NullFactory_Throws()
        => Assert.Throws<ArgumentNullException>(() => new EvalRegistration("k", typeof(StubEval), null!));

    [Fact] // EvalType is load-bearing for content equality; a non-IEval type is a programmer error.
    public void EvalRegistration_EvalTypeThatIsNotAnIEval_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new EvalRegistration("k", typeof(NotAnEval), (_, _) => new StubEval()));
        Assert.Contains(nameof(NotAnEval), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => new EvalRegistry().Register(null!));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void TryGet_BlankKey_Throws(string key)
        => Assert.Throws<ArgumentException>(() => new EvalRegistry().TryGet(key));

    [Fact]
    public void TryGet_NullKey_Throws()
        => Assert.Throws<ArgumentNullException>(() => new EvalRegistry().TryGet(null!));

    [Fact]
    public void Resolve_BlankKey_Throws()
        => Assert.Throws<ArgumentException>(() => new EvalRegistry().Resolve(" ", new StubEvaluator(), null));

    // ── Reset ────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_EmptiesTheRegistry()
    {
        var registry = new EvalRegistry();
        registry.Register(StubEntry());
        Assert.Single(registry.All);

        registry.Reset();

        Assert.Empty(registry.All);
        Assert.Null(registry.TryGet("stub_key"));
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    [Fact]
    public void Shared_IsASingleInstance()
        => Assert.Same(EvalRegistry.Shared, EvalRegistry.Shared);

    [Fact] // The interface is what consumers hold; the shared instance must satisfy it.
    public void Shared_IsAnIEvalRegistry()
        => Assert.IsAssignableFrom<IEvalRegistry>(EvalRegistry.Shared);
}
