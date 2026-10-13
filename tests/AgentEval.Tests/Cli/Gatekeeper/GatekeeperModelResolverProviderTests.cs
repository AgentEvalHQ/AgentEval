// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli;
using AgentEval.Cli.Commands.Gatekeeper;
using AgentEval.Tests.Cli;
using Xunit;

namespace AgentEval.Tests.Cli.Gatekeeper;

/// <summary>
/// With no model flags, <c>gatekeeper inspect --gate judge:*</c>, the panel and <c>gatekeeper calibrate</c> build their
/// judge from the provider <c>AI_INFERENCE_PROVIDER</c> selects, like the bench judges and the samples. Before, the
/// fallback read only <c>AZURE_OPENAI_*</c>, so a Bitdeer-configured machine had to pass explicit flags (or, with old
/// Azure variables still set, silently judged on Azure). No client built here makes a network call.
/// </summary>
[Collection("EnvVarTests")]
public sealed class GatekeeperModelResolverProviderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Resolve_CancelledCall_RetainsConfiguredModelWithoutInventingUsage(int source)
    {
        using var meter = AgentEval.Cli.Infrastructure.CliUsageMeter.Begin();
        await using var endpoint = await AgentEval.Tests.TestHelpers.LocalChatEndpoint.StartAsync();
        using var env = new ProviderEnvironmentScope(
            ("AI_INFERENCE_PROVIDER", "openai-compatible"), ("OPENAI_COMPATIBLE_ENDPOINT", endpoint.Endpoint),
            ("OPENAI_COMPATIBLE_MODEL", "configured-model"), ("OPENAI_COMPATIBLE_API_KEY", "local-test"));
        using var client = source == 3
            ? AgentEval.Cli.Commands.LogFileCommand.ResolveAgainst(endpoint.Endpoint, "configured-model", "local-test", false).Client
            : GatekeeperModelResolver.Resolve(source == 1, source == 2 ? null : endpoint.Endpoint,
                source == 1 ? "configured-model" : null, "configured-model", "local-test", new StringWriter()).Client;
        Assert.NotNull(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "question")],
            cancellationToken: cancellation.Token));
        var usage = Assert.Single(meter.Snapshot()!);
        Assert.Equal("configured-model", usage.Model);
        Assert.Equal(source == 3 ? AgentEval.Results.Writing.AefUsageRole.Agent : AgentEval.Results.Writing.AefUsageRole.Judge, usage.Role);
        Assert.Null(usage.InputTokens);
        Assert.Null(usage.CostUsd);
        Assert.Empty(endpoint.Requests);
    }

    private static ModelResolution ResolveFromEnvironment(out string stderr)
    {
        var err = new StringWriter();
        var r = GatekeeperModelResolver.Resolve(azure: false, endpoint: null, deploymentName: null, model: null, apiKey: null, err);
        stderr = err.ToString();
        return r;
    }

    [Fact]
    public void Bitdeer_Selected_BuildsTheJudgeOnBitdeer()
    {
        using var env = new ProviderEnvironmentScope(("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", "test-key"));

        var r = ResolveFromEnvironment(out var stderr);

        Assert.Equal(ExitCodes.Success, r.ExitCode);
        Assert.NotNull(r.Client);
        Assert.StartsWith("bitdeer:api-inference.bitdeer.ai/v1:zai-org/GLM-5.3-Flash@", r.Fingerprint);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void ExplicitSelector_WinsOverLeftoverAzureVariables()
    {
        using var env = new ProviderEnvironmentScope(
            ("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", "test-key"),
            ("AZURE_OPENAI_ENDPOINT", "https://old.openai.azure.com/"), ("AZURE_OPENAI_API_KEY", "old-key"),
            ("AZURE_OPENAI_DEPLOYMENT", "gpt-5.5"));

        var r = ResolveFromEnvironment(out _);

        Assert.Equal(ExitCodes.Success, r.ExitCode);
        Assert.StartsWith("bitdeer:", r.Fingerprint);
    }

    [Fact]
    public void AzureOnly_KeepsTheFingerprintItHadBefore_SoExistingCertificatesStayValid()
    {
        using var env = new ProviderEnvironmentScope(
            ("AZURE_OPENAI_ENDPOINT", "https://x.openai.azure.com/"), ("AZURE_OPENAI_API_KEY", "k"),
            ("AZURE_OPENAI_DEPLOYMENT", "judge-deployment"));

        var r = ResolveFromEnvironment(out _);

        Assert.Equal(ExitCodes.Success, r.ExitCode);
        Assert.Equal(GatekeeperModelResolver.Fingerprint("azure", "https://x.openai.azure.com/", "judge-deployment"), r.Fingerprint);
    }

    [Fact]
    public void NothingConfigured_IsAUsageError_ThatNamesTheProviderSelector()
    {
        using var env = new ProviderEnvironmentScope();

        var r = ResolveFromEnvironment(out var stderr);

        Assert.Equal(ExitCodes.UsageError, r.ExitCode);
        Assert.Null(r.Client);
        Assert.Contains("AI_INFERENCE_PROVIDER", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitSelectorWithoutItsKey_FailsClosed_AndSaysWhy()
    {
        // An explicit choice whose variables are missing must not fall back to another host.
        using var env = new ProviderEnvironmentScope(
            ("AI_INFERENCE_PROVIDER", "bitdeer"),
            ("AZURE_OPENAI_ENDPOINT", "https://old.openai.azure.com/"), ("AZURE_OPENAI_API_KEY", "old-key"),
            ("AZURE_OPENAI_DEPLOYMENT", "gpt-5.5"));

        var r = ResolveFromEnvironment(out var stderr);

        Assert.Equal(ExitCodes.UsageError, r.ExitCode);
        Assert.Null(r.Client);
        Assert.Contains("BITDEER_API_KEY", stderr, StringComparison.Ordinal);
    }
}
