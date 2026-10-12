// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/Cli/EndpointFactoryTests.cs
//
// M14: the --api-key help promises an OPENAI_API_KEY env fallback for the OpenAI-compatible path. These tests pin
// the resolution order (explicit key > OPENAI_API_KEY env > "no-key-needed" sentinel) so the help is honest.
using AgentEval.Cli.Infrastructure;
using AgentEval.Results;
using AgentEval.Tests.TestHelpers;
using Microsoft.Extensions.AI;

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

    [Theory]
    [InlineData(null, "Bearer no-key-needed")]
    [InlineData("scoped-key", "Bearer scoped-key")]
    public async Task CreateForRunner_AmbientKeyPresent_UsesOnlyScopedCredential(string? key, string expected)
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "ambient-key-must-not-be-used");
        await using var endpoint = await LocalChatEndpoint.StartAsync();
        using var client = EndpointFactory.CreateForRunner(endpoint.Endpoint, "local-model", key);

        await client.GetResponseAsync("Hello");

        Assert.Equal(expected, Assert.Single(endpoint.AuthorizationHeaders));
        Assert.Equal("ambient-key-must-not-be-used", Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
    }

    [Theory]
    [InlineData(429, false)]
    [InlineData(500, false)]
    [InlineData(503, false)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    public async Task CreateForRunner_RetryableResponse_MakesOnlyOneHttpAttempt(int statusCode, bool azure)
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync((_, _) => Task.FromResult("unused"), statusCode);
        using var client = EndpointFactory.CreateForRunner(azure ? endpoint.Endpoint[..^3] : endpoint.Endpoint,
            "local-model", "scoped-key", azure);

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync("Hello"));

        Assert.Single(endpoint.Requests);
    }

    [Fact]
    public async Task CreateForRunner_AzureWithScopedKey_DeliversKeyToEndpoint()
    {
        await using var endpoint = await LocalChatEndpoint.StartAsync();
        using var client = EndpointFactory.CreateForRunner(endpoint.Endpoint[..^3], "local-model", "scoped-azure-key", azure: true);

        await client.GetResponseAsync("Hello");

        Assert.Equal("scoped-azure-key", Assert.Single(endpoint.ApiKeyHeaders));
    }

    [Fact]
    public void CreateForRunner_AzureWithoutScopedKey_RefusesEvenWhenAmbientKeyExists()
    {
        var saved = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "ambient-azure-key");
            var error = Assert.Throws<AefInputException>(() => EndpointFactory.CreateForRunner(
                "https://example.openai.azure.com/", "deployment", null, azure: true));
            Assert.DoesNotContain("ambient-azure-key", error.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", saved);
        }
    }

    [Theory]
    [InlineData("https://secret:password@example.test/v1")]
    [InlineData("https://example.test/v1?api-key=secret")]
    [InlineData("https://example.test/v1#secret")]
    [InlineData("file:///secret")]
    public void CreateForRunner_UnsafeEndpoint_RefusesWithoutEchoingIt(string endpoint)
    {
        var error = Assert.Throws<AefInputException>(() => EndpointFactory.CreateForRunner(endpoint, "model", null));
        Assert.DoesNotContain("secret", error.Message);
        Assert.DoesNotContain(endpoint, error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void CreateForRunner_EmptyScopedCredential_Refuses(string key)
    {
        Assert.Throws<AefInputException>(() => EndpointFactory.CreateForRunner("https://example.test/v1", "model", key));
    }
}
