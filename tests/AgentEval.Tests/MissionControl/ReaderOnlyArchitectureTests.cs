// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using System.Reflection;
using AgentEval.MissionControl.GraphQL;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// Locks down the MC1.11.2 architectural invariant: <see cref="AgentEval.MissionControl"/>
/// must consume <see cref="IOutputStoreReader"/> and never <see cref="IOutputStore"/>
/// (the write surface). Plan-07 §4 introduced the reader/writer split specifically so
/// Mode A's local viewer cannot accidentally mutate the user's <c>.agenteval/</c> folder.
/// </summary>
/// <remarks>
/// Approach: walk every method on every type in the MissionControl assembly via
/// reflection; for each method, check parameter types and return types and assert
/// that <see cref="IOutputStore"/> never appears. Compile-time would be ideal but
/// .NET doesn't have a good cross-assembly "no-reference-to-this-type" check —
/// reflection at test time is the cleanest enforcement available.
/// </remarks>
public class ReaderOnlyArchitectureTests
{
    [Fact]
    public void NoMissionControlMember_ReferencesIOutputStore()
    {
        var assembly = typeof(Query).Assembly;
        var failures = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            // Compiler-generated types (closures, async state machines) are noisy and
            // their parameter lists are internal; skip them.
            if (type.IsCompilerGenerated()) continue;

            // Constructors
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
            {
                CheckMethodSignature(ctor, type, failures);
            }

            // Methods (public + private + static + instance, but only declared on this type)
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                CheckMethodSignature(method, type, failures);
            }

            // Fields and properties typed as IOutputStore
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (typeof(IOutputStore).IsAssignableFrom(field.FieldType) && field.FieldType != typeof(IOutputStoreReader))
                {
                    failures.Add($"Field {type.FullName}.{field.Name} is typed as {field.FieldType} (write-side); use IOutputStoreReader instead.");
                }
            }
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (typeof(IOutputStore).IsAssignableFrom(prop.PropertyType) && prop.PropertyType != typeof(IOutputStoreReader))
                {
                    failures.Add($"Property {type.FullName}.{prop.Name} is typed as {prop.PropertyType} (write-side); use IOutputStoreReader instead.");
                }
            }
        }

        Assert.True(failures.Count == 0,
            "Mission Control must use IOutputStoreReader only — never IOutputStore (the write surface). Violations:\n" +
            string.Join("\n", failures));
    }

    private static void CheckMethodSignature(MethodBase method, Type declaringType, List<string> failures)
    {
        foreach (var param in method.GetParameters())
        {
            if (typeof(IOutputStore) == param.ParameterType)
            {
                failures.Add($"{declaringType.FullName}.{method.Name} parameter '{param.Name}' is IOutputStore; use IOutputStoreReader.");
            }
        }
        if (method is MethodInfo mi && mi.ReturnType == typeof(IOutputStore))
        {
            failures.Add($"{declaringType.FullName}.{method.Name} returns IOutputStore; use IOutputStoreReader.");
        }
    }
}

internal static class TypeReflectionExtensions
{
    public static bool IsCompilerGenerated(this Type type)
    {
        return type.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null
            || (type.Name.Contains('<') && type.Name.Contains('>'));
    }
}

#endif
