// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace AgentEval.Tests.Core;

/// <summary>
/// Core assembly v1 API freeze: the public surface of <c>AgentEval.Core</c> is snapshotted.
/// Any change to a public type or member fails this test until the new snapshot is reviewed and
/// accepted, so the surface cannot move silently. Each type is listed with abstract/sealed, its base class and its
/// public interfaces, inherited ones included; members include the protected ones a subclass can reach.
/// <para>
/// Note: <c>AgentEval.Guardrails*</c> namespaces are excluded here because they are already
/// frozen by <see cref="AgentEval.Tests.MAF.Gatekeeper.GatekeeperPublicApiSnapshotTests"/>.
/// </para>
/// Types marked <c>[Experimental]</c> are listed with that marker and are preview, outside the v1 promise.
/// </summary>
public class CorePublicApiSnapshotTests
{
    // Exclude Guardrails (covered by GatekeeperPublicApiSnapshotTests)
    private static bool InScope(Type t) =>
        t.Namespace is { } ns
        && !ns.Equals("AgentEval.Guardrails", StringComparison.Ordinal)
        && !ns.StartsWith("AgentEval.Guardrails.", StringComparison.Ordinal);

    [Fact]
    public Task ThePublicSurface_MatchesTheApprovedSnapshot()
    {
        var asm = typeof(AgentEval.Assertions.ResponseAssertions).Assembly;

        var types = asm.GetExportedTypes()
            .Where(InScope)
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        var sb = new StringBuilder();
        foreach (var t in types)
        {
            var experimental = (t.GetCustomAttribute<ExperimentalAttribute>() ?? t.Assembly.GetCustomAttribute<ExperimentalAttribute>()) is { } x
                ? $" [Experimental({x.DiagnosticId})]"
                : "";
            sb.Append(Kind(t)).Append(' ').Append(Name(t)).Append(Bases(t)).AppendLine(experimental);
            foreach (var m in Members(t))
                sb.Append("    ").AppendLine(m);
        }
        return Verify(sb.ToString());
    }

    // abstract and sealed are part of the contract: sealing a class breaks subclasses, making one abstract breaks `new`.
    private static string Kind(Type t) =>
        t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct"
        : typeof(Delegate).IsAssignableFrom(t) ? "delegate" : t.IsAbstract && t.IsSealed ? "static class"
        : t.IsAbstract ? "abstract class" : t.IsSealed ? "sealed class" : "class";

    // The base class and every public interface, inherited ones included: dropping either breaks callers that convert.
    private static string Bases(Type t)
    {
        if (t.IsEnum || typeof(Delegate).IsAssignableFrom(t))
            return "";
        var bases = new List<string>();
        if (t.BaseType is { } b && b != typeof(object) && b != typeof(ValueType))
            bases.Add(Name(b));
        bases.AddRange(t.GetInterfaces().Where(i => i.IsVisible).Select(Name).OrderBy(n => n, StringComparer.Ordinal));
        return bases.Count == 0 ? "" : " : " + string.Join(", ", bases);
    }

    // Public members, and protected ones where a subclass can reach them (the type is not sealed). A record's
    // compiler-generated protected members (EqualityContract, PrintMembers, the copy constructor) are left out:
    // they follow from its being a record, which the IEquatable<T> in its header already shows.
    private static bool Visible(MethodBase? m, Type t) =>
        m is not null && (m.IsPublic || (!t.IsSealed && (m.IsFamily || m.IsFamilyOrAssembly)
            && !m.IsDefined(typeof(CompilerGeneratedAttribute), false)
            && !(m.IsSpecialName && m.Name == "get_EqualityContract")));

    private static bool Visible(FieldInfo f, Type t) =>
        f.IsPublic || (!t.IsSealed && (f.IsFamily || f.IsFamilyOrAssembly));

    private static string Protected(MethodBase m) => m.IsPublic ? "" : "protected ";

    private static IEnumerable<string> Members(Type t)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        if (t.IsEnum)
            return Enum.GetNames(t).Select(n => $"{n}");

        var list = new List<string>();
        foreach (var c in t.GetConstructors(flags).Where(c => Visible(c, t)))
            list.Add($"{Protected(c)}ctor({Params(c.GetParameters())})");
        foreach (var p in t.GetProperties(flags).Where(p => Visible(p.GetMethod, t) || Visible(p.SetMethod, t)))
        {
            var acc = string.Join(" ", new[] { Visible(p.GetMethod, t) ? Protected(p.GetMethod!) + "get;" : null, Visible(p.SetMethod, t) ? Protected(p.SetMethod!) + (p.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit") ? "init;" : "set;") : null }.Where(s => s is not null));
            var isStatic = (p.GetMethod ?? p.SetMethod)?.IsStatic == true ? "static " : "";
            list.Add($"{isStatic}{Name(p.PropertyType)} {p.Name} {{ {acc} }}");
        }
        foreach (var f in t.GetFields(flags).Where(f => Visible(f, t)))
            list.Add($"{(f.IsPublic ? "" : "protected ")}{(f.IsLiteral ? "const " : f.IsStatic ? "static " : "")}{Name(f.FieldType)} {f.Name}");
        foreach (var e in t.GetEvents(flags).Where(e => Visible(e.AddMethod, t)))
            list.Add($"{Protected(e.AddMethod!)}event {Name(e.EventHandlerType!)} {e.Name}");
        foreach (var m in t.GetMethods(flags).Where(m => !m.IsSpecialName && Visible(m, t)))
            list.Add($"{Protected(m)}{(m.IsStatic ? "static " : "")}{Name(m.ReturnType)} {m.Name}{GenericArgs(m)}({Params(m.GetParameters())})");
        return list.OrderBy(s => s, StringComparer.Ordinal);
    }

    private static string GenericArgs(MethodInfo m) =>
        m.IsGenericMethodDefinition ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : "";

    private static string Params(ParameterInfo[] ps) =>
        string.Join(", ", ps.Select(p => $"{(p.ParameterType.IsByRef ? (p.IsOut ? "out " : "ref ") : "")}{Name(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType)} {p.Name}{(p.HasDefaultValue ? " = " + Default(p) : "")}"));

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
