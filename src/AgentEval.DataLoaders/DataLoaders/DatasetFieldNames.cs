// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;

namespace AgentEval.DataLoaders;

/// <summary>
/// How every dataset loader matches a field name: case, underscores, hyphens and spaces are ignored, so
/// <c>expected_output</c>, <c>expectedOutput</c>, <c>ExpectedOutput</c> and <c>expected-output</c> are one field.
/// </summary>
/// <remarks>
/// The loaders used to read snake_case only. A dataset written in camelCase, the natural spelling in .NET, lost its
/// expected outputs without a word (JSON kept them as metadata, YAML dropped them), and with no judge configured a test
/// case with no expected output passes any non-empty answer: every test passed, whatever the agent said.
/// </remarks>
internal static class DatasetFieldNames
{
    /// <summary>Lower-cases <paramref name="name"/> and drops underscores, hyphens and spaces.</summary>
    public static string Normalize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is not ('_' or '-' or ' '))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    /// <summary>The fields a test case is read from; any other key is kept as metadata.</summary>
    public static readonly IReadOnlySet<string> KnownFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "id", "category",
        "input", "question", "prompt", "query",
        "expected", "expectedoutput", "answer", "response",
        "context", "contexts", "documents",
        "expectedtools", "tools",
        "groundtruth", "function", "arguments",
        "evaluationcriteria", "tags", "passingscore",
    };

    /// <summary>Whether <paramref name="name"/>, in any spelling, is one of <see cref="KnownFields"/>.</summary>
    public static bool IsKnown(string name) => KnownFields.Contains(Normalize(name));
}
