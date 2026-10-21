// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.RegularExpressions;

namespace AgentEval.Tests.Census;

/// <summary>
/// Finds every product type that could fold many results into one number or verdict, so a new aggregation cannot ship
/// without a census row (or a stated reason it needs none).
/// </summary>
/// <remarks>
/// A type is discovered when any of these holds (exported types only; not interfaces, enums or delegates):
/// <list type="bullet">
///   <item><b>R1</b> — it declares a public method or constructor with a parameter that is a collection of a result
///   type. A result type is an AgentEval type whose name ends in Result, Score or Summary, or is TrustSignal,
///   MemoryBaseline or CalibrationReport; a dictionary's values count as its elements.</item>
///   <item><b>R2</b> — it declares a public property that is such a collection, and a public double, float or decimal
///   property (nullable or not).</item>
///   <item><b>R3</b> — its name says it aggregates (see <see cref="NamePattern"/>).</item>
///   <item><b>R4</b> — it implements one of the aggregation interfaces in <see cref="AggregationInterfaces"/>.</item>
/// </list>
/// The predicate over-reports on purpose: a data type that only carries an aggregate is cheap to list with a reason, and
/// an aggregation the predicate missed is invisible.
/// </remarks>
internal static class AggregationDiscovery
{
    private static readonly string[] s_resultSuffixes = ["Result", "Score", "Summary"];

    private static readonly HashSet<string> s_extraResultTypes = new(StringComparer.Ordinal)
    {
        "TrustSignal", "MemoryBaseline", "CalibrationReport",
    };

    /// <summary>R3: names that say "this aggregates".</summary>
    internal static readonly Regex NamePattern = new(
        "(Aggregat|Rollup|Comparer|Comparison|Summary|Statistics|Ranking|Index|Census|Consolidator|Calculator)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>R4: the interfaces an aggregation implements, by simple name.</summary>
    internal static readonly HashSet<string> AggregationInterfaces = new(StringComparer.Ordinal)
    {
        "IAggregationStrategy", "IModelComparer", "IBaselineComparer", "IStatisticsCalculator", "IComplianceReport",
        "ISnapshotComparer",
    };

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Every discovered type, by full name (a nested type as <c>Outer+Inner</c>), with the rules it matched.</summary>
    internal static IReadOnlyDictionary<string, string> Discover() =>
        ProductAssemblies.ExportedTypes()
            .Where(t => !t.IsInterface && !t.IsEnum && !typeof(Delegate).IsAssignableFrom(t))
            .Select(t => (Type: t, Rules: Rules(t)))
            .Where(x => x.Rules.Length > 0)
            .ToDictionary(x => x.Type.FullName!, x => x.Rules, StringComparer.Ordinal);

    private static string Rules(Type type)
    {
        var rules = new List<string>(4);

        var members = type.GetMethods(Declared).Cast<MethodBase>()
            .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        if (members.Any(m => m.GetParameters().Any(p => IsResultCollection(p.ParameterType))))
            rules.Add("R1");

        var properties = type.GetProperties(Declared);
        if (properties.Any(p => IsResultCollection(p.PropertyType)) && properties.Any(p => IsNumber(p.PropertyType)))
            rules.Add("R2");

        if (NamePattern.IsMatch(SimpleName(type)))
            rules.Add("R3");

        if (type.GetInterfaces().Any(i => AggregationInterfaces.Contains(SimpleName(i))))
            rules.Add("R4");

        return string.Join(",", rules);
    }

    private static bool IsNumber(Type type) =>
        (Nullable.GetUnderlyingType(type) ?? type) is var t && (t == typeof(double) || t == typeof(float) || t == typeof(decimal));

    private static bool IsResultCollection(Type type) => ElementOf(type) is { } element && IsResultType(element);

    private static bool IsResultType(Type type)
    {
        if (type.Namespace is not { } ns || !ns.StartsWith("AgentEval", StringComparison.Ordinal))
            return false;
        var name = SimpleName(type);
        return s_resultSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal)) || s_extraResultTypes.Contains(name);
    }

    // The element type of a collection (a dictionary's value type), or null for a type that is not one. A string is not.
    private static Type? ElementOf(Type type)
    {
        if (type == typeof(string))
            return null;
        if (type.IsArray)
            return type.GetElementType();

        var enumerable = new[] { type }.Concat(type.GetInterfaces())
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable is null)
            return null;

        var element = enumerable.GetGenericArguments()[0];
        return element.IsGenericType && element.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)
            ? element.GetGenericArguments()[1]
            : element;
    }

    // A type's name without its generic arity ("Foo`1" → "Foo").
    private static string SimpleName(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick >= 0 ? name[..tick] : name;
    }
}
