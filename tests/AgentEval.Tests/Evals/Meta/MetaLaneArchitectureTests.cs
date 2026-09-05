// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Evals.Meta;

/// <summary>
/// ADR-030 §4.6 and §4.2, enforced. The load-bearing rule of the whole design is not expressible in
/// the type system, so it is a test:
/// <blockquote>
/// <b>Meta-evaluation never implements <c>IEval</c>.</b> A floor is a property OF a comparison. A
/// control is a RUN OF an eval. A comparison is a FUNCTION OF results.
/// </blockquote>
/// The moment one of them returns <c>EvalResult</c> as its OWN verdict, AgentEval has a seventh
/// result model and it is the one holding pass/fail authority — the worst possible place for a fork.
/// </summary>
public class MetaLaneArchitectureTests
{
    private const string MetaNamespace = "AgentEval.Evals.Meta";

    private static IReadOnlyList<Type> MetaTypes() =>
        typeof(MeasurementState).Assembly.GetTypes()
            .Concat(typeof(EvalResult).Assembly.GetTypes())
            .Where(t => t.Namespace?.StartsWith(MetaNamespace, StringComparison.Ordinal) == true)
            .Distinct()
            .ToList();

    [Fact]
    public void MetaTypes_NeverImplement_IEval()
    {
        var offenders = MetaTypes().Where(t => typeof(IEval).IsAssignableFrom(t)).ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void MetaNamespace_Exists_SoTheRuleIsActuallyBeingTested()
    {
        // An architecture test over an empty set passes for the wrong reason. Extreme values are
        // wiring faults until proven otherwise: pin that there is something here to enforce against.
        var types = MetaTypes();

        Assert.NotEmpty(types);
        Assert.Contains(types, t => t == typeof(MeasurementState));
        Assert.Contains(types, t => t == typeof(ObservationCensus));
    }

    [Fact]
    public void MetaNamespace_HasNoNonBclDependencies()
    {
        // §4.1: defined over BCL types only, the meta layer is a module that AgentEval,
        // Microsoft.Extensions.AI.Evaluation and a future Python port could all consume — and that
        // could be contributed upstream. Defined over EvalResult it is an AgentEval internal nobody
        // else can adopt. That portability is the design's best card and it is one careless `using`
        // from being spent.
        var offenders = new List<string>();

        foreach (var type in MetaTypes())
        {
            foreach (var referenced in ReferencedTypes(type))
            {
                if (IsAllowed(referenced)) continue;
                offenders.Add($"{type.FullName} -> {referenced.FullName}");
            }
        }

        Assert.Empty(offenders);
    }

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var f in type.GetFields(All)) yield return f.FieldType;
        foreach (var p in type.GetProperties(All)) yield return p.PropertyType;
        foreach (var m in type.GetMethods(All))
        {
            yield return m.ReturnType;
            foreach (var parameter in m.GetParameters()) yield return parameter.ParameterType;
        }
        foreach (var c in type.GetConstructors(All))
            foreach (var parameter in c.GetParameters()) yield return parameter.ParameterType;
        foreach (var i in type.GetInterfaces()) yield return i;
        if (type.BaseType is { } b) yield return b;
    }

    // "Allowed" = the BCL, or the meta namespace itself. Decomposed recursively so
    // IEquatable&lt;ObservationCensus&gt; (a compiler-generated record interface, System namespace,
    // meta type argument) is judged on both halves rather than on the outer one alone.
    private static bool IsAllowed(Type type)
    {
        var t = type.HasElementType ? type.GetElementType()! : type;
        if (t.IsGenericParameter) return true;
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
            return IsAllowed(t.GetGenericTypeDefinition()) && t.GetGenericArguments().All(IsAllowed);

        if (t.Namespace?.StartsWith(MetaNamespace, StringComparison.Ordinal) == true) return true;

        var asm = t.Assembly.GetName().Name ?? string.Empty;
        return asm is "System.Private.CoreLib" or "System.Runtime" or "netstandard" or "mscorlib"
            || asm.StartsWith("System.", StringComparison.Ordinal);
    }

    [Fact]
    public void MetaNamespace_BclCheck_ActuallyRejectsAnAgentEvalType()
    {
        // The gate must be shown able to fail, or an empty offender list proves nothing. EvalScore is
        // exactly the type §4.1 says the meta layer must never reach for.
        Assert.False(IsAllowed(typeof(EvalScore)));
        Assert.False(IsAllowed(typeof(IReadOnlyList<EvalResult>)));
        Assert.True(IsAllowed(typeof(ObservationCensus)));
        Assert.True(IsAllowed(typeof(IReadOnlyList<string>)));
    }

    // ── §4.2's style rule, and the escape hatch it exists to watch ─────────────────────────────

    [Fact]
    public void Measurement_IsOnlyAssigned_InsideEvalScoreItself()
    {
        // Two greps, one test:
        //   (a) `Measurement = MeasurementState.NotApplicable` outside EvalScore.NotApplicable() —
        //       a style rule now that the accessor is the real enforcement point, but it keeps the
        //       one sanctioned constructor from acquiring rivals;
        //   (b) `Measurement = MeasurementState.Measured` in ANY with-block — the DECLARED escape
        //       hatch from ADR-030 §4.2. `notApplicableScore with { Measurement = Measured, Passed
        //       = true }` succeeds by design, because forbidding it would require Measurement to be
        //       immutable after construction and that would break the legal NotMeasured-on-a-timeout
        //       path. It fails in the FLATTERING direction, so this grep is the only thing standing
        //       between the hatch and someone using it to clear an inconvenient n/a.
        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(repoRoot!, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var isEvalScore = Path.GetFileName(file) == "EvalScore.cs";
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (line.TrimStart().StartsWith("///", StringComparison.Ordinal)) continue;
                if (!line.Contains("Measurement = MeasurementState.", StringComparison.Ordinal)) continue;

                var assignsMeasured = line.Contains("Measurement = MeasurementState.Measured", StringComparison.Ordinal);
                if (assignsMeasured || !isEvalScore)
                    offenders.Add($"{Path.GetRelativePath(repoRoot!, file)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.Empty(offenders);
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AgentEval.sln"))) return dir;
            var parent = Directory.GetParent(dir);
            if (parent is null) return null;
            dir = parent.FullName;
        }
        return null;
    }
}
