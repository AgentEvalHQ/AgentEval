// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/Cli/EndpointFactoryTests.cs
//
// M14: the --api-key help promises an OPENAI_API_KEY env fallback for the OpenAI-compatible path. These tests pin
// the resolution order (explicit key > OPENAI_API_KEY env > "no-key-needed" sentinel) so the help is honest.
using AgentEval.Cli.Infrastructure;

namespace AgentEval.Tests.Cli;

[Collection("EnvVarTests")]
public sealed class EndpointFactoryTests : IDisposable
{
    private readonly string? _saved = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

    public void Dispose() => Environment.SetEnvironmentVariable("OPENAI_API_KEY", _saved);

    [Fact]
    public void ResolveOpenAIKey_ExplicitKey_Wins()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "env-key");
        Assert.Equal("explicit", EndpointFactory.ResolveOpenAIKey("explicit"));
    }

    [Fact]
    public void ResolveOpenAIKey_FallsBackToEnvVar_WhenNoExplicitKey()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "env-key");
        Assert.Equal("env-key", EndpointFactory.ResolveOpenAIKey(null));
    }

    [Fact]
    public void ResolveOpenAIKey_NeitherSet_UsesNoKeyNeededSentinel()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        Assert.Equal("no-key-needed", EndpointFactory.ResolveOpenAIKey(null));
    }
}
