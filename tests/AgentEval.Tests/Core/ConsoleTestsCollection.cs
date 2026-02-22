// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Collection definition for tests that modify Console.Out.
/// Tests in this collection run sequentially to prevent race conditions.
/// </summary>
[CollectionDefinition("ConsoleTests", DisableParallelization = true)]
public class ConsoleTestsCollection : ICollectionFixture<ConsoleTestsFixture>
{
}

/// <summary>
/// Fixture to ensure Console.Out is properly managed across tests.
/// </summary>
public class ConsoleTestsFixture : IDisposable
{
    private static readonly object ConsoleLock = new();
    
    public static object Lock => ConsoleLock;
    
    public void Dispose()
    {
        // Nothing to dispose
    }
}
