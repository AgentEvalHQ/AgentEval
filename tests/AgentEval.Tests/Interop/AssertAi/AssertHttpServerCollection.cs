// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;

namespace AgentEval.Tests.Interop.AssertAi;

/// <summary>
/// Isolates the ASSERT HTTP sample and server tests from parallel suite load. These tests reserve then
/// release ephemeral ports before HttpListener binds, including a wildcard host listener.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AssertHttpServerCollection
{
    public const string Name = "AssertHttpServer";
}
