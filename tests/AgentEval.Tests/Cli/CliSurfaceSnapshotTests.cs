// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Reflection;
using System.Text;
using AgentEval.Cli;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// CLI freeze: 1.0 promises the CLI's documented commands, options and exit codes, so every command, alias,
/// argument and option (its value type, arity, required, hidden, recursive, default value) and every exit code is
/// snapshotted. Any change fails these tests until the new snapshot is reviewed and accepted, so the surface cannot
/// move silently. Descriptions are left out: they are documentation, not contract.
/// </summary>
public class CliSurfaceSnapshotTests
{
    [Fact]
    public Task TheCommandTree_MatchesTheApprovedSnapshot()
    {
        var sb = new StringBuilder();
        Write(sb, CliCommandTree.Build().Root, "agenteval", 0);
        return Verify(sb.ToString());
    }

    [Fact]
    public Task TheExitCodes_MatchTheApprovedSnapshot()
    {
        var codes = typeof(ExitCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .OrderBy(f => (int)f.GetRawConstantValue()!)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => $"{(int)f.GetRawConstantValue()!,3} {f.Name}");
        return Verify(string.Join(Environment.NewLine, codes));
    }

    private static void Write(StringBuilder sb, Command command, string name, int depth)
    {
        var pad = new string(' ', depth * 4);
        sb.Append(pad).Append("command ").Append(name).Append(Aliases(command.Aliases)).AppendLine(command.Hidden ? " hidden" : "");
        foreach (var a in command.Arguments)
            sb.Append(pad).Append("    argument <").Append(a.Name).Append("> ").Append(TypeName(a.ValueType)).Append(' ').Append(Arity(a.Arity))
              .Append(a.Hidden ? " hidden" : "").AppendLine(a.HasDefaultValue ? " default=" + Value(a.GetDefaultValue()) : "");
        foreach (var o in command.Options.OrderBy(o => o.Name, StringComparer.Ordinal))
            sb.Append(pad).Append("    option ").Append(o.Name).Append(Aliases(o.Aliases)).Append(' ').Append(TypeName(o.ValueType)).Append(' ').Append(Arity(o.Arity))
              .Append(o.Required ? " required" : "").Append(o.Hidden ? " hidden" : "").Append(o.Recursive ? " recursive" : "")
              .AppendLine(o.HasDefaultValue ? " default=" + Value(o.GetDefaultValue()) : "");
        foreach (var sub in command.Subcommands.OrderBy(c => c.Name, StringComparer.Ordinal))
            Write(sb, sub, sub.Name, depth + 1);
    }

    private static string Aliases(ICollection<string> aliases) =>
        aliases.Count == 0 ? "" : " (" + string.Join(", ", aliases.OrderBy(a => a, StringComparer.Ordinal)) + ")";

    private static string Arity(ArgumentArity arity) =>
        $"[{arity.MinimumNumberOfValues}..{(arity.MaximumNumberOfValues == int.MaxValue ? "*" : arity.MaximumNumberOfValues.ToString(CultureInfo.InvariantCulture))}]";

    private static string TypeName(Type t)
    {
        var nullable = Nullable.GetUnderlyingType(t);
        if (nullable is not null) return TypeName(nullable) + "?";
        if (t.IsArray) return TypeName(t.GetElementType()!) + "[]";
        if (!t.IsGenericType) return t.Name;
        var bare = t.Name[..t.Name.IndexOf('`')];
        return bare + "<" + string.Join(", ", t.GetGenericArguments().Select(TypeName)) + ">";
    }

    /// <summary>A stable, culture-invariant rendering of a default value.</summary>
    private static string Value(object? v) => v switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        System.Collections.IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Value)) + "]",
        _ => v.ToString() ?? "?",
    };
}
