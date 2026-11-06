// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Adversarial;
using AgentEval.Evals.Agentic.Safety;

namespace AgentEval.Tests.TestHelpers;

/// <summary>
/// Runs a production pattern with a long, finite match budget in place of its own short one, for tests that assert what
/// a deterministic pattern path decides.
/// <para>
/// The production patterns carry ReDoS budgets of 200 to 300 ms. The budget is wall-clock time: a thread that is
/// descheduled in the middle of a match uses it up while doing nothing, so on a loaded test host (a dozen test processes
/// on a two-core CI runner) it runs out on ordinary input. Production then reports "could not check", which is right
/// there; in a test that asserts the decision it turns the verdict into a result of machine load. Those tests pass these
/// matchers through the components' internal seams (<c>RegexMatch</c>, <c>RegexReplace</c>). The timeout path has its own
/// tests, which pass a matcher that throws <see cref="RegexMatchTimeoutException"/>.
/// </para>
/// <para>
/// <see cref="Budget"/> is finite on purpose. Load cannot use up 30 s, but a pattern that backtracks catastrophically
/// still fails the one test that ran it, by name, instead of hanging the CI job until its time limit.
/// </para>
/// </summary>
internal static class UntimedRegex
{
    /// <summary>The match budget of every pattern run through this helper.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    // One copy per production pattern instance (Regex compares by reference). Compiled is dropped: interpreting is
    // cheaper than compiling a pattern that a test runs a few times, and it matches the same strings.
    private static readonly ConcurrentDictionary<Regex, Regex> s_copies = new();

    /// <summary>A copy of <paramref name="regex"/> with the same pattern and options, and <see cref="Budget"/>.</summary>
    public static Regex Copy(Regex regex) =>
        s_copies.GetOrAdd(regex, static r => new Regex(r.ToString(), r.Options & ~RegexOptions.Compiled, Budget));

    /// <summary>A matcher for a <c>RegexMatch</c> seam.</summary>
    public static readonly Func<Regex, string, Match> Match = static (regex, input) => Copy(regex).Match(input);

    /// <summary>A replacer for a <c>RegexReplace</c> seam.</summary>
    public static readonly Func<Regex, string, string, string> Replace =
        static (regex, input, replacement) => Copy(regex).Replace(input, replacement);

    /// <summary>
    /// Resolves <paramref name="key"/> from <paramref name="registry"/> and passes the result through
    /// <see cref="WithUntimedPatterns(IEval?, IEvaluator, string?)"/>.
    /// </summary>
    public static IEval? Resolve(IEvalRegistry registry, string key, IEvaluator judge, string? judgeModel = null) =>
        WithUntimedPatterns(registry.Resolve(key, judge, judgeModel), judge, judgeModel);

    /// <summary>
    /// For an evaluator whose deterministic path scans with patterns, an equivalent instance that scans through
    /// <see cref="Match"/>; a composite is rebuilt with each component mapped the same way; any other eval is returned as
    /// it is.
    /// </summary>
    /// <remarks>
    /// The seam is init-only, so the instance is rebuilt from <paramref name="judge"/> and <paramref name="judgeModel"/>
    /// with default options, as the registry and the presets build it. An instance built with other options (a threshold,
    /// custom patterns, a pattern cap, another judge model) would silently change what the test runs, so the two
    /// instances' configuration and judge-leaf definitions are compared and a mismatch throws: set
    /// <c>RegexMatch = UntimedRegex.Match</c> where such an instance is constructed instead.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The instance was built with options this helper does not copy.</exception>
    public static IEval? WithUntimedPatterns(IEval? eval, IEvaluator judge, string? judgeModel = null) => eval switch
    {
        JailbreakResistanceEval original  => Same(original, new JailbreakResistanceEval(judge, judgeModel)  { RegexMatch = Match }),
        DirectInjectionEval original      => Same(original, new DirectInjectionEval(judge, judgeModel)      { RegexMatch = Match }),
        PersonaAttackEval original        => Same(original, new PersonaAttackEval(judge, judgeModel)        { RegexMatch = Match }),
        SensitiveDataLeakageEval original => Same(original, new SensitiveDataLeakageEval(judge, judgeModel) { RegexMatch = Match }),
        CompositeEval composite           => WithUntimedPatterns(composite, judge, judgeModel),
        _                                 => eval,
    };

    /// <summary>
    /// <paramref name="composite"/> with every component passed through
    /// <see cref="WithUntimedPatterns(IEval?, IEvaluator, string?)"/>, nested composites included, and every other setting kept.
    /// </summary>
    public static CompositeEval WithUntimedPatterns(CompositeEval composite, IEvaluator judge, string? judgeModel = null) =>
        composite.WithComponents(composite.Components
            .Select(c => c with { Eval = WithUntimedPatterns(c.Eval, judge, judgeModel)! })
            .ToList());

    private static T Same<T>(T original, T rebuilt) where T : IEvalInstrumentSource
    {
        var same = original.CaptureConfiguration().ToJsonString() == rebuilt.CaptureConfiguration().ToJsonString()
            && original.DefinitionInstruments.Count == rebuilt.DefinitionInstruments.Count
            && original.DefinitionInstruments.Zip(rebuilt.DefinitionInstruments).All(pair => pair switch
            {
                // Threshold, judge model, criteria and prompt of each judge leaf.
                (AtomicLlmEval a, AtomicLlmEval b) => a.CaptureDefinition() == b.CaptureDefinition(),
                var (a, b) => a.GetType() == b.GetType(),
            });
        return same
            ? rebuilt
            : throw new InvalidOperationException(
                $"{original.Key} was built with options UntimedRegex does not copy; " +
                "set RegexMatch = UntimedRegex.Match where it is constructed.");
    }
}
