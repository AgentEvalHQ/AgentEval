// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>Discriminates between agent and workflow subjects.</summary>
public enum SubjectKind
{
    Agent,
    Workflow
}

/// <summary>Extension methods for <see cref="SubjectKind"/>.</summary>
public static class SubjectKindExtensions
{
    /// <summary>Returns the folder name used for this subject kind within the output store.</summary>
    public static string Folder(this SubjectKind k) => k switch
    {
        SubjectKind.Agent    => "agents",
        SubjectKind.Workflow => "workflows",
        _ => throw new ArgumentOutOfRangeException(nameof(k))
    };
}
