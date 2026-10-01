// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper v1 API freeze: the public surface of Gatekeeper (<c>AgentEval.MAF.Gatekeeper*</c>,
/// <c>AgentEval.Guardrails*</c>, and the AgentHooks adapter) is snapshotted. Any change to a public type or member
/// fails this test until the new snapshot is reviewed and accepted, so the surface cannot move silently.
/// Types marked <c>[Experimental]</c>, directly or by their assembly, are listed with that marker: they are preview,
/// outside the v1 promise. Parameter names and default values are part of the surface (renaming a parameter breaks
/// named-argument callers; changing a default changes compiled callers), so both are in the snapshot.
/// </summary>
public class GatekeeperPublicApiSnapshotTests
{
    private static readonly string[] Namespaces = ["AgentEval.MAF.Gatekeeper", "AgentEval.Guardrails", "AgentEval.MAF.AgentHooks"];

    private static IEnumerable<Assembly> Assemblies() =>
    [
        typeof(AgentEval.MAF.Gatekeeper.IToolGate).Assembly,
        typeof(AgentEval.Guardrails.GateRegexTimeouts).Assembly,
        typeof(AgentEval.MAF.AgentHooks.GatekeeperInterceptor).Assembly,
    ];

    private static bool InScope(Type t) =>
        t.Namespace is { } ns && Namespaces.Any(n => ns == n || ns.StartsWith(n + ".", StringComparison.Ordinal));

    [Fact]
    public Task ThePublicSurface_MatchesTheApprovedSnapshot()
    {
        var types = Assemblies().Distinct()
            .SelectMany(a => a.GetExportedTypes())
            .Where(InScope)
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        var sb = new StringBuilder();
        foreach (var t in types)
        {
            var experimental = (t.GetCustomAttribute<ExperimentalAttribute>() ?? t.Assembly.GetCustomAttribute<ExperimentalAttribute>()) is { } x
                ? $" [Experimental({x.DiagnosticId})]"
                : "";
            sb.Append(Kind(t)).Append(' ').Append(Name(t)).AppendLine(experimental);
            foreach (var m in Members(t))
                sb.Append("    ").AppendLine(m);
        }
        return Verify(sb.ToString());
    }

    private static string Kind(Type t) =>
        t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct"
        : typeof(Delegate).IsAssignableFrom(t) ? "delegate" : t.IsAbstract && t.IsSealed ? "static class" : "class";

    private static IEnumerable<string> Members(Type t)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        if (t.IsEnum)
            return Enum.GetNames(t).Select(n => $"{n}");

        var list = new List<string>();
        foreach (var c in t.GetConstructors(flags))
            list.Add($"ctor({Params(c.GetParameters())})");
        foreach (var p in t.GetProperties(flags))
        {
            var acc = string.Join(" ", new[] { p.GetMethod is { IsPublic: true } ? "get;" : null, p.SetMethod is { IsPublic: true } ? (p.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit") ? "init;" : "set;") : null }.Where(s => s is not null));
            var isStatic = (p.GetMethod ?? p.SetMethod)?.IsStatic == true ? "static " : "";
            list.Add($"{isStatic}{Name(p.PropertyType)} {p.Name} {{ {acc} }}");
        }
        foreach (var f in t.GetFields(flags))
            list.Add($"{(f.IsLiteral ? "const " : f.IsStatic ? "static " : "")}{Name(f.FieldType)} {f.Name}");
        foreach (var e in t.GetEvents(flags))
            list.Add($"event {Name(e.EventHandlerType!)} {e.Name}");
        foreach (var m in t.GetMethods(flags).Where(m => !m.IsSpecialName))
            list.Add($"{(m.IsStatic ? "static " : "")}{Name(m.ReturnType)} {m.Name}{GenericArgs(m)}({Params(m.GetParameters())})");
        return list.OrderBy(s => s, StringComparer.Ordinal);
    }

    private static string GenericArgs(MethodInfo m) =>
        m.IsGenericMethodDefinition ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : "";

    private static string Params(ParameterInfo[] ps) =>
        string.Join(", ", ps.Select(p => $"{(p.ParameterType.IsByRef ? (p.IsOut ? "out " : "ref ") : "")}{Name(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType)} {p.Name}{(p.HasDefaultValue ? " = " + Default(p) : "")}"));

    /// <summary>A stable, culture-invariant rendering of a parameter's default value.</summary>
    private static string Default(ParameterInfo p) => p.DefaultValue switch
    {
        null when p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) is null => "default",
        null => "null",
        DBNull or Missing => "default",
        string s => "\"" + s + "\"",
        bool b => b ? "true" : "false",
        Enum e => e.GetType().Name + "." + e,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        var v => v.ToString() ?? "?",
    };

    private static string Name(Type t)
    {
        if (t.IsGenericParameter) return t.Name;
        if (t.IsArray) return Name(t.GetElementType()!) + "[]";
        var nullable = Nullable.GetUnderlyingType(t);
        if (nullable is not null) return Name(nullable) + "?";
        var name = t.IsNested ? Name(t.DeclaringType!) + "." + t.Name : (t.Namespace is { } ns && InScope(t) ? ns + "." : "") + t.Name;
        if (!t.IsGenericType) return name;
        var tick = name.IndexOf('`');
        var bare = tick >= 0 ? name[..tick] : name;
        return bare + "<" + string.Join(", ", t.GetGenericArguments().Select(Name)) + ">";
    }
}
